using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Soenneker.ConcurrentProcessing.Executor.Abstract;

/// <summary>
/// Defines an executor capable of running multiple asynchronous operations concurrently
/// with a bounded level of parallelism.
/// </summary>
/// <remarks>
/// Implementations are expected to:
/// <list type="bullet">
/// <item><description>Limit concurrent execution to a configured maximum.</description></item>
/// <item><description>Safely coordinate work distribution across workers.</description></item>
/// <item><description>Honor <see cref="CancellationToken"/> for cooperative cancellation.</description></item>
/// </list>
/// </remarks>
public interface IConcurrentProcessingExecutor
{
    /// <summary>
    /// Executes a collection of asynchronous task factories concurrently, bounded by the executor's
    /// maximum concurrency level.
    /// </summary>
    /// <param name="taskFactories">
    /// A list of factories that each produce a <see cref="Task"/> when invoked.
    /// Each factory will be executed at most once.
    /// </param>
    /// <param name="cancellationToken">
    /// A token used to signal cancellation of the operation.
    /// </param>
    /// <returns>
    /// A <see cref="ValueTask"/> that completes when all task factories have finished execution.
    /// </returns>
    /// <remarks>
    /// This overload requires creating one delegate per work item and may allocate closures
    /// at the call site. Prefer the state-based overload when minimizing allocations is important.
    /// </remarks>
    ValueTask Execute(List<Func<Task>> taskFactories, CancellationToken cancellationToken = default);

    /// <summary>
    /// Executes a collection of asynchronous operations concurrently with retry support,
    /// bounded by the executor's maximum concurrency level.
    /// </summary>
    /// <param name="tasks">
    /// A list of functions that execute a unit of work and accept a <see cref="CancellationToken"/>.
    /// Each function will be executed at most once, with retries applied on failure.
    /// </param>
    /// <param name="maxRetries">
    /// The maximum number of retry attempts per task before the exception is rethrown.
    /// </param>
    /// <param name="initialDelayMs">
    /// The initial delay (in milliseconds) before the first retry attempt.
    /// Subsequent retries use exponential backoff with jitter.
    /// </param>
    /// <param name="cancellationToken">
    /// A token used to signal cancellation of the operation.
    /// </param>
    /// <returns>
    /// A <see cref="ValueTask"/> that completes when all tasks have either succeeded
    /// or exhausted their retry attempts.
    /// </returns>
    ValueTask ExecuteWithRetry(List<Func<CancellationToken, ValueTask>> tasks, int maxRetries = 5, int initialDelayMs = 200,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Executes a collection of state-based work items concurrently using a static work delegate,
    /// bounded by the executor's maximum concurrency level.
    /// </summary>
    /// <typeparam name="TState">
    /// The type representing the immutable state required to perform a single unit of work.
    /// </typeparam>
    /// <param name="states">
    /// A read-only list of state objects, one per unit of work.
    /// </param>
    /// <param name="work">
    /// A delegate that performs the work for a single state instance.
    /// This delegate should be <c>static</c> to avoid closure allocations.
    /// </param>
    /// <param name="cancellationToken">
    /// A token used to signal cancellation of the operation.
    /// </param>
    /// <returns>
    /// A <see cref="ValueTask"/> that completes when all work items have finished execution.
    /// </returns>
    /// <remarks>
    /// This overload is the preferred execution model for high-performance scenarios.
    /// It avoids per-item closure allocations by passing all required data via <typeparamref name="TState"/>.
    /// </remarks>
    ValueTask Execute<TState>(IReadOnlyList<TState> states, Func<TState, CancellationToken, ValueTask> work, CancellationToken cancellationToken = default);
}