using Amazon.S3;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Moq;
using Xunit;

namespace TabTabGo.Storage.Aws.Tests;

public class S3DiTests
{
    private static ServiceCollection Services()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        return services;
    }

    private static ServiceProvider Build(IServiceCollection services) =>
        services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true, ValidateOnBuild = true });

    private static void Valid(S3Configuration c)
    {
        c.BucketName = "bucket";
        c.Region = "eu-west-1";
        c.AccessKey = "ak-test";
        c.SecretKey = "sk-test";
    }

    [Fact]
    public void AddTabTabGoS3Storage_ClientIsSingleton()
    {
        var services = Services();
        services.AddTabTabGoS3Storage(Valid);

        using var provider = Build(services);

        Assert.Same(provider.GetRequiredService<IAmazonS3>(), provider.GetRequiredService<IAmazonS3>());
        var storage = provider.GetRequiredService<IStorageProvider>();
        Assert.IsType<S3StorageProvider>(storage);
        Assert.Same(storage, provider.GetRequiredService<IStorageProvider>());
        Assert.Same(storage, provider.GetRequiredService<S3StorageProvider>());
    }

    [Fact]
    public void AddTabTabGoS3Storage_BindsFromConfigurationSection()
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["aws:BucketName"] = "cfg-bucket",
            ["aws:Region"] = "us-east-1",
            ["aws:ExpirationMinutes"] = "15",
            ["aws:ForcePathStyle"] = "true",
        }).Build();
        var services = Services();
        services.AddTabTabGoS3Storage(configuration.GetSection("aws"));

        using var provider = Build(services);

        var options = provider.GetRequiredService<IOptions<S3Configuration>>().Value;
        Assert.Equal("cfg-bucket", options.BucketName);
        Assert.Equal(15, options.ExpirationMinutes);
        Assert.True(options.ForcePathStyle);
        Assert.Null(options.AccessKey);
    }

    [Fact]
    public void AddTabTabGoS3Storage_DoesNotReplaceAHostRegisteredClient()
    {
        var mine = new Mock<IAmazonS3>().Object;
        var services = Services();
        services.AddSingleton(mine);
        services.AddTabTabGoS3Storage(Valid);

        using var provider = Build(services);

        Assert.Same(mine, provider.GetRequiredService<IAmazonS3>());
    }

    [Theory]
    [InlineData("", "eu-west-1", null, "BucketName")]
    [InlineData("b", null, null, "Region")]
    public void Options_Invalid_FailValidation(string bucket, string? region, string? serviceUrl, string expected)
    {
        var services = Services();
        services.AddTabTabGoS3Storage(c =>
        {
            c.BucketName = bucket;
            c.Region = region;
            c.ServiceUrl = serviceUrl;
        });
        using var provider = services.BuildServiceProvider();

        var ex = Assert.Throws<OptionsValidationException>(() => provider.GetRequiredService<IOptions<S3Configuration>>().Value);

        Assert.Contains(expected, ex.Message);
    }

    [Fact]
    public void Options_ServiceUrlWithoutRegion_IsValid()
    {
        var services = Services();
        services.AddTabTabGoS3Storage(c =>
        {
            c.BucketName = "b";
            c.ServiceUrl = "http://localhost:4566";
        });
        using var provider = services.BuildServiceProvider();

        Assert.Equal("b", provider.GetRequiredService<IOptions<S3Configuration>>().Value.BucketName);
    }

    [Fact]
    public void AddTabTabGoS3Storage_NullArguments_Throw()
    {
        var services = new ServiceCollection();

        Assert.Throws<ArgumentNullException>(() => ((IServiceCollection)null!).AddTabTabGoS3Storage(_ => { }));
        Assert.Throws<ArgumentNullException>(() => services.AddTabTabGoS3Storage((Action<S3Configuration>)null!));
        Assert.Throws<ArgumentNullException>(() => services.AddTabTabGoS3Storage((IConfiguration)null!));
    }
}
