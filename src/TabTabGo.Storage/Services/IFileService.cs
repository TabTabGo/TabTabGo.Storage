using System.Linq.Expressions;
using TabTabGo.Core.Models;
using TabTabGo.Core.Services;
using TabTabGo.Storage.Entities;
using TabTabGo.Storage.Enums;
using TabTabGo.Storage.Models;
using TabTabGo.Storage.ViewModels;

namespace TabTabGo.Storage.Services;

/// <summary>Application service for file metadata and content.</summary>
/// <typeparam name="TFile">File entity type.</typeparam>
public interface IFileService<TFile> : IBaseService<TFile, long> where TFile : class, IFile
{
    /// <summary>Hard-deletes the matching rows, then their stored objects and thumbnails.</summary>
    /// <param name="query">Filter selecting the files.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A task that completes when the rows and objects are removed.</returns>
    Task Delete(Expression<Func<TFile, bool>> query, CancellationToken cancellationToken = default);

    /// <summary>Reads an image or one of its thumbnails; a missing thumbnail is generated on demand.</summary>
    /// <param name="id">File id.</param>
    /// <param name="size">Size preset.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The image. The caller disposes it.</returns>
    Task<FileResult> GetImage(long id, ImageSize size = ImageSize.Default, CancellationToken cancellationToken = default);

    /// <summary>Reads a file's content.</summary>
    /// <param name="id">File id.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The content. The caller disposes it.</returns>
    Task<FileResult> GetFile(long id, CancellationToken cancellationToken = default);

    /// <summary>Stores content and inserts its metadata row.</summary>
    /// <param name="metadata">Client-supplied metadata; storage-owned fields are overwritten.</param>
    /// <param name="content">The content to store.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The inserted entity.</returns>
    Task<TFile> Upload(TFile metadata, FileUpload content, CancellationToken cancellationToken = default);

    /// <summary>Replaces the stored content of an existing file and removes the previous objects.</summary>
    /// <param name="id">File id.</param>
    /// <param name="content">The new content.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The updated entity.</returns>
    Task<TFile> Replace(long id, FileUpload content, CancellationToken cancellationToken = default);

    /// <summary>Lists files newest first (by id), filtered and paged.</summary>
    /// <param name="query">Filters and paging.</param>
    /// <param name="scope">Additional host filter (tenant, owner), applied with AND.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>One page of files.</returns>
    Task<PageList<TFile>> ListAsync(FileListQuery query, Expression<Func<TFile, bool>>? scope = null,
        CancellationToken cancellationToken = default);
}
