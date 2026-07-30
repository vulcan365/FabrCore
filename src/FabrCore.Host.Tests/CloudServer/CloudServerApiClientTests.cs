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

    [TestMethod]
    public async Task ConnectPoll_Success_ParsesCommand_AndSendsRoutingHeaders()
    {
        var commandId = Guid.NewGuid().ToString("N");
        var handler = new FakeCloudServerHandler(_ =>
            Task.FromResult(FakeCloudServerHandler.Json(HttpStatusCode.OK, new
            {
                commandId,
                method = "POST",
                pathAndQuery = "/fabrcoreapi/memory/admin/v1/search?q=hello",
                headers = new Dictionary<string, string[]> { ["Accept"] = ["application/json"] },
                body = Convert.ToBase64String("payload"u8.ToArray()),
                expiresAt = DateTimeOffset.UtcNow.AddMinutes(1)
            })));
        var client = CloudServerTestFactory.ApiClient(
            handler,
            CloudServerTestFactory.Options(o =>
            {
                o.ClusterId = "cluster-1";
                o.Connect.PollWait = TimeSpan.FromSeconds(7);
            }),
            environmentName: "Production");

        var command = await client.PollAdminCommandAsync("silo:1");

        Assert.IsNotNull(command);
        Assert.AreEqual(commandId, command.CommandId);
        Assert.AreEqual("POST", command.Method);
        Assert.AreEqual("/fabrcoreapi/memory/admin/v1/search?q=hello", command.PathAndQuery);
        CollectionAssert.AreEqual("payload"u8.ToArray(), command.Body);

        var request = handler.Requests[0];
        Assert.AreEqual(
            $"https://forge.test{CloudServerProtocol.ConnectPath}?waitSeconds=7&hostInstanceId=silo%3A1",
            request.RequestUri!.ToString());
        Assert.AreEqual("frg_test_key", request.Headers.Authorization!.Parameter);
        Assert.AreEqual("cluster-1", request.Headers.GetValues(CloudServerProtocol.ClusterIdHeader).Single());
        Assert.AreEqual("Production", request.Headers.GetValues(CloudServerProtocol.EnvironmentHeader).Single());
    }

    [TestMethod]
    public async Task ConnectPoll_NoContent_ReturnsNull()
    {
        var handler = new FakeCloudServerHandler(_ =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.NoContent)));
        var client = CloudServerTestFactory.ApiClient(handler, CloudServerTestFactory.Options());

        var command = await client.PollAdminCommandAsync("silo-1");

        Assert.IsNull(command);
    }

    [TestMethod]
    public async Task ConnectResponse_PostsResultToCommandPath()
    {
        var handler = new FakeCloudServerHandler(_ =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.NoContent)));
        var client = CloudServerTestFactory.ApiClient(handler, CloudServerTestFactory.Options());
        var commandId = "command/with spaces";

        await client.SendAdminCommandResponseAsync(new CloudAdminCommandResponse
        {
            CommandId = commandId,
            StatusCode = (int)HttpStatusCode.OK,
            Headers = new Dictionary<string, string[]> { ["Content-Type"] = ["application/json"] },
            Body = """{"ok":true}"""u8.ToArray()
        });

        var request = handler.Requests[0];
        Assert.AreEqual(
            $"https://forge.test{CloudServerProtocol.ConnectResponsePath(commandId)}",
            request.RequestUri!.AbsoluteUri);
        Assert.AreEqual(HttpMethod.Post, request.Method);
        Assert.IsTrue(handler.RequestBodies[0]!.Contains("\"commandId\":\"command/with spaces\""));
        Assert.IsTrue(handler.RequestBodies[0]!.Contains("\"statusCode\":200"));
    }
}
