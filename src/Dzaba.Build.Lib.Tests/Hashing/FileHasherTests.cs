using System;
using System.IO;
using System.Linq;
using AutoFixture;
using Dzaba.Build.Lib.Hashing;
using Dzaba.TestUtils;
using FluentAssertions;
using NUnit.Framework;

namespace Dzaba.Build.Lib.Tests.Hashing;

[TestFixture]
public class FileHasherTests : AutoFixtureTestFixture
{
    private FileHasher CreateSut()
    {
        return Fixture.Create<FileHasher>();
    }

    private string CreateFile(string relativePath, string content)
    {
        var path = Path.Combine(Temp, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(path));
        File.WriteAllText(path, content);
        return path;
    }

    [Test]
    public void HashFile_SameContentTwice_ReturnsSameHash()
    {
        var filePath = CreateFile("a.txt", "hello world");
        var sut = CreateSut();

        var first = sut.HashFile(filePath);
        var second = sut.HashFile(filePath);

        second.Should().Be(first);
    }

    [Test]
    public void HashFile_DifferentContent_ReturnsDifferentHash()
    {
        var filePathA = CreateFile("a.txt", "hello world");
        var filePathB = CreateFile("b.txt", "goodbye world");
        var sut = CreateSut();

        var hashA = sut.HashFile(filePathA);
        var hashB = sut.HashFile(filePathB);

        hashB.Should().NotBe(hashA);
    }

    [Test]
    public void HashFile_EmptyFile_ReturnsNonEmptyHash()
    {
        var filePath = CreateFile("empty.txt", string.Empty);
        var sut = CreateSut();

        var hash = sut.HashFile(filePath);

        hash.Should().NotBeNullOrEmpty();
    }

    [TestCase(null)]
    [TestCase("")]
    public void HashFile_NullOrEmptyFilePath_Throws(string filePath)
    {
        var sut = CreateSut();

        sut.Invoking(s => s.HashFile(filePath)).Should().Throw<ArgumentException>();
    }

    [Test]
    public void HashDirectory_ReturnsAllFilesSortedByRelativePath()
    {
        CreateFile("b.txt", "b");
        CreateFile("a.txt", "a");
        CreateFile(Path.Combine("sub", "c.txt"), "c");
        var sut = CreateSut();

        var results = sut.HashDirectory(Temp, null, null);

        results.Should().HaveCount(3);
        results.Select(r => r.RelativePath).Should().Equal("a.txt", "b.txt", "sub/c.txt");
    }

    [Test]
    public void HashDirectory_ExcludesSpecifiedDirectoryNames()
    {
        CreateFile("a.txt", "a");
        CreateFile(Path.Combine("obj", "generated.txt"), "generated");
        var sut = CreateSut();

        var results = sut.HashDirectory(Temp, new[] { "obj" }, null);

        results.Select(r => r.RelativePath).Should().Equal("a.txt");
    }

    [Test]
    public void HashDirectory_ExcludesFilesMatchingPattern()
    {
        CreateFile("a.txt", "a");
        CreateFile("a.tmp", "temp");
        var sut = CreateSut();

        var results = sut.HashDirectory(Temp, null, new[] { "*.tmp" });

        results.Select(r => r.RelativePath).Should().Equal("a.txt");
    }

    [Test]
    public void HashDirectory_HashMatchesHashFile()
    {
        var filePath = CreateFile("a.txt", "hello world");
        var sut = CreateSut();

        var expectedHash = sut.HashFile(filePath);
        var results = sut.HashDirectory(Temp, null, null);

        results.Should().ContainSingle(r => r.RelativePath == "a.txt" && r.Hash == expectedHash);
    }

    [TestCase(null)]
    [TestCase("")]
    public void HashDirectory_NullOrEmptyRootDirectory_Throws(string rootDirectory)
    {
        var sut = CreateSut();

        sut.Invoking(s => s.HashDirectory(rootDirectory, null, null)).Should().Throw<ArgumentException>();
    }
}
