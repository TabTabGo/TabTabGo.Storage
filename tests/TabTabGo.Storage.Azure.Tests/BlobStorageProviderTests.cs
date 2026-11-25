using System.Net;
using System.Text;
using Azure;
using Azure.Core;
using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;
using Azure.Storage.Sas;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Testing;
using Microsoft.Extensions.Options;
using Moq;
using TabTabGo.Storage.AzureStorage;
using TabTabGo.Storage.Exceptions;
using TabTabGo.Storage.Models;
using TabTabGo.Storage.TestKit;
using Xunit;

namespace TabTabGo.Storage.Azure.Tests;

public class AzureFakeContractTests : StorageProviderContractTests<BlobStorageProvider>
{
    protected override Task<BlobStorageProvider> CreateProviderAsync() =>
        Task.FromResult(new BlobStorageProvider(new FakeAzure().Service.Object,
            Options.Create(new AzureBlobStorageOptions { ConnectionString = "x" }), new FakeLogger<BlobStorageProvider>()));
}

public class BlobStorageProviderTests
{
    private readonly FakeAzure _azure = new();
    private readonly FakeLogger<BlobStorageProvider> _logger = new();

    private BlobStorageProvider Create(Action<AzureBlobStorageOptions>? configure = null)
    {
        var options = new AzureBlobStorageOptions { ConnectionString = "x" };
        configure?.Invoke(options);
        return new BlobStorageProvider(_azure.Service.Object, Options.Create(options), _logger);
    }

    private static byte[] Bytes(string text) => Encoding.UTF8.GetBytes(text);

    // Z-1
    [Fact]
    public async Task GetPublicUrl_ReturnsAbsoluteUriWithSignature()
    {
        var uri = await Create().GetPublicUrlAsync("a/b.png", new PublicUrlOptions(), "Image");

        Assert.True(uri!.IsAbsoluteUri);
        Assert.Equal("account.blob.core.windows.net", uri.Host);
        Assert.Contains("sig=", uri.Query);
    }

    [Fact]
    public async Task GetPublicPath_Legacy_IsAbsolute()
    {
#pragma warning disable CS0618
        var path = await Create().GetPublicPath("a.png", "image/png", "a.png", 1, "Image");
#pragma warning restore CS0618

        Assert.StartsWith("https://", path);
        Assert.Contains("sig=", path);
    }

    // Z-2
    [Fact]
    public async Task GetPublicUrl_WritesNothingToConsole()
    {
        var original = Console.Out;
        using var capture = new StringWriter();
        Console.SetOut(capture);
        try
        {
            await Create().GetPublicUrlAsync("a.png", new PublicUrlOptions(), "Image");
        }
        finally
        {
            Console.SetOut(original);
        }

        Assert.Equal(string.Empty, capture.ToString());
    }

    [Fact]
    public async Task Logs_NeverContainSasSignature()
    {
        var provider = Create();
        await provider.StoreAsync("a.png", [1], "Image");
        await provider.RetrieveAsync("a.png", "Image");
        await provider.DeleteAsync("a.png", "Image");
        await provider.GetPublicUrlAsync("a.png", new PublicUrlOptions(), "Image");

        var logged = string.Join('\n', _logger.Collector.GetSnapshot().Select(r => r.Message));
        Assert.DoesNotContain("sig=", logged);
        Assert.DoesNotContain("SECRETSIGNATURE", logged);
    }

    // Z-3
    [Fact]
    public async Task Retrieve_DoesNotCreateContainer()
    {
        var provider = Create();

        await provider.RetrieveAsync("a.png", "Image");
        await provider.ExistsAsync("a.png", "Image");
        await provider.DeleteAsync("a.png", "Image");
        await provider.GetPublicUrlAsync("a.png", new PublicUrlOptions(), "Image");

        Assert.Equal(0, _azure.CreateCalls);
    }

    [Fact]
    public async Task Store_CreatesContainerOnce()
    {
        var provider = Create();

        await Task.WhenAll(Enumerable.Range(0, 20).Select(i => provider.StoreAsync($"f{i}.txt", [1], "Image")));
        await provider.StoreAsync("again.txt", [1], "Image");

        Assert.Equal(1, _azure.CreateCalls);
    }

