using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using AwesomeAssertions;

namespace Soenneker.ConcurrentProcessing.Executor.Tests;

public class SequentialRegressionTests
{
    [Test]
    public async Task SequentialFailuresAreAggregatedAfterRemainingItems()
    {
        var seen = new List<int>();
        var executor = new ConcurrentProcessingExecutor(1);
        Func<Task> run = async () => await executor.Execute<int>(new[] { 0, 1, 2, 3 }, async (i, ct) =>
        {
            await Task.Yield();
            seen.Add(i);
            if (i % 2 == 0) throw new InvalidOperationException(i.ToString());
        });
        var failure = await run.Should().ThrowAsync<AggregateException>();
        failure.Which.InnerExceptions.Count.Should().Be(2);
        seen.Should().Equal(0, 1, 2, 3);
    }

    [Test]
    public async Task CancellationAfterFinalItemIsObserved()
    {
        using var cts = new CancellationTokenSource();
        var executor = new ConcurrentProcessingExecutor(8);
        Func<Task> run = async () => await executor.Execute<int>(new[] { 0 }, (i, ct) =>
        {
            cts.Cancel();
            return ValueTask.CompletedTask;
        }, cts.Token);
        await run.Should().ThrowAsync<OperationCanceledException>();
    }

    [Test]
    public async Task SynchronousWorkCompletesWithoutSchedulingTasks()
    {
        var executor = new ConcurrentProcessingExecutor(1);
        int seen = 0;
        ValueTask result = executor.Execute<int>(new[] { 0, 1, 2 }, (i, ct) => { seen++; return ValueTask.CompletedTask; });
        result.IsCompletedSuccessfully.Should().BeTrue();
        await result;
        seen.Should().Be(3);
    }
}
