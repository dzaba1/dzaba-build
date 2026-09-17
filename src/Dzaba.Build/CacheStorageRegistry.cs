namespace Dzaba.Build;

/// <summary>
/// Holds the configured <see cref="ICacheStorage"/> for the lifetime of the current task
/// invocation, once resolved - see <see cref="Tasks.ResolveCachedProjectReferencesTask"/> and
/// ADR-0004. This is a genuine CLR static (thread-safe via a lock), which only works because
/// it is set and read entirely from within Dzaba.Build's own single loaded copy of itself: the
/// resolve task loads a configured backend assembly (e.g. Dzaba.Build.FileCache.dll) itself, by
/// path, into the same AssemblyLoadContext it is already running in, rather than relying on a
/// separately-declared MSBuild task to publish an instance across assembly boundaries. MSBuild
/// loads each `UsingTask` `AssemblyFile` into its own isolated load context, so two
/// independently-declared tasks would not reliably share type identity for `ICacheStorage`
/// even if both assemblies are "the same" Dzaba.Build.dll by content.
/// </summary>
public static class CacheStorageRegistry
{
    private static readonly object syncRoot = new object();
    private static ICacheStorage current;

    public static ICacheStorage Current
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
