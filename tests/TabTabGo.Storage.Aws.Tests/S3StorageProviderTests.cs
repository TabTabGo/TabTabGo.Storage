using System.Net;
using System.Text;
using Amazon;
using Amazon.Runtime;
using Amazon.S3;
using Amazon.S3.Model;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Testing;
using Microsoft.Extensions.Options;
using Moq;
using TabTabGo.Storage.Exceptions;
using TabTabGo.Storage.Models;
using TabTabGo.Storage.TestKit;
using Xunit;

namespace TabTabGo.Storage.Aws.Tests;

public class S3FakeContractTests : StorageProviderContractTests<S3StorageProvider>
{
    protected override Task<S3StorageProvider> CreateProviderAsync() =>
        Task.FromResult(new S3StorageProvider(new FakeS3().Client.Object,
            Options.Create(new S3Configuration { BucketName = "bucket", Region = "eu-west-1" }),
            new FakeLogger<S3StorageProvider>()));
}

public class S3StorageProviderTests
{
    private const string Bucket = "unit-bucket";

    private readonly FakeS3 _s3 = new();
    private readonly FakeLogger<S3StorageProvider> _logger = new();

    private S3StorageProvider Create(Action<S3Configuration>? configure = null)
    {
        var configuration = new S3Configuration { BucketName = Bucket, Region = "eu-west-1" };
        configure?.Invoke(configuration);
        return new S3StorageProvider(_s3.Client.Object, Options.Create(configuration), _logger);
    }

    private static AmazonS3Exception S3Error(HttpStatusCode status, string code) =>
        new("failure") { StatusCode = status, ErrorCode = code };

    // A-1
    [Fact]
    public async Task Retrieve_ReturnsStreamAtPositionZero()
    {
        var provider = Create();
        await provider.StoreAsync("a.txt", Encoding.UTF8.GetBytes("12345"), "Text");

        await using var stream = await provider.RetrieveAsync("a.txt", "Text");

        Assert.Equal(0, stream!.Position);
        Assert.Equal(5, stream.Length);
    }

