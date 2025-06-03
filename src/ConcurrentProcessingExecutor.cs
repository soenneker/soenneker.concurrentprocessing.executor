using Nito.AsyncEx;
using Soenneker.ConcurrentProcessing.Executor.Abstract;
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Soenneker.Utils.Random;
using Soenneker.Extensions.Task;
using Soenneker.Extensions.ValueTask;
using Soenneker.Utils.Delay;

namespace Soenneker.ConcurrentProcessing.Executor;

/// <inheritdoc cref="IConcurrentProcessingExecutor"/>
public sealed class ConcurrentProcessingExecutor : IConcurrentProcessingExecutor
{
    private readonly AsyncSemaphore _semaphore;
    private readonly ILogger? _logger;

    public ConcurrentProcessingExecutor(int maxConcurrency, ILogger? logger = null)
    {
        _semaphore = new AsyncSemaphore(maxConcurrency);
        _logger = logger;
    }

    public async ValueTask Execute(List<Func<Task>> taskFactories, CancellationToken cancellationToken = default)
    {
        // Pre-size the list of running tasks to avoid internal resizes
        var runningTasks = new List<Task>(taskFactories.Count);

        for (var i = 0; i < taskFactories.Count; i++)
        {
            Func<Task> taskFactory = taskFactories[i];
            await _semaphore.WaitAsync(cancellationToken).NoSync();

            Task runningTask = ExecuteTaskWithSemaphoreAsync(taskFactory, _semaphore, _logger);
            runningTasks.Add(runningTask);
        }

        // WaitAll with NoSync() to avoid capturing the sync context
        await Task.WhenAll(runningTasks).NoSync();
    }

    private static async Task ExecuteTaskWithSemaphoreAsync(Func<Task> taskFactory, AsyncSemaphore semaphore, ILogger? logger)
    {
        try
        {
            await taskFactory().NoSync();
        }
        catch (Exception ex)
        {
            logger?.LogError(ex, "Error occurred while executing a task.");
        }
        finally
        {
            semaphore.Release();
        }
    }

    public async ValueTask ExecuteWithRetry(List<Func<CancellationToken, ValueTask>> tasks, int maxRetries = 5, int initialDelayMs = 200,
        CancellationToken cancellationToken = default)
    {
        var runningTasks = new List<Task>(tasks.Count);

        for (var i = 0; i < tasks.Count; i++)
        {
            Func<CancellationToken, ValueTask> taskFunc = tasks[i];
            await _semaphore.WaitAsync(cancellationToken).NoSync();

            Task runningTask = ExecuteTaskWithRetryAsync(taskFunc, maxRetries, initialDelayMs, cancellationToken, _semaphore, _logger);
            runningTasks.Add(runningTask);
        }

        await Task.WhenAll(runningTasks).NoSync();
    }

    private static async Task ExecuteTaskWithRetryAsync(Func<CancellationToken, ValueTask> taskFunc, int maxRetries, int initialDelayMs,
        CancellationToken cancellationToken, AsyncSemaphore semaphore, ILogger? logger)
    {
        try
        {
            await RetryWithBackoffAsync(() => taskFunc(cancellationToken), maxRetries, initialDelayMs, cancellationToken, logger).NoSync();
        }
        finally
        {
            semaphore.Release();
        }
    }

    private static async ValueTask RetryWithBackoffAsync(Func<ValueTask> operation, int maxRetries, int initialDelayMs, CancellationToken cancellationToken,
        ILogger? logger)
    {
        int delayMs = initialDelayMs;

        for (var attempt = 0; attempt < maxRetries; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            try
            {
                await operation().NoSync();
                return;
            }
            catch (Exception ex) when (ex is TaskCanceledException or OperationCanceledException)
            {
                // Propagate cancellation
                throw;
            }
            catch (Exception ex)
            {
                int waitTime = delayMs + RandomUtil.Next(0, 100);
                logger?.LogWarning("Task failed (Attempt {Attempt}/{MaxRetries}). Retrying in {WaitTime}ms... Error: {Error}", attempt + 1, maxRetries,
                    waitTime, ex.Message);

                await DelayUtil.Delay(waitTime, null, cancellationToken).NoSync();
                delayMs *= 2;
            }
        }

        logger?.LogError("Exceeded max retry attempts.");
        throw new Exception("Exceeded max retry attempts.");
    }
}