using System.IO;

namespace Dzaba.Build.Lib.Hashing;

public interface IHashCommandHandler
{
    string Execute(
        string[] dirs,
        FileInfo dirsFile,
        string[] excludeDirs,
        string[] excludeFiles);
}
