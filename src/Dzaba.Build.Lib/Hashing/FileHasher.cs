using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Enumeration;
using System.Linq;
using Blake3;
using Microsoft.Extensions.Logging;

namespace Dzaba.Build.Lib.Hashing;

public sealed class FileHasher : IFileHasher
{
    private readonly ILogger<FileHasher> logger;

    public FileHasher(ILogger<FileHasher> logger)
    {
        ArgumentNullException.ThrowIfNull(logger);

        this.logger = logger;
    }

    public string HashFile(string filePath)
    {
        ArgumentException.ThrowIfNullOrEmpty(filePath);

        var stopwatch = Stopwatch.StartNew();

        using var hasher = Hasher.New();
        using var stream = File.OpenRead(filePath);

        var buffer = new byte[81920];
        int read;
        while ((read = stream.Read(buffer, 0, buffer.Length)) > 0)
        {
            hasher.Update(buffer.AsSpan(0, read));
        }

        var hash = hasher.Finalize().ToString();

        stopwatch.Stop();
        logger.LogInformation("Hashed file '{FilePath}' in {Elapsed}.", filePath, stopwatch.Elapsed);

        return hash;
    }

    public IReadOnlyList<FileHash> HashDirectory(
        string rootDirectory,
        IEnumerable<string> excludedDirectoryNames,
        IEnumerable<string> excludedFilePatterns)
    {
        ArgumentException.ThrowIfNullOrEmpty(rootDirectory);

        var stopwatch = Stopwatch.StartNew();

        var directoryNames = ToSet(excludedDirectoryNames);
        var filePatterns = excludedFilePatterns?.Where(p => !string.IsNullOrWhiteSpace(p)).ToArray() ?? Array.Empty<string>();

        var results = EnumerateFiles(rootDirectory, directoryNames, filePatterns)
            .Select(file => new FileHash(Path.GetRelativePath(rootDirectory, file).Replace('\\', '/'), HashFile(file)))
            .ToList();

        results.Sort((a, b) => string.CompareOrdinal(a.RelativePath, b.RelativePath));

        stopwatch.Stop();
        logger.LogInformation("Hashed directory '{RootDirectory}' ({FileCount} files) in {Elapsed}.", rootDirectory, results.Count, stopwatch.Elapsed);

        return results;
    }

    private static HashSet<string> ToSet(IEnumerable<string> values)
    {
        return new HashSet<string>(
            values?.Where(v => !string.IsNullOrWhiteSpace(v)) ?? Array.Empty<string>(),
            StringComparer.OrdinalIgnoreCase);
    }

    private static IEnumerable<string> EnumerateFiles(string directory, HashSet<string> excludedDirectoryNames, string[] excludedFilePatterns)
    {
        foreach (var file in Directory.EnumerateFiles(directory))
        {
            var fileName = Path.GetFileName(file);
            if (excludedFilePatterns.Any(pattern => FileSystemName.MatchesSimpleExpression(pattern, fileName)))
            {
                continue;
            }

            yield return file;
        }

        foreach (var subDirectory in Directory.EnumerateDirectories(directory))
        {
            var name = Path.GetFileName(subDirectory);
            if (excludedDirectoryNames.Contains(name))
            {
                continue;
            }

            foreach (var file in EnumerateFiles(subDirectory, excludedDirectoryNames, excludedFilePatterns))
            {
                yield return file;
            }
        }
    }
}
