using System.Net;
using System.Text;
using Amazon.Runtime;
using Amazon.S3;
using Microsoft.Extensions.Logging.Testing;
using Microsoft.Extensions.Options;
using TabTabGo.Storage.Exceptions;
using Xunit;

namespace TabTabGo.Storage.Aws.Tests;

/// <summary>
/// Drives the real AWS SDK client against a stub HTTP endpoint so the error codes the SDK reports for 403 answers
/// (including the body-less answer to a HEAD) are the ones the provider really sees.
/// </summary>
public sealed class S3ErrorCodeTests : IDisposable
{
    private readonly HttpListener _listener = new();
    private readonly string _url;
    private string? _code;

    public S3ErrorCodeTests()
    {
        var port = FreePort();
        _url = $"http://127.0.0.1:{port}/";
        _listener.Prefixes.Add(_url);
        _listener.Start();
        _ = Task.Run(Serve);
    }

    public void Dispose() => _listener.Close();

    private static int FreePort()
    {
        var probe = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        var port = ((IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();
        return port;
    }

    private async Task Serve()
    {
        while (_listener.IsListening)
        {
            HttpListenerContext context;
            try
            {
                context = await _listener.GetContextAsync();
            }
            catch (Exception e) when (e is HttpListenerException or ObjectDisposedException or InvalidOperationException)
            {
                return;
            }

            context.Response.StatusCode = 403;
            if (context.Request.HttpMethod != "HEAD" && _code is not null)
            {
                var body = Encoding.UTF8.GetBytes(
                    $"<?xml version=\"1.0\" encoding=\"UTF-8\"?><Error><Code>{_code}</Code><Message>m</Message>" +
                    "<RequestId>REQ1</RequestId></Error>");
                context.Response.ContentType = "application/xml";
                context.Response.ContentLength64 = body.Length;
                await context.Response.OutputStream.WriteAsync(body);
            }

            context.Response.Close();
        }
    }

    private (S3StorageProvider Provider, FakeLogger<S3StorageProvider> Logger) Create()
    {
        var client = new AmazonS3Client(new BasicAWSCredentials("k", "s"),
            new AmazonS3Config { ServiceURL = _url.TrimEnd('/'), ForcePathStyle = true, MaxErrorRetry = 0 });
        var logger = new FakeLogger<S3StorageProvider>();
        var provider = new S3StorageProvider(client,
            Options.Create(new S3Configuration { BucketName = "b", Region = "eu-west-1" }), logger);
        return (provider, logger);
    }

    // Finding: a HEAD 403 has no body, so the SDK v4 reports the HTTP status text as the error code.
    [Fact]
    public async Task Head403_SdkReportsForbiddenAsTheErrorCode()
    {
        var client = new AmazonS3Client(new BasicAWSCredentials("k", "s"),
            new AmazonS3Config { ServiceURL = _url.TrimEnd('/'), ForcePathStyle = true, MaxErrorRetry = 0 });

        var ex = await Assert.ThrowsAsync<AmazonS3Exception>(() => client.GetObjectMetadataAsync("b", "k"));

        Assert.Equal(HttpStatusCode.Forbidden, ex.StatusCode);
        Assert.Equal("Forbidden", ex.ErrorCode);
    }

    [Fact]
    public async Task Exists_RealSdkHead403_IsFalse_AndLogsTheEvent()
    {
        var (provider, logger) = Create();

        Assert.False(await provider.ExistsAsync("a.txt", "Text"));

        var record = Assert.Single(logger.Collector.GetSnapshot());
        Assert.Equal("S3ReadForbiddenAsNotFound", record.Id.Name);
    }

    [Fact]
    public async Task Retrieve_RealSdkAccessDenied_IsNull()
    {
        _code = "AccessDenied";
        var (provider, logger) = Create();

        Assert.Null(await provider.RetrieveAsync("a.txt", "Text"));

        var record = Assert.Single(logger.Collector.GetSnapshot());
        Assert.Contains("AccessDenied", record.Message);
        Assert.Contains("REQ1", record.Message);
    }

    [Theory]
    [InlineData("InvalidAccessKeyId")]
    [InlineData("SignatureDoesNotMatch")]
    [InlineData("RequestTimeTooSkewed")]
    [InlineData("InvalidObjectState")]
    public async Task Retrieve_RealSdk403Fault_Throws(string code)
    {
        _code = code;

        await Assert.ThrowsAsync<StorageException>(() => Create().Provider.RetrieveAsync("a.txt", "Text"));
    }
}
