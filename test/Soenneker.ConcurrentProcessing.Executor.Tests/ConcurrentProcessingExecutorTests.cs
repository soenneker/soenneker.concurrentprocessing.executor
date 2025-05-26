using AwesomeAssertions;
using Soenneker.Tests.FixturedUnit;
using System.Collections.Generic;
using System.Threading.Tasks;
using System.Threading;
using System;
using Xunit;

namespace Soenneker.ConcurrentProcessing.Executor.Tests;

[Collection("Collection")]
public class ConcurrentProcessingExecutorTests : FixturedUnitTest
{
    private readonly ConcurrentProcessingExecutor _executor;

    public ConcurrentProcessingExecutorTests(Fixture fixture, ITestOutputHelper output) : base(fixture, output)
    {
        _executor = new ConcurrentProcessingExecutor(maxConcurrency: 3, Logger); // Limiting concurrency to 3
    }

    [Fact]
    public async Task Execute_ShouldRunAllTasks_WithinConcurrencyLimit()
    {
        // Arrange
        var concurrentCounter = 0;
        var maxObservedConcurrency = 0;

        var taskFactories = new List<Func<Task>>();

        for (int i = 0; i < 10; i++)
        {
            taskFactories.Add(async () =>
            {
                int current = Interlocked.Increment(ref concurrentCounter);
                maxObservedConcurrency = Math.Max(maxObservedConcurrency, current);

                await Task.Delay(200);
                Interlocked.Decrement(ref concurrentCounter);
            });
        }

        // Act
        await _executor.Execute(taskFactories);

        // Assert
        maxObservedConcurrency.Should()
                              .BeLessThanOrEqualTo(3);
    }

    [Fact]
    public async Task ExecuteWithRetry_ShouldRetryFailedTasks()
    {
        // Arrange
        int attemptCount = 0;
        var tasks = new List<Func<CancellationToken, ValueTask>>
        {
            async (cancellationToken) =>
            {
                attemptCount++;
                if (attemptCount < 3)
                    throw new Exception("Simulated failure");
                await Task.CompletedTask;
            }
        };

        // Act
        Func<Task> act = async () => await _executor.ExecuteWithRetry(tasks, maxRetries: 5, initialDelayMs: 50);

        // Assert
        await act.Should()
                 .NotThrowAsync();
        attemptCount.Should()
                    .Be(3); // Task should have retried twice before succeeding
    }

    [Fact]
    public async Task ExecuteWithRetry_ShouldFailAfterMaxRetries()
    {
        // Arrange
        int attemptCount = 0;
        var tasks = new List<Func<CancellationToken, ValueTask>>
        {
            async (cancellationToken) =>
            {
                attemptCount++;
                throw new Exception("Simulated failure");
            }
        };

        // Act
        Func<Task> act = async () => await _executor.ExecuteWithRetry(tasks, maxRetries: 3, initialDelayMs: 50);

        // Assert
        await act.Should()
                 .ThrowAsync<Exception>()
                 .WithMessage("Exceeded max retry attempts.");
        attemptCount.Should()
                    .Be(3); // Should attempt 3 times before failing
    }

    [Fact]
    public async Task ExecuteWithRetry_ShouldRespectCancellationToken()
    {
        // Arrange
        using var cts = new CancellationTokenSource(100); // Cancel after 100ms
        var tasks = new List<Func<CancellationToken, ValueTask>>
        {
            async (cancellationToken) => { await Task.Delay(500, cancellationToken); }
        };

        // Act
        Func<Task> act = async () => await _executor.ExecuteWithRetry(tasks, cancellationToken: cts.Token);

        // Assert
        await act.Should()
                 .ThrowAsync<TaskCanceledException>();
    }

    [Fact]
    public async Task Execute_ShouldHandleTaskFailuresWithoutCrashing()
    {
        // Arrange
        var tasks = new List<Func<Task>>
        {
            async () => throw new Exception("Simulated failure"), // Exception thrown within execution
            async () => await Task.CompletedTask
        };

        // Act
        Func<Task> act = async () => await _executor.Execute(tasks);

        // Assert
        await act.Should()
                 .NotThrowAsync();
    }
}