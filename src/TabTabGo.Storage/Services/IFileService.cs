using System;
using System.Linq.Expressions;

using System.Threading;
using System.Threading.Tasks;
using TabTabGo.Core.Services;
using TabTabGo.Storage.Entities;
using TabTabGo.Storage.Enums;
using TabTabGo.Storage.ViewModels;

namespace TabTabGo.Storage.Services;

public interface IFileService<TFile> : IBaseService<TFile, long> where TFile : class, IFile
{
    Task Delete(Expression<Func<TFile, bool>> query, CancellationToken cancellationToken = default(CancellationToken));

    Task<FileResult> GetImage(long id, ImageSize size = ImageSize.Default,
        CancellationToken cancellationToken = default(CancellationToken));

    Task<FileResult> GetFile(long id, CancellationToken cancellationToken = default(CancellationToken));
}