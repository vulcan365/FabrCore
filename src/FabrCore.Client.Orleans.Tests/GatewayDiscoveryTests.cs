using System.Net;
using System.Text;
using System.Text.Json;
using FabrCore.Core.Connectivity;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Orleans;
using Orleans.Configuration;

namespace FabrCore.Client.Orleans.Tests;

[TestClass]
public sealed class GatewayDiscoveryTests
{
    [TestMethod]
    public async Task DiscoveryClient_ValidDocument_IsReturned()
    {
        using var httpClient = CreateHttpClient(_ => JsonResponse(CreateDocument()));
        var client = CreateDiscoveryClient(httpClient, allowInsecure: true);

        var document = await client.GetGatewayDiscoveryAsync();

        Assert.AreEqual("cluster-a", document.ClusterId);
        Assert.AreEqual("service-a", document.ServiceId);
        CollectionAssert.AreEqual(
            new[] { "gwy.tcp://127.0.0.1:30000/0" },
            document.Gateways);
    }

    [TestMethod]
    [DataRow("version")]
    [DataRow("cluster")]
    [DataRow("service")]
    [DataRow("gateways")]
    [DataRow("uri")]
    [DataRow("refresh")]
    public async Task DiscoveryClient_InvalidDocument_FailsClearly(string invalidField)
    {
        var document = CreateDocument();
        switch (invalidField)
        {
            case "version": document.Version = 99; break;
            case "cluster": document.ClusterId = string.Empty; break;
            case "service": document.ServiceId = string.Empty; break;
            case "gateways": document.Gateways = []; break;
            case "uri": document.Gateways = ["https://not-an-orleans-gateway"]; break;
            case "refresh": document.RefreshPeriodSeconds = 0; break;
        }

        using var httpClient = CreateHttpClient(_ => JsonResponse(document));
        var client = CreateDiscoveryClient(httpClient, allowInsecure: true);

        await Assert.ThrowsAsync<FabrCoreGatewayDiscoveryException>(
            () => client.GetGatewayDiscoveryAsync());
    }

    [TestMethod]
    public async Task DiscoveryClient_InsecureHostWithoutOptIn_FailsClearly()
    {
        using var httpClient = CreateHttpClient(_ => JsonResponse(CreateDocument(requireTls: false)));
        var client = CreateDiscoveryClient(httpClient, allowInsecure: false);

        var exception = await Assert.ThrowsAsync<FabrCoreGatewayDiscoveryException>(
            () => client.GetGatewayDiscoveryAsync());

        StringAssert.Contains(exception.Message, "disallows insecure Orleans transport");
    }

    [TestMethod]
    public async Task DiscoveryClient_NonSuccessStatus_IncludesStatusAndBody()
    {
        using var httpClient = CreateHttpClient(_ => new HttpResponseMessage(HttpStatusCode.BadGateway)
        {
            Content = new StringContent("upstream unavailable")
        });
        var client = CreateDiscoveryClient(httpClient, allowInsecure: true);

        var exception = await Assert.ThrowsAsync<FabrCoreGatewayDiscoveryException>(
            () => client.GetGatewayDiscoveryAsync());

        Assert.AreEqual(HttpStatusCode.BadGateway, exception.StatusCode);
        StringAssert.Contains(exception.Message, "upstream unavailable");
    }

    [TestMethod]
    public async Task DiscoveryClient_TransportFailure_IncludesDiscoveryEndpoint()
    {
        using var httpClient = new HttpClient(new ThrowingHandler());
        var client = CreateDiscoveryClient(httpClient, allowInsecure: true);

        var exception = await Assert.ThrowsAsync<FabrCoreGatewayDiscoveryException>(
            () => client.GetGatewayDiscoveryAsync());

        StringAssert.Contains(exception.Message, "/fabrcoreapi/cluster/gateways");
        Assert.IsInstanceOfType<HttpRequestException>(exception.InnerException);
    }

    [TestMethod]
    public async Task GatewayProvider_RefreshFailureRetainsLastKnownGood_ThenRecovers()
    {
        var requests = 0;
        using var httpClient = CreateHttpClient(_ =>
        {
            requests++;
            if (requests == 1)
            {
                return new HttpResponseMessage(HttpStatusCode.ServiceUnavailable);
            }

            return JsonResponse(CreateDocument("gwy.tcp://127.0.0.2:30000/0"));
        });
        var discoveryClient = CreateDiscoveryClient(httpClient, allowInsecure: true);
        var provider = new FabrCoreHostGatewayListProvider(
            discoveryClient,
            CreateDocument(),
            NullLogger<FabrCoreHostGatewayListProvider>.Instance);

        var initial = await provider.GetGateways();
        var duringOutage = await provider.GetGateways();
        var recovered = await provider.GetGateways();

        Assert.AreEqual("127.0.0.1", initial.Single().Host);
        Assert.AreEqual("127.0.0.1", duringOutage.Single().Host);
        Assert.AreEqual("127.0.0.2", recovered.Single().Host);
    }

