namespace FrameworkGetter;

internal static class Framework
{
    public static string GetCurrentFramework()
    {
#if NET48
        return "net48";
#elif NET10_0_WINDOWS
        return "net10.0-windows";
#elif NET10_0
        return "net10.0";
#elif NETSTANDARD2_0
        return "netstandard2.0";
#else
        return "unknown";
#endif
    }
}