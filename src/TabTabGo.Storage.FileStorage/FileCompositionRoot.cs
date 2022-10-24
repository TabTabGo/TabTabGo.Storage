using LightInject;

namespace TabTabGo.Storage.FileStorage;

public class FileCompositionRoot : ICompositionRoot
{
    public void Compose(IServiceRegistry serviceRegistry)
    {
        serviceRegistry.Register(typeof(IStorageProvider), typeof(FileStorageProvider), "FileStorageEngine");
    }
}