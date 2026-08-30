[![](https://img.shields.io/nuget/v/soenneker.concurrentprocessing.executor.svg?style=for-the-badge)](https://www.nuget.org/packages/soenneker.concurrentprocessing.executor/)
[![](https://img.shields.io/github/actions/workflow/status/soenneker/soenneker.concurrentprocessing.executor/publish-package.yml?style=for-the-badge)](https://github.com/soenneker/soenneker.concurrentprocessing.executor/actions/workflows/publish-package.yml)
[![](https://img.shields.io/nuget/dt/soenneker.concurrentprocessing.executor.svg?style=for-the-badge)](https://www.nuget.org/packages/soenneker.concurrentprocessing.executor/)
[![](https://img.shields.io/github/actions/workflow/status/soenneker/soenneker.concurrentprocessing.executor/codeql.yml?label=CodeQL&style=for-the-badge)](https://github.com/soenneker/soenneker.concurrentprocessing.executor/actions/workflows/codeql.yml)

# Soenneker.ConcurrentProcessing.Executor

Runs a finite collection of asynchronous work with a fixed concurrency limit and optional per-item retries.

## Install

```bash
dotnet add package Soenneker.ConcurrentProcessing.Executor
```

## Execute state-based work

Construct an executor with a positive concurrency limit. The state-based overload avoids creating a closure for every item:

```csharp
using Soenneker.ConcurrentProcessing.Executor;

var executor = new ConcurrentProcessingExecutor(maxConcurrency: 8);

await executor.Execute(
    customerIds,
    static async (customerId, cancellationToken) =>
    {
        await SynchronizeCustomer(customerId, cancellationToken);
    },
    cancellationToken);
```

Each item is claimed once. Work-item failures are logged when an `ILogger` is supplied, other items continue, and the completed batch throws an `AggregateException` containing the failures.

## Execute task factories

Use the delegate-list overload when the work is already represented by task factories:

```csharp
var work = urls
    .Select(url => (Func<Task>)(() => Download(url, cancellationToken)))
    .ToList();

await executor.Execute(work, cancellationToken);
```

The factories do not receive the executor's cancellation token. Cancellation prevents new factories from starting; running factories stop only if they observe cancellation through their own captured state.

## Retry failed work

```csharp
var work = customerIds
    .Select(id => (Func<CancellationToken, ValueTask>)(ct => SynchronizeCustomer(id, ct)))
    .ToList();

await executor.ExecuteWithRetry(
    work,
    maxRetries: 4,
    initialDelayMs: 250,
    cancellationToken);
```

`maxRetries` is the total attempt count, including the first call. Delays use exponential backoff with full jitter and are capped at 30 seconds. After an item exhausts its attempts, its original exception is rethrown; cancellation is never retried.

## Operational notes

- This is an in-process batch executor, not a durable queue or background service.
- Do not mutate the supplied list until execution completes.
- A single executor can be reused across batches; the concurrency limit applies independently to each simultaneous `Execute` call, not globally across the instance.
- Retries require idempotent work or an operation-specific strategy for handling partial success.
- The logger is optional and does not replace exception handling by the caller.
