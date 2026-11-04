using System.Net;
using TabTabGo.Core.Exceptions;
using TabTabGo.Storage.Enums;
using TabTabGo.Storage.Exceptions;
using TabTabGo.Storage.Models;
using Xunit;

namespace TabTabGo.Storage.Tests;

public class FileUploadTests
{
    [Fact]
    public void FromBytes_RoundTrips_AndCanBeReadRepeatedly()
    {
        var upload = FileUpload.FromBytes([1, 2, 3], "a.bin", "application/x-test");

        using var first = upload.OpenReadStream();
        using var second = upload.OpenReadStream();

        Assert.Equal("a.bin", upload.FileName);
        Assert.Equal("application/x-test", upload.ContentType);
        Assert.Equal(3, upload.Length);
        Assert.Equal(3, first.Length);
        Assert.Equal(3, second.Length);
    }

    [Fact]
    public void FromStream_Seekable_CanBeReadAgain_AndReportsRemainingLength()
    {
        var source = new MemoryStream([9, 9, 1, 2, 3]);
        source.Position = 2;
        var upload = FileUpload.FromStream(source, "s.bin");

        using (var first = upload.OpenReadStream())
        {
            Assert.Equal(1, first.ReadByte());
        }

        using var second = upload.OpenReadStream();
        Assert.Equal(1, second.ReadByte());
        Assert.Equal(3, upload.Length);
    }

    [Fact]
    public void FromStream_DisposingTheOpenedStream_LeavesSourceOpen()
    {
        var source = new MemoryStream([1]);
        var upload = FileUpload.FromStream(source, "s.bin");

        upload.OpenReadStream().Dispose();

        Assert.True(source.CanRead);
    }

    [Fact]
    public void FromStream_NonSeekable_ReadsOnce()
    {
        var upload = FileUpload.FromStream(new NonSeekable([1, 2]), "n.bin", length: 2);

        using var first = upload.OpenReadStream();

        Assert.Equal(2, upload.Length);
        Assert.Throws<InvalidOperationException>(() => upload.OpenReadStream());
    }

    [Fact]
    public void FromFactory_OpensNewStreamEachTime()
    {
        var opened = 0;
        var upload = FileUpload.FromFactory(() =>
        {
            opened++;
            return new MemoryStream([1]);
        }, "f.bin", "x/y", 1);

        upload.OpenReadStream().Dispose();
        upload.OpenReadStream().Dispose();

        Assert.Equal(2, opened);
        Assert.Equal(1, upload.Length);
    }

    [Fact]
    public void Factories_NullArguments_Throw()
    {
        Assert.Throws<ArgumentNullException>(() => FileUpload.FromBytes(null!, "a"));
        Assert.Throws<ArgumentNullException>(() => FileUpload.FromBytes([], null!));
        Assert.Throws<ArgumentNullException>(() => FileUpload.FromStream(null!, "a"));
        Assert.Throws<ArgumentNullException>(() => FileUpload.FromStream(new MemoryStream(), null!));
        Assert.Throws<ArgumentNullException>(() => FileUpload.FromFactory(null!, "a"));
        Assert.Throws<ArgumentNullException>(() => FileUpload.FromFactory(() => new MemoryStream(), null!));
    }

    private sealed class NonSeekable(byte[] data) : MemoryStream(data)
    {
        public override bool CanSeek => false;
    }
}

public class OptionsTests
{
    [Fact]
    public void StorageWriteOptions_Defaults_OverwriteTrue()
    {
        var options = new StorageWriteOptions();

        Assert.True(options.Overwrite);
        Assert.Null(options.ContentType);
    }

    [Theory]
    [InlineData(null, 3600)]
    [InlineData(1, 60)]
    [InlineData(10, 60)]
    [InlineData(300, 300)]
    [InlineData(99999999, 604800)]
    public void PublicUrlOptions_GetEffectiveExpiry_ClampsToOneMinuteAndSevenDays(int? seconds, int expectedSeconds)
    {
        var options = new PublicUrlOptions { ExpiresIn = seconds is null ? null : TimeSpan.FromSeconds(seconds.Value) };

        var effective = options.GetEffectiveExpiry(TimeSpan.FromHours(1));

        Assert.Equal(TimeSpan.FromSeconds(expectedSeconds), effective);
    }

    [Fact]
    public void PublicUrlOptions_DefaultsAndLimits()
    {
        var options = new PublicUrlOptions();

        Assert.Null(options.ContentType);
        Assert.Null(options.DownloadFileName);
        Assert.False(options.Inline);
        Assert.Equal(TimeSpan.FromMinutes(1), PublicUrlOptions.MinExpiry);
        Assert.Equal(TimeSpan.FromDays(7), PublicUrlOptions.MaxExpiry);
    }

    [Fact]
    public void FileListQuery_Defaults()
    {
        var query = new FileListQuery();

        Assert.Equal(1, query.Page);
        Assert.Equal(20, query.PageSize);
        Assert.Equal(100, FileListQuery.MaxPageSize);
        Assert.Null(query.FileType);
    }
}

public class StorageExceptionTests
{
    [Fact]
    public void Ctor_CarriesContext_AndDefaultsToBadGateway()
    {
        var inner = new IOException("boom");

        var ex = new StorageException("failed", "S3", "Store", "Image", "a/b.png", inner: inner);

        Assert.IsAssignableFrom<ApiException>(ex);
        Assert.Equal("S3", ex.Provider);
        Assert.Equal("Store", ex.Operation);
        Assert.Equal("Image", ex.Container);
        Assert.Equal("a/b.png", ex.FilePath);
        Assert.Equal(HttpStatusCode.BadGateway, ex.HttpStatusCode);
        Assert.Equal("STORAGE_ERROR", ex.ErrorCode);
        Assert.Same(inner, ex.InnerException);
    }

    [Fact]
    public void Ctor_StatusAndCodeOverride()
    {
        var ex = new StorageException("bad", "FileStorage", "Store", statusCode: HttpStatusCode.BadRequest,
            code: "STORAGE_INVALID_PATH");

        Assert.Equal(HttpStatusCode.BadRequest, ex.HttpStatusCode);
        Assert.Equal("STORAGE_INVALID_PATH", ex.ErrorCode);
        Assert.Null(ex.Container);
        Assert.Null(ex.FilePath);
    }
}

public class EnumValueTests
{
    [Fact]
    public void FileType_NumericValues_Unchanged()
    {
        Assert.Equal(0, (int)FileType.Unknown);
        Assert.Equal(10, (int)FileType.Image);
        Assert.Equal(20, (int)FileType.Audio);
        Assert.Equal(30, (int)FileType.Video);
        Assert.Equal(40, (int)FileType.Text);
        Assert.Equal(41, (int)FileType.PDF);
        Assert.Equal(42, (int)FileType.WordDocument);
    }

    [Fact]
    public void ImageSize_Order_Unchanged()
    {
        Assert.Equal(0, (int)ImageSize.Default);
        Assert.Equal(1, (int)ImageSize.ThumbLarge);
        Assert.Equal(2, (int)ImageSize.ThumbMedium);
        Assert.Equal(3, (int)ImageSize.ThumbSmall);
    }

    [Fact]
    public void StorageProvider_NumericValues_Unchanged()
    {
        Assert.Equal(0, (int)Enums.StorageProvider.Local);
        Assert.Equal(10, (int)Enums.StorageProvider.AzureBlob);
        Assert.Equal(12, (int)Enums.StorageProvider.AzureFile);
        Assert.Equal(20, (int)Enums.StorageProvider.AwsS3);
    }
}
