namespace Dzaba.Build.Hashing;

public sealed class FileHash
{
    public string RelativePath { get; }

    public string Hash { get; }

    public FileHash(string relativePath, string hash)
    {
        RelativePath = relativePath;
        Hash = hash;
    }
}
