using System;
using Microsoft.Extensions.Logging;

namespace Soenneker.ConcurrentProcessing.Executor;

internal static class Log
{
    internal static readonly Action<ILogger, int, Exception?> LogWorkerError =
        LoggerMessage.Define<int>(LogLevel.Error, new EventId(1, nameof(LogWorkerError)), "Error occurred while executing task #{Index}.");

    internal static readonly Action<ILogger, int, int, Exception?> LogRetryFailed = LoggerMessage.Define<int, int>(LogLevel.Error,
        new EventId(2, nameof(LogRetryFailed)), "Task #{Index} failed after {MaxRetries} retries.");
}