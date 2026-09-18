using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.Loader;
using Dzaba.Build.Caching;
using Dzaba.Build.Concurrency;
using Dzaba.Build.Graph;
using Dzaba.Build.Lib.Hashing;
using Dzaba.Build.Logging;
using Microsoft.Build.Framework;
using Microsoft.Build.Utilities;
using Microsoft.Extensions.Logging;

namespace Dzaba.Build.Tasks;

/// <summary>
/// Combines ADR-0002's DzabaRestoreCachedDeps and DzabaBuildMissingProjectRefs into a single
/// task, invoked once per project with DzabaBuildEnabled=true. Walks the entry project's full
/// ProjectReference closure (not just direct references - the SDK's own reference resolution
/// puts every transitively referenced project's output on the compiler's reference list, so a
/// cache hit on a direct reference does not let deeper levels be skipped). For each node: compute
/// its cache key (per ADR-0003), then either use an already-valid local output, restore from
/// cache, or run a nested in-process build and publish the result - then recurse into that
/// node's own references the same way. A node reached from multiple paths (a diamond dependency)
/// is only resolved once per task execution.
/// </summary>
public sealed class ResolveCachedProjectReferencesTask : Task
{
    [Required]
    public string ProjectFile { get; set; }

    [Required]
    public string Configuration { get; set; }

    public string Platform { get; set; }

    [Required]
    public string TargetFramework { get; set; }

    public bool CacheDisabled { get; set; }

    public string CacheEnvVars { get; set; }

    public string ExcludedDirectoryNames { get; set; }

    public string ExcludedFilePatterns { get; set; }

    public string AncestorFileNames { get; set; }

    public string VersionFileName { get; set; }

    /// <summary>Path to the assembly implementing the configured <see cref="ICacheStorage"/> backend.</summary>
    public string CacheStorageAssembly { get; set; }

    /// <summary>Full type name of the <see cref="ICacheStorage"/> implementation in <see cref="CacheStorageAssembly"/>.
    /// The type must have a public constructor taking a single string (the root/storage directory).</summary>
    public string CacheStorageType { get; set; }

    public string CacheStorageDirectory { get; set; }

    public override bool Execute()
    {
        ICacheStorage storage;
        try
        {
            storage = ResolveCacheStorage();
        }
        catch (Exception ex)
        {
            Log.LogError($"Failed to load cache storage backend '{CacheStorageType}' from '{CacheStorageAssembly}': {ex}");
            return false;
        }

        if (storage == null)
        {
            Log.LogError(
                "DzabaBuildEnabled is set but no cache storage backend is configured (DzabaCacheStorageAssembly / " +
                "DzabaCacheStorageType). Import a storage backend package (e.g. Dzaba.Build.FileCache) alongside Dzaba.Build.");
            return false;
        }

        var logger = new TaskLoggingHelperLogger(Log);
        var evaluator = new ProjectEvaluator();
        var fileHasher = new FileHasher(new TaskLoggingHelperLogger<FileHasher>(Log));
        var cacheKeyService = new CacheKeyService(evaluator, fileHasher, logger);

        var options = new CacheKeyOptions
        {
            ExcludedDirectoryNames = Split(ExcludedDirectoryNames),
            ExcludedFilePatterns = Split(ExcludedFilePatterns),
            AncestorFileNames = Split(AncestorFileNames),
            VersionFileName = VersionFileName,
            EnvVarNames = Split(CacheEnvVars)
        };

        ProjectInfo entryInfo;
        try
        {
            entryInfo = evaluator.Evaluate(ProjectFile, Configuration, Platform, TargetFramework);
        }
        catch (Exception ex)
        {
            Log.LogError("Failed to evaluate '{0}': {1}", ProjectFile, ex.Message);
            return false;
        }

        var resolved = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var success = true;
        foreach (var reference in entryInfo.ProjectReferences)
        {
            try
            {
                if (!ResolveReferenceRecursive(reference, entryInfo.TargetFramework, cacheKeyService, storage, options, resolved))
                {
                    success = false;
                }
            }
            catch (Exception ex)
            {
                Log.LogError("Failed resolving reference '{0}': {1}", reference, ex.Message);
                success = false;
            }
        }

        return success;
    }

    /// <summary>
    /// Resolves <paramref name="referenceProjectFile"/> (hit-restore or miss-build), then
    /// recurses into *its own* ProjectReference items too, regardless of whether it was itself a
    /// hit or a miss. This is required, not just an optimization: the SDK's own
    /// ResolveProjectReferences/ResolveAssemblyReferences pipeline puts every transitively
    /// referenced project's output on the compiler's reference list, not just direct references
    /// - restoring only App1's direct references (Lib7, Lib10) leaves Lib7's own reference
    /// (Lib1) missing on disk whenever Lib7 itself was a cache hit (no nested build ran to
    /// materialize it). <paramref name="resolved"/> dedupes nodes reached from multiple paths
    /// (e.g. Lib1 via both Lib7 and Lib10's own references) within this one task execution.
    /// </summary>
    private bool ResolveReferenceRecursive(
        string referenceProjectFile,
        string callerTargetFramework,
        CacheKeyService cacheKeyService,
        ICacheStorage storage,
        CacheKeyOptions options,
        HashSet<string> resolved)
    {
        var (key, info) = cacheKeyService.ComputeKey(referenceProjectFile, Configuration, Platform, callerTargetFramework, options);

        var memoKey = info.ProjectFile.ToLowerInvariant() + "|" + info.TargetFramework;
        if (!resolved.Add(memoKey))
        {
            return true;
        }

        var success = ResolveReference(referenceProjectFile, key, info, storage);

        foreach (var childReference in info.ProjectReferences)
        {
            if (!ResolveReferenceRecursive(childReference, info.TargetFramework, cacheKeyService, storage, options, resolved))
            {
                success = false;
            }
        }

        return success;
    }

