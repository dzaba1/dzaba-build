using System;

namespace Dzaba.Build.GhcrOciCache;

/// <summary>
/// Thrown for any GHCR OCI Distribution API response that isn't a plain success or an expected
/// "not found" on a lookup. The message is the only thing that reaches the user - the calling
/// <c>ResolveCachedProjectReferencesTask</c> (in Dzaba.Build) logs <c>ex.Message</c> only - so it always
/// carries the method, full URL, status code, and a truncated response body.
/// </summary>
public sealed class GhcrRegistryException : InvalidOperationException
{
    public GhcrRegistryException(string message) : base(message)
    {
    }
}