    [Fact]
    public async Task Store_FailedContainerCreation_IsNotCached()
    {
        var failures = 1;
        _azure.OnCreateContainer = () =>
            Interlocked.Exchange(ref failures, 0) == 1
                ? throw new RequestFailedException(500, "boom")
                : Task.CompletedTask;
        var provider = Create();

        await Assert.ThrowsAsync<StorageException>(() => provider.StoreAsync("a.txt", [1], "Image"));
        await provider.StoreAsync("a.txt", [1], "Image");

        Assert.Equal(2, _azure.CreateCalls);
    }

    // Z-4
    [Fact]
    public async Task Store_ExistingBlob_Overwritten()
    {
        var provider = Create();
        await provider.StoreAsync("a.txt", Bytes("first"), "Text");

        await provider.StoreAsync("a.txt", Bytes("second"), "Text");

        Assert.Equal(Bytes("second"), _azure.Blobs["text/a.txt"]);
    }

    // Z-5
    [Fact]
    public async Task Retrieve_Missing_Null() => Assert.Null(await Create().RetrieveAsync("none.txt", "Text"));

    [Fact]
    public async Task Retrieve_DisposesDownload()
    {
        var provider = Create();
        await provider.StoreAsync("a.txt", Bytes("data"), "Text");
        var tracked = new TrackingStream();
        _azure.TrackedDownload = tracked;

        await using var stream = await provider.RetrieveAsync("a.txt", "Text");

        Assert.True(tracked.IsDisposed);
        Assert.Equal("data", new StreamReader(stream!).ReadToEnd());
    }

    [Fact]
    public async Task OpenRead_ReturnsDownloadContent_AndMissingIsNull()
    {
        var provider = Create();
        await provider.StoreAsync("a.txt", Bytes("data"), "Text");

        await using var stream = await provider.OpenReadAsync("a.txt", "Text");

        Assert.Equal("data", new StreamReader(stream!).ReadToEnd());
        Assert.Null(await provider.OpenReadAsync("none.txt", "Text"));
    }

    [Fact]
    public async Task Retrieve_UpstreamError_ThrowsStorageException()
    {
        _azure.Service.Setup(s => s.GetBlobContainerClient(It.IsAny<string>()))
            .Throws(new RequestFailedException(403, "forbidden"));

        var ex = await Assert.ThrowsAsync<RequestFailedException>(() => Create().RetrieveAsync("a", "Text"));

        Assert.Equal(403, ex.Status);
    }

    // Z-6
    [Theory]
    [InlineData("a.png", "image/png")]
    [InlineData("a.pdf", "application/pdf")]
    [InlineData("a.unknown", "application/octet-stream")]
    public async Task Store_SetsContentType(string path, string expected)
    {
        await Create().StoreAsync(path, [1], "Text");

        Assert.Equal(expected, Assert.Single(_azure.Uploads).Options.HttpHeaders.ContentType);
    }

    [Fact]
    public async Task Store_ExplicitContentType_Wins()
    {
        using var content = new MemoryStream([1]);

        await Create().StoreAsync(content, "a.bin", new StorageWriteOptions { ContentType = "x/custom" }, "Text");

        Assert.Equal("x/custom", Assert.Single(_azure.Uploads).Options.HttpHeaders.ContentType);
    }

    [Fact]
    public async Task Store_UpstreamFailure_ThrowsStorageException()
    {
        _azure.OnCreateContainer = () => throw new RequestFailedException(500, "boom");

        var ex = await Assert.ThrowsAsync<StorageException>(() => Create().StoreAsync("a.txt", [1], "Text"));

        Assert.Equal("AzureBlob", ex.Provider);
        Assert.Equal(HttpStatusCode.BadGateway, ex.HttpStatusCode);
    }

    [Fact]
    public async Task Store_GeneratedName_HasGuidAndExtension()
    {
        var path = await Create().StoreAsync([1], ".png", "Image");

        Assert.EndsWith(".png", path);
        Assert.True(Guid.TryParse(Path.GetFileNameWithoutExtension(path), out _));
    }

    [Fact]
    public async Task Store_GeneratedName_NonAlphanumericExtension_Rejected()
    {
        var ex = await Assert.ThrowsAsync<StorageException>(() => Create().StoreAsync([1], ".png/x", "Image"));

        Assert.Equal("STORAGE_INVALID_PATH", ex.ErrorCode);
        Assert.Empty(_azure.Blobs);
    }

