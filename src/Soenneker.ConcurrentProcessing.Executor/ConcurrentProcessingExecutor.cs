using Microsoft.Extensions.Logging;
using Soenneker.Atomics.Ints;
using Soenneker.ConcurrentProcessing.Executor.Abstract;
using Soenneker.Extensions.Enumerable;
using Soenneker.Extensions.Task;
using Soenneker.Extensions.ValueTask;
using Soenneker.Utils.Delay;
using Soenneker.Utils.Random;
using System;
using System.Buffers;
using System.Collections.Generic;
using System.Runtime.ExceptionServices;
using System.Threading;
using System.Threading.Tasks;

namespace Soenneker.ConcurrentProcessing.Executor;

/// <inheritdoc cref="IConcurrentProcessingExecutor"/>
public sealed class ConcurrentProcessingExecutor : IConcurrentProcessingExecutor
{
    private readonly int _maxConcurrency;
    private readonly ILogger? _logger;

    public ConcurrentProcessingExecutor(int maxConcurrency, ILogger? logger = null)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(maxConcurrency, nameof(maxConcurrency));
        _maxConcurrency = maxConcurrency;
        _logger = logger;
    }

    public async ValueTask Execute(List<Func<Task>> taskFactories, CancellationToken cancellationToken = default)
    {
        if (taskFactories.IsNullOrEmpty())
            return;

        int workersCount = Math.Min(_maxConcurrency, taskFactories.Count);

        Task[] workers = ArrayPool<Task>.Shared.Rent(workersCount);

        // Shared ref-type counter; initialize to -1 so first Increment() returns 0.
        var counter = new AtomicInt(-1);

        for (var w = 0; w < workersCount; w++)
            workers[w] = WorkerCore(taskFactories, counter, cancellationToken, _logger);

        try
        {
            await Task.WhenAll(workers.AsSpan(0, workersCount))
                      .NoSync();
        }
        finally
        {
            Array.Clear(workers, 0, workersCount);
            ArrayPool<Task>.Shared.Return(workers, clearArray: false);
        }
    }

    private static async Task WorkerCore(List<Func<Task>> taskFactories, AtomicInt counter, CancellationToken cancellationToken, ILogger? logger)
    {
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();

            int i = counter.Increment(); // must return incremented value
            if ((uint)i >= (uint)taskFactories.Count)
                break;

            try
            {
                await taskFactories[i]()
                    .NoSync();
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                if (logger is not null)
                    Log.LogWorkerError(logger, i, ex);
            }
        }
    }

    public async ValueTask ExecuteWithRetry(List<Func<CancellationToken, ValueTask>> tasks, int maxRetries = 5, int initialDelayMs = 200,
        CancellationToken cancellationToken = default)
    {
        if (tasks.IsNullOrEmpty())
            return;

        ArgumentOutOfRangeException.ThrowIfLessThan(maxRetries, 1, nameof(maxRetries));
        ArgumentOutOfRangeException.ThrowIfNegative(initialDelayMs, nameof(initialDelayMs));

        int workersCount = Math.Min(_maxConcurrency, tasks.Count);

        Task[] workers = ArrayPool<Task>.Shared.Rent(workersCount);

        var counter = new AtomicInt(-1);

        for (var w = 0; w < workersCount; w++)
            workers[w] = RetryWorkerCore(tasks, counter, maxRetries, initialDelayMs, cancellationToken, _logger);

        try
        {
            await Task.WhenAll(workers.AsSpan(0, workersCount))
                      .NoSync();
        }
        finally
        {
            Array.Clear(workers, 0, workersCount);
            ArrayPool<Task>.Shared.Return(workers, clearArray: false);
        }
    }

    private static async Task RetryWorkerCore(List<Func<CancellationToken, ValueTask>> tasks, AtomicInt counter, int maxRetries, int initialDelayMs,
        CancellationToken cancellationToken, ILogger? logger)
    {
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();

            int i = counter.Increment();
            if ((uint)i >= (uint)tasks.Count)
                break;

            try
            {
                // No closure: ValueTuple state + static delegate
                (Func<CancellationToken, ValueTask> Task, CancellationToken Ct) opState = (Task: tasks[i], Ct: cancellationToken);

                await RetryWithBackoff(static s => s.Task(s.Ct), opState, maxRetries, initialDelayMs, cancellationToken)
                    .NoSync();
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                if (logger is not null)
                    Log.LogRetryFailed(logger, i, maxRetries, ex);

                throw;
            }
        }
    }

    public async ValueTask Execute<TState>(IReadOnlyList<TState> states, Func<TState, CancellationToken, ValueTask> work,
        CancellationToken cancellationToken = default)
    {
        if (states is null)
            throw new ArgumentNullException(nameof(states));

        if (work is null)
            throw new ArgumentNullException(nameof(work));

        if (states.Count == 0)
            return;

        int workersCount = Math.Min(_maxConcurrency, states.Count);

        Task[] workers = ArrayPool<Task>.Shared.Rent(workersCount);

        var counter = new AtomicInt(-1);

        for (var w = 0; w < workersCount; w++)
            workers[w] = GenericWorkerCore(states, work, counter, cancellationToken, _logger);

        try
        {
            await Task.WhenAll(workers.AsSpan(0, workersCount))
                      .NoSync();
        }
        finally
        {
            Array.Clear(workers, 0, workersCount);
            ArrayPool<Task>.Shared.Return(workers, clearArray: false);
        }
    }

    private static async Task GenericWorkerCore<TState>(IReadOnlyList<TState> states, Func<TState, CancellationToken, ValueTask> work, AtomicInt counter,
        CancellationToken cancellationToken, ILogger? logger)
    {
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();

            int i = counter.Increment();
            if ((uint)i >= (uint)states.Count)
                break;

            try
            {
                await work(states[i], cancellationToken)
                    .NoSync();
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                if (logger is not null)
                    Log.LogWorkerError(logger, i, ex);
            }
        }
    }

    private static async ValueTask RetryWithBackoff<TState>(Func<TState, ValueTask> operation, TState operationState, int maxRetries, int initialDelayMs,
        CancellationToken cancellationToken, int maxDelayMs = 30_000)
    {
        if (maxRetries < 1)
            throw new ArgumentOutOfRangeException(nameof(maxRetries));

        if (initialDelayMs < 0)
            throw new ArgumentOutOfRangeException(nameof(initialDelayMs));

        Exception? last = null;

        // Wait using the current delay value, then increase for the next failure.
        int delay = Math.Max(initialDelayMs, 1);

        for (var attempt = 1; attempt <= maxRetries; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            try
            {
                await operation(operationState)
                    .NoSync();
                return;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                last = ex;

                if (attempt == maxRetries)
                    break;

                int jitter = RandomUtil.Next(0, delay + 1);

                await DelayUtil.Delay(jitter, null, cancellationToken)
                               .NoSync();

                delay = Math.Min(delay << 1, maxDelayMs);
            }
        }

        ExceptionDispatchInfo.Capture(last!)
                             .Throw();
    }
}