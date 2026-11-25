using System.Collections.Concurrent;
using Azure;
using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;
using Azure.Storage.Sas;
using Moq;

namespace TabTabGo.Storage.Azure.Tests;

/// <summary>An in-process Blob service double: Moq over the SDK clients, backed by a dictionary.</summary>
public sealed class FakeAzure
{
    private readonly ConcurrentDictionary<string, Mock<BlobContainerClient>> _containers = new();

    public FakeAzure()
    {
        Service = new Mock<BlobServiceClient>();
        Service.Setup(s => s.GetBlobContainerClient(It.IsAny<string>())).Returns<string>(n => Container(n).Object);
    }

    public Mock<BlobServiceClient> Service { get; }

    /// <summary>key = "{container}/{blobName}".</summary>
    public ConcurrentDictionary<string, byte[]> Blobs { get; } = new();

    public ConcurrentBag<(string Container, string BlobName, BlobUploadOptions Options)> Uploads { get; } = new();

    public ConcurrentBag<BlobSasBuilder> SasRequests { get; } = new();

    public ConcurrentBag<string> RequestedBlobNames { get; } = new();

    public int CreateCalls;

    public int ContainerLookups;

    public Func<Task>? OnCreateContainer { get; set; }

    public Exception? UploadFailure { get; set; }

    public bool UploadFailureOnce { get; set; }

    public Stream? TrackedDownload { get; set; }

    public bool CanSas { get; set; } = true;

    public string SasQuery { get; set; } = "sv=2024-01-01&sp=r&sig=SECRETSIGNATURE";

    private Mock<BlobContainerClient> Container(string name) => _containers.GetOrAdd(name, n =>
    {
        Interlocked.Increment(ref ContainerLookups);
        var container = new Mock<BlobContainerClient>();
        container.SetupGet(c => c.Name).Returns(n);
        container.Setup(c => c.CreateIfNotExistsAsync(
                It.IsAny<PublicAccessType>(), It.IsAny<IDictionary<string, string>>(),
                It.IsAny<BlobContainerEncryptionScopeOptions>(), It.IsAny<CancellationToken>()))
            .Returns(async () =>
            {
                Interlocked.Increment(ref CreateCalls);
                if (OnCreateContainer is not null)
                {
                    await OnCreateContainer();
                }

                return Response.FromValue(BlobsModelFactory.BlobContainerInfo(new ETag("e"), DateTimeOffset.UtcNow),
                    new Mock<Response>().Object);
            });
        container.Setup(c => c.GetBlobClient(It.IsAny<string>())).Returns<string>(blobName => Blob(n, blobName).Object);
        return container;
    });

    private Mock<BlobClient> Blob(string container, string blobName)
    {
        RequestedBlobNames.Add(blobName);
        var key = container + "/" + blobName;
        var blob = new Mock<BlobClient>();
        blob.SetupGet(b => b.Name).Returns(blobName);
        blob.SetupGet(b => b.BlobContainerName).Returns(container);
        blob.SetupGet(b => b.CanGenerateSasUri).Returns(() => CanSas);
        blob.Setup(b => b.GenerateSasUri(It.IsAny<BlobSasBuilder>())).Returns<BlobSasBuilder>(builder =>
        {
            SasRequests.Add(builder);
            return new Uri($"https://account.blob.core.windows.net/{container}/{blobName}?{SasQuery}");
        });
        blob.Setup(b => b.UploadAsync(It.IsAny<Stream>(), It.IsAny<BlobUploadOptions>(), It.IsAny<CancellationToken>()))
            .Returns<Stream, BlobUploadOptions, CancellationToken>(async (content, options, ct) =>
            {
                ct.ThrowIfCancellationRequested();
                if (UploadFailure is not null)
                {
                    var failure = UploadFailure;
                    if (UploadFailureOnce)
                    {
                        UploadFailure = null;
                    }

                    throw failure;
                }

                if (options?.Conditions?.IfNoneMatch == ETag.All && Blobs.ContainsKey(key))
                {
                    throw new RequestFailedException(409, "exists", "BlobAlreadyExists", null);
                }

                using var copy = new MemoryStream();
                await content.CopyToAsync(copy, ct);
                Blobs[key] = copy.ToArray();
                Uploads.Add((container, blobName, options!));
                return Response.FromValue(BlobsModelFactory.BlobContentInfo(new ETag("e"), DateTimeOffset.UtcNow, null, null, 0),
                    new Mock<Response>().Object);
            });
        blob.Setup(b => b.DownloadStreamingAsync(It.IsAny<BlobDownloadOptions>(), It.IsAny<CancellationToken>()))
            .Returns<BlobDownloadOptions, CancellationToken>((_, ct) =>
            {
                ct.ThrowIfCancellationRequested();
                if (!Blobs.TryGetValue(key, out var data))
                {
                    throw new RequestFailedException(404, "not found", "BlobNotFound", null);
                }

                var stream = TrackedDownload ?? new MemoryStream(data);
                if (TrackedDownload is MemoryStream tracked)
                {
                    tracked.SetLength(0);
                    tracked.Write(data);
                    tracked.Position = 0;
                }

                return Task.FromResult(Response.FromValue(
                    BlobsModelFactory.BlobDownloadStreamingResult(stream, BlobsModelFactory.BlobDownloadDetails()),
                    new Mock<Response>().Object));
            });
        blob.Setup(b => b.ExistsAsync(It.IsAny<CancellationToken>())).Returns<CancellationToken>(ct =>
        {
            ct.ThrowIfCancellationRequested();
            return Task.FromResult(Response.FromValue(Blobs.ContainsKey(key), new Mock<Response>().Object));
        });
        blob.Setup(b => b.DeleteIfExistsAsync(It.IsAny<DeleteSnapshotsOption>(), It.IsAny<BlobRequestConditions>(),
                It.IsAny<CancellationToken>()))
            .Returns<DeleteSnapshotsOption, BlobRequestConditions, CancellationToken>((_, _, ct) =>
            {
                ct.ThrowIfCancellationRequested();
                return Task.FromResult(Response.FromValue(Blobs.TryRemove(key, out _), new Mock<Response>().Object));
            });
        return blob;
    }
}
