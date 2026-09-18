using System.Collections.Generic;
using AutoFixture;
using Dzaba.Build.Lib.Hashing;
using Dzaba.TestUtils;
using FluentAssertions;
using NUnit.Framework;

namespace Dzaba.Build.Lib.Tests.Hashing;

[TestFixture]
public class HashCombinerTests : AutoFixtureTestFixture
{
    private HashCombiner CreateSut()
    {
        return Fixture.Create<HashCombiner>();
    }

    private static string ComputeHash(HashCombiner sut)
    {
        using var combination = sut.CreateCombination();
        combination.AddValue("a");
        combination.AddCount(2);
        combination.AddValue("bc");
        return combination.GetHash();
    }

    [Test]
    public void CreateCombination_ReturnsUsableCombination()
    {
        var sut = CreateSut();

        using var combination = sut.CreateCombination();

        combination.Should().NotBeNull();
    }

    [Test]
    public void GetHash_SameValuesAddedTwice_ReturnsSameHash()
    {
        var sut = CreateSut();

        using var first = sut.CreateCombination();
        first.AddValue("a");
        first.AddValue("b");
        var firstHash = first.GetHash();

        using var second = sut.CreateCombination();
        second.AddValue("a");
        second.AddValue("b");
        var secondHash = second.GetHash();

        secondHash.Should().Be(firstHash);
    }

    [Test]
    public void GetHash_SameSequenceOfOperations_IsDeterministicAcrossRepeatedRuns()
    {
        var sut = CreateSut();

        var hashes = new List<string>();
        for (var i = 0; i < 5; i++)
        {
            hashes.Add(ComputeHash(sut));
        }

        hashes.Should().OnlyContain(h => h == hashes[0]);
    }

    [Test]
    public void GetHash_SameSequenceOfOperations_IsDeterministicAcrossDifferentInstances()
    {
        var firstSut = CreateSut();
        var secondSut = CreateSut();

        var firstHash = ComputeHash(firstSut);
        var secondHash = ComputeHash(secondSut);

        secondHash.Should().Be(firstHash);
    }

    [Test]
    public void GetHash_DifferentValues_ReturnsDifferentHash()
    {
        var sut = CreateSut();

        using var first = sut.CreateCombination();
        first.AddValue("a");
        var firstHash = first.GetHash();

        using var second = sut.CreateCombination();
        second.AddValue("b");
        var secondHash = second.GetHash();

        secondHash.Should().NotBe(firstHash);
    }

    [Test]
    public void GetHash_DifferentValueBoundaries_ReturnsDifferentHash()
    {
        var sut = CreateSut();

        using var first = sut.CreateCombination();
        first.AddValue("ab");
        first.AddValue("c");
        var firstHash = first.GetHash();

        using var second = sut.CreateCombination();
        second.AddValue("a");
        second.AddValue("bc");
        var secondHash = second.GetHash();

        secondHash.Should().NotBe(firstHash);
    }

    [Test]
    public void AddValue_NullValue_TreatedAsEmptyString()
    {
        var sut = CreateSut();

        using var first = sut.CreateCombination();
        first.AddValue(null);
        var firstHash = first.GetHash();

        using var second = sut.CreateCombination();
        second.AddValue(string.Empty);
        var secondHash = second.GetHash();

        secondHash.Should().Be(firstHash);
    }

    [Test]
    public void GetHash_DifferentCounts_ReturnsDifferentHash()
    {
        var sut = CreateSut();

        using var first = sut.CreateCombination();
        first.AddCount(1);
        var firstHash = first.GetHash();

        using var second = sut.CreateCombination();
        second.AddCount(2);
        var secondHash = second.GetHash();

        secondHash.Should().NotBe(firstHash);
    }

    [Test]
    public void CreateCombination_CalledMultipleTimes_ReturnsIndependentCombinations()
    {
        var sut = CreateSut();

        using var first = sut.CreateCombination();
        first.AddValue("only-in-first");

        using var second = sut.CreateCombination();
        var secondHash = second.GetHash();

        first.GetHash().Should().NotBe(secondHash);
    }

    [Test]
    public void GetHash_NoValuesAdded_ReturnsNonEmptyHash()
    {
        var sut = CreateSut();

        using var combination = sut.CreateCombination();

        var hash = combination.GetHash();

        hash.Should().NotBeNullOrEmpty();
    }
}
