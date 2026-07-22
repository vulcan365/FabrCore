using FabrCore.Host;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Orleans;

namespace FabrCore.Client.Orleans.Tests;

[TestClass]
public sealed class LocalhostConnectivityTests
{
    [TestMethod]
    [TestCategory("Integration")]
    public async Task DiscoveryBackedClient_GrainRpcAndObserverCallbackWork()
    {
        var clusterId = $"discovery-test-{Guid.NewGuid():N}";
        var webBuilder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            EnvironmentName = Environments.Development
        });
        webBuilder.WebHost.UseUrls("http://127.0.0.1:0");
        webBuilder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Orleans:ClusterId"] = clusterId,
            ["Orleans:ServiceId"] = "fabrcore-discovery-tests",
            ["Orleans:ClusteringMode"] = "Localhost",
            ["FabrCore:Host:GatewayDiscovery:RequireOrleansTls"] = "false"
        });
        webBuilder.AddFabrCoreServer(new FabrCoreServerOptions
        {
            AdditionalAssemblies = [typeof(GatewayDiscoveryTestGrain).Assembly]
        });

        await using var webApp = webBuilder.Build();
        webApp.UseFabrCoreServer();
        await webApp.StartAsync();

        var server = webApp.Services.GetRequiredService<IServer>();
        var hostUrl = server.Features.Get<IServerAddressesFeature>()!.Addresses.Single();
        using var discoveryHttpClient = new HttpClient();
        var clientBuilder = Microsoft.Extensions.Hosting.Host.CreateApplicationBuilder();
        await clientBuilder.AddFabrCoreOrleansClientAsync(
            discoveryHttpClient,
            options =>
            {
                options.FabrCoreHostUrl = hostUrl;
                options.AllowInsecureOrleansTransport = true;
            });

        using var clientHost = clientBuilder.Build();
        await clientHost.StartAsync();
        try
        {
            var clusterClient = clientHost.Services.GetRequiredService<IClusterClient>();
            var grain = clusterClient.GetGrain<IGatewayDiscoveryTestGrain>("rpc-and-observer");
            Assert.AreEqual("echo:hello", await grain.Echo("hello"));

            var observer = new GatewayDiscoveryTestObserver();
            var observerReference = clusterClient.CreateObjectReference<IGatewayDiscoveryTestObserver>(observer);
            try
            {
                await grain.Subscribe(observerReference);
                await grain.Publish("observer-message");

                var callback = await observer.Message.Task.WaitAsync(TimeSpan.FromSeconds(10));
                Assert.AreEqual("observer-message", callback);
            }
            finally
            {
                clusterClient.DeleteObjectReference<IGatewayDiscoveryTestObserver>(observerReference);
            }
        }
        finally
        {
            await clientHost.StopAsync();
            await webApp.StopAsync();
        }
    }

    private sealed class GatewayDiscoveryTestObserver : IGatewayDiscoveryTestObserver
    {
        public TaskCompletionSource<string> Message { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public void OnMessage(string message) => Message.TrySetResult(message);
    }
}

public interface IGatewayDiscoveryTestObserver : IGrainObserver
{
    void OnMessage(string message);
}

public interface IGatewayDiscoveryTestGrain : IGrainWithStringKey
{
    Task<string> Echo(string message);
    Task Subscribe(IGatewayDiscoveryTestObserver observer);
    Task Publish(string message);
}

public sealed class GatewayDiscoveryTestGrain : Grain, IGatewayDiscoveryTestGrain
{
    private IGatewayDiscoveryTestObserver? _observer;

    public Task<string> Echo(string message) => Task.FromResult($"echo:{message}");

    public Task Subscribe(IGatewayDiscoveryTestObserver observer)
    {
        _observer = observer;
        return Task.CompletedTask;
    }

    public Task Publish(string message)
    {
        _observer?.OnMessage(message);
        return Task.CompletedTask;
    }
}
