using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using Dzaba.Build.Graph;
using Dzaba.Build.Lib.Hashing;
using Microsoft.Extensions.Logging;

namespace Dzaba.Build.Caching;

/// <summary>
/// Computes ADR-0003 cache keys bottom-up over the ProjectReference graph: a BLAKE3 digest over
/// the project's own file content, ancestor build-config files, the nearest version file, its
/// dependencies' already-computed keys, and tracked env vars - or a project's verbatim
/// <c>DzabaCacheKey</c> override.
///
/// One instance is scoped to a single top-level resolution (one Task execution). It is safe to
/// call <see cref="ComputeKey"/> concurrently from multiple threads for the same instance (e.g.
/// if a future version resolves sibling references in parallel): a diamond dependency reached
/// from two different threads is computed exactly once (via <see cref="ConcurrentDictionary{TKey,TValue}"/>
/// of <see cref="Lazy{T}"/>), while a genuine cycle within a single synchronous call chain is
/// detected via thread-local recursion tracking and reported with a clear error instead of
/// deadlocking or stack-overflowing.
/// </summary>
public sealed class CacheKeyService
{
    private readonly IProjectEvaluator evaluator;
    private readonly IFileHasher fileHasher;
    private readonly IHashCombiner hashCombiner;
    private readonly ILogger logger;
    private readonly ConcurrentDictionary<string, Lazy<(string Key, ProjectInfo Info)>> computedKeys =
        new ConcurrentDictionary<string, Lazy<(string, ProjectInfo)>>(StringComparer.OrdinalIgnoreCase);

    [ThreadStatic]
    private static HashSet<string> inProgressOnThisChain;

    private string sourceRoot;

    public CacheKeyService(IProjectEvaluator evaluator, IFileHasher fileHasher, IHashCombiner hashCombiner, ILogger logger)
    {
        this.evaluator = evaluator;
        this.fileHasher = fileHasher;
        this.hashCombiner = hashCombiner;
        this.logger = logger;
    }

    public (string Key, ProjectInfo Info) ComputeKey(
        string projectFile,
        string configuration,
        string platform,
        string callerTargetFramework,
        CacheKeyOptions options)
    {
        var info = evaluator.Evaluate(projectFile, configuration, platform, callerTargetFramework);

        // Thread-safe one-time initialization without a manually-managed volatile field:
        // LazyInitializer.EnsureInitialized publishes the value with the correct memory barriers
        // itself. Whichever thread gets there first "wins"; GetSourceTreeRoot is a pure function
        // of the repo layout, so any thread would compute the same result.
        LazyInitializer.EnsureInitialized(ref sourceRoot, () => evaluator.GetSourceTreeRoot(info.ProjectDirectory));

        var memoKey = info.ProjectFile.ToLowerInvariant() + "|" + info.TargetFramework;

        var chain = inProgressOnThisChain ??= new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (!chain.Add(memoKey))
        {
            throw new InvalidOperationException(
                $"Circular ProjectReference detected: '{info.ProjectFile}' ({info.TargetFramework}) references itself, directly or transitively.");
        }

        try
        {
            var lazy = computedKeys.GetOrAdd(
                memoKey,
                _ => new Lazy<(string, ProjectInfo)>(
                    () => ComputeKeyOnce(info, configuration, platform, options),
                    LazyThreadSafetyMode.ExecutionAndPublication));

            return lazy.Value;
        }
        finally
        {
            chain.Remove(memoKey);
        }
    }

    private (string Key, ProjectInfo Info) ComputeKeyOnce(ProjectInfo info, string configuration, string platform, CacheKeyOptions options)
    {
        string key;
        if (!string.IsNullOrEmpty(info.CacheKeyOverride))
        {
            key = info.CacheKeyOverride;
            logger.LogInformation("{Project} ({Tfm}): using manual DzabaCacheKey override '{Key}'", info.ProjectFile, info.TargetFramework, key);
        }
        else
        {
            key = ComputeContentKey(info, configuration, platform, options);
            logger.LogInformation("{Project} ({Tfm}): computed cache key {Key}", info.ProjectFile, info.TargetFramework, key);
        }

        return (key, info);
    }

    private string ComputeContentKey(ProjectInfo info, string configuration, string platform, CacheKeyOptions options)
    {
        using var combination = hashCombiner.CreateCombination();

        var relativeProjectPath = Path.GetRelativePath(sourceRoot, info.ProjectFile).Replace('\\', '/');
        combination.AddValue(relativeProjectPath);
        combination.AddValue(info.TargetFramework);

        var fileHashes = fileHasher.HashDirectory(info.ProjectDirectory, options.ExcludedDirectoryNames, options.ExcludedFilePatterns);
        logger.LogDebug("{Project} ({Tfm}): hashed {Count} project files", info.ProjectFile, info.TargetFramework, fileHashes.Count);
        combination.AddCount(fileHashes.Count);
        foreach (var fileHash in fileHashes)
        {
            combination.AddValue(fileHash.RelativePath);
            combination.AddValue(fileHash.Hash);
        }

        var ancestorFiles = evaluator.GetAncestorFiles(info.ProjectDirectory, options.AncestorFileNames)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(f => f, StringComparer.OrdinalIgnoreCase)
            .ToList();
        logger.LogDebug("{Project} ({Tfm}): {Count} ancestor build files matched", info.ProjectFile, info.TargetFramework, ancestorFiles.Count);
        combination.AddCount(ancestorFiles.Count);
        foreach (var file in ancestorFiles)
        {
            combination.AddValue(file);
            combination.AddValue(fileHasher.HashFile(file));
        }

        var versionFile = evaluator.GetNearestAncestorFile(info.ProjectDirectory, options.VersionFileName);
        combination.AddValue(versionFile != null ? fileHasher.HashFile(versionFile) : string.Empty);

        combination.AddCount(info.ProjectReferences.Count);
        foreach (var reference in info.ProjectReferences)
        {
            var (depKey, _) = ComputeKey(reference, configuration, platform, info.TargetFramework, options);
            combination.AddValue(depKey);
        }

        var envPairs = (options.EnvVarNames ?? Array.Empty<string>())
            .Where(n => !string.IsNullOrWhiteSpace(n))
            .Distinct(StringComparer.Ordinal)
            .OrderBy(n => n, StringComparer.Ordinal)
            .Select(name => (Name: name, Value: Environment.GetEnvironmentVariable(name) ?? string.Empty))
            .ToList();
        combination.AddCount(envPairs.Count);
        foreach (var (name, value) in envPairs)
        {
            combination.AddValue(name);
            combination.AddValue(value);
        }

        return combination.GetHash();
    }
}
