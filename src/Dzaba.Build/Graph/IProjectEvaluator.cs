using System.Collections.Generic;

namespace Dzaba.Build.Graph;

public interface IProjectEvaluator
{
    ProjectInfo Evaluate(string projectFile, string configuration, string platform, string callerTargetFramework);

    IReadOnlyList<string> GetAncestorFiles(string startDirectory, IEnumerable<string> fileNames);

    string GetNearestAncestorFile(string startDirectory, string fileName);

    string GetSourceTreeRoot(string startDirectory);
}
