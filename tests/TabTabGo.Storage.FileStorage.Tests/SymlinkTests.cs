using System.Runtime.Versioning;
using System.Text;
using TabTabGo.Storage.Exceptions;
using Xunit;

namespace TabTabGo.Storage.FileStorage.Tests;

// QA: a symlink planted inside a container directory must not let reads, writes or deletes leave the root.
[UnsupportedOSPlatform("windows")]
public class SymlinkTests : IDisposable
{
    private readonly FileStorageHarness _h = new();
    private readonly string _outside;

    public SymlinkTests()
    {
        _outside = Path.Combine(Path.GetTempPath(), "ttg-outside-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_outside);
        File.WriteAllText(Path.Combine(_outside, "secret.txt"), "TOP SECRET");
    }

    public void Dispose()
    {
        _h.Dispose();
        Directory.Delete(_outside, recursive: true);
    }

    private async Task PlantDirectoryLink()
    {
        await _h.Provider.StoreAsync("seed.txt", [1], "Image");
        Directory.CreateSymbolicLink(_h.Disk("store", "Image", "evil"), _outside);
    }

    [Fact]
    public async Task Store_ThroughPlantedDirectoryLink_IsRejected_AndWritesNothingOutside()
    {
        await PlantDirectoryLink();

        var ex = await Assert.ThrowsAsync<StorageException>(() => _h.Provider.StoreAsync("evil/dropped.txt", Encoding.UTF8.GetBytes("x"), "Image"));

        Assert.Equal(System.Net.HttpStatusCode.BadRequest, ex.HttpStatusCode);
        Assert.False(File.Exists(Path.Combine(_outside, "dropped.txt")));
    }

    [Fact]
    public async Task Retrieve_And_OpenRead_And_Exists_ThroughPlantedDirectoryLink_AreRejected()
    {
        await PlantDirectoryLink();

        await Assert.ThrowsAsync<StorageException>(() => _h.Provider.RetrieveAsync("evil/secret.txt", "Image"));
        await Assert.ThrowsAsync<StorageException>(() => _h.Provider.OpenReadAsync("evil/secret.txt", "Image"));
        await Assert.ThrowsAsync<StorageException>(() => _h.Provider.ExistsAsync("evil/secret.txt", "Image"));
    }

    [Fact]
    public async Task Retrieve_PlantedFileLink_IsRejected()
    {
        await _h.Provider.StoreAsync("seed.txt", [1], "Image");
        File.CreateSymbolicLink(_h.Disk("store", "Image", "link.txt"), Path.Combine(_outside, "secret.txt"));

        await Assert.ThrowsAsync<StorageException>(() => _h.Provider.RetrieveAsync("link.txt", "Image"));
    }

    [Fact]
    public async Task Delete_ThroughPlantedLink_IsRejected_AndTheTargetSurvives()
    {
        await PlantDirectoryLink();

        await Assert.ThrowsAsync<StorageException>(() => _h.Provider.DeleteAsync("evil/secret.txt", "Image"));

        Assert.True(File.Exists(Path.Combine(_outside, "secret.txt")));
    }

    [Fact]
    public async Task Store_Overwrite_OfAPlantedFileLink_DoesNotWriteThroughIt()
    {
        await _h.Provider.StoreAsync("seed.txt", [1], "Image");
        var target = Path.Combine(_outside, "secret.txt");
        File.CreateSymbolicLink(_h.Disk("store", "Image", "link.txt"), target);

        await Assert.ThrowsAsync<StorageException>(() => _h.Provider.StoreAsync("link.txt", Encoding.UTF8.GetBytes("pwned"), "Image"));

        Assert.Equal("TOP SECRET", File.ReadAllText(target));
    }

    [Fact]
    public async Task ARootThatIsItselfALink_StillWorks()
    {
        var realRoot = Path.Combine(_outside, "real-root");
        Directory.CreateDirectory(realRoot);
        Directory.CreateSymbolicLink(_h.Disk("store"), realRoot);

        var path = await _h.Provider.StoreAsync("ok.txt", Encoding.UTF8.GetBytes("fine"), "Image");

        await using var stream = await _h.Provider.RetrieveAsync(path, "Image");
        Assert.Equal("fine", new StreamReader(stream!).ReadToEnd());
    }
}
