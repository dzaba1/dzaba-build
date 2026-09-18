using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Microsoft.Extensions.Logging;

namespace Dzaba.Build.Lib.Hashing;

internal sealed class HashCommandHandler : IHashCommandHandler
{
    private readonly IFileHasher fileHasher;
    private readonly IHashCombiner hashCombiner;
    private readonly ILogger<HashCommandHandler> logger;

    public HashCommandHandler(
        IFileHasher fileHasher,
        IHashCombiner hashCombiner,
        ILogger<HashCommandHandler> logger)
    {
        ArgumentNullException.ThrowIfNull(fileHasher);
        ArgumentNullException.ThrowIfNull(hashCombiner);
        ArgumentNullException.ThrowIfNull(logger);

        this.fileHasher = fileHasher;
        this.hashCombiner = hashCombiner;
        this.logger = logger;
    }

    public string Execute(
        string[] dirs,
        FileInfo dirsFile,
        string[] excludeDirs,
        string[] excludeFiles)
    {
        var directories = new List<string>(dirs);

        if (dirsFile != null)
        {
            if (!dirsFile.Exists)
            {
                logger.LogError("Directories file '{DirsFile}' does not exist.", dirsFile.FullName);
                throw new FileNotFoundException($"Directories file '{dirsFile.FullName}' does not exist.", dirsFile.FullName);
            }

            directories.AddRange(File.ReadLines(dirsFile.FullName).Where(line => !string.IsNullOrWhiteSpace(line)));
        }

        var normalizedDirs = directories
            .Select(Path.GetFullPath)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(d => d, StringComparer.Ordinal)
            .ToList();

        if (normalizedDirs.Count == 0)
        {
            logger.LogError("No directories specified. Use --dirs and/or --dirsFile.");
            throw new ArgumentException("No directories specified. Use --dirs and/or --dirsFile.");
        }

        var missingDirs = normalizedDirs.Where(d => !Directory.Exists(d)).ToList();
        if (missingDirs.Count > 0)
        {
            foreach (var missingDir in missingDirs)
            {
                logger.LogError("Directory '{Directory}' does not exist.", missingDir);
            }

            throw new DirectoryNotFoundException($"Directories do not exist: {string.Join(", ", missingDirs)}");
        }

        using var combination = hashCombiner.CreateCombination();
        foreach (var dir in normalizedDirs)
        {
            var fileHashes = fileHasher.HashDirectory(dir, excludeDirs, excludeFiles);

            combination.AddValue(dir);
            combination.AddCount(fileHashes.Count);
            foreach (var fileHash in fileHashes)
            {
                combination.AddValue(fileHash.RelativePath);
                combination.AddValue(fileHash.Hash);
            }
        }

        return combination.GetHash();
    }
}
