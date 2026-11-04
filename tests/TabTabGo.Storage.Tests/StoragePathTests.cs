using TabTabGo.Storage.Paths;
using Xunit;

namespace TabTabGo.Storage.Tests;

public class StoragePathTests
{
    [Theory]
    [InlineData("a/b.txt", "a/b.txt")]
    [InlineData("a\\b\\c.txt", "a/b/c.txt")]
    [InlineData("a//b///c.txt", "a/b/c.txt")]
    [InlineData("a/b/", "a/b")]
    [InlineData("%2e%2e/x.txt", "%2e%2e/x.txt")]
    [InlineData("name with spaces.txt", "name with spaces.txt")]
    [InlineData("..data/x", "..data/x")]
    [InlineData("a/.hidden", "a/.hidden")]
    public void Normalize_SafePath_ReturnsForwardSlashRelativePath(string input, string expected) =>
        Assert.Equal(expected, StoragePath.Normalize(input));

    [Theory]
    [InlineData("..")]
    [InlineData(".")]
    [InlineData("../x")]
    [InlineData("a/../b")]
    [InlineData("a/..")]
    [InlineData("a/./b")]
    [InlineData("a\\..\\b")]
    [InlineData("...")]
    [InlineData(".. ")]
    [InlineData("a/ . /b")]
    [InlineData("/rooted")]
    [InlineData("\\rooted")]
    [InlineData("\\\\server\\share\\x")]
    [InlineData("//server/share")]
    [InlineData("C:\\x")]
    [InlineData("C:/x")]
    [InlineData("a:b")]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("/")]
    [InlineData("a\0b")]
    [InlineData("a\nb")]
    [InlineData("a\tb")]
    [InlineData("a\u007fb")]
    public void Normalize_UnsafePath_Throws(string input) =>
        Assert.Throws<ArgumentException>(() => StoragePath.Normalize(input));

    [Fact]
    public void Normalize_Null_Throws() => Assert.Throws<ArgumentNullException>(() => StoragePath.Normalize(null!));

    [Fact]
    public void Combine_JoinsAndNormalises() =>
        Assert.Equal("ThumbLarge/20260101/x.png", StoragePath.Combine("ThumbLarge", "20260101\\x.png"));

    [Fact]
    public void Combine_SkipsEmptySegments() => Assert.Equal("a/b", StoragePath.Combine("a", "", "b"));

    [Fact]
    public void Combine_RootedFirstSegment_Throws() =>
        Assert.Throws<ArgumentException>(() => StoragePath.Combine("/etc", "passwd"));

    [Fact]
    public void Combine_TraversalInLaterSegment_Throws() =>
        Assert.Throws<ArgumentException>(() => StoragePath.Combine("a", "../b"));

    [Fact]
    public void Combine_Null_Throws() => Assert.Throws<ArgumentNullException>(() => StoragePath.Combine(null!));

    [Fact]
    public void Combine_NothingLeft_Throws() => Assert.Throws<ArgumentException>(() => StoragePath.Combine("", ""));

    [Theory]
    [InlineData("a/b.txt", true)]
    [InlineData("../b", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void IsSafe_ReportsNormalizeOutcome(string? input, bool expected) =>
        Assert.Equal(expected, StoragePath.IsSafe(input));
}
