using Microsoft.Extensions.Logging;
using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Linq.Expressions;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using TabTabGo.Core.Enums;
using TabTabGo.Core.Exceptions;
using TabTabGo.Core.Extensions;
using TabTabGo.Core.Infrastructure.Data;
using TabTabGo.Core.Services;
using TabTabGo.Storage;
using TabTabGo.Storage.Entities;
using TabTabGo.Storage.Enums;
using TabTabGo.Storage.Services;
using TabTabGo.Storage.ViewModels;


namespace TabTabGo.Storage.Services;

public class FileService<TFile> : BaseService<TFile, long>, IFileService<TFile> where TFile : class, IFile
{
    //private readonly IPathService _pathService;
    private readonly IImageService _imageService;
    private readonly IStorageProvider _storageProvider;

    public FileService(
        IUnitOfWork unitOfWork,
        ILogger<FileService<TFile>> logger,
        //IPathService pathService,
        IStorageProvider storageProvider,
        IImageService imageService) : base(unitOfWork, logger)
    {
        //_pathService = pathService;
        _imageService = imageService;
        _storageProvider = storageProvider;
    }

    protected override Expression<Func<TFile, bool>> GetKeyPredicate(long id) => entity => entity.FileId == id;
    protected override long GetKey(TFile entity) => entity.FileId;

    public async Task<FileResult> GetImage(long id, ImageSize size,
        CancellationToken cancellationToken = default(CancellationToken))
    {
        await LoadEntityAsync(id, cancellationToken);

        if (CurrentEntity == null || CurrentEntity.FileType != FileType.Image)
            throw new FileNotFoundException($@"Image id {id} not found.");
        /*
         * TODO: to support custom images for different sizes (not a resize of the default image) do something here.
         * Perhaps we can store the custom/ override image path in extraProperties.
        */

        string fileNameWithSize = (size == ImageSize.Default)
            ? CurrentEntity.FilePath
            : Path.Combine(size.ToString(), CurrentEntity.FilePath);
        var stream = await _storageProvider.RetrieveAsync(fileNameWithSize, CurrentEntity.FileType.ToString());
        if (stream == null)
        {
            //if default size exists, try to resize it to the desired size
            stream = await _storageProvider.RetrieveAsync(CurrentEntity.FilePath,
                CurrentEntity.FileType.ToString());

            //if default size exists, try to resize it to the desired size
            if (stream != null)
            {
                Size sizePreset = size.GetSize();
                try
                {
                    // Let us ignore any resize of images on read for now 
                    //_imageService.Resize(defaultSizePhysicalPath, sizePreset, fullPhysicalPath, CurrentEntity.FileType, true, null);
                }
                catch (Exception x)
                {
                    if (x is TTGException) throw x;

                    throw new TTGException(
                        string.Format("Image resize failed. width: {0} height: {1} outputPath {2}",
                            sizePreset.Width, sizePreset.Height, CurrentEntity.FilePath),
                        code: "IMAGE_RESIZE_FAILED", inner: x);
                }
            }
            else
            {
                this.Logger.LogError($@"Image file for Id {id} not found. file {CurrentEntity.FilePath}");
                throw new FileNotFoundException($@"Image file forId {id} not found.");
            }
        }

        //caller should take care of closing the stream
        return new FileResult()
        {
            ContentType = CurrentEntity.OriginalMediaType,
            Stream = stream
        };
    }

    public async Task<FileResult> GetFile(long id, CancellationToken cancellationToken = default(CancellationToken))
    {
        await LoadEntityAsync(id, cancellationToken);

        if (CurrentEntity == null)
            throw new FileNotFoundException($@"File id {id} not found.");

        var stream =
            await _storageProvider.RetrieveAsync(CurrentEntity.FilePath, CurrentEntity.FileType.ToString());
        if (stream == null)
            throw new FileNotFoundException($@"file for if {id} not found.");

        return new FileResult()
        {
            ContentType = CurrentEntity.OriginalMediaType,
            Stream = stream
        };
    }

    public override async Task<TFile> Create(TFile entity,
        CancellationToken cancellationToken = default(CancellationToken))
    {
        if (entity.FormFile == null)
            throw new ArgumentNullException(nameof(entity.FormFile), "Select a file to upload.");
        else
            await SaveFile(entity);
        var newEntity = await CurrentRepository.InsertAsync(entity, cancellationToken);
        //var updatedRefResult = await UpdateReferenceType(newEntity);
        await _unitOfWork.SaveChangesAsync(cancellationToken: cancellationToken);
        return newEntity;
    }

