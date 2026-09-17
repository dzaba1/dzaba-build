using System;
using System.IO;
using Dzaba.Build;

namespace Dzaba.Build.FileCache;

/// <summary>
/// Local disk / network-share <see cref="ICacheStorage"/> implementation - the natural default
/// for local dev, shared drives, and air-gapped setups (ADR-0003).
///
/// Publishes are written to a temp subfolder under <see cref="root"/> first, with a completion
/// marker file written last, then atomically renamed into place - so a reader can never observe
/// a partially-written cache entry, and a process that dies mid-publish just leaves an orphaned
/// temp folder rather than a corrupt one under the real key.
/// </summary>
public sealed class FileCacheStorage : ICacheStorage
{
    private const string CompleteMarkerFileName = ".complete";

    private readonly string root;

    public FileCacheStorage(string rootDirectory)
    {
        root = Path.GetFullPath(rootDirectory);
        Directory.CreateDirectory(root);
    }

    public bool Exists(string cacheKey)
    {
        return File.Exists(Path.Combine(EntryDirectory(cacheKey), CompleteMarkerFileName));
    }

    public void Fetch(string cacheKey, string destinationDirectory)
    {
        var entryDirectory = EntryDirectory(cacheKey);
        Directory.CreateDirectory(destinationDirectory);

        foreach (var file in Directory.EnumerateFiles(entryDirectory))
        {
            var fileName = Path.GetFileName(file);
            if (string.Equals(fileName, CompleteMarkerFileName, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            File.Copy(file, Path.Combine(destinationDirectory, fileName), overwrite: true);
        }
    }

    public void Publish(string cacheKey, string sourceDirectory)
    {
        var finalDirectory = EntryDirectory(cacheKey);
        if (Directory.Exists(finalDirectory) && File.Exists(Path.Combine(finalDirectory, CompleteMarkerFileName)))
        {
            // Someone else already published this key; content-addressed by definition, so first
            // writer wins and this is a no-op rather than a conflict.
            return;
        }

        var tempDirectory = Path.Combine(root, ".tmp-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDirectory);

        foreach (var file in Directory.EnumerateFiles(sourceDirectory))
        {
            File.Copy(file, Path.Combine(tempDirectory, Path.GetFileName(file)), overwrite: true);
        }

        File.WriteAllText(Path.Combine(tempDirectory, CompleteMarkerFileName), DateTime.UtcNow.ToString("O"));

        try
        {
            Directory.Move(tempDirectory, finalDirectory);
        }
        catch (IOException) when (Directory.Exists(finalDirectory))
        {
            // Lost the race to another writer; discard our copy.
            Directory.Delete(tempDirectory, recursive: true);
        }
    }

    private string EntryDirectory(string cacheKey)
    {
        return Path.Combine(root, cacheKey);
    }
}
