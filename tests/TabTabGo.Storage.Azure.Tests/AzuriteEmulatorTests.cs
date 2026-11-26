using System.Text;
using Azure.Storage.Blobs;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Testcontainers.Azurite;
using TabTabGo.Storage.AzureStorage;
using TabTabGo.Storage.Models;
using TabTabGo.Storage.TestKit;
using Xunit;

namespace TabTabGo.Storage.Azure.Tests;

/// <summary>Starts Azurite once per class. Does nothing when Docker is not available (the tests skip themselves).</summary>
public sealed class AzuriteFixture : IAsyncLifetime
{
    private AzuriteContainer? _container;

    public string ConnectionString { get; private set; } = string.Empty;

    public async ValueTask InitializeAsync()
    {
        if (!DockerAvailable.IsAvailable)
        {
            return; // each test skips itself; a fixture cannot skip
        }

        _container = new AzuriteBuilder().WithCommand("--skipApiVersionCheck").Build();
        await EmulatorStart.RunAsync("Azurite", _container.StartAsync, dispose: _container.DisposeAsync);
        ConnectionString = _container.GetConnectionString();
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
public class AzureEmulatorTests(AzuriteFixture azurite) : StorageProviderContractTests<BlobStorageProvider>, IClassFixture<AzuriteFixture>
{
    protected override Task<BlobStorageProvider> CreateProviderAsync()
    {
        Assert.SkipUnless(DockerAvailable.IsAvailable, "Docker is not available; emulator tests skipped.");
        return Task.FromResult(new BlobStorageProvider(new BlobServiceClient(azurite.ConnectionString),
            Options.Create(new AzureBlobStorageOptions { ConnectionString = azurite.ConnectionString }),
            NullLogger<BlobStorageProvider>.Instance));
    }

    [Fact]
    public async Task SasUrl_FetchedFromAzurite_ReturnsTheBytes_AndOverriddenHeaders()
    {
        await Provider.StoreAsync("sas/a.png", Encoding.UTF8.GetBytes("png-bytes"), Container);

        var uri = await Provider.GetPublicUrlAsync("sas/a.png",
            new PublicUrlOptions { DownloadFileName = "my pic.png", ExpiresIn = TimeSpan.FromMinutes(5) }, Container);

        Assert.NotNull(uri);
        using var http = new HttpClient();
        using var response = await http.GetAsync(uri, TestContext.Current.CancellationToken);
        Assert.True(response.IsSuccessStatusCode);
        Assert.Equal("png-bytes", await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        Assert.Equal("image/png", response.Content.Headers.ContentType?.MediaType);
        Assert.Contains("attachment", response.Content.Headers.ContentDisposition?.ToString());
    }

    [Fact]
    public async Task Store_SetsBlobContentType()
    {
        await Provider.StoreAsync("t/a.pdf", [1], Container);

        var service = new BlobServiceClient(azurite.ConnectionString);
        var blob = service.GetBlobContainerClient(Container).GetBlobClient("t/a.pdf");
        var properties = await blob.GetPropertiesAsync(cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal("application/pdf", properties.Value.ContentType);
    }

    [Fact]
    public async Task Retrieve_FromMissingContainer_IsNull_AndDoesNotCreateIt()
    {
        var container = "m" + Guid.NewGuid().ToString("N");

        Assert.Null(await Provider.RetrieveAsync("x.txt", container));

        var service = new BlobServiceClient(azurite.ConnectionString);
        Assert.False((await service.GetBlobContainerClient(container).ExistsAsync(TestContext.Current.CancellationToken)).Value);
    }
}
