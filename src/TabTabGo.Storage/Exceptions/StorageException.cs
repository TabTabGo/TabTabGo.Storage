using System.Net;
using Microsoft.Extensions.Logging;
using TabTabGo.Core.Exceptions;

namespace TabTabGo.Storage.Exceptions;

/// <summary>
/// A storage operation failed. Not-found is never reported with this exception (providers return <c>null</c>);
/// everything else is. Status is 400 for invalid paths and 502 for upstream failures.
/// </summary>
public class StorageException : ApiException
{
    /// <summary>Creates the exception.</summary>
    /// <param name="message">What went wrong. Must not contain credentials or signed URLs.</param>
    /// <param name="provider">Provider name, for example <c>S3</c>.</param>
    /// <param name="operation">Operation name, for example <c>Store</c>.</param>
    /// <param name="container">Container the operation targeted.</param>
    /// <param name="filePath">Object path the operation targeted.</param>
    /// <param name="statusCode">HTTP status to report.</param>
    /// <param name="inner">Underlying SDK or IO exception.</param>
    /// <param name="code">Stable machine-readable code.</param>
    public StorageException(
        string message,
        string provider,
        string operation,
        string? container = null,
        string? filePath = null,
        HttpStatusCode statusCode = HttpStatusCode.BadGateway,
        Exception? inner = null,
        string code = "STORAGE_ERROR")
        : base(message, inner, LogLevel.Error, null, statusCode, code)
    {
        Provider = provider;
        Operation = operation;
        Container = container;
        FilePath = filePath;
    }

    /// <summary>Provider name.</summary>
    public string Provider { get; }

    /// <summary>Operation name.</summary>
    public string Operation { get; }

    /// <summary>Container the operation targeted.</summary>
    public string? Container { get; }

    /// <summary>Object path the operation targeted.</summary>
    public string? FilePath { get; }
}
