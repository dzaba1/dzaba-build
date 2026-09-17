using System;
using System.Collections.Generic;

namespace Dzaba.Build.Caching;

/// <summary>
/// User-configurable inputs to cache-key computation (ADR-0003). Nothing here is defaulted
/// inside the core library - every list is whatever the caller (ultimately, MSBuild properties
/// set by the consuming repo) supplies, since project layout, VCS, and CPM usage vary.
/// </summary>
public sealed class CacheKeyOptions
{
    public IReadOnlyList<string> ExcludedDirectoryNames { get; init; } = Array.Empty<string>();

    public IReadOnlyList<string> ExcludedFilePatterns { get; init; } = Array.Empty<string>();

    public IReadOnlyList<string> AncestorFileNames { get; init; } = Array.Empty<string>();

    public string VersionFileName { get; init; }

    public IReadOnlyList<string> EnvVarNames { get; init; } = Array.Empty<string>();
}
