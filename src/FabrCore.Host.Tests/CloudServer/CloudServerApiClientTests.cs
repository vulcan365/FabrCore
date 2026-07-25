using FabrCore.Core;
using FabrCore.Core.CloudServer;
using FabrCore.Host.Services.CloudServer;
using System.Net;

namespace FabrCore.Host.Tests.CloudServer;

[TestClass]
public sealed class CloudServerApiClientTests
{
    private static object Envelope(string version = "v1", int schemaVersion = CloudServerProtocol.CurrentSchemaVersion) => new
    {
        schemaVersion,
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

    [TestMethod]
    public async Task Fetch_Success_ParsesCamelCaseEnvelope_AndSendsProtocolHeaders()
    {
        var handler = new FakeCloudServerHandler(_ =>
            Task.FromResult(FakeCloudServerHandler.Json(HttpStatusCode.OK, Envelope("abc123"))));
        var client = CloudServerTestFactory.ApiClient(
            handler,
            CloudServerTestFactory.Options(o => o.ClusterId = "cluster-1"),
            environmentName: "Production");

        var result = await client.FetchConfigurationAsync(currentVersion: null);

        Assert.AreEqual(CloudConfigurationFetchStatus.Success, result.Status);
        Assert.AreEqual("abc123", result.Envelope!.ConfigurationVersion);
        Assert.AreEqual("default", result.Envelope.Configuration.ModelConfigurations[0].Name);
        Assert.AreEqual("sk-test", result.Envelope.Configuration.ApiKeys[0].Value);

        var request = handler.Requests[0];
        Assert.AreEqual($"https://forge.test{CloudServerProtocol.ConfigurationPath}", request.RequestUri!.ToString());
        Assert.AreEqual("Bearer", request.Headers.Authorization!.Scheme);
        Assert.AreEqual("frg_test_key", request.Headers.Authorization.Parameter);
        Assert.AreEqual("cluster-1", request.Headers.GetValues(CloudServerProtocol.ClusterIdHeader).Single());
        Assert.AreEqual("Production", request.Headers.GetValues(CloudServerProtocol.EnvironmentHeader).Single());
        Assert.AreEqual(0, request.Headers.IfNoneMatch.Count);
    }

    [TestMethod]
    public async Task Fetch_UsesOrleansClusterId_WhenNotConfigured()
    {
        var handler = new FakeCloudServerHandler(_ =>
            Task.FromResult(FakeCloudServerHandler.Json(HttpStatusCode.OK, Envelope())));
        var client = CloudServerTestFactory.ApiClient(
            handler,
            CloudServerTestFactory.Options(),
            configValues: new Dictionary<string, string?>
            {
                ["Orleans:ClusterId"] = "orleans-cluster-7",
                ["Orleans:ServiceId"] = "orleans-service-7"
            });

        Assert.AreEqual("orleans-cluster-7", client.EffectiveClusterId);
        Assert.AreEqual("orleans-service-7", client.ServiceId);

        await client.FetchConfigurationAsync(null);
        Assert.AreEqual(
            "orleans-cluster-7",
            handler.Requests[0].Headers.GetValues(CloudServerProtocol.ClusterIdHeader).Single());
    }

    [TestMethod]
    public async Task Fetch_WithCurrentVersion_SendsIfNoneMatch_And304ReturnsNotModified()
    {
        var handler = new FakeCloudServerHandler(_ =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotModified)));
        var client = CloudServerTestFactory.ApiClient(handler, CloudServerTestFactory.Options());

        var result = await client.FetchConfigurationAsync("abc123");

        Assert.AreEqual(CloudConfigurationFetchStatus.NotModified, result.Status);
        Assert.AreEqual("\"abc123\"", handler.Requests[0].Headers.IfNoneMatch.Single().Tag);
    }

    [TestMethod]
    public async Task Fetch_UnsupportedSchemaVersion_Fails()
    {
        var handler = new FakeCloudServerHandler(_ =>
            Task.FromResult(FakeCloudServerHandler.Json(
                HttpStatusCode.OK, Envelope(schemaVersion: CloudServerProtocol.CurrentSchemaVersion + 1))));
        var client = CloudServerTestFactory.ApiClient(handler, CloudServerTestFactory.Options());

        var result = await client.FetchConfigurationAsync(null);

        Assert.AreEqual(CloudConfigurationFetchStatus.Failed, result.Status);
        Assert.IsTrue(result.Error!.Contains("schemaVersion"));
    }

    [TestMethod]
    public async Task Fetch_ServerError_Fails()
    {
        var handler = new FakeCloudServerHandler(_ =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.Unauthorized)));
        var client = CloudServerTestFactory.ApiClient(handler, CloudServerTestFactory.Options());

        var result = await client.FetchConfigurationAsync(null);

        Assert.AreEqual(CloudConfigurationFetchStatus.Failed, result.Status);
        Assert.IsTrue(result.Error!.Contains("401"));
    }

    [TestMethod]
    public async Task Fetch_Timeout_Fails()
    {
        var handler = new FakeCloudServerHandler(async _ =>
        {
            await Task.Delay(TimeSpan.FromSeconds(30));
            return new HttpResponseMessage(HttpStatusCode.OK);
        });
        var client = CloudServerTestFactory.ApiClient(
            handler,
            CloudServerTestFactory.Options(o => o.RequestTimeout = TimeSpan.FromMilliseconds(100)));

        var result = await client.FetchConfigurationAsync(null);

        Assert.AreEqual(CloudConfigurationFetchStatus.Failed, result.Status);
        Assert.IsTrue(result.Error!.Contains("timed out"));
    }

    [TestMethod]
    public async Task Heartbeat_Success_ParsesResponse()
    {
        var handler = new FakeCloudServerHandler(_ =>
            Task.FromResult(FakeCloudServerHandler.Json(
                HttpStatusCode.OK, new { refreshRequested = true, latestConfigurationVersion = "v9" })));
        var client = CloudServerTestFactory.ApiClient(handler, CloudServerTestFactory.Options());

        var response = await client.SendHeartbeatAsync(new CloudHeartbeatRequest
        {
            ClusterId = client.EffectiveClusterId,
            Environment = client.EffectiveEnvironment,
            HostInstanceId = "test:1"
        });

        Assert.IsNotNull(response);
        Assert.IsTrue(response.RefreshRequested!.Value);
        Assert.AreEqual("v9", response.LatestConfigurationVersion);
        Assert.AreEqual($"https://forge.test{CloudServerProtocol.HeartbeatPath}", handler.Requests[0].RequestUri!.ToString());
        Assert.IsTrue(handler.RequestBodies[0]!.Contains("hostInstanceId"), "Heartbeat body should be camelCase JSON.");
    }

    [TestMethod]
    public async Task Heartbeat_Failure_ReturnsNull()
    {
        var handler = new FakeCloudServerHandler(_ =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.InternalServerError)));
        var client = CloudServerTestFactory.ApiClient(handler, CloudServerTestFactory.Options());

        var response = await client.SendHeartbeatAsync(new CloudHeartbeatRequest());

        Assert.IsNull(response);
    }
}
