using FabrCore.Core;
using FabrCore.Core.CloudServer;
using FabrCore.Host.Services;
using FabrCore.Host.Services.CloudServer;
using FabrCore.Sdk;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Logging.Abstractions;

namespace FabrCore.Host.Tests;

[TestClass]
public sealed class ModelConfigurationResolverTests
{
    [TestMethod]
    public async Task LocalFileResolver_ReturnsModelAndApiKey()
    {
        var contentRoot = CreateTemporaryContentRoot();
        try
        {
            await File.WriteAllTextAsync(
                Path.Combine(contentRoot, "fabrcore.json"),
                ConfigurationJson());
            var store = new LocalFileConfigurationStore(
                NullLogger<LocalFileConfigurationStore>.Instance,
                new TestWebHostEnvironment(contentRoot));
            var resolver = new ConfigurationStoreModelConfigurationResolver(store);

            var model = await resolver.GetModelConfigurationAsync("default");
            var apiKey = await resolver.GetApiKeyAsync("provider");

            Assert.AreEqual("gpt-test", model.Model);
            Assert.AreEqual("provider-key", apiKey);
        }
        finally
        {
            Directory.Delete(contentRoot, recursive: true);
        }
    }

    [TestMethod]
    public async Task CloudServerResolver_ReturnsAppliedSnapshot()
    {
        var store = new CloudServerConfigurationStore(
            NullLogger<CloudServerConfigurationStore>.Instance);
        store.ApplySnapshot(new CloudConfigurationEnvelope
        {
            ConfigurationVersion = "v1",
            IssuedAt = DateTimeOffset.UtcNow,
            Configuration = CreateConfiguration()
        });
        var resolver = new ConfigurationStoreModelConfigurationResolver(store);

        var model = await resolver.GetModelConfigurationAsync("default");
        var apiKey = await resolver.GetApiKeyAsync("provider");

        Assert.AreEqual("gpt-test", model.Model);
        Assert.AreEqual("provider-key", apiKey);
    }

    [TestMethod]
    public async Task AddFabrCoreServices_CustomStoreUsesInProcessResolver()
    {
        var builder = WebApplication.CreateBuilder();
        builder.AddFabrCoreServices(
            new FabrCoreServerOptions().UseConfigurationStore<CustomConfigurationStore>());

        await using var provider = builder.Services.BuildServiceProvider();
        var chatClientService = provider.GetRequiredService<IFabrCoreChatClientService>();

        var model = await chatClientService.GetModelConfigurationAsync("default");

        Assert.AreEqual("gpt-test", model.Model);
        Assert.IsInstanceOfType<ConfigurationStoreModelConfigurationResolver>(
            provider.GetRequiredService<IFabrCoreModelConfigurationResolver>());
    }

    [TestMethod]
    public async Task StoreResolver_MissingEntriesProduceExplicitErrors()
    {
        var resolver = new ConfigurationStoreModelConfigurationResolver(
            new InMemoryConfigurationStore(new FabrCoreConfiguration()));

        var modelException = await Assert.ThrowsAsync<KeyNotFoundException>(
            () => resolver.GetModelConfigurationAsync("missing"));
        var keyException = await Assert.ThrowsAsync<KeyNotFoundException>(
            () => resolver.GetApiKeyAsync("missing-key"));

        StringAssert.Contains(modelException.Message, "missing");
        StringAssert.Contains(keyException.Message, "missing-key");
    }

    private static FabrCoreConfiguration CreateConfiguration() => new()
    {
        ModelConfigurations =
        [
            new ModelConfiguration
            {
                Name = "default",
                Provider = "OpenAI",
                Uri = "https://openai.test/v1",
                Model = "gpt-test",
                ApiKeyAlias = "provider"
            }
        ],
        ApiKeys = [new ApiKeyConfiguration { Alias = "provider", Value = "provider-key" }]
    };

    private static string ConfigurationJson() =>
        """
        {
          "ModelConfigurations": [
            {
              "Name": "default",
              "Provider": "OpenAI",
              "Uri": "https://openai.test/v1",
              "Model": "gpt-test",
              "ApiKeyAlias": "provider"
            }
          ],
          "ApiKeys": [
            { "Alias": "provider", "Value": "provider-key" }
          ]
        }
        """;

    private static string CreateTemporaryContentRoot()
    {
        var path = Path.Combine(Path.GetTempPath(), $"fabrcore-model-resolver-{Guid.NewGuid():N}");
        Directory.CreateDirectory(path);
        return path;
    }

    private sealed class CustomConfigurationStore : InMemoryConfigurationStore
    {
        public CustomConfigurationStore() : base(CreateConfiguration())
        {
        }
    }

    private class InMemoryConfigurationStore(FabrCoreConfiguration configuration)
        : IFabrCoreConfigurationStore
    {
        public bool SupportsWrites => false;

        public Task<FabrCoreConfiguration> GetConfigurationAsync(
            CancellationToken cancellationToken = default) => Task.FromResult(configuration);

        public Task SaveConfigurationAsync(
            FabrCoreConfiguration configuration,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }

    private sealed class TestWebHostEnvironment(string contentRootPath) : IWebHostEnvironment
    {
        public string ApplicationName { get; set; } = "FabrCore.Host.Tests";
        public IFileProvider WebRootFileProvider { get; set; } = new NullFileProvider();
        public string WebRootPath { get; set; } = contentRootPath;
        public string EnvironmentName { get; set; } = "Test";
        public string ContentRootPath { get; set; } = contentRootPath;
        public IFileProvider ContentRootFileProvider { get; set; } = new PhysicalFileProvider(contentRootPath);
    }
}
