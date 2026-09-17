using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Microsoft.Build.Evaluation;

namespace Dzaba.Build.Graph;

/// <summary>
/// Evaluates project files via the MSBuild evaluation API (not a full build) to discover
/// direct ProjectReference items, the TFM slice to use for a multi-targeted reference, and
/// the ancestor build-config files an ADR-0003 cache key must include.
///
/// Which ancestor file names count (Directory.Build.props, Directory.Packages.props, ...) is
/// not hardcoded here - callers pass the list in (see DzabaCacheAncestorFiles), since not every
/// consumer uses Central Package Management or the same set of shared build files.
/// </summary>
public sealed class ProjectEvaluator : IProjectEvaluator
{
    public ProjectInfo Evaluate(string projectFile, string configuration, string platform, string callerTargetFramework)
    {
        projectFile = Path.GetFullPath(projectFile);
        var projectDirectory = Path.GetDirectoryName(projectFile);
        var tfmToUse = ResolveTargetFramework(projectFile, configuration, platform, callerTargetFramework);

        var globalProperties = new Dictionary<string, string>
        {
            ["Configuration"] = configuration,
            ["Platform"] = platform,
            ["TargetFramework"] = tfmToUse
        };

        using var collection = new ProjectCollection();
        try
        {
            var project = collection.LoadProject(projectFile, globalProperties, toolsVersion: null);

            var references = project.GetItems("ProjectReference")
                .Select(i => Path.GetFullPath(Path.Combine(projectDirectory, i.EvaluatedInclude)))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            var targetPath = ResolveFullPath(projectDirectory, project.GetPropertyValue("TargetPath"));
            var outputDirectory = string.IsNullOrEmpty(targetPath) ? null : Path.GetDirectoryName(targetPath);
            var cacheKeyOverride = project.GetPropertyValue("DzabaCacheKey");

            return new ProjectInfo(
                projectFile,
                projectDirectory,
                tfmToUse,
                references,
                targetPath,
                outputDirectory,
                string.IsNullOrEmpty(cacheKeyOverride) ? null : cacheKeyOverride);
        }
        finally
        {
            collection.UnloadAllProjects();
        }
    }

    /// <summary>
    /// Walks upward from <paramref name="startDirectory"/> looking for any of <paramref name="fileNames"/>
    /// at each directory level, stopping once the directory containing ".git" has been checked (or the
    /// filesystem root is reached - a directory-parent chain terminates naturally, it cannot cycle).
    /// </summary>
    public IReadOnlyList<string> GetAncestorFiles(string startDirectory, IEnumerable<string> fileNames)
    {
        var names = fileNames?.Where(n => !string.IsNullOrWhiteSpace(n)).ToArray() ?? Array.Empty<string>();
        var found = new List<string>();

        if (names.Length == 0)
        {
            return found;
        }

        foreach (var directory in WalkUpToSourceTreeRoot(startDirectory))
        {
            foreach (var fileName in names)
            {
                var candidate = Path.Combine(directory, fileName);
                if (File.Exists(candidate))
                {
                    found.Add(candidate);
                }
            }
        }

        return found;
    }

    /// <summary>
    /// Finds the nearest ancestor file named <paramref name="fileName"/>, walking upward the same
    /// way as <see cref="GetAncestorFiles"/>. Returns null if <paramref name="fileName"/> is empty
    /// (the input is disabled) or nothing is found.
    /// </summary>
    public string GetNearestAncestorFile(string startDirectory, string fileName)
    {
        if (string.IsNullOrWhiteSpace(fileName))
        {
            return null;
        }

        foreach (var directory in WalkUpToSourceTreeRoot(startDirectory))
        {
            var candidate = Path.Combine(directory, fileName);
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        return null;
    }

    /// <summary>
    /// The directory containing ".git", if found walking upward from <paramref name="startDirectory"/>;
    /// otherwise the topmost directory reached. Used to make cache keys source-tree-relative rather
    /// than dependent on the absolute path a repo happens to be checked out at.
    /// </summary>
    public string GetSourceTreeRoot(string startDirectory)
    {
        string last = startDirectory;
        foreach (var directory in WalkUpToSourceTreeRoot(startDirectory))
        {
            last = directory;
        }

        return last;
    }

    private static IEnumerable<string> WalkUpToSourceTreeRoot(string startDirectory)
    {
        var directory = new DirectoryInfo(startDirectory);
        while (directory != null)
        {
            yield return directory.FullName;

            if (Directory.Exists(Path.Combine(directory.FullName, ".git")))
            {
                yield break;
            }

            directory = directory.Parent;
        }
    }

    private static string ResolveTargetFramework(string projectFile, string configuration, string platform, string callerTargetFramework)
    {
        using var collection = new ProjectCollection();
        try
        {
            var globalProperties = new Dictionary<string, string>
            {
                ["Configuration"] = configuration,
                ["Platform"] = platform
            };

            var outerProject = collection.LoadProject(projectFile, globalProperties, toolsVersion: null);
            var targetFrameworks = outerProject.GetPropertyValue("TargetFrameworks");
            var singleTargetFramework = outerProject.GetPropertyValue("TargetFramework");

            if (!string.IsNullOrEmpty(targetFrameworks))
            {
                var list = targetFrameworks.Split(';').Select(t => t.Trim()).Where(t => t.Length > 0).ToArray();
                if (callerTargetFramework != null && list.Contains(callerTargetFramework, StringComparer.OrdinalIgnoreCase))
                {
                    return callerTargetFramework;
                }

                return list.First();
            }

            if (!string.IsNullOrEmpty(singleTargetFramework))
            {
                return singleTargetFramework;
            }

            return callerTargetFramework;
        }
        finally
        {
            collection.UnloadAllProjects();
        }
    }

    private static string ResolveFullPath(string baseDirectory, string path)
    {
        if (string.IsNullOrEmpty(path))
        {
            return null;
        }

        return Path.IsPathRooted(path) ? Path.GetFullPath(path) : Path.GetFullPath(Path.Combine(baseDirectory, path));
    }
}
