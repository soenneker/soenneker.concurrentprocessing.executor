using Nito.AsyncEx;
using Soenneker.ConcurrentProcessing.Executor.Abstract;
using System.Collections.Generic;
using System.Threading.Tasks;
using System.Threading;
using System;
using Microsoft.Extensions.Logging;
using Soenneker.Utils.Random;
using Soenneker.Extensions.Task;
using Soenneker.Extensions.ValueTask;

namespace Soenneker.ConcurrentProcessing.Executor;

/// <inheritdoc cref="IConcurrentProcessingExecutor"/>
public class ConcurrentProcessingExecutor : IConcurrentProcessingExecutor
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
        var runningTasks = new List<Task>(taskFactories.Count);

        foreach (Func<Task> taskFactory in taskFactories)
        {
            await _semaphore.WaitAsync(cancellationToken)
                            .ConfigureAwait(false);

            Task runningTask = ExecuteTaskWithSemaphoreAsync(taskFactory);
            runningTasks.Add(runningTask);
        }

        await Task.WhenAll(runningTasks)
                  .NoSync();
    }

    private async Task ExecuteTaskWithSemaphoreAsync(Func<Task> taskFactory)
    {
        try
        {
            await taskFactory()
                .ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Error occurred while executing a task.");
        }
        finally
        {
            _semaphore.Release();
        }
    }

    public async ValueTask ExecuteWithRetry(List<Func<CancellationToken, ValueTask>> tasks, int maxRetries = 5, int initialDelayMs = 200, CancellationToken cancellationToken = default)
    {
        var runningTasks = new List<Task>(tasks.Count);

        foreach (Func<CancellationToken, ValueTask> taskFunc in tasks)
        {
            await _semaphore.WaitAsync(cancellationToken)
                            .NoSync();

            // Start task execution immediately and track it
            runningTasks.Add(ExecuteTaskWithRetryAsync(taskFunc, maxRetries, initialDelayMs, cancellationToken));
        }

        // Wait for all tasks concurrently
        await Task.WhenAll(runningTasks)
                  .NoSync();
    }

    private async Task ExecuteTaskWithRetryAsync(Func<CancellationToken, ValueTask> taskFunc, int maxRetries, int initialDelayMs, CancellationToken cancellationToken)
    {
        try
        {
            await RetryWithBackoff(() => taskFunc(cancellationToken), maxRetries, initialDelayMs, cancellationToken)
                .NoSync();
        }
        finally
        {
            _semaphore.Release(); // Ensure slot release
        }
    }

    private async ValueTask RetryWithBackoff(Func<ValueTask> operation, int maxRetries, int initialDelayMs, CancellationToken cancellationToken)
    {
        int delayMs = initialDelayMs;

        for (var attempt = 0; attempt < maxRetries; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            try
            {
                await operation()
                    .NoSync();
                return; // Success
            }
            catch (Exception ex) when (ex is TaskCanceledException or OperationCanceledException)
            {
                throw; // Ensure proper cancellation handling
            }
            catch (Exception ex)
            {
                int waitTime = delayMs + RandomUtil.Next(0, 100); // Add jitter
                _logger?.LogWarning("Task failed (Attempt {Attempt}/{MaxRetries}). Retrying in {WaitTime}ms... Error: {Error}", attempt + 1, maxRetries, waitTime, ex.Message);

                await Task.Delay(waitTime, cancellationToken)
                          .NoSync();
                delayMs *= 2; // Exponential backoff
            }
        }

        _logger?.LogError("Exceeded max retry attempts.");
        throw new Exception("Exceeded max retry attempts.");
    }
}