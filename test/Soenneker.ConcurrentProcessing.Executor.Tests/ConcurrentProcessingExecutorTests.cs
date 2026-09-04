using AwesomeAssertions;
using Soenneker.Tests.HostedUnit;
using System.Collections.Generic;
using System.Threading.Tasks;
using System.Threading;
using System;

namespace Soenneker.ConcurrentProcessing.Executor.Tests;

[ClassDataSource<Host>(Shared = SharedType.PerTestSession)]
public class ConcurrentProcessingExecutorTests : HostedUnitTest
{
    private readonly ConcurrentProcessingExecutor _executor;

    public ConcurrentProcessingExecutorTests(Host host) : base(host)
    {
        _executor = new ConcurrentProcessingExecutor(maxConcurrency: 3, Logger); // Limiting concurrency to 3
    }

    [Test]
    public async Task Execute_ShouldRunAllTasks_WithinConcurrencyLimit(CancellationToken cancellationToken)
    {
        // Arrange
        var concurrentCounter = 0;
        var maxObservedConcurrency = 0;

        var taskFactories = new List<Func<Task>>();

        for (var i = 0; i < 10; i++)
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
        await _executor.Execute(taskFactories, cancellationToken: cancellationToken);

        // Assert
        maxObservedConcurrency.Should()
                              .BeLessThanOrEqualTo(3);
    }

    [Test]
    public async Task ExecuteWithRetry_ShouldRetryFailedTasks(CancellationToken cancellationToken)
    {
        // Arrange
        var attemptCount = 0;
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
        Func<Task> act = async () => await _executor.ExecuteWithRetry(tasks, maxRetries: 5, initialDelayMs: 50, cancellationToken: cancellationToken);

        // Assert
        await act.Should()
                 .NotThrowAsync();
        attemptCount.Should()
                    .Be(3); // Task should have retried twice before succeeding
    }

    [Test]
    public async Task ExecuteWithRetry_ShouldFailAfterMaxRetries(CancellationToken cancellationToken)
    {
        // Arrange
        var attemptCount = 0;
        var completedCount = 0;
        var executor = new ConcurrentProcessingExecutor(maxConcurrency: 1, Logger);
        var tasks = new List<Func<CancellationToken, ValueTask>>
        {
            async (cancellationToken) =>
            {
                attemptCount++;
                throw new InvalidOperationException("Simulated failure");
            },
            (cancellationToken) =>
            {
                completedCount++;
                return ValueTask.CompletedTask;
            }
        };

        // Act
        Func<Task> act = async () => await executor.ExecuteWithRetry(tasks, maxRetries: 3, initialDelayMs: 50, cancellationToken: cancellationToken);

        // Assert
        var assertion = await act.Should()
                                 .ThrowAsync<AggregateException>();

        assertion.Which.InnerExceptions.Should()
                 .ContainSingle()
                 .Which.Should()
                 .BeOfType<InvalidOperationException>()
                 .Which.Message.Should()
                 .Be("Simulated failure");

        attemptCount.Should()
                    .Be(3); // Should attempt 3 times before failing
        completedCount.Should().Be(1);
    }

    [Test]
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

    [Test]
    public async Task Execute_ShouldAggregateFailuresAfterCompletingRemainingTasks(CancellationToken cancellationToken)
    {
        // Arrange
        var completedCount = 0;
        var tasks = new List<Func<Task>>
        {
            async () => throw new InvalidOperationException("Simulated failure"),
            () =>
            {
                Interlocked.Increment(ref completedCount);
                return Task.CompletedTask;
            }
        };

        // Act
        Func<Task> act = async () => await _executor.Execute(tasks, cancellationToken: cancellationToken);

        // Assert
        var assertion = await act.Should()
                                 .ThrowAsync<AggregateException>();

        assertion.Which.InnerExceptions.Should()
                 .ContainSingle()
                 .Which.Should()
                 .BeOfType<InvalidOperationException>()
                 .Which.Message.Should()
                 .Be("Simulated failure");

        completedCount.Should().Be(1);
    }
}
