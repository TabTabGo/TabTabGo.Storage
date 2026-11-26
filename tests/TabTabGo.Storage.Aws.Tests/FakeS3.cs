using System.Collections.Concurrent;
using System.Net;
using Amazon.S3;
using Amazon.S3.Model;
using Moq;

namespace TabTabGo.Storage.Aws.Tests;

/// <summary>An in-process S3 double: Moq over <see cref="IAmazonS3"/> backed by a dictionary.</summary>
public sealed class FakeS3
{
    public FakeS3()
    {
        Client = new Mock<IAmazonS3>();
        Client.Setup(c => c.PutObjectAsync(It.IsAny<PutObjectRequest>(), It.IsAny<CancellationToken>()))
            .Returns<PutObjectRequest, CancellationToken>(async (request, ct) =>
            {
                ct.ThrowIfCancellationRequested();
                lock (Objects)
                {
                    if (request.IfNoneMatch == "*" && Objects.ContainsKey(request.BucketName + "/" + request.Key))
                    {
                        throw new AmazonS3Exception("precondition failed") { StatusCode = HttpStatusCode.PreconditionFailed, ErrorCode = "PreconditionFailed" };
                    }
                }

                using var copy = new MemoryStream();
                await request.InputStream.CopyToAsync(copy, ct);
                PutRequests.Add(request);
                Objects[request.BucketName + "/" + request.Key] = copy.ToArray();
                return new PutObjectResponse();
            });
        Client.Setup(c => c.GetObjectAsync(It.IsAny<GetObjectRequest>(), It.IsAny<CancellationToken>()))
            .Returns<GetObjectRequest, CancellationToken>((request, ct) =>
            {
                ct.ThrowIfCancellationRequested();
                if (!Objects.TryGetValue(request.BucketName + "/" + request.Key, out var data))
                {
                    throw new AmazonS3Exception("The specified key does not exist.")
                    {
                        ErrorCode = "NoSuchKey",
                        StatusCode = HttpStatusCode.NotFound,
                    };
                }

                return Task.FromResult(new GetObjectResponse { ResponseStream = ResponseStreamFactory?.Invoke(data) ?? new MemoryStream(data) });
            });
        Client.Setup(c => c.GetObjectMetadataAsync(It.IsAny<GetObjectMetadataRequest>(), It.IsAny<CancellationToken>()))
            .Returns<GetObjectMetadataRequest, CancellationToken>((request, ct) =>
            {
                ct.ThrowIfCancellationRequested();
                if (!Objects.ContainsKey(request.BucketName + "/" + request.Key))
                {
                    throw new AmazonS3Exception("Not Found") { StatusCode = HttpStatusCode.NotFound };
                }

                return Task.FromResult(new GetObjectMetadataResponse());
            });
        Client.Setup(c => c.DeleteObjectAsync(It.IsAny<DeleteObjectRequest>(), It.IsAny<CancellationToken>()))
            .Returns<DeleteObjectRequest, CancellationToken>((request, ct) =>
            {
                ct.ThrowIfCancellationRequested();
                Objects.TryRemove(request.BucketName + "/" + request.Key, out _);
                return Task.FromResult(new DeleteObjectResponse());
            });
    }

    public Mock<IAmazonS3> Client { get; }

    public Func<byte[], Stream>? ResponseStreamFactory { get; set; }

    public ConcurrentDictionary<string, byte[]> Objects { get; } = new();

    public ConcurrentBag<PutObjectRequest> PutRequests { get; } = new();
}
