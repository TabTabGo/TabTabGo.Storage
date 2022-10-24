using LightInject;

namespace TabTabGo.Storage.AzureStorage;

public class AzureCompositionRoot : ICompositionRoot
{
    public void Compose(IServiceRegistry serviceRegistry)
    {
        serviceRegistry.Register(typeof(IStorageProvider), typeof(BlobStorageProvider), "AzureStorageEngine");
    }
}