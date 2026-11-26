using Microsoft.Extensions.Logging;
using TabTabGo.Data.EF;

namespace TabTabGo.Storage.Data.EF;

/// <summary>Unit of work over <see cref="StorageDbContext"/>.</summary>
public class StorageDbProvider : UnitOfWork
{
    /// <summary>Creates the unit of work.</summary>
    /// <param name="dbContext">The storage context.</param>
    /// <param name="logger">Logger.</param>
    public StorageDbProvider(StorageDbContext dbContext, ILogger<StorageDbProvider> logger) : base(dbContext, logger)
    {
    }
}
