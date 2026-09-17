using System;
using System.Security.Cryptography;
using System.Text;
using System.Threading;

namespace Dzaba.Build.Concurrency;

/// <summary>
/// A named, cross-process OS mutex keyed by a cache key - required per ADR-0002, since MSBuild's
/// own parallel build nodes, Dzaba's nested per-miss processes, and separate CI agents sharing a
/// local cache directory can all race to restore or build the same dependency (e.g. a diamond
/// dependency reached via two different parent projects in the same build).
/// </summary>
public sealed class CrossProcessLock : IDisposable
{
    private readonly Mutex mutex;
    private bool acquired;

    public CrossProcessLock(string cacheKey)
    {
        mutex = new Mutex(initiallyOwned: false, name: "Local\\DzabaBuild_" + Sanitize(cacheKey));
    }

    public void Wait()
    {
        try
        {
            acquired = mutex.WaitOne();
        }
        catch (AbandonedMutexException)
        {
            // The process that held the lock died mid-operation. Its cache entry may be partial,
            // but ICacheStorage implementations are expected to publish atomically (see
            // Dzaba.Build.FileCache's temp-then-rename + completion marker), so proceeding is safe.
            acquired = true;
        }
    }

    public void Dispose()
    {
        if (acquired)
        {
            mutex.ReleaseMutex();
        }

        mutex.Dispose();
    }

    private static string Sanitize(string cacheKey)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(cacheKey ?? string.Empty));
        return Convert.ToHexString(bytes).Substring(0, 32);
    }
}
