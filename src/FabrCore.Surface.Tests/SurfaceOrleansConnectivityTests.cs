using System.Net;
using System.Text;
using FabrCore.Client.Orleans;
using FabrCore.Surface.Configuration;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Orleans;
using Orleans.Configuration;
using Orleans.Hosting;
using Xunit;

namespace FabrCore.Surface.Tests;

public sealed class SurfaceOrleansConnectivityTests
{
    [Fact]
    public async Task AddFabrCoreSurfaceAsyncDiscoversProviderNeutralGateways()
    {
        Uri? requestedUri = null;
        using var discoveryHttpClient = new HttpClient(new StubHttpMessageHandler(request =>
        {
            requestedUri = request.RequestUri;
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    """
                    {
                      "version": 1,
                      "clusterId": "surface-cluster",
                      "serviceId": "surface-service",
                      "gateways": ["gwy.tcp://127.0.0.1:30000/0"],
                      "refreshPeriodSeconds": 30,
                      "requireOrleansTls": false
                    }
                    """,
                    Encoding.UTF8,
                    "application/json")
            };
        }));

        var builder = Host.CreateApplicationBuilder();
        await builder.AddFabrCoreSurfaceAsync(
            discoveryHttpClient,
            configure: options => options.FabrCoreHostUrl = "https://host.example",
            configureClient: options => options.AllowInsecureOrleansTransport = true);

        using var host = builder.Build();
        var clusterOptions = host.Services.GetRequiredService<IOptions<ClusterOptions>>().Value;

        Assert.Equal("surface-cluster", clusterOptions.ClusterId);
        Assert.Equal("surface-service", clusterOptions.ServiceId);
        Assert.NotNull(host.Services.GetRequiredService<IClusterClient>());
        Assert.Equal(
            "https://host.example/fabrcoreapi/cluster/gateways",
            requestedUri?.AbsoluteUri);
    }

    [Theory]
    [InlineData("SqlServer")]
    [InlineData("AzureStorage")]
    public void AddFabrCoreSurfaceDirectsSplitClientsToGatewayDiscovery(string clusteringMode)
    {
        var builder = Host.CreateApplicationBuilder();
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Orleans:ClusteringMode"] = clusteringMode
        });

        var exception = Assert.Throws<InvalidOperationException>(() => builder.AddFabrCoreSurface());

        Assert.Contains("AddFabrCoreSurfaceAsync", exception.Message);
        Assert.Contains("discovery HttpClient", exception.Message);
    }

    [Fact]
    public async Task AddFabrCoreSurfaceAsyncReusesAnExistingClusterClientWithoutDiscovery()
    {
        var discoveryRequested = false;
        using var discoveryHttpClient = new HttpClient(new StubHttpMessageHandler(_ =>
        {
            discoveryRequested = true;
            return new HttpResponseMessage(HttpStatusCode.InternalServerError);
        }));

        var builder = Host.CreateApplicationBuilder();
        builder.UseOrleansClient(client => client.UseLocalhostClustering());

        await builder.AddFabrCoreSurfaceAsync(discoveryHttpClient);

        using var host = builder.Build();
        Assert.NotNull(host.Services.GetRequiredService<IClusterClient>());
        Assert.False(discoveryRequested);
    }

    private sealed class StubHttpMessageHandler(
        Func<HttpRequestMessage, HttpResponseMessage> responseFactory) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
            => Task.FromResult(responseFactory(request));
    }
}
