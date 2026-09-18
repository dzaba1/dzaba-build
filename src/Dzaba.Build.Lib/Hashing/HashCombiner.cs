using System;
using System.Diagnostics;
using System.Text;
using Blake3;
using Microsoft.Extensions.Logging;

namespace Dzaba.Build.Lib.Hashing;

public sealed class HashCombiner : IHashCombiner
{
    private readonly ILogger<HashCombiner> logger;

    public HashCombiner(ILogger<HashCombiner> logger)
    {
        ArgumentNullException.ThrowIfNull(logger);

        this.logger = logger;
    }

    public IHashCombination CreateCombination()
    {
        return new HashCombination(logger);
    }

    private sealed class HashCombination : IHashCombination
    {
        private readonly ILogger logger;
        private readonly Hasher hasher;
        private readonly Stopwatch stopwatch;
        private int valueCount;

        public HashCombination(ILogger logger)
        {
            this.logger = logger;
            hasher = Hasher.New();
            stopwatch = Stopwatch.StartNew();
        }

        public void AddValue(string value)
        {
            var bytes = Encoding.UTF8.GetBytes(value ?? string.Empty);
            AddCount(bytes.Length);
            hasher.Update(bytes);
            valueCount++;
        }

        public void AddCount(int count)
        {
            hasher.Update(BitConverter.GetBytes(count));
        }

        public string GetHash()
        {
            var hash = hasher.Finalize().ToString();

            stopwatch.Stop();
            logger.LogInformation("Combined {ValueCount} values into hash in {Elapsed}.", valueCount, stopwatch.Elapsed);

            return hash;
        }

        public void Dispose()
        {
            hasher.Dispose();
        }
    }
}
