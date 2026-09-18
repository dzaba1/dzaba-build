using System;
using System.CommandLine;
using System.IO;
using Dzaba.Build.Lib.Hashing;
using Microsoft.Extensions.DependencyInjection;

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
            var handler = serviceProvider.GetRequiredService<IHashCommandHandler>();

            var dirs = parseResult.GetValue(dirsOption) ?? Array.Empty<string>();
            var dirsFile = parseResult.GetValue(dirsFileOption);
            var excludeDirs = parseResult.GetValue(excludeDirsOption) ?? Array.Empty<string>();
            var excludeFiles = parseResult.GetValue(excludeFilesOption) ?? Array.Empty<string>();

            try
            {
                var hash = handler.Execute(dirs, dirsFile, excludeDirs, excludeFiles);
                Console.WriteLine(hash);
                return 0;
            }
            catch (Exception)
            {
                return 1;
            }
        });

        return command;
    }
}