    [Fact]
    public async Task Store_NoOverwrite_BlobAlreadyExists_ReportsObjectExists()
    {
        await Create().StoreAsync("a.txt", [1], "Text");
        using var content = new MemoryStream([2]);

        var ex = await Assert.ThrowsAsync<StorageException>(() =>
            Create().StoreAsync(content, "a.txt", new StorageWriteOptions { Overwrite = false }, "Text"));

        Assert.Equal("STORAGE_OBJECT_EXISTS", ex.ErrorCode);
    }

    [Fact]
    public async Task Store_ContainerDeletedAfterCreation_RecreatesAndRetriesOnce()
    {
        var provider = Create();
        await provider.StoreAsync("a.txt", [1], "Text");
        _azure.UploadFailure = new RequestFailedException(404, "gone", "ContainerNotFound", null);
        _azure.UploadFailureOnce = true;

        await provider.StoreAsync("b.txt", [2], "Text");

        Assert.Equal(2, _azure.CreateCalls);
        Assert.True(_azure.Blobs.ContainsKey("text/b.txt"));
    }

    [Fact]
    public async Task Store_ContainerStaysMissing_RetriesOnlyOnce_ThenFails()
    {
        var provider = Create();
        await provider.StoreAsync("a.txt", [1], "Text");
        _azure.UploadFailure = new RequestFailedException(404, "gone", "ContainerNotFound", null);

        await Assert.ThrowsAsync<StorageException>(() => provider.StoreAsync("b.txt", [2], "Text"));

        Assert.Equal(2, _azure.CreateCalls);
    }

    [Fact]
    public async Task Store_NoOverwrite_Other409_IsGenericStorageFailure()
    {
        _azure.UploadFailure = new RequestFailedException(409, "deleting", "ContainerBeingDeleted", null);
        using var content = new MemoryStream([2]);

        var ex = await Assert.ThrowsAsync<StorageException>(() =>
            Create().StoreAsync(content, "a.txt", new StorageWriteOptions { Overwrite = false }, "Text"));

        Assert.NotEqual("STORAGE_OBJECT_EXISTS", ex.ErrorCode);
    }

    // Z-8
    [Fact]
    public async Task GetPublicUrl_HonoursExpiryAndHeaders()
    {
        await Create().GetPublicUrlAsync("a.png",
            new PublicUrlOptions { ExpiresIn = TimeSpan.FromMinutes(10), ContentType = "image/png", DownloadFileName = "pic.png" },
            "Image");

        var builder = Assert.Single(_azure.SasRequests);
        Assert.InRange(builder.ExpiresOn - DateTimeOffset.UtcNow, TimeSpan.FromMinutes(9), TimeSpan.FromMinutes(11));
        Assert.Equal("image/png", builder.ContentType);
        Assert.StartsWith("attachment; filename=\"pic.png\"", builder.ContentDisposition);
        Assert.Equal("image", builder.BlobContainerName);
        Assert.Equal("a.png", builder.BlobName);
    }

    [Fact]
    public async Task GetPublicUrl_DefaultExpiry_UsesOptionDefault()
    {
        await Create(o => o.DefaultSasExpiry = TimeSpan.FromMinutes(5)).GetPublicUrlAsync("a.png", new PublicUrlOptions(), "Image");

        Assert.InRange(Assert.Single(_azure.SasRequests).ExpiresOn - DateTimeOffset.UtcNow,
            TimeSpan.FromMinutes(4.9), TimeSpan.FromMinutes(5.1));
    }

    [Fact]
    public async Task GetPublicUrl_ExpiryClampedTo7Days()
    {
        await Create().GetPublicUrlAsync("a.png", new PublicUrlOptions { ExpiresIn = TimeSpan.FromDays(60) }, "Image");

        Assert.InRange(Assert.Single(_azure.SasRequests).ExpiresOn - DateTimeOffset.UtcNow,
            TimeSpan.FromDays(6.99), TimeSpan.FromDays(7.01));
    }

