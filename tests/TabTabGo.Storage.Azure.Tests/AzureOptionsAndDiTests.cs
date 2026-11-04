using System.Text;
using Azure.Core;
using Azure.Storage.Blobs;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Moq;
using TabTabGo.Storage.AzureStorage;
using Xunit;

namespace TabTabGo.Storage.Azure.Tests;

public class ContainerNameTests
{
    // Z-9
    [Theory]
    [InlineData("Image", "image")]
    [InlineData("PDF", "pdf")]
    [InlineData("Word Document", "word-document")]
    [InlineData("ab", "ab0")]
    [InlineData("a", "a00")]
    [InlineData("a--b", "a-b")]
    [InlineData("-lead-and-trail-", "lead-and-trail")]
    [InlineData("under_score", "under-score")]
    [InlineData("UPPER_and Spaces!!", "upper-and-spaces")]
    [InlineData("unknown", "unknown")]
    [InlineData("-a-", "a00")]
    [InlineData("c4d5e6f7a8b9c0d1e2f3a4b5c6d7e8f9a0b1c2d3e4f5a6b7c8d9e0f1a2b3c4d5e6f7", "c4d5e6f7a8b9c0d1e2f3a4b5c6d7e8f9a0b1c2d3e4f5a6b7c8d9e0f1a2b3c4d")]
    public void ContainerName_Theory(string input, string expected) =>
        Assert.Equal(expected, ContainerNameNormalizer.Normalize(input));

    [Fact]
    public void ContainerName_TruncatedAtHyphen_IsTrimmed()
    {
        var input = new string('a', 62) + "-b";

        var name = ContainerNameNormalizer.Normalize(input);

        Assert.Equal(new string('a', 62), name);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("!!!")]
    [InlineData("---")]
    public void ContainerName_NothingValid_Throws(string? input) =>
        Assert.Throws<ArgumentException>(() => ContainerNameNormalizer.Normalize(input));
}

public class AzureOptionsTests
{
    private sealed class FakeCredential : TokenCredential
    {
        public override AccessToken GetToken(TokenRequestContext requestContext, CancellationToken cancellationToken) =>
            new("t", DateTimeOffset.UtcNow.AddHours(1));

        public override ValueTask<AccessToken> GetTokenAsync(TokenRequestContext requestContext, CancellationToken cancellationToken) =>
            new(GetToken(requestContext, cancellationToken));
    }

    private static readonly AzureBlobStorageOptionsValidator Validator = new();

    [Fact]
    public void Defaults()
    {
        var options = new AzureBlobStorageOptions();

        Assert.Equal(TimeSpan.FromHours(1), options.DefaultSasExpiry);
        Assert.True(options.LowercaseBlobNames);
        Assert.Null(options.ConnectionString);
        Assert.Equal("AzureStorageConnection", AzureBlobStorageOptions.LegacyConnectionStringName);
    }

    // Z-7
    [Fact]
    public void Options_NoSource_FailsValidationAtStart()
    {
        var result = Validator.Validate(null, new AzureBlobStorageOptions());

        Assert.True(result.Failed);
        Assert.Contains("ConnectionString", result.FailureMessage);
    }

    [Fact]
    public void Options_ServiceUriWithoutCredential_Fails() =>
        Assert.True(Validator.Validate(null, new AzureBlobStorageOptions { ServiceUri = new Uri("https://a.blob.core.windows.net") }).Failed);

    [Fact]
    public void Options_ConnectionString_Succeeds() =>
        Assert.True(Validator.Validate(null, new AzureBlobStorageOptions { ConnectionString = "x" }).Succeeded);

    [Fact]
    public void Options_ServiceUriAndCredential_Succeeds() =>
        Assert.True(Validator.Validate(null, new AzureBlobStorageOptions
        {
            ServiceUri = new Uri("https://a.blob.core.windows.net"), Credential = new FakeCredential(),
        }).Succeeded);

    [Fact]
    public void Validate_Null_Throws() => Assert.Throws<ArgumentNullException>(() => Validator.Validate(null, null!));

    [Fact]
    public void Factory_ConnectionString_And_TokenCredential_And_Nothing()
    {
        var key = Convert.ToBase64String(Encoding.UTF8.GetBytes(new string('k', 32)));
        var viaKey = AzureBlobClientFactory.Create(new AzureBlobStorageOptions
        {
            ConnectionString = $"DefaultEndpointsProtocol=https;AccountName=acct;AccountKey={key};EndpointSuffix=core.windows.net",
        });
        var viaToken = AzureBlobClientFactory.Create(new AzureBlobStorageOptions
        {
            ServiceUri = new Uri("https://acct2.blob.core.windows.net"), Credential = new FakeCredential(),
        });

        Assert.Equal("acct", viaKey.AccountName);
        Assert.Equal("acct2", viaToken.AccountName);
        Assert.Throws<InvalidOperationException>(() => AzureBlobClientFactory.Create(new AzureBlobStorageOptions()));
        Assert.Throws<ArgumentNullException>(() => AzureBlobClientFactory.Create(null!));
    }
}

