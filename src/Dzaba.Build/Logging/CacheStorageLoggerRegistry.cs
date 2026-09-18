using Microsoft.Extensions.Logging;

namespace Dzaba.Build.Logging;

/// <summary>
/// Holds the current task invocation's <see cref="ILoggerFactory"/>, so a pluggable
/// <see cref="ICacheStorage"/> backend loaded via ADR-0004's reflection convention - a plain
/// <c>(string)</c> constructor, with no way to receive dependencies directly - can still obtain a
/// typed <c>ILogger&lt;T&gt;</c> that flows into the same TaskLoggingHelper-backed sink as the rest
/// of the build's log output. Same same-AssemblyLoadContext reasoning as
/// <see cref="CacheStorageRegistry"/> applies here: this only works because it is set and read
/// entirely from within Dzaba.Build's own single loaded copy of itself.
/// </summary>
public static class CacheStorageLoggerRegistry
{
    private static readonly object syncRoot = new object();
    private static ILoggerFactory current;

    public static ILoggerFactory Current
    {
        get
        {
            lock (syncRoot)
            {
                return current;
            }
        }
        set
        {
            lock (syncRoot)
            {
                current = value;
            }
        }
    }
}