    // X-2 parity with S3
    [Theory]
    [InlineData("text/html", "a.png", "image/png")]
    [InlineData("image/svg+xml", "a.png", "image/png")]
    [InlineData("image/png", "a.html", "application/octet-stream")]
    [InlineData(null, "a.xml", "application/octet-stream")]
    public async Task GetPublicUrl_UnsafeContentTypeOverride_FallsBackToExtensionType(string? requested, string path, string expected)
    {
        await Create().GetPublicUrlAsync(path, new PublicUrlOptions { ContentType = requested, Inline = true }, "Image");

        var builder = Assert.Single(_azure.SasRequests);
        Assert.Equal(expected, builder.ContentType);
        if (expected == "application/octet-stream")
        {
            Assert.StartsWith("attachment", builder.ContentDisposition);
        }
    }

    [Fact]
    public async Task GetPublicPath_Legacy_IgnoresCallerMediaType()
    {
#pragma warning disable CS0618
        await Create().GetPublicPath("a.png", "text/html", "x.png", 1, "Image");
#pragma warning restore CS0618

        Assert.Equal("image/png", Assert.Single(_azure.SasRequests).ContentType);
    }

    [Fact]
    public async Task GetPublicUrl_NoSharedKey_NoCredential_ReturnsNull_WithWarning()
    {
        _azure.CanSas = false;

        Assert.Null(await Create().GetPublicUrlAsync("a.png", new PublicUrlOptions(), "Image"));
        Assert.Contains(_logger.Collector.GetSnapshot(), r => r.Level == LogLevel.Warning);
    }

    [Fact]
    public async Task GetPublicUrl_SharedKeyConnectionString_SignsLocally()
    {
        var key = Convert.ToBase64String(Encoding.UTF8.GetBytes(new string('k', 32)));
        var service = new BlobServiceClient($"DefaultEndpointsProtocol=https;AccountName=acct;AccountKey={key};EndpointSuffix=core.windows.net");
        var provider = new BlobStorageProvider(service, Options.Create(new AzureBlobStorageOptions { ConnectionString = "x" }), _logger);

        var uri = await provider.GetPublicUrlAsync("Folder/A.png",
            new PublicUrlOptions { ContentType = "image/png", DownloadFileName = "A.png" }, "Image");

        Assert.NotNull(uri);
        Assert.StartsWith("https://acct.blob.core.windows.net/image/folder/a.png?", uri.AbsoluteUri);
        var query = System.Web.HttpUtility.ParseQueryString(uri.Query);
        Assert.Equal("r", query["sp"]);
        Assert.Equal("image/png", query["rsct"]);
        Assert.NotNull(query["sig"]);
        Assert.Null(query["skoid"]);
    }

