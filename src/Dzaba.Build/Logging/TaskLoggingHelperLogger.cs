using System;
using Microsoft.Build.Framework;
using Microsoft.Build.Utilities;
using Microsoft.Extensions.Logging;

namespace Dzaba.Build.Logging;

/// <summary>
/// Adapts <see cref="Microsoft.Extensions.Logging.ILogger"/> to an MSBuild <see cref="TaskLoggingHelper"/>,
/// so core logic can log via the standard abstraction while a real Task run shows up in the
/// same build console/binlog output a consumer is already looking at.
/// </summary>
public sealed class TaskLoggingHelperLogger : Microsoft.Extensions.Logging.ILogger
{
    private readonly TaskLoggingHelper log;

    public TaskLoggingHelperLogger(TaskLoggingHelper log)
    {
        this.log = log;
    }

    public IDisposable BeginScope<TState>(TState state) where TState : notnull
    {
        return NullScope.Instance;
    }

    public bool IsEnabled(LogLevel logLevel)
    {
        return logLevel != LogLevel.None;
    }

    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception exception, Func<TState, Exception, string> formatter)
    {
        if (!IsEnabled(logLevel))
        {
            return;
        }

        var message = formatter(state, exception);

        switch (logLevel)
        {
            case LogLevel.Trace:
            case LogLevel.Debug:
                log.LogMessage(MessageImportance.Low, message);
                break;
            case LogLevel.Information:
                log.LogMessage(MessageImportance.High, message);
                break;
            case LogLevel.Warning:
                log.LogWarning(message);
                break;
            default:
                log.LogError(message);
                break;
        }

        if (exception != null)
        {
            log.LogMessage(MessageImportance.Low, exception.ToString());
        }
    }

    private sealed class NullScope : IDisposable
    {
        public static readonly NullScope Instance = new NullScope();

        public void Dispose()
        {
        }
    }
}

/// <summary>
/// Generic sibling of <see cref="TaskLoggingHelperLogger"/> for consumers that need an
/// <see cref="Microsoft.Extensions.Logging.ILogger{TCategoryName}"/>, e.g. types built with plain
/// constructor injection rather than the non-generic <see cref="Microsoft.Extensions.Logging.ILogger"/>.
/// </summary>
public sealed class TaskLoggingHelperLogger<T> : Microsoft.Extensions.Logging.ILogger<T>
{
    private readonly TaskLoggingHelperLogger inner;

    public TaskLoggingHelperLogger(TaskLoggingHelper log)
    {
        inner = new TaskLoggingHelperLogger(log);
    }

    public IDisposable BeginScope<TState>(TState state) where TState : notnull
    {
        return inner.BeginScope(state);
    }

    public bool IsEnabled(LogLevel logLevel)
    {
        return inner.IsEnabled(logLevel);
    }

    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception exception, Func<TState, Exception, string> formatter)
    {
        inner.Log(logLevel, eventId, state, exception, formatter);
    }
}

/// <summary>
/// Adapts <see cref="TaskLoggingHelperLogger"/> as an <see cref="ILoggerProvider"/>, so it can be
/// registered with <c>Microsoft.Extensions.Logging</c>'s <c>AddLogging</c>/<see cref="ILoggerFactory"/>
/// pipeline (e.g. to satisfy DI-resolved <see cref="ILogger{TCategoryName}"/> dependencies) instead
/// of only being usable via direct construction.
/// </summary>
public sealed class TaskLoggingHelperLoggerProvider : ILoggerProvider
{
    private readonly TaskLoggingHelper log;

    public TaskLoggingHelperLoggerProvider(TaskLoggingHelper log)
    {
        this.log = log;
    }

    public Microsoft.Extensions.Logging.ILogger CreateLogger(string categoryName)
    {
        return new TaskLoggingHelperLogger(log);
    }

    public void Dispose()
    {
    }
}
