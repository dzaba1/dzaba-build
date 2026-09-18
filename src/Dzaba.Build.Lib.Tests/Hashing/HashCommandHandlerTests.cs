using System;
using System.Collections.Generic;
using System.IO;
using AutoFixture;
using Dzaba.Build.Lib.Hashing;
using Dzaba.TestUtils;
using FluentAssertions;
using Moq;
using NUnit.Framework;

namespace Dzaba.Build.Lib.Tests.Hashing;

[TestFixture]
public class HashCommandHandlerTests : AutoFixtureTestFixture
{
    private Mock<IFileHasher> fileHasherMock;
    private Mock<IHashCombiner> hashCombinerMock;
    private Mock<IHashCombination> combinationMock;

    [SetUp]
    public void SetUp()
    {
        fileHasherMock = Fixture.Freeze<Mock<IFileHasher>>();
        hashCombinerMock = Fixture.Freeze<Mock<IHashCombiner>>();
        combinationMock = new Mock<IHashCombination>();

        hashCombinerMock.Setup(h => h.CreateCombination()).Returns(combinationMock.Object);
    }

    private HashCommandHandler CreateSut()
    {
        return Fixture.Create<HashCommandHandler>();
    }

    private string CreateDirectory(string relativePath)
    {
        var path = Path.Combine(Temp, relativePath);
        Directory.CreateDirectory(path);
        return path;
    }

    [Test]
    public void Execute_NoDirectoriesSpecified_ThrowsArgumentException()
    {
        var sut = CreateSut();

        sut.Invoking(s => s.Execute(Array.Empty<string>(), null, null, null))
            .Should().Throw<ArgumentException>();
    }

    [Test]
    public void Execute_DirsFileDoesNotExist_ThrowsFileNotFoundException()
    {
        var sut = CreateSut();
        var dirsFile = new FileInfo(Path.Combine(Temp, "missing.txt"));

        sut.Invoking(s => s.Execute(Array.Empty<string>(), dirsFile, null, null))
            .Should().Throw<FileNotFoundException>();
    }

    [Test]
    public void Execute_DirectoryDoesNotExist_ThrowsDirectoryNotFoundException()
    {
        var sut = CreateSut();
        var missingDir = Path.Combine(Temp, "does-not-exist");

        sut.Invoking(s => s.Execute(new[] { missingDir }, null, null, null))
            .Should().Throw<DirectoryNotFoundException>();
    }

    [Test]
    public void Execute_ValidDirectory_ReturnsHashFromCombination()
    {
        var dir = CreateDirectory("dir1");
        var fileHashes = new List<FileHash> { new FileHash("a.txt", "hash-a") };
        fileHasherMock.Setup(f => f.HashDirectory(dir, null, null)).Returns(fileHashes);
        combinationMock.Setup(c => c.GetHash()).Returns("combined-hash");
        var sut = CreateSut();

        var result = sut.Execute(new[] { dir }, null, null, null);

        result.Should().Be("combined-hash");
        combinationMock.Verify(c => c.AddValue(dir), Times.Once);
        combinationMock.Verify(c => c.AddCount(1), Times.Once);
        combinationMock.Verify(c => c.AddValue("a.txt"), Times.Once);
        combinationMock.Verify(c => c.AddValue("hash-a"), Times.Once);
        combinationMock.Verify(c => c.Dispose(), Times.Once);
    }

    [Test]
    public void Execute_DuplicateDirectories_HashesOnce()
    {
        var dir = CreateDirectory("dir1");
        fileHasherMock.Setup(f => f.HashDirectory(dir, null, null)).Returns(new List<FileHash>());
        var sut = CreateSut();

        sut.Execute(new[] { dir, dir }, null, null, null);

        fileHasherMock.Verify(f => f.HashDirectory(dir, null, null), Times.Once);
    }

    [Test]
    public void Execute_DirsFileProvided_MergesDirectoriesFromFile()
    {
        var dirFromArg = CreateDirectory("dir1");
        var dirFromFile = CreateDirectory("dir2");
        var dirsFilePath = Path.Combine(Temp, "dirs.txt");
        File.WriteAllLines(dirsFilePath, new[] { dirFromFile, string.Empty, "   " });
        fileHasherMock.Setup(f => f.HashDirectory(It.IsAny<string>(), null, null)).Returns(new List<FileHash>());
        var sut = CreateSut();

        sut.Execute(new[] { dirFromArg }, new FileInfo(dirsFilePath), null, null);

        fileHasherMock.Verify(f => f.HashDirectory(dirFromArg, null, null), Times.Once);
        fileHasherMock.Verify(f => f.HashDirectory(dirFromFile, null, null), Times.Once);
    }
}