    private async Task<TFile> UpdateInternal(long id, TFile entity, JObject jentity,
        CancellationToken cancellationToken)
    {
        var currentModel = await CurrentRepository.FirstOrDefaultAsync(mf => mf, mf => mf.FileId == id,
            flags: QueryFlags.DisableTracking, cancellationToken: cancellationToken);
        string oldFilePath = currentModel.FilePath;

        if (entity == null && jentity != null)
        {
            jentity["FileId"] = currentModel.FileId;
            jentity.Populate(currentModel);
            entity = currentModel;
        }

        if (entity.FormFile != null)
            await SaveFile(entity);

        entity.FileId = id;
        var updatedEntity = await CurrentRepository.UpdateAsync(entity, cancellationToken);
        //var updatedRefResult = await UpdateReferenceType(updatedEntity);
        await _unitOfWork.SaveChangesAsync(cancellationToken: cancellationToken);
        if (entity.FormFile != null && !string.IsNullOrEmpty(oldFilePath))
        {
            await _storageProvider.DeleteAsync(oldFilePath, entity.FileType.ToString());
            if (entity.FileType == FileType.Image)
            {
                await DeleteImage(oldFilePath);
            }
        }

        return updatedEntity;
    }

    public override async Task<TFile> Update(long id, JObject entity,
        CancellationToken cancellationToken = default(CancellationToken))
    {
        return await UpdateInternal(id, null, entity, cancellationToken);
    }

    public override async Task<TFile> Update(long id, TFile entity,
        CancellationToken cancellationToken = default(CancellationToken))
    {
        return await UpdateInternal(id, entity, null, cancellationToken);
    }

    private async Task SaveFile(TFile entity)
    {
        var memoryStream = new MemoryStream();
        await entity.FormFile.CopyToAsync(memoryStream);
        memoryStream.Flush();
        memoryStream.Seek(0, SeekOrigin.Begin);
        var extension = Path.GetExtension(entity.FormFile.FileName);
        var fileRelativePath =
            await _storageProvider.StoreAsync(memoryStream.ToArray(), extension, entity.FileType.ToString());
        var rootDirectory = _storageProvider.GetRootDirectory(entity.FileType.ToString());

        long? fileSize = memoryStream.Length;
        //Post save file should move to background process
        switch (entity.FileType)
        {
            case FileType.Image:
                await SaveImage(memoryStream, rootDirectory, fileRelativePath, extension);
                break;
        }

        entity.OriginalFileName = entity.FormFile.FileName;
        entity.OriginalMediaType = entity.FormFile.ContentType;
        entity.FilePath = fileRelativePath;
        entity.FileSize = fileSize;
        entity.FileExtension = extension;
    }

    protected override Task PostCreate(TFile entity, CancellationToken cancellationToken = new CancellationToken())
    {
        return base.PostCreate(entity, cancellationToken);
    }

    //private void SaveImage(string uploadedPath, string outputDirectory, string defaultFileNameWithoutExtension, string fileType)
    private async Task SaveImage(Stream stream, string rootDirectory, string sourceImagePath, string extension)
    {
        foreach (ImageSize imageSize in Enum.GetValues(typeof(ImageSize)))
        {
            if (imageSize == ImageSize.Default) continue;
            var outputMemoryStream = new MemoryStream();
            var sizeSpecificFullPath = Path.Combine(imageSize.ToString(), sourceImagePath);
            var size = imageSize.GetSize();
            stream.Seek(0, SeekOrigin.Begin);
            _imageService.Resize(stream, size, outputMemoryStream, extension, true, null);
            await _storageProvider.StoreAsync(sizeSpecificFullPath, outputMemoryStream.ToArray(),
                FileType.Image.ToString());
            await outputMemoryStream.DisposeAsync();
        }
    }

    private async Task DeleteImage(string sourceImagePath)
    {
        foreach (ImageSize imageSize in Enum.GetValues(typeof(ImageSize)))
        {
            if (imageSize == ImageSize.Default) continue;
            var sizeSpecificFullPath = Path.Combine(imageSize.ToString(), sourceImagePath);
            await _storageProvider.DeleteAsync(sizeSpecificFullPath, FileType.Image.ToString());
        }
    }

    public async Task Delete(Expression<Func<TFile, bool>> query,
        CancellationToken cancellationToken = default(CancellationToken))
    {
        var filesForDeleting =
            await this.CurrentRepository.GetAsync(filter: query, cancellationToken: cancellationToken);
        foreach (var file in filesForDeleting)
        {
            this.CurrentRepository.Delete(file);
            await this.DeleteImage(file.FilePath);
        }

        await this._unitOfWork.SaveChangesAsync(cancellationToken: cancellationToken);
    }
}