    [TestMethod]
    public async Task AddFabrCoreOrleansClientAsync_ConfiguresIdentityAndNormalClusterClient()
    {
        using var httpClient = CreateHttpClient(_ => JsonResponse(CreateDocument(requireTls: false)));
        var builder = Microsoft.Extensions.Hosting.Host.CreateApplicationBuilder();

        await builder.AddFabrCoreOrleansClientAsync(
            httpClient,
            options =>
            {
                options.FabrCoreHostUrl = "https://host.example";
                options.AllowInsecureOrleansTransport = true;
            });

        using var host = builder.Build();
        var clusterOptions = host.Services.GetRequiredService<IOptions<ClusterOptions>>().Value;
        var clusterClient = host.Services.GetRequiredService<IClusterClient>();

        Assert.AreEqual("cluster-a", clusterOptions.ClusterId);
        Assert.AreEqual("service-a", clusterOptions.ServiceId);
        Assert.IsNotNull(clusterClient);
    }

    [TestMethod]
    public async Task AddFabrCoreOrleansClientAsync_TlsRequiredWithoutCallback_FailsStartupRegistration()
    {
        using var httpClient = CreateHttpClient(_ => JsonResponse(CreateDocument(requireTls: true)));
        var builder = Microsoft.Extensions.Hosting.Host.CreateApplicationBuilder();

        var exception = await Assert.ThrowsAsync<FabrCoreGatewayDiscoveryException>(() =>
            builder.AddFabrCoreOrleansClientAsync(
                httpClient,
                options => options.FabrCoreHostUrl = "https://host.example"));

        StringAssert.Contains(exception.Message, "requires Orleans TLS");
    }

    [TestMethod]
    public void ClientProject_DoesNotReferenceHostClusteringProviders()
    {
        var project = FindProjectFile("FabrCore.Client.Orleans.csproj");
        var text = File.ReadAllText(project);

        Assert.IsFalse(text.Contains("Microsoft.Orleans.Clustering.AdoNet", StringComparison.Ordinal));
        Assert.IsFalse(text.Contains("Microsoft.Orleans.Clustering.AzureStorage", StringComparison.Ordinal));
        Assert.IsFalse(text.Contains("FabrCore.Host.SqlServer", StringComparison.Ordinal));
        Assert.IsFalse(text.Contains("FabrCore.Host.AzureStorage", StringComparison.Ordinal));
    }

    [TestMethod]
    public void OrleansClusteringPackages_AreReferencedOnlyByTheirHostProviderProjects()
    {
        var srcDirectory = FindSrcDirectory();
        var projectFiles = Directory.EnumerateFiles(srcDirectory, "*.csproj", SearchOption.AllDirectories)
            .Where(path => !path.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase))
            .ToArray();

        var adoNetReferences = projectFiles
            .Where(path => File.ReadAllText(path).Contains("Microsoft.Orleans.Clustering.AdoNet", StringComparison.Ordinal))
            .Select(Path.GetFileName)
            .ToArray();
        var azureReferences = projectFiles
            .Where(path => File.ReadAllText(path).Contains("Microsoft.Orleans.Clustering.AzureStorage", StringComparison.Ordinal))
            .Select(Path.GetFileName)
            .ToArray();

        CollectionAssert.AreEqual(new[] { "FabrCore.Host.SqlServer.csproj" }, adoNetReferences);
        CollectionAssert.AreEqual(new[] { "FabrCore.Host.AzureStorage.csproj" }, azureReferences);
    }

    private static FabrCoreGatewayDiscoveryClient CreateDiscoveryClient(HttpClient httpClient, bool allowInsecure)
        => new(httpClient, new FabrCoreOrleansClientOptions
        {
            FabrCoreHostUrl = "https://host.example",
            AllowInsecureOrleansTransport = allowInsecure
        });

    private static FabrCoreGatewayDiscoveryDocument CreateDocument(
        string gateway = "gwy.tcp://127.0.0.1:30000/0",
        bool requireTls = false)
        => new()
        {
            ClusterId = "cluster-a",
            ServiceId = "service-a",
            Gateways = [gateway],
            RefreshPeriodSeconds = 30,
            RequireOrleansTls = requireTls
        };

    private static HttpClient CreateHttpClient(Func<HttpRequestMessage, HttpResponseMessage> responder)
        => new(new DelegateHandler(responder));

    private static HttpResponseMessage JsonResponse(FabrCoreGatewayDiscoveryDocument document)
        => new(HttpStatusCode.OK)
        {
            Content = new StringContent(
                JsonSerializer.Serialize(document, new JsonSerializerOptions(JsonSerializerDefaults.Web)),
                Encoding.UTF8,
                "application/json")
        };

    private static string FindProjectFile(string fileName)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            var localCandidate = Path.Combine(directory.FullName, fileName);
            if (File.Exists(localCandidate))
            {
                return localCandidate;
            }

            var siblingCandidate = Path.Combine(directory.FullName, "FabrCore.Client.Orleans", fileName);
            if (File.Exists(siblingCandidate))
            {
                return siblingCandidate;
            }

            directory = directory.Parent;
        }

        throw new FileNotFoundException($"Could not locate {fileName} from {AppContext.BaseDirectory}.");
    }

    private static string FindSrcDirectory()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "FabrCore.sln")))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException($"Could not locate the FabrCore src directory from {AppContext.BaseDirectory}.");
    }

    private sealed class DelegateHandler(Func<HttpRequestMessage, HttpResponseMessage> responder) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
            => Task.FromResult(responder(request));
    }

    private sealed class ThrowingHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
            => throw new HttpRequestException("Host unavailable");
    }
}
