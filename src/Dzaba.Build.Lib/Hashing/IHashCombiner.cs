using System;

namespace Dzaba.Build.Lib.Hashing;

public interface IHashCombiner
{
    IHashCombination CreateCombination();
}

/// <summary>
/// Feeds an ordered sequence of values into a single BLAKE3 digest. Every value is written
/// length-prefixed - per ADR-0003 - so no two different input sequences can concatenate to the
/// same bytes (e.g. ["ab", "c"] vs ["a", "bc"]).
/// </summary>
public interface IHashCombination : IDisposable
{
    void AddValue(string value);

    void AddCount(int count);

    string GetHash();
}