    /// <summary>
    /// Loads the configured backend assembly into the SAME <see cref="AssemblyLoadContext"/> this
    /// task assembly is already running in (rather than the default `Assembly.LoadFrom`
    /// resolution, whose behavior for an already-loaded dependency is less predictable), so its
    /// reference to <see cref="ICacheStorage"/> resolves to the exact same loaded Dzaba.Build
    /// assembly and the cast below is type-safe. Cached in <see cref="CacheStorageRegistry"/>.
    /// </summary>
    private ICacheStorage ResolveCacheStorage()
    {
        if (CacheStorageRegistry.Current != null)
        {
            return CacheStorageRegistry.Current;
        }

        if (string.IsNullOrEmpty(CacheStorageAssembly) || string.IsNullOrEmpty(CacheStorageType))
        {
            return null;
        }

        var alc = AssemblyLoadContext.GetLoadContext(typeof(ResolveCachedProjectReferencesTask).Assembly)
            ?? AssemblyLoadContext.Default;
        var backendAssembly = alc.LoadFromAssemblyPath(Path.GetFullPath(CacheStorageAssembly));
        var backendType = backendAssembly.GetType(CacheStorageType, throwOnError: true);
        var storage = (ICacheStorage)Activator.CreateInstance(backendType, CacheStorageDirectory);

        CacheStorageRegistry.Current = storage;
        return storage;
    }

    private bool ResolveReference(string referenceProjectFile, string key, ProjectInfo info, ICacheStorage storage)
    {
        if (HasValidLocalOutput(info))
        {
            Log.LogMessage(MessageImportance.High, $"{info.ProjectFile} ({info.TargetFramework}): local output already present, using as-is.");
            return true;
        }

        using var gate = new CrossProcessLock(key);
        gate.Wait();

        // Re-check after acquiring the lock: another process may have built or restored this
        // exact node while we were waiting.
        if (HasValidLocalOutput(info))
        {
            Log.LogMessage(MessageImportance.High, $"{info.ProjectFile} ({info.TargetFramework}): local output appeared while waiting for lock, using as-is.");
            return true;
        }

        if (!CacheDisabled && storage.Exists(key))
        {
            Log.LogMessage(MessageImportance.High, $"{info.ProjectFile} ({info.TargetFramework}): cache HIT ({key}) - restoring.");
            Directory.CreateDirectory(info.OutputDirectory);
            storage.Fetch(key, info.OutputDirectory);
            TouchOutputs(info.OutputDirectory);
            return true;
        }

        Log.LogMessage(MessageImportance.High, $"{info.ProjectFile} ({info.TargetFramework}): cache MISS ({key}) - building.");
        if (!RunNestedBuild(referenceProjectFile, info.TargetFramework))
        {
            return false;
        }

        if (!string.IsNullOrEmpty(info.OutputDirectory) && Directory.Exists(info.OutputDirectory))
        {
            storage.Publish(key, info.OutputDirectory);
            Log.LogMessage(MessageImportance.High, $"{info.ProjectFile} ({info.TargetFramework}): published to cache ({key}).");
        }
        else
        {
            Log.LogWarning($"{info.ProjectFile} ({info.TargetFramework}): nested build succeeded but output directory '{info.OutputDirectory}' was not found; nothing published.");
        }

        return true;
    }

    private static bool HasValidLocalOutput(ProjectInfo info)
    {
        return !string.IsNullOrEmpty(info.TargetPath) && File.Exists(info.TargetPath);
    }

    private static void TouchOutputs(string outputDirectory)
    {
        var now = DateTime.UtcNow;
        foreach (var file in Directory.EnumerateFiles(outputDirectory))
        {
            File.SetLastWriteTimeUtc(file, now);
        }
    }

    /// <summary>
    /// Builds a cache-miss reference in-process, as part of the *current* build session, via
    /// <see cref="IBuildEngine3.BuildProjectFilesInParallel"/> - the same mechanism the built-in
    /// <c>&lt;MSBuild&gt;</c> task uses to build other projects from within a custom task. No new
    /// OS process is spawned: the nested build shares this build's BuildManager/node pool and
    /// logger chain (so its own log output flows through automatically), and re-applies
    /// DzabaBuildEnabled=true as a global property so recursion into that project's own
    /// references happens the same way, per ADR-0002.
    /// </summary>
    private bool RunNestedBuild(string referenceProjectFile, string tfm)
    {
        var globalProperties = new Hashtable
        {
            ["Configuration"] = Configuration,
            ["TargetFramework"] = tfm,
            ["DzabaBuildEnabled"] = "true",
        };
        if (!string.IsNullOrEmpty(Platform))
        {
            globalProperties["Platform"] = Platform;
        }

        var engine = (IBuildEngine3)BuildEngine;
        var success = engine.BuildProjectFilesInParallel(
            new[] { referenceProjectFile },
            new[] { "Build" },
            new IDictionary[] { globalProperties },
            new IDictionary[1],
            new string[1],
            useResultsCache: false,
            unloadProjectsOnCompletion: true);

        if (!success)
        {
            Log.LogError($"Nested build of '{referenceProjectFile}' ({tfm}) failed.");
            return false;
        }

        return true;
    }

    private static string[] Split(string value)
    {
        return string.IsNullOrWhiteSpace(value)
            ? Array.Empty<string>()
            : value.Split(';').Select(v => v.Trim()).Where(v => v.Length > 0).ToArray();
    }
}
