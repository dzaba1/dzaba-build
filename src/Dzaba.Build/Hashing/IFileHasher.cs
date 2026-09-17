using System.Collections.Generic;

namespace Dzaba.Build.Hashing;

public interface IFileHasher
{
    string HashFile(string filePath);

    /// <summary>
    /// Sorted, by relative path, for every file under <paramref name="rootDirectory"/> - per
    /// ADR-0003. Which directories and file name patterns to exclude is caller-supplied.
    /// </summary>
    IReadOnlyList<FileHash> HashDirectory(
        string rootDirectory,
        IEnumerable<string> excludedDirectoryNames,
        IEnumerable<string> excludedFilePatterns);
}
