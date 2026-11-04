using System.Text;
using Amazon.S3;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Testcontainers.LocalStack;
using TabTabGo.Storage.Aws;
using TabTabGo.Storage.Models;
using TabTabGo.Storage.TestKit;
using Xunit;

namespace TabTabGo.Storage.Aws.Tests;

/// <summary>Starts LocalStack (S3) once per class and creates the bucket. Does nothing when Docker is not available (the tests skip themselves).</summary>
public sealed class LocalStackFixture : IAsyncLifetime
{
    public const string Bucket = "ttg-storage-tests";

    private LocalStackContainer? _container;

    public S3Configuration Configuration { get; private set; } = new();

    public async ValueTask InitializeAsync()
    {
        if (!DockerAvailable.IsAvailable)
        {
            return; // each test skips itself; a fixture cannot skip
        }

        _container = new LocalStackBuilder().Build();
        await _container.StartAsync();
        Configuration = new S3Configuration
        {
            BucketName = Bucket,
            Region = "us-east-1",
            ServiceUrl = _container.GetConnectionString(),
            ForcePathStyle = true,
            // LocalStack accepts any static credentials.
            AccessKey = "test",
            SecretKey = "test",
        };
        using var admin = (AmazonS3Client)S3ClientFactory.Create(Configuration);
        await admin.PutBucketAsync(Bucket);
    }

    public async ValueTask DisposeAsync()
    {
        if (_container is not null)
        {
            await _container.DisposeAsync();
        }
    }
}

[Trait("Category", "Emulator")]
public class S3EmulatorTests(LocalStackFixture localStack) : StorageProviderContractTests<S3StorageProvider>, IClassFixture<LocalStackFixture>
{
    private IAmazonS3? _client;

    protected override Task<S3StorageProvider> CreateProviderAsync()
    {
        Assert.SkipUnless(DockerAvailable.IsAvailable, "Docker is not available; emulator tests skipped.");
        _client = S3ClientFactory.Create(localStack.Configuration);
        return Task.FromResult(new S3StorageProvider(_client, Options.Create(localStack.Configuration),
            NullLogger<S3StorageProvider>.Instance));
    }

    protected override Task CleanupAsync()
    {
        _client?.Dispose();
        return Task.CompletedTask;
    }

    [Fact]
    public async Task PresignedUrl_FetchedFromEmulator_ReturnsTheBytes_AndOverriddenContentType()
    {
        await Provider.StoreAsync("p/a.png", Encoding.UTF8.GetBytes("png-bytes"), Container);

        var uri = await Provider.GetPublicUrlAsync("p/a.png",
            new PublicUrlOptions { ContentType = "image/png", DownloadFileName = "pic.png", ExpiresIn = TimeSpan.FromMinutes(5) }, Container);

        using var http = new HttpClient();
        using var response = await http.GetAsync(uri, TestContext.Current.CancellationToken);
        Assert.True(response.IsSuccessStatusCode, response.StatusCode.ToString());
        Assert.Equal("png-bytes", await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        Assert.Equal("image/png", response.Content.Headers.ContentType?.MediaType);
        Assert.StartsWith("attachment", response.Content.Headers.ContentDisposition?.ToString());
    }

    [Fact]
    public async Task Store_RecordsContentTypeFromExtension()
    {
        await Provider.StoreAsync("t/a.pdf", [1], Container);

        var metadata = await _client!.GetObjectMetadataAsync(LocalStackFixture.Bucket, $"{Container}/t/a.pdf", TestContext.Current.CancellationToken);

        Assert.Equal("application/pdf", metadata.Headers.ContentType);
    }

    [Fact]
    public async Task Store_Overwrite_ReplacesObject_AndOpenReadStreams()
    {
        await Provider.StoreAsync("o.txt", Encoding.UTF8.GetBytes("longer original content"), Container);
        await Provider.StoreAsync("o.txt", Encoding.UTF8.GetBytes("short"), Container);

        await using var stream = await Provider.OpenReadAsync("o.txt", Container);

        Assert.Equal("short", await new StreamReader(stream!).ReadToEndAsync(TestContext.Current.CancellationToken));
    }
}
