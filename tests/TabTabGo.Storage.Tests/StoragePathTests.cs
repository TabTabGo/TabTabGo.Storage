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

    // Unicode format characters (category Cf) are rejected.
    [Theory]
    [InlineData("a\u202eb.txt")]      // right-to-left override
    [InlineData("a/\u200bb")]          // zero-width space
    [InlineData("\u200fa")]            // right-to-left mark
    [InlineData("a\u00adb")]           // soft hyphen
    [InlineData("a\ufeffb")]           // BOM / zero-width no-break space
    [InlineData("a\U000e0001b")]       // language tag (supplementary plane, surrogate pair)
    public void Normalize_UnicodeFormatCharacter_Throws(string input)
    {
        Assert.Throws<ArgumentException>(() => StoragePath.Normalize(input));
        Assert.False(StoragePath.IsSafe(input));
        Assert.False(StoragePath.IsSafe(input, StoragePathLimits.FileSystem));
    }

    [Fact]
    public void Normalize_NonFormatUnicode_Accepted() =>
        Assert.Equal("caf\u00e9/\u65e5\u672c\u8a9e.txt", StoragePath.Normalize("caf\u00e9/\u65e5\u672c\u8a9e.txt"));

    [Theory]
    [InlineData(255, true)]
    [InlineData(256, false)]
    public void Normalize_FileSystemLimits_SegmentBoundary(int length, bool accepted)
    {
        var path = "dir/" + new string('a', length);

        if (accepted)
        {
            Assert.Equal(path, StoragePath.Normalize(path, StoragePathLimits.FileSystem));
        }
        else
        {
            Assert.Throws<ArgumentException>(() => StoragePath.Normalize(path, StoragePathLimits.FileSystem));
            Assert.False(StoragePath.IsSafe(path, StoragePathLimits.FileSystem));
        }
    }

    [Fact]
    public void Normalize_FileSystemLimits_CountsUtf8Bytes()
    {
        Assert.Equal(new string('\u00e9', 127), StoragePath.Normalize(new string('\u00e9', 127), StoragePathLimits.FileSystem));
        Assert.Throws<ArgumentException>(() => StoragePath.Normalize(new string('\u00e9', 128), StoragePathLimits.FileSystem));
    }

    [Fact]
    public void Normalize_FileSystemLimits_DoNotLimitTotalLength() =>
        Assert.True(StoragePath.IsSafe(string.Join('/', Enumerable.Repeat(new string('a', 200), 10)), StoragePathLimits.FileSystem));

    [Theory]
    [InlineData(1024, true)]
    [InlineData(1025, false)]
    public void Normalize_AzureLimits_CountsCharacters(int length, bool accepted)
    {
        Assert.Equal(accepted, StoragePath.IsSafe(new string('a', length), StoragePathLimits.AzureBlobName));
        Assert.Equal(accepted, StoragePath.IsSafe(new string('\u00e9', length), StoragePathLimits.AzureBlobName));
    }

    [Fact]
    public void Normalize_AzureLimits_CountSeparators() =>
        Assert.False(StoragePath.IsSafe(new string('a', 1021) + "/bcd", StoragePathLimits.AzureBlobName));

    [Theory]
    [InlineData("Text", 1019, true)]    // "Text/" + 1019 = 1024 bytes
    [InlineData("Text", 1020, false)]
    [InlineData("", 1023, true)]
    [InlineData("", 1024, false)]
    public void Normalize_S3Limits_ContainerPrefixCounts(string container, int length, bool accepted) =>
        Assert.Equal(accepted, StoragePath.IsSafe(new string('a', length), StoragePathLimits.S3ObjectKey(container)));

    [Fact]
    public void S3ObjectKey_CountsUtf8BytesOfTheContainer() =>
        Assert.False(StoragePath.IsSafe(new string('a', 1022), StoragePathLimits.S3ObjectKey("\u00e9")));

    [Fact]
    public void Normalize_WithLimits_StillAppliesSafetyRules()
    {
        Assert.Throws<ArgumentException>(() => StoragePath.Normalize("../x", StoragePathLimits.FileSystem));
        Assert.Throws<ArgumentNullException>(() => StoragePath.Normalize("a", null!));
        Assert.Throws<ArgumentNullException>(() => StoragePath.IsSafe("a", null!));
        Assert.False(StoragePath.IsSafe(null, StoragePathLimits.FileSystem));
    }
}
