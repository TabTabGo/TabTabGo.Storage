
using Microsoft.Extensions.Logging;
using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Mvc;
using TabTabGo.Core.Infrastructure.Data;
using TabTabGo.Storage.Entities;
using TabTabGo.Storage.Enums;
using TabTabGo.Storage.Services;
using TabTabGo.Core.WebApi;
namespace TabTabGo.Storage.WebApi.Controllers
{
    public class FileBaseController<TFile> : <TFile, long, IFileService<TFile>> where TFile : class, IFile
    {
        public FileBaseController(IFileService<TFile> service, ILogger<FileBaseController<TFile>> logger, IUnitOfWork unitOfWork) : base(service, logger, unitOfWork)
        {

        }


        [HttpGet]
        [Route("{id}/Image/{size}")]
        public virtual async Task<IActionResult> GetImage(int id, ImageSize size = ImageSize.Default)
        {
            if (!ModelState.IsValid)
            {
                return BadRequest(ModelState);
            }

            try
            {
                var result = await _currentService.GetImage(id, size);
                return File(result.Stream, result.ContentType);
            }
            catch (Exception x)
            {
                _logger.LogError(x, "Failed get Image file.");
                if (x is System.IO.FileNotFoundException)
                    return NotFound();
                else
                    throw x;
            }
        }

        public override async Task<IActionResult> Get(long id, [FromQuery] DateTimeOffset? lastUpdatedDate = null, [FromQuery] string expand = null, CancellationToken cancellationToken = default(CancellationToken))
        {
            if (!ModelState.IsValid)
            {
                return BadRequest(ModelState);
            }

            try
            {
                var result = await _currentService.GetFile(id);
                //can use enableRangeProcessing overload of File call below, once ASP.NET Core 2.1 released
                //ref: https://github.com/aspnet/Mvc/pull/6895#issuecomment-356608038
                return new FileStreamResult(result.Stream, result.ContentType);
            }
            catch (Exception x)
            {
                _logger.LogError(x, "Failed get file.");
                if (x is System.IO.FileNotFoundException)
                    return NotFound();
                else
                    throw x;
            }
        }

        [HttpPost]
        [Route("{id}/Update")]
        public new async Task<IActionResult> UpdateFile(long id, [FromForm] TFile entity, CancellationToken cancellationToken = default(CancellationToken))
        {
            if (!ModelState.IsValid)
            {
                return BadRequest(ModelState);
            }
            if (!await _currentService.Exists(id, cancellationToken))
            {
                return NotFound();
            }

            var toUpdateInstance = _currentService.Update(id, entity, cancellationToken);
            return Ok(toUpdateInstance);
        }

        [HttpPost]
        [Route("Upload")]
        public new async Task<IActionResult> CreateFile([FromForm] TFile entity, CancellationToken cancellationToken = default(CancellationToken))
        {
            if (!ModelState.IsValid)
            {
                return BadRequest(ModelState);
            }

            var newFile = await _currentService.Create(entity, cancellationToken);
            return Created("", newFile);
        }


    }
}