    // Z-7
    [Fact]
    public async Task TokenCredential_GeneratesUserDelegationSas()
    {
        var credential = new StaticTokenCredential();
        var service = new Mock<BlobServiceClient>(new Uri("https://acct.blob.core.windows.net"), credential, null!) { CallBase = true };
        service.Setup(s => s.GetUserDelegationKeyAsync(It.IsAny<DateTimeOffset?>(), It.IsAny<DateTimeOffset>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Response.FromValue(BlobsModelFactory.UserDelegationKey(
                "11111111-1111-1111-1111-111111111111", "22222222-2222-2222-2222-222222222222",
                DateTimeOffset.UtcNow.AddMinutes(-5), DateTimeOffset.UtcNow.AddHours(2), "b", "2024-11-04",
                Convert.ToBase64String(Encoding.UTF8.GetBytes(new string('d', 32)))), new Mock<Response>().Object));
        var provider = new BlobStorageProvider(service.Object,
            Options.Create(new AzureBlobStorageOptions { ServiceUri = new Uri("https://acct.blob.core.windows.net"), Credential = credential }),
            _logger);

        var uri = await provider.GetPublicUrlAsync("a.png", new PublicUrlOptions(), "Image");

        Assert.NotNull(uri);
        var query = System.Web.HttpUtility.ParseQueryString(uri.Query);
        Assert.Equal("11111111-1111-1111-1111-111111111111", query["skoid"]);
        Assert.NotNull(query["sig"]);
    }

    [Fact]
    public async Task TokenCredential_SevenDayExpiry_RequestsAKeyWithinAzuresSevenDayLimit()
    {
        var credential = new StaticTokenCredential();
        var service = new Mock<BlobServiceClient>(new Uri("https://acct.blob.core.windows.net"), credential, null!) { CallBase = true };
        DateTimeOffset? requestedStart = null;
        DateTimeOffset requestedExpiry = default;
        service.Setup(s => s.GetUserDelegationKeyAsync(It.IsAny<DateTimeOffset?>(), It.IsAny<DateTimeOffset>(), It.IsAny<CancellationToken>()))
            .Callback<DateTimeOffset?, DateTimeOffset, CancellationToken>((start, expiry, _) =>
            {
                requestedStart = start;
                requestedExpiry = expiry;
            })
            .ReturnsAsync(Response.FromValue(BlobsModelFactory.UserDelegationKey(
                "11111111-1111-1111-1111-111111111111", "22222222-2222-2222-2222-222222222222",
                DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddDays(7), "b", "2024-11-04",
                Convert.ToBase64String(Encoding.UTF8.GetBytes(new string('d', 32)))), new Mock<Response>().Object));
        var provider = new BlobStorageProvider(service.Object,
            Options.Create(new AzureBlobStorageOptions { ServiceUri = new Uri("https://acct.blob.core.windows.net"), Credential = credential }),
            _logger);
        var before = DateTimeOffset.UtcNow;

        var uri = await provider.GetPublicUrlAsync("a.png", new PublicUrlOptions { ExpiresIn = TimeSpan.FromDays(7) }, "Image");

        Assert.NotNull(uri);
        Assert.NotNull(requestedStart);
        Assert.True(requestedStart >= before, "the key must not start in the past");
        Assert.True(requestedExpiry - requestedStart < TimeSpan.FromDays(7), "the key window must stay under Azure's 7-day cap");
        var signedExpiry = DateTimeOffset.Parse(System.Web.HttpUtility.ParseQueryString(uri.Query)["se"]!,
            System.Globalization.CultureInfo.InvariantCulture);
        Assert.True(signedExpiry <= requestedExpiry.AddSeconds(1), "the SAS must not outlive the key");
        Assert.True(signedExpiry > before.AddDays(6), "the SAS keeps the requested lifetime up to the clamp");
    }

    [Fact]
    public async Task TokenCredential_KeyRequestFails_ThrowsStorageException()
    {
        var credential = new StaticTokenCredential();
        var service = new Mock<BlobServiceClient>(new Uri("https://acct.blob.core.windows.net"), credential, null!) { CallBase = true };
        service.Setup(s => s.GetUserDelegationKeyAsync(It.IsAny<DateTimeOffset?>(), It.IsAny<DateTimeOffset>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new RequestFailedException(403, "denied"));
        var provider = new BlobStorageProvider(service.Object,
            Options.Create(new AzureBlobStorageOptions { ServiceUri = new Uri("https://acct.blob.core.windows.net"), Credential = credential }),
            _logger);

        await Assert.ThrowsAsync<StorageException>(() => provider.GetPublicUrlAsync("a.png", new PublicUrlOptions(), "Image"));
    }

    // Z-9 / Z-10 / Z-11
    [Fact]
    public async Task BlobName_LowercasedByDefault()
    {
        var path = await Create().StoreAsync("Folder/MixedCase.PNG", [1], "Image");

        Assert.Equal("Folder/MixedCase.PNG", path);
        Assert.Contains("folder/mixedcase.png", _azure.RequestedBlobNames);
    }

    [Fact]
    public async Task BlobName_CasePreserved_WhenDisabled()
    {
        await Create(o => o.LowercaseBlobNames = false).StoreAsync("Folder/MixedCase.PNG", [1], "Image");

        Assert.Contains("Folder/MixedCase.PNG", _azure.RequestedBlobNames);
    }

    [Fact]
    public async Task Container_IsNormalisedForAzure()
    {
        await Create().StoreAsync("a.txt", [1], "Word Document");

        Assert.Contains("word-document/a.txt", _azure.Blobs.Keys);
    }

    [Theory]
    [InlineData("!!!")]
    [InlineData("---")]
    public async Task InvalidContainer_ThrowsBadRequest(string container)
    {
        var ex = await Assert.ThrowsAsync<StorageException>(() => Create().StoreAsync("a.txt", [1], container));

        Assert.Equal(HttpStatusCode.BadRequest, ex.HttpStatusCode);
    }

    [Theory]
    [InlineData("../x")]
    [InlineData("a/b")]
    [InlineData("..")]
    public async Task Container_Traversal_IsRejected_NotSanitised(string container)
    {
        var ex = await Assert.ThrowsAsync<StorageException>(() => Create().StoreAsync("a.txt", [1], container));

        Assert.Equal("STORAGE_INVALID_CONTAINER", ex.ErrorCode);
        Assert.Equal(HttpStatusCode.BadRequest, ex.HttpStatusCode);
        Assert.Empty(_azure.Blobs);
    }

    [Fact]
    public async Task GetPublicUrl_TextPlain_IsAlwaysAnAttachment_EvenWhenInlineIsRequested()
    {
        await Create().GetPublicUrlAsync("note.txt", new PublicUrlOptions { Inline = true, DownloadFileName = "note.txt" }, "Text");

        Assert.StartsWith("attachment", Assert.Single(_azure.SasRequests).ContentDisposition);
    }

    [Fact]
    public async Task Logs_AreStructured()
    {
        var provider = Create();
        await provider.StoreAsync("a.txt", [1], "Text");
        await provider.DeleteAsync("a.txt", "Text");

        var records = _logger.Collector.GetSnapshot().Where(r => r.Level == LogLevel.Trace).ToList();
        Assert.NotEmpty(records);
        Assert.All(records, r => Assert.DoesNotContain("{0}", r.Message));
        Assert.Contains(records, r => r.StructuredState!.Any(p => p.Key == "BlobName"));
        Assert.Contains(records, r => r.StructuredState!.Any(p => p.Key == "Container"));
    }

    [Fact]
    public void GetRootDirectory_IsEmpty() => Assert.Equal(string.Empty, Create().GetRootDirectory("Image"));

    [Fact]
    public async Task Exists_ReflectsBlob()
    {
        var provider = Create();
        Assert.False(await provider.ExistsAsync("a.txt", "Text"));
        await provider.StoreAsync("a.txt", [1], "Text");
        Assert.True(await provider.ExistsAsync("a.txt", "Text"));
    }

    [Fact]
    public async Task GetPublicUrl_NullOptions_Throws() =>
        await Assert.ThrowsAsync<ArgumentNullException>(() => Create().GetPublicUrlAsync("a", null!));

    [Fact]
    public void Ctor_NullArguments_Throw()
    {
        var options = Options.Create(new AzureBlobStorageOptions());
        Assert.Throws<ArgumentNullException>(() => new BlobStorageProvider(null!, options, _logger));
        Assert.Throws<ArgumentNullException>(() => new BlobStorageProvider(_azure.Service.Object, null!, _logger));
        Assert.Throws<ArgumentNullException>(() => new BlobStorageProvider(_azure.Service.Object, options, null!));
    }

    [Fact]
    public void LegacyCtor_ReadsConnectionStringFromConfiguration_AndThrowsWhenMissing()
    {
        var key = Convert.ToBase64String(Encoding.UTF8.GetBytes(new string('k', 32)));
        var configured = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["ConnectionStrings:AzureStorageConnection"] =
                $"DefaultEndpointsProtocol=https;AccountName=acct;AccountKey={key};EndpointSuffix=core.windows.net",
        }).Build();
        var empty = new ConfigurationBuilder().Build();

#pragma warning disable CS0618
        Assert.NotNull(new BlobStorageProvider(configured, _logger));
        Assert.Throws<InvalidOperationException>(() => new BlobStorageProvider(empty, _logger));
        Assert.Throws<ArgumentNullException>(() => new BlobStorageProvider((IConfiguration)null!, _logger));
#pragma warning restore CS0618
    }

    private sealed class TrackingStream : MemoryStream
    {
        public bool IsDisposed { get; private set; }

        protected override void Dispose(bool disposing)
        {
            IsDisposed = true;
            base.Dispose(disposing);
        }
    }

    private sealed class StaticTokenCredential : TokenCredential
    {
        public override AccessToken GetToken(TokenRequestContext requestContext, CancellationToken cancellationToken) =>
            new("token", DateTimeOffset.UtcNow.AddHours(1));

        public override ValueTask<AccessToken> GetTokenAsync(TokenRequestContext requestContext, CancellationToken cancellationToken) =>
            new(GetToken(requestContext, cancellationToken));
    }
}
