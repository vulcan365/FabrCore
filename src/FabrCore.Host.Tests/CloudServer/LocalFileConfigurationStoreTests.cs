using FabrCore.Core;
using FabrCore.Host.Services;
using Microsoft.Extensions.Logging.Abstractions;

namespace FabrCore.Host.Tests.CloudServer;

[TestClass]
public sealed class LocalFileConfigurationStoreTests
{
    [TestMethod]
    public async Task Get_WhenFileMissing_CreatesEmptyFile()
    {
        var contentRoot = CreateTempDir();
        try
        {
            var store = CreateStore(contentRoot);
            var config = await store.GetConfigurationAsync();

            Assert.AreEqual(0, config.ModelConfigurations.Count);
            Assert.AreEqual(0, config.ApiKeys.Count);
            Assert.IsTrue(File.Exists(Path.Combine(contentRoot, "fabrcore.json")),
                "Missing fabrcore.json should be created empty, matching the original controller behavior.");
        }
        finally
        {
            Directory.Delete(contentRoot, recursive: true);
        }
    }

    [TestMethod]
    public async Task SaveAndGet_RoundTrips()
    {
        var contentRoot = CreateTempDir();
        try
        {
            var store = CreateStore(contentRoot);
            await store.SaveConfigurationAsync(new FabrCoreConfiguration
            {
                ModelConfigurations =
                [
                    new ModelConfiguration
                    {
                        Name = "default",
                        Provider = "OpenAI",
                        Uri = "https://api.openai.test",
                        Model = "gpt-test",
                        ApiKeyAlias = "openai"
                    }
                ],
                ApiKeys = [new ApiKeyConfiguration { Alias = "openai", Value = "sk-test" }]
            });

            var roundTripped = await store.GetConfigurationAsync();
            Assert.AreEqual(1, roundTripped.ModelConfigurations.Count);
            Assert.AreEqual("default", roundTripped.ModelConfigurations[0].Name);
            Assert.AreEqual("sk-test", roundTripped.ApiKeys[0].Value);
            Assert.IsTrue(store.SupportsWrites);
        }
        finally
        {
            Directory.Delete(contentRoot, recursive: true);
        }
    }

    private static string CreateTempDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"fabrcore-store-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        return dir;
    }

    private static LocalFileConfigurationStore CreateStore(string contentRoot) =>
        new(NullLogger<LocalFileConfigurationStore>.Instance, new TestHostEnvironment(contentRoot));
}
