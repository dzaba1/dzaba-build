using System.Collections.Generic;

namespace Dzaba.Build.Graph;

public sealed class ProjectInfo
{
    public string ProjectFile { get; }

    public string ProjectDirectory { get; }

    public string TargetFramework { get; }

    public IReadOnlyList<string> ProjectReferences { get; }

    public string TargetPath { get; }

    public string OutputDirectory { get; }

    public string CacheKeyOverride { get; }

    public ProjectInfo(
        string projectFile,
        string projectDirectory,
        string targetFramework,
        IReadOnlyList<string> projectReferences,
        string targetPath,
        string outputDirectory,
        string cacheKeyOverride)
    {
        ProjectFile = projectFile;
        ProjectDirectory = projectDirectory;
        TargetFramework = targetFramework;
        ProjectReferences = projectReferences;
        TargetPath = targetPath;
        OutputDirectory = outputDirectory;
        CacheKeyOverride = cacheKeyOverride;
    }
}
