using FabrCore.Host.Configuration;
using FabrCore.Host.Services.CloudServer;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using System.Net;
using System.Text.Json;

namespace FabrCore.Host.Tests.CloudServer;

[TestClass]
public sealed class CloudServerSyncServiceTests
{
    private static object Envelope(string version) => new
    {
        schemaVersion = 1,
        configurationVersion = version,
        issuedAt = DateTimeOffset.UtcNow,
        configuration = new
        {
            modelConfigurations = new[]
            {
                new
                {
                    name = "default",
                    provider = "OpenAI",
                    uri = "https://api.openai.test",
                    model = "gpt-test",
                    apiKeyAlias = "openai"
                }
            },
            apiKeys = new[] { new { alias = "openai", value = "sk-test" } }
        }
    };

    private sealed class Harness : IAsyncDisposable
    {
        public string ContentRoot { get; } = Path.Combine(Path.GetTempPath(), $"fabrcore-sync-{Guid.NewGuid():N}");
        public CloudServerConfigurationStore Store { get; } = new(NullLogger<CloudServerConfigurationStore>.Instance);
        public CloudConfigurationDiskCache DiskCache { get; private set; } = null!;
        public CloudServerSyncService Service { get; private set; } = null!;

        public static Harness Create(FakeCloudServerHandler handler, Action<CloudServerOptions>? configure = null)
        {
            var harness = new Harness();
            Directory.CreateDirectory(harness.ContentRoot);

            var options = CloudServerTestFactory.Options(o =>
            {
                o.RefreshInterval = TimeSpan.FromHours(1);
                o.Heartbeat.Enabled = false;
                configure?.Invoke(o);
            });
            var optionsWrapper = Microsoft.Extensions.Options.Options.Create(options);
            var environment = new TestHostEnvironment(harness.ContentRoot);

            harness.DiskCache = new CloudConfigurationDiskCache(
                optionsWrapper, environment, NullLogger<CloudConfigurationDiskCache>.Instance);
            harness.Service = new CloudServerSyncService(
                CloudServerTestFactory.ApiClient(handler, options),
                harness.Store,
                harness.DiskCache,
                optionsWrapper,
                new ServiceCollection().BuildServiceProvider(),
                NullLogger<CloudServerSyncService>.Instance);
            return harness;
        }

        public async ValueTask DisposeAsync()
        {
            try
            {
                await Service.StopAsync(CancellationToken.None);
            }
            catch
            {
                // Service may never have started.
            }

            Service.Dispose();
            Directory.Delete(ContentRoot, recursive: true);
        }
    }

    [TestMethod]
    public async Task StartAsync_Success_PopulatesStore_AndWritesDiskCache()
    {
        var handler = new FakeCloudServerHandler(_ =>
            Task.FromResult(FakeCloudServerHandler.Json(HttpStatusCode.OK, Envelope("v1"))));
        await using var harness = Harness.Create(handler);

        await harness.Service.StartAsync(CancellationToken.None);

        Assert.IsTrue(harness.Store.HasSnapshot);
        Assert.AreEqual("v1", harness.Store.CurrentConfigurationVersion);
        var config = await harness.Store.GetConfigurationAsync();
        Assert.AreEqual("default", config.ModelConfigurations.Single().Name);
        Assert.IsTrue(File.Exists(harness.DiskCache.CacheFilePath), "Successful fetch should write the disk cache.");
    }

    [TestMethod]
    public async Task StartAsync_Failure_WithDiskCache_UsesCachedSnapshot()
    {
        var failing = new FakeCloudServerHandler(_ =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)));
        await using var harness = Harness.Create(failing);

        var cachedJson = JsonSerializer.Serialize(Envelope("cached-v5"), JsonSerializerOptions.Web);
        await File.WriteAllTextAsync(harness.DiskCache.CacheFilePath, cachedJson);

        await harness.Service.StartAsync(CancellationToken.None);

        Assert.IsTrue(harness.Store.HasSnapshot);
        Assert.AreEqual("cached-v5", harness.Store.CurrentConfigurationVersion);
    }

    [TestMethod]
    public async Task StartAsync_Failure_NoCache_FailBehavior_Throws()
    {
        var failing = new FakeCloudServerHandler(_ =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)));
        await using var harness = Harness.Create(failing);

        var exception = await Assert.ThrowsExactlyAsync<InvalidOperationException>(
            () => harness.Service.StartAsync(CancellationToken.None));
        Assert.IsTrue(exception.Message.Contains("StartDegraded"), "Error should point at the degraded-start escape hatch.");
        Assert.IsFalse(harness.Store.HasSnapshot);
    }

    [TestMethod]
    public async Task StartAsync_Failure_NoCache_StartDegraded_ServesEmptyConfiguration()
    {
        var failing = new FakeCloudServerHandler(_ =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)));
        await using var harness = Harness.Create(failing, o => o.StartupFailureBehavior = CloudServerStartupFailureBehavior.StartDegraded);

        await harness.Service.StartAsync(CancellationToken.None);

        Assert.IsFalse(harness.Store.HasSnapshot);
        var config = await harness.Store.GetConfigurationAsync();
        Assert.AreEqual(0, config.ModelConfigurations.Count, "Degraded start serves an empty configuration (404s, not errors).");
    }

    [TestMethod]
    public async Task RefreshLoop_HotSwapsSnapshot_WhenVersionChanges()
    {
        var fetchCount = 0;
        var handler = new FakeCloudServerHandler(_ =>
        {
            var version = Interlocked.Increment(ref fetchCount) == 1 ? "v1" : "v2";
            return Task.FromResult(FakeCloudServerHandler.Json(HttpStatusCode.OK, Envelope(version)));
        });
        await using var harness = Harness.Create(handler, o => o.RefreshInterval = TimeSpan.FromMilliseconds(50));

        await harness.Service.StartAsync(CancellationToken.None);
        Assert.AreEqual("v1", harness.Store.CurrentConfigurationVersion);

        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (harness.Store.CurrentConfigurationVersion != "v2" && DateTime.UtcNow < deadline)
        {
            await Task.Delay(25);
        }

        Assert.AreEqual("v2", harness.Store.CurrentConfigurationVersion, "Refresh loop should hot-swap the snapshot.");
    }

    [TestMethod]
    public async Task HeartbeatLoop_RefreshRequested_TriggersImmediateFetch()
    {
        var configFetches = 0;
        var handler = new FakeCloudServerHandler(request =>
        {
            if (request.RequestUri!.AbsolutePath.EndsWith("/heartbeat"))
            {
                return Task.FromResult(FakeCloudServerHandler.Json(HttpStatusCode.OK, new { refreshRequested = true }));
            }

            var version = Interlocked.Increment(ref configFetches) == 1 ? "v1" : "v2";
            return Task.FromResult(FakeCloudServerHandler.Json(HttpStatusCode.OK, Envelope(version)));
        });
        await using var harness = Harness.Create(handler, o =>
        {
            o.RefreshInterval = TimeSpan.FromHours(1);
            o.Heartbeat.Enabled = true;
            o.Heartbeat.Interval = TimeSpan.FromMilliseconds(50);
        });

        await harness.Service.StartAsync(CancellationToken.None);
        Assert.AreEqual("v1", harness.Store.CurrentConfigurationVersion);

        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (harness.Store.CurrentConfigurationVersion != "v2" && DateTime.UtcNow < deadline)
        {
            await Task.Delay(25);
        }

        Assert.AreEqual("v2", harness.Store.CurrentConfigurationVersion,
            "A heartbeat response with refreshRequested should trigger an immediate configuration fetch.");
    }
}