public class AzureDiTests
{
    private static string ConnectionString()
    {
        var key = Convert.ToBase64String(Encoding.UTF8.GetBytes(new string('k', 32)));
        return $"DefaultEndpointsProtocol=https;AccountName=acct;AccountKey={key};EndpointSuffix=core.windows.net";
    }

    private static ServiceCollection Services()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        return services;
    }

    private static ServiceProvider Build(IServiceCollection services) =>
        services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true, ValidateOnBuild = true });

    private static void AssertSingleton(ServiceProvider provider)
    {
        var storage = provider.GetRequiredService<IStorageProvider>();
        Assert.IsType<BlobStorageProvider>(storage);
        Assert.Same(storage, provider.GetRequiredService<IStorageProvider>());
        Assert.Same(storage, provider.GetRequiredService<BlobStorageProvider>());
        Assert.Same(provider.GetRequiredService<BlobServiceClient>(), provider.GetRequiredService<BlobServiceClient>());
    }

    // Z-12
    [Fact]
    public void AddTabTabGoAzureBlobStorage_ResolvesSingleton_FromAction()
    {
        var services = Services();
        services.AddTabTabGoAzureBlobStorage(o => o.ConnectionString = ConnectionString());

        using var provider = Build(services);

        AssertSingleton(provider);
    }

    [Fact]
    public void AddTabTabGoAzureBlobStorage_ResolvesSingleton_FromSection()
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["AzureBlobStorage:ConnectionString"] = ConnectionString(),
            ["AzureBlobStorage:LowercaseBlobNames"] = "false",
            ["AzureBlobStorage:DefaultSasExpiry"] = "00:30:00",
        }).Build();
        var services = Services();
        services.AddTabTabGoAzureBlobStorage(configuration.GetSection("AzureBlobStorage"));

        using var provider = Build(services);

        AssertSingleton(provider);
        var options = provider.GetRequiredService<IOptions<AzureBlobStorageOptions>>().Value;
        Assert.False(options.LowercaseBlobNames);
        Assert.Equal(TimeSpan.FromMinutes(30), options.DefaultSasExpiry);
    }

    [Fact]
    public void AddTabTabGoAzureBlobStorage_ResolvesSingleton_FromTokenCredential()
    {
        var services = Services();
        services.AddTabTabGoAzureBlobStorage(new Uri("https://acct.blob.core.windows.net"), new Mock<TokenCredential>().Object);

        using var provider = Build(services);

        AssertSingleton(provider);
        Assert.Equal("acct", provider.GetRequiredService<BlobServiceClient>().AccountName);
    }

    [Fact]
    public void ConnectionString_LegacyKeyStillRead()
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["ConnectionStrings:AzureStorageConnection"] = ConnectionString(),
        }).Build();
        var services = Services();
        services.AddSingleton<IConfiguration>(configuration);
        services.AddTabTabGoAzureBlobStorage();

        using var provider = Build(services);

        Assert.Equal(ConnectionString(), provider.GetRequiredService<IOptions<AzureBlobStorageOptions>>().Value.ConnectionString);
        AssertSingleton(provider);
    }

    [Fact]
    public void ConnectionString_LegacyKey_DoesNotOverrideExplicitServiceUri()
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["ConnectionStrings:AzureStorageConnection"] = ConnectionString(),
        }).Build();
        var services = Services();
        services.AddSingleton<IConfiguration>(configuration);
        services.AddTabTabGoAzureBlobStorage(new Uri("https://acct.blob.core.windows.net"), new Mock<TokenCredential>().Object);

        using var provider = Build(services);

        Assert.Null(provider.GetRequiredService<IOptions<AzureBlobStorageOptions>>().Value.ConnectionString);
    }

    [Fact]
    public void NoSourceAnywhere_ResolvingOptionsFailsValidation()
    {
        var services = Services();
        services.AddTabTabGoAzureBlobStorage();
        using var provider = services.BuildServiceProvider();

        Assert.Throws<OptionsValidationException>(() => provider.GetRequiredService<IOptions<AzureBlobStorageOptions>>().Value);
    }

    [Fact]
    public void AddTabTabGoAzureBlobStorage_NullArguments_Throw()
    {
        var services = new ServiceCollection();

        Assert.Throws<ArgumentNullException>(() => ((IServiceCollection)null!).AddTabTabGoAzureBlobStorage());
        Assert.Throws<ArgumentNullException>(() => services.AddTabTabGoAzureBlobStorage((Action<AzureBlobStorageOptions>)null!));
        Assert.Throws<ArgumentNullException>(() => services.AddTabTabGoAzureBlobStorage((IConfiguration)null!));
        Assert.Throws<ArgumentNullException>(() => services.AddTabTabGoAzureBlobStorage(null!, new Mock<TokenCredential>().Object));
        Assert.Throws<ArgumentNullException>(() => services.AddTabTabGoAzureBlobStorage(new Uri("https://a.blob.core.windows.net"), null!));
    }
}
