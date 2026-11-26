using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.OData;
using Microsoft.AspNetCore.OData.Extensions;
using Microsoft.AspNetCore.OData.Query;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.OData.ModelBuilder;
using Microsoft.OData.UriParser;
using TabTabGo.Storage.TestKit;
using Xunit;
using File = TabTabGo.Storage.Entities.File;

namespace TabTabGo.Storage.Services.Tests;

// R-12: the OData read paths hide soft-deleted files, proven by behaviour with a real ODataQueryOptions<File>.
public class ODataSoftDeleteTests : IDisposable
{
    private readonly FileServiceHarness _h = new();
    private readonly IServiceProvider _services;
    private readonly Microsoft.OData.Edm.IEdmModel _model;

    public ODataSoftDeleteTests()
    {
        var builder = new ODataConventionModelBuilder();
        builder.EntitySet<File>("Files");
        _model = builder.GetEdmModel();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddRouting();
        services.AddControllers().AddOData(o => o.EnableQueryFeatures().AddRouteComponents("odata", _model));
        _services = services.BuildServiceProvider();
    }

    public void Dispose() => _h.Dispose();

    private ODataQueryOptions<File> Options(string query)
    {
        var http = new DefaultHttpContext { RequestServices = _services };
        http.Request.Method = "GET";
        http.Request.Scheme = "http";
        http.Request.Host = new HostString("localhost");
        http.Request.Path = "/odata/Files";
        http.Request.QueryString = new QueryString(query);
        var feature = http.Request.ODataFeature();
        feature.Model = _model;
        feature.RoutePrefix = "odata";
        feature.Path = new ODataPath(new EntitySetSegment(_model.EntityContainer.FindEntitySet("Files")));
        var context = new ODataQueryContext(_model, typeof(File), feature.Path);
        return new ODataQueryOptions<File>(context, http.Request);
    }

    private async Task<(long Kept, long Gone)> SeedAsync()
    {
        var kept = await _h.UploadTextAsync();
        var gone = await _h.UploadTextAsync();
        await _h.Service.Delete(gone.FileId);
        return (kept.FileId, gone.FileId);
    }

    [Fact]
    public async Task GetPageList_Odata_DoesNotReturnSoftDeletedFiles()
    {
        var (kept, gone) = await SeedAsync();

        var page = await _h.Service.GetPageList(Options("?$top=50"));

        Assert.Contains(page.Items, f => f.FileId == kept);
        Assert.DoesNotContain(page.Items, f => f.FileId == gone);
        Assert.Equal(1, page.TotalItems);
    }

    [Fact]
    public async Task GetPageList_Odata_WithAFilterThatWouldMatchTheDeletedRow_StillHidesIt()
    {
        var (_, gone) = await SeedAsync();

        var page = await _h.Service.GetPageList(Options($"?$filter=FileId eq {gone}"));

        Assert.Empty(page.Items);
    }

    [Fact]
    public async Task GetViewModels_Odata_DoesNotReturnSoftDeletedFiles()
    {
        var (kept, gone) = await SeedAsync();

        var page = await _h.Service.GetViewModels<File>(Options("?$top=50"), f => f);

        Assert.Contains(page.Items, f => f.FileId == kept);
        Assert.DoesNotContain(page.Items, f => f.FileId == gone);
    }

    [Fact]
    public async Task TheCallersOwnFixCriteria_IsStillApplied_NextToTheActiveFilter()
    {
        var (kept, _) = await SeedAsync();
        var other = await _h.UploadTextAsync();

        var page = await _h.Service.GetPageList(Options("?$top=50"), f => f.FileId == other.FileId);

        Assert.Equal([other.FileId], page.Items.Select(f => f.FileId));
        Assert.NotEqual(kept, other.FileId);
    }
}