    // A-2
    [Fact]
    public async Task Store_S3Error_ThrowsStorageException()
    {
        _s3.Client.Setup(c => c.PutObjectAsync(It.IsAny<PutObjectRequest>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(S3Error(HttpStatusCode.InternalServerError, "InternalError"));
        var provider = Create();

        var ex = await Assert.ThrowsAsync<StorageException>(() => provider.StoreAsync("a.txt", [1], "Text"));

        Assert.Equal("S3", ex.Provider);
        Assert.Equal("Store", ex.Operation);
        Assert.Equal("Text", ex.Container);
        Assert.Equal(HttpStatusCode.BadGateway, ex.HttpStatusCode);
        Assert.IsType<AmazonS3Exception>(ex.InnerException);
    }

    [Fact]
    public async Task Store_HttpRequestException_ThrowsStorageException()
    {
        _s3.Client.Setup(c => c.PutObjectAsync(It.IsAny<PutObjectRequest>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new HttpRequestException("network"));

        await Assert.ThrowsAsync<StorageException>(() => Create().StoreAsync("a.txt", [1], "Text"));
    }

    // A-3
    [Fact]
    public async Task Retrieve_NoSuchKey_Null() => Assert.Null(await Create().RetrieveAsync("none.txt", "Text"));

    [Fact]
    public async Task Retrieve_NotFoundStatusWithoutCode_Null()
    {
        _s3.Client.Setup(c => c.GetObjectAsync(It.IsAny<GetObjectRequest>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(S3Error(HttpStatusCode.NotFound, "SomethingElse"));

        Assert.Null(await Create().RetrieveAsync("none.txt", "Text"));
    }

    [Fact]
    public async Task Retrieve_AccessDenied_Throws()
    {
        _s3.Client.Setup(c => c.GetObjectAsync(It.IsAny<GetObjectRequest>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(S3Error(HttpStatusCode.Forbidden, "AccessDenied"));

        var ex = await Assert.ThrowsAsync<StorageException>(() => Create().RetrieveAsync("a.txt", "Text"));

        Assert.Equal("Retrieve", ex.Operation);
    }

    [Fact]
    public async Task OpenRead_AccessDenied_Throws_AndMissingIsNull()
    {
        var provider = Create();
        Assert.Null(await provider.OpenReadAsync("none.txt", "Text"));
        _s3.Client.Setup(c => c.GetObjectAsync(It.IsAny<GetObjectRequest>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(S3Error(HttpStatusCode.Forbidden, "AccessDenied"));

        await Assert.ThrowsAsync<StorageException>(() => provider.OpenReadAsync("a.txt", "Text"));
    }

    [Fact]
    public async Task OpenRead_ReturnsForwardOnlyStream_ThatDisposesTheResponse()
    {
        var provider = Create();
        await provider.StoreAsync("a.txt", [1, 2, 3], "Text");

        var stream = await provider.OpenReadAsync("a.txt", "Text");

        Assert.NotNull(stream);
        Assert.False(stream.CanSeek);
        Assert.False(stream.CanWrite);
        Assert.True(stream.CanRead);
        Assert.Throws<NotSupportedException>(() => stream.Position);
        Assert.Throws<NotSupportedException>(() => stream.Seek(0, SeekOrigin.Begin));
        Assert.Throws<NotSupportedException>(() => stream.SetLength(1));
        Assert.Throws<NotSupportedException>(() => stream.Write([1], 0, 1));
        var buffer = new byte[3];
        Assert.Equal(3, await stream.ReadAsync(buffer));
        Assert.Equal(new byte[] { 1, 2, 3 }, buffer);
        await stream.DisposeAsync();
    }

    // A-4
    [Fact]
    public async Task Delete_S3Error_Throws()
    {
        _s3.Client.Setup(c => c.DeleteObjectAsync(It.IsAny<DeleteObjectRequest>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(S3Error(HttpStatusCode.Forbidden, "AccessDenied"));

        await Assert.ThrowsAsync<StorageException>(() => Create().DeleteAsync("a.txt", "Text"));
    }

    [Fact]
    public async Task Delete_SendsBucketAndContainerPrefixedKey()
    {
        var provider = Create();

        await provider.DeleteAsync("a/b.txt", "Text");

        _s3.Client.Verify(c => c.DeleteObjectAsync(
            It.Is<DeleteObjectRequest>(r => r.BucketName == Bucket && r.Key == "Text/a/b.txt"),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    // A-9
    [Theory]
    [InlineData("a.png", "image/png")]
    [InlineData("a.pdf", "application/pdf")]
    [InlineData("a.unknown", "application/octet-stream")]
    [InlineData("a.html", "application/octet-stream")]
    public async Task Store_SetsContentTypeFromExtension(string path, string expected)
    {
        await Create().StoreAsync(path, [1], "Text");

        Assert.Equal(expected, Assert.Single(_s3.PutRequests).ContentType);
    }

    [Fact]
    public async Task Store_ExplicitContentType_Wins_AndStreamIsNotClosed()
    {
        using var content = new MemoryStream([1, 2]);

        await Create().StoreAsync(content, "a.bin", new StorageWriteOptions { ContentType = "x/custom" }, "Text");

        var request = Assert.Single(_s3.PutRequests);
        Assert.Equal("x/custom", request.ContentType);
        Assert.False(request.AutoCloseStream);
        Assert.Equal(Bucket, request.BucketName);
        Assert.Equal("Text/a.bin", request.Key);
    }

    [Fact]
    public async Task Store_NonSeekableStream_IsBufferedAndStored()
    {
        using var content = new NonSeekableStream([1, 2, 3, 4]);

        await Create().StoreAsync(content, "ns.bin", null, "Text");

        Assert.Equal(new byte[] { 1, 2, 3, 4 }, _s3.Objects[$"{Bucket}/Text/ns.bin"]);
    }

    // A-10
    [Fact]
    public async Task Key_BackslashesNormalised()
    {
        await Create().StoreAsync("ThumbLarge\\20260101\\x.png", [1], "Image");

        Assert.True(_s3.Objects.ContainsKey($"{Bucket}/Image/ThumbLarge/20260101/x.png"));
    }

    [Fact]
    public async Task Store_GeneratedName_HasGuidAndExtension()
    {
        var path = await Create().StoreAsync([1], ".png", "Image");

        Assert.EndsWith(".png", path);
        Assert.True(Guid.TryParse(Path.GetFileNameWithoutExtension(path), out _));
    }

    [Fact]
    public async Task Store_OverwriteFalse_ChecksExistenceFirst()
    {
        var provider = Create();
        await provider.StoreAsync("a.txt", [1], "Text");
        using var content = new MemoryStream([2]);

        var ex = await Assert.ThrowsAsync<StorageException>(() =>
            provider.StoreAsync(content, "a.txt", new StorageWriteOptions { Overwrite = false }, "Text"));

        Assert.Equal(HttpStatusCode.Conflict, ex.HttpStatusCode);
    }

    [Fact]
    public async Task Exists_UpstreamError_Throws()
    {
        _s3.Client.Setup(c => c.GetObjectMetadataAsync(It.IsAny<GetObjectMetadataRequest>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(S3Error(HttpStatusCode.Forbidden, "AccessDenied"));

        await Assert.ThrowsAsync<StorageException>(() => Create().ExistsAsync("a.txt", "Text"));
    }

    [Fact]
    public void GetRootDirectory_IsContainerPrefix() => Assert.Equal("Image/", Create().GetRootDirectory("Image"));

    // A-8
    [Fact]
    public async Task GetPublicUrl_SetsResponseOverrides()
    {
        GetPreSignedUrlRequest? captured = null;
        _s3.Client.Setup(c => c.GetPreSignedURLAsync(It.IsAny<GetPreSignedUrlRequest>()))
            .Callback<GetPreSignedUrlRequest>(r => captured = r)
            .ReturnsAsync("https://bucket.s3.amazonaws.com/Image/a.png?X-Amz-Signature=abc");

        var uri = await Create().GetPublicUrlAsync("a.png",
            new PublicUrlOptions { ContentType = "image/png", DownloadFileName = "my pic.png", ExpiresIn = TimeSpan.FromMinutes(10) },
            "Image");

        Assert.Equal("https://bucket.s3.amazonaws.com/Image/a.png?X-Amz-Signature=abc", uri!.ToString());
        Assert.Equal(Bucket, captured!.BucketName);
        Assert.Equal("Image/a.png", captured.Key);
        Assert.Equal(HttpVerb.GET, captured.Verb);
        Assert.Equal(Protocol.HTTPS, captured.Protocol);
        Assert.Equal("image/png", captured.ResponseHeaderOverrides.ContentType);
        Assert.StartsWith("attachment; filename=\"my pic.png\"", captured.ResponseHeaderOverrides.ContentDisposition);
        Assert.InRange(captured.Expires!.Value - DateTime.UtcNow, TimeSpan.FromMinutes(9), TimeSpan.FromMinutes(11));
    }

    [Fact]
    public async Task GetPublicUrl_ExpiryClampedTo7Days()
    {
        GetPreSignedUrlRequest? captured = null;
        _s3.Client.Setup(c => c.GetPreSignedURLAsync(It.IsAny<GetPreSignedUrlRequest>()))
            .Callback<GetPreSignedUrlRequest>(r => captured = r).ReturnsAsync("https://h/x");

        await Create().GetPublicUrlAsync("a.png", new PublicUrlOptions { ExpiresIn = TimeSpan.FromDays(30) }, "Image");

        Assert.InRange(captured!.Expires!.Value - DateTime.UtcNow, TimeSpan.FromDays(6.99), TimeSpan.FromDays(7.01));
    }

    [Fact]
    public async Task GetPublicUrl_DefaultExpiry_UsesConfiguredMinutes()
    {
        GetPreSignedUrlRequest? captured = null;
        _s3.Client.Setup(c => c.GetPreSignedURLAsync(It.IsAny<GetPreSignedUrlRequest>()))
            .Callback<GetPreSignedUrlRequest>(r => captured = r).ReturnsAsync("https://h/x");

        await Create(c => c.ExpirationMinutes = 5).GetPublicUrlAsync("a.png", new PublicUrlOptions(), "Image");

        Assert.InRange(captured!.Expires!.Value - DateTime.UtcNow, TimeSpan.FromMinutes(4.9), TimeSpan.FromMinutes(5.1));
    }

    [Fact]
    public async Task GetPublicUrl_FileNameWithQuotesAndUnicode_Encoded()
    {
        GetPreSignedUrlRequest? captured = null;
        _s3.Client.Setup(c => c.GetPreSignedURLAsync(It.IsAny<GetPreSignedUrlRequest>()))
            .Callback<GetPreSignedUrlRequest>(r => captured = r).ReturnsAsync("https://h/x");

        await Create().GetPublicUrlAsync("a.png",
            new PublicUrlOptions { DownloadFileName = "ré\"sumé\r\nX: y.png" }, "Image");

        var disposition = captured!.ResponseHeaderOverrides.ContentDisposition;
        Assert.DoesNotContain('\r', disposition);
        Assert.DoesNotContain('\n', disposition);
        Assert.Contains("filename*=UTF-8''r%C3%A9sum%C3%A9X%3A%20y.png", disposition);
    }

    [Fact]
    public async Task GetPublicUrl_HttpServiceUrl_UsesHttpProtocol()
    {
        GetPreSignedUrlRequest? captured = null;
        _s3.Client.Setup(c => c.GetPreSignedURLAsync(It.IsAny<GetPreSignedUrlRequest>()))
            .Callback<GetPreSignedUrlRequest>(r => captured = r).ReturnsAsync("http://localhost:4566/x");

        await Create(c => c.ServiceUrl = "http://localhost:4566").GetPublicUrlAsync("a.png", new PublicUrlOptions(), "Image");

        Assert.Equal(Protocol.HTTP, captured!.Protocol);
    }

    // X-2: a caller-chosen content type must not reach the signed URL
    [Theory]
    [InlineData("text/html", "a.png", "image/png")]
    [InlineData("image/svg+xml", "a.png", "image/png")]
    [InlineData("image/png,text/html", "a.png", "image/png")]
    [InlineData("image/png\r\nX-Evil: 1", "a.png", "image/png")]
    [InlineData("image/png;charset=utf-8", "a.png", "image/png")]
    [InlineData("application/xml", "a.pdf", "application/pdf")]
    [InlineData("image/png", "a.html", "application/octet-stream")]
    [InlineData(null, "a.xml", "application/octet-stream")]
    public async Task GetPublicUrl_UnsafeContentTypeOverride_FallsBackToExtensionType(string? requested, string path, string expected)
    {
        GetPreSignedUrlRequest? captured = null;
        _s3.Client.Setup(c => c.GetPreSignedURLAsync(It.IsAny<GetPreSignedUrlRequest>()))
            .Callback<GetPreSignedUrlRequest>(r => captured = r).ReturnsAsync("https://h/x");

        await Create().GetPublicUrlAsync(path, new PublicUrlOptions { ContentType = requested, Inline = true }, "Image");

        Assert.Equal(expected, captured!.ResponseHeaderOverrides.ContentType);
        if (expected == "application/octet-stream")
        {
            Assert.StartsWith("attachment", captured.ResponseHeaderOverrides.ContentDisposition);
        }
    }

    [Fact]
    public async Task GetPublicUrl_InlineOnlyForSafeTypes()
    {
        GetPreSignedUrlRequest? captured = null;
        _s3.Client.Setup(c => c.GetPreSignedURLAsync(It.IsAny<GetPreSignedUrlRequest>()))
            .Callback<GetPreSignedUrlRequest>(r => captured = r).ReturnsAsync("https://h/x");

        await Create().GetPublicUrlAsync("a.png", new PublicUrlOptions { Inline = true, DownloadFileName = "a.png" }, "Image");

        Assert.StartsWith("inline", captured!.ResponseHeaderOverrides.ContentDisposition);
    }

    [Fact]
    public async Task GetPublicPath_Legacy_IgnoresCallerMediaType()
    {
        GetPreSignedUrlRequest? captured = null;
        _s3.Client.Setup(c => c.GetPreSignedURLAsync(It.IsAny<GetPreSignedUrlRequest>()))
            .Callback<GetPreSignedUrlRequest>(r => captured = r).ReturnsAsync("https://h/signed");

#pragma warning disable CS0618
        var url = await Create().GetPublicPath("a.png", "text/html", "photo.png", 3, "Image");
#pragma warning restore CS0618

        Assert.Equal("https://h/signed", url);
        Assert.Equal("image/png", captured!.ResponseHeaderOverrides.ContentType);
        Assert.Contains("photo.png", captured.ResponseHeaderOverrides.ContentDisposition);
    }

    [Fact]
    public async Task GetPublicUrl_PresignFailure_ThrowsStorageException()
    {
        _s3.Client.Setup(c => c.GetPreSignedURLAsync(It.IsAny<GetPreSignedUrlRequest>()))
            .ThrowsAsync(new AmazonClientException("no credentials"));

        await Assert.ThrowsAsync<StorageException>(() =>
            Create().GetPublicUrlAsync("a.png", new PublicUrlOptions(), "Image"));
    }

    [Fact]
    public async Task GetPublicUrl_NullOptions_Throws() =>
        await Assert.ThrowsAsync<ArgumentNullException>(() => Create().GetPublicUrlAsync("a", null!));

    // A-5
    [Fact]
    public async Task Logs_NeverContainSecretKey()
    {
        var secret = "unit-test-secret-" + Guid.NewGuid().ToString("N");
        _s3.Client.Setup(c => c.PutObjectAsync(It.IsAny<PutObjectRequest>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(S3Error(HttpStatusCode.InternalServerError, "InternalError"));
        var provider = Create(c =>
        {
            c.AccessKey = "unit-test-access";
            c.SecretKey = secret;
        });

        var ex = await Assert.ThrowsAsync<StorageException>(() => provider.StoreAsync("a.txt", [1], "Text"));

        var logged = string.Join('\n', _logger.Collector.GetSnapshot().Select(r => r.Message + r.Exception));
        Assert.NotEmpty(logged);
        Assert.DoesNotContain(secret, logged);
        Assert.DoesNotContain(secret, ex.ToString());
        Assert.All(_logger.Collector.GetSnapshot(), r => Assert.Equal(LogLevel.Error, r.Level));
    }

    [Fact]
    public async Task UnsafePathOrContainer_ThrowsBadRequest_WithoutCallingS3()
    {
        var provider = Create();

        var ex = await Assert.ThrowsAsync<StorageException>(() => provider.StoreAsync("../x", [1], "Text"));
        await Assert.ThrowsAsync<StorageException>(() => provider.StoreAsync("x", [1], "../Text"));

        Assert.Equal(HttpStatusCode.BadRequest, ex.HttpStatusCode);
        _s3.Client.Verify(c => c.PutObjectAsync(It.IsAny<PutObjectRequest>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Cancelled_Retrieve_Throws() =>
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
        {
            using var cts = new CancellationTokenSource();
            await cts.CancelAsync();
            await Create().RetrieveAsync("a.txt", "Text", cts.Token);
        });

    // A-6
    [Fact]
    public void ParameterlessCtor_Call_ThrowsInvalidOperation()
    {
#pragma warning disable CS0618
        Assert.Throws<InvalidOperationException>(() => new S3StorageProvider());
#pragma warning restore CS0618
    }

    [Theory]
    [InlineData("us-east-1", null, null, null)]
    [InlineData(null, "http://other:4566", null, null)]
    [InlineData(null, null, "other-key", null)]
    [InlineData(null, null, null, "other-secret")]
    public void SetConfiguration_RegionEndpointOrCredentialChange_Throws(string? region, string? serviceUrl, string? access, string? secret)
    {
        var provider = Create(c =>
        {
            c.AccessKey = "key";
            c.SecretKey = "secret";
            c.ServiceUrl = "http://localhost:4566";
        });
        var next = new S3Configuration
        {
            BucketName = "other",
            Region = region ?? "eu-west-1",
            ServiceUrl = serviceUrl ?? "http://localhost:4566",
            AccessKey = access ?? "key",
            SecretKey = secret ?? "secret",
        };

#pragma warning disable CS0618
        Assert.Throws<InvalidOperationException>(() => provider.SetConfiguration(next));
#pragma warning restore CS0618
    }

    [Fact]
    public async Task SetConfiguration_BucketOnly_Switches_AndNullThrows()
    {
        var provider = Create();
#pragma warning disable CS0618
        provider.SetConfiguration(new S3Configuration { BucketName = "second", Region = "eu-west-1" });
        Assert.Throws<ArgumentNullException>(() => provider.SetConfiguration(null!));
#pragma warning restore CS0618

        await provider.StoreAsync("a.txt", [1], "Text");

        Assert.True(_s3.Objects.ContainsKey("second/Text/a.txt"));
    }

    // A-7
    [Fact]
    public void LegacyCtor_DisposesOwnedClient()
    {
        var configuration = new S3Configuration
        {
            BucketName = Bucket, Region = "eu-west-1", AccessKey = "unit-test-access", SecretKey = "unit-test-secret",
        };
#pragma warning disable CS0618
        var provider = new S3StorageProvider(Options.Create(configuration), _logger);
#pragma warning restore CS0618
        var owns = (bool)typeof(S3StorageProvider)
            .GetField("_ownsClient", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
            .GetValue(provider)!;

        // The SDK client exposes no disposed flag, so assert ownership (which gates the dispose) and idempotence.
        Assert.True(owns);
        provider.Dispose();
        provider.Dispose();
    }

    [Fact]
    public void Dispose_DoesNotDisposeAnInjectedSharedClient()
    {
        var provider = Create();

        provider.Dispose();

        _s3.Client.Verify(c => c.Dispose(), Times.Never);
    }

    [Fact]
    public void Ctor_NullArguments_Throw()
    {
        var options = Options.Create(new S3Configuration());
        Assert.Throws<ArgumentNullException>(() => new S3StorageProvider(null!, options, _logger));
        Assert.Throws<ArgumentNullException>(() => new S3StorageProvider(_s3.Client.Object, null!, _logger));
        Assert.Throws<ArgumentNullException>(() => new S3StorageProvider(_s3.Client.Object, options, null!));
    }

    private sealed class NonSeekableStream(byte[] data) : MemoryStream(data)
    {
        public override bool CanSeek => false;
    }
}

public class S3ClientFactoryTests
{
    // A-5
    [Fact]
    public void Client_NoKeys_UsesDefaultChain() =>
        Assert.Null(S3ClientFactory.ResolveCredentials(new S3Configuration { Region = "eu-west-1" }));

    [Theory]
    [InlineData("key", null)]
    [InlineData(null, "secret")]
    [InlineData("", "")]
    public void Client_OnlyOneKey_UsesDefaultChain(string? access, string? secret) =>
        Assert.Null(S3ClientFactory.ResolveCredentials(new S3Configuration { AccessKey = access, SecretKey = secret }));

    [Fact]
    public void Client_BothKeys_UsesBasicCredentials()
    {
        var credentials = S3ClientFactory.ResolveCredentials(new S3Configuration { AccessKey = "ak-test", SecretKey = "sk-test" });

        var basic = Assert.IsType<BasicAWSCredentials>(credentials);
        var resolved = basic.GetCredentials();
        Assert.Equal("ak-test", resolved.AccessKey);
        Assert.Equal("sk-test", resolved.SecretKey);
    }

    [Fact]
    public void CreateConfig_Region_SetsRegionEndpoint_AndTimeout()
    {
        var config = S3ClientFactory.CreateConfig(new S3Configuration { Region = "eu-west-1", TimeoutSeconds = 7 });

        Assert.Equal(RegionEndpoint.EUWest1, config.RegionEndpoint);
        Assert.Equal(TimeSpan.FromSeconds(7), config.Timeout);
        Assert.False(config.ForcePathStyle);
        Assert.Null(config.ServiceURL);
    }

    [Fact]
    public void CreateConfig_InvalidTimeout_FallsBackTo100Seconds() =>
        Assert.Equal(TimeSpan.FromSeconds(100),
            S3ClientFactory.CreateConfig(new S3Configuration { Region = "eu-west-1", TimeoutSeconds = 0 }).Timeout);

    [Fact]
    public void CreateConfig_ServiceUrl_SetsEndpoint_PathStyle_AndRelaxedChecksums()
    {
        var config = S3ClientFactory.CreateConfig(new S3Configuration
        {
            ServiceUrl = "http://localhost:4566", ForcePathStyle = true, Region = "eu-west-1",
        });

        Assert.StartsWith("http://localhost:4566", config.ServiceURL);
        Assert.Equal("eu-west-1", config.AuthenticationRegion);
        Assert.True(config.ForcePathStyle);
        Assert.Equal(RequestChecksumCalculation.WHEN_REQUIRED, config.RequestChecksumCalculation);
    }

    [Fact]
    public void CreateConfig_ServiceUrlWithoutRegion_DefaultsAuthenticationRegion() =>
        Assert.Equal("us-east-1", S3ClientFactory.CreateConfig(new S3Configuration { ServiceUrl = "http://x:1" }).AuthenticationRegion);

    [Fact]
    public void Create_WithKeys_ReturnsClientForRegion()
    {
        using var client = (AmazonS3Client)S3ClientFactory.Create(new S3Configuration
        {
            Region = "eu-west-1", AccessKey = "ak-test", SecretKey = "sk-test",
        });

        Assert.Equal(RegionEndpoint.EUWest1, client.Config.RegionEndpoint);
    }

    [Fact]
    public void Create_NoKeys_WithEnvironmentCredentials_UsesDefaultChain()
    {
        var access = Environment.GetEnvironmentVariable("AWS_ACCESS_KEY_ID");
        var secret = Environment.GetEnvironmentVariable("AWS_SECRET_ACCESS_KEY");
        Environment.SetEnvironmentVariable("AWS_ACCESS_KEY_ID", "env-test-access");
        Environment.SetEnvironmentVariable("AWS_SECRET_ACCESS_KEY", "env-test-secret");
        try
        {
            using var client = (AmazonS3Client)S3ClientFactory.Create(new S3Configuration { Region = "eu-west-1" });

            Assert.NotNull(client);
        }
        finally
        {
            Environment.SetEnvironmentVariable("AWS_ACCESS_KEY_ID", access);
            Environment.SetEnvironmentVariable("AWS_SECRET_ACCESS_KEY", secret);
        }
    }

    [Fact]
    public void NullConfiguration_Throws()
    {
        Assert.Throws<ArgumentNullException>(() => S3ClientFactory.ResolveCredentials(null!));
        Assert.Throws<ArgumentNullException>(() => S3ClientFactory.CreateConfig(null!));
    }
}
