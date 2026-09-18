using System;
using System.Collections.Generic;
using System.CommandLine;
using System.IO;
using System.Linq;
using Dzaba.Build.Lib.Hashing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Dzaba.Build.Cmd.Commands;

public static class HashCommand
{
    public static Command Build(IServiceProvider serviceProvider)
    {
        ArgumentNullException.ThrowIfNull(serviceProvider);

        var dirsOption = new Option<string[]>("--dirs")
        {
            Description = "Directories to hash.",
            AllowMultipleArgumentsPerToken = true
        };

        var dirsFileOption = new Option<FileInfo>("--dirsFile")
        {
            Description = "Path to a .txt file listing one directory per line. Combined with --dirs."
        };

        var excludeDirsOption = new Option<string[]>("--excludeDirs")
        {
            Description = "Directory names to exclude from hashing.",
            AllowMultipleArgumentsPerToken = true
        };

        var excludeFilesOption = new Option<string[]>("--excludeFiles")
        {
            Description = "File name patterns to exclude from hashing.",
            AllowMultipleArgumentsPerToken = true
        };

        var command = new Command("hash", "Computes a single combined content hash for a set of directories.")
        {
            dirsOption,
            dirsFileOption,
            excludeDirsOption,
            excludeFilesOption
        };

        command.SetAction(parseResult =>
        {
            var fileHasher = serviceProvider.GetRequiredService<IFileHasher>();
            var hashCombiner = serviceProvider.GetRequiredService<IHashCombiner>();
            var logger = serviceProvider.GetRequiredService<ILoggerFactory>().CreateLogger("hash");

            var dirs = parseResult.GetValue(dirsOption) ?? Array.Empty<string>();
            var dirsFile = parseResult.GetValue(dirsFileOption);
            var excludeDirs = parseResult.GetValue(excludeDirsOption) ?? Array.Empty<string>();
            var excludeFiles = parseResult.GetValue(excludeFilesOption) ?? Array.Empty<string>();

            return Execute(dirs, dirsFile, excludeDirs, excludeFiles, fileHasher, hashCombiner, logger);
        });

        return command;
    }

    private static int Execute(
        string[] dirs,
        FileInfo dirsFile,
        string[] excludeDirs,
        string[] excludeFiles,
        IFileHasher fileHasher,
        IHashCombiner hashCombiner,
        ILogger logger)
    {
        var directories = new List<string>(dirs);

        if (dirsFile != null)
        {
            if (!dirsFile.Exists)
            {
                logger.LogError("Directories file '{DirsFile}' does not exist.", dirsFile.FullName);
                return 1;
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
            return 1;
        }

        var missingDirs = normalizedDirs.Where(d => !Directory.Exists(d)).ToList();
        if (missingDirs.Count > 0)
        {
            foreach (var missingDir in missingDirs)
            {
                logger.LogError("Directory '{Directory}' does not exist.", missingDir);
            }

            return 1;
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

        Console.WriteLine(combination.GetHash());
        return 0;
    }
}
