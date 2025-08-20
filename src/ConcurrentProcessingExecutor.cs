using Microsoft.Extensions.Logging;
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
using Soenneker.ConcurrentProcessing.Executor.Dtos;

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
        var counter = new IndexCounter {Value = -1};

        for (var w = 0; w < workersCount; w++)
        {
            workers[w] = Worker(taskFactories, counter, cancellationToken);
        }

        try
        {
            Span<Task> span = workers.AsSpan(0, workersCount);
            await Task.WhenAll(span).NoSync();
        }
        finally
        {
            ArrayPool<Task>.Shared.Return(workers, clearArray: true);
        }
    }

    private Task Worker(List<Func<Task>> taskFactories, IndexCounter counter, CancellationToken cancellationToken) => Task.Run(async () =>
    {
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();

            int i = Interlocked.Increment(ref counter.Value);

            if ((uint) i >= (uint) taskFactories.Count)
                break;

            try
            {
                await taskFactories[i]().NoSync();
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger?.LogError(ex, "Error occurred while executing task #{Index}.", i);
            }
        }
    }, cancellationToken);

    public async ValueTask ExecuteWithRetry(List<Func<CancellationToken, ValueTask>> tasks, int maxRetries = 5, int initialDelayMs = 200,
        CancellationToken cancellationToken = default)
    {
        if (tasks.IsNullOrEmpty())
            return;

        int workersCount = Math.Min(_maxConcurrency, tasks.Count);
        Task[] workers = ArrayPool<Task>.Shared.Rent(workersCount);
        var counter = new IndexCounter {Value = -1};

        for (var w = 0; w < workersCount; w++)
        {
            workers[w] = RetryWorker(tasks, counter, maxRetries, initialDelayMs, cancellationToken);
        }

        try
        {
            Span<Task> span = workers.AsSpan(0, workersCount);
            await Task.WhenAll(span).NoSync();
        }
        finally
        {
            ArrayPool<Task>.Shared.Return(workers, clearArray: true);
        }
    }

    private Task RetryWorker(List<Func<CancellationToken, ValueTask>> tasks, IndexCounter counter, int maxRetries, int initialDelayMs,
        CancellationToken cancellationToken) => Task.Run(async () =>
    {
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();

            int i = Interlocked.Increment(ref counter.Value);
            if ((uint) i >= (uint) tasks.Count)
                break;

            try
            {
                await RetryWithBackoff(() => tasks[i](cancellationToken), maxRetries, initialDelayMs, cancellationToken).NoSync();
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger?.LogError(ex, "Task #{Index} failed after {MaxRetries} retries.", i, maxRetries);
                throw;
            }
        }
    }, cancellationToken);

    private static async ValueTask RetryWithBackoff(Func<ValueTask> operation, int maxRetries, int initialDelayMs, CancellationToken cancellationToken,
        int maxDelayMs = 30_000)
    {
        if (maxRetries < 1)
            throw new ArgumentOutOfRangeException(nameof(maxRetries));

        if (initialDelayMs < 0)
            throw new ArgumentOutOfRangeException(nameof(initialDelayMs));

        Exception? last = null;
        int delay = Math.Max(initialDelayMs, 1);

        for (var attempt = 1; attempt <= maxRetries; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            try
            {
                await operation().NoSync();
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

                delay = Math.Min(delay << 1, maxDelayMs);
                int jitter = RandomUtil.Next(0, delay + 1);
                await DelayUtil.Delay(jitter, null, cancellationToken).NoSync();
            }
        }

        ExceptionDispatchInfo.Capture(last!).Throw();
    }
}