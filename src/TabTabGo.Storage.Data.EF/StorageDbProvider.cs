using Microsoft.Extensions.Logging;
using TabTabGo.Data.EF;

namespace TabTabGo.Storage.Data.EF;

public class StorageDbProvider : UnitOfWork
{
    public StorageDbProvider(StorageDbContext dbContext, ILogger<StorageDbProvider> logger) : base(dbContext, logger)
    {
    }
}