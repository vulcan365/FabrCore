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
                ["FabrCore:Orleans:ClusterId"] = "orleans-cluster-7",
                ["FabrCore:Orleans:ServiceId"] = "orleans-service-7"
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
            CloudServerTestFactory.Options(o => o.ClusterId = "cluster-1"),
            CloudServerTestFactory.RemoteOptions(o => o.PollWait = TimeSpan.FromSeconds(7)),
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
        var handler = new FakeCloudServerHandler(async (_, cancellationToken) =>
        {
            await Task.Delay(TimeSpan.FromMilliseconds(100), cancellationToken);
            return new HttpResponseMessage(HttpStatusCode.NoContent);
        });
        var client = CloudServerTestFactory.ApiClient(handler, CloudServerTestFactory.Options());

        var command = await client.PollAdminCommandAsync("silo-1");

        Assert.IsNull(command);
        Assert.AreEqual(1, handler.Requests.Count, "An empty long poll is a successful terminal outcome, not a retry.");
    }

    [TestMethod]
    public async Task ConnectPoll_TransientFailure_RetriesSequentially()
    {
        var attempts = 0;
        var active = 0;
        var maximumActive = 0;
        var handler = new FakeCloudServerHandler(async (_, cancellationToken) =>
        {
            var currentActive = Interlocked.Increment(ref active);
            InterlockedExtensions.Max(ref maximumActive, currentActive);
            try
            {
                await Task.Delay(TimeSpan.FromMilliseconds(25), cancellationToken);
                return Interlocked.Increment(ref attempts) == 1
                    ? new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)
                    : new HttpResponseMessage(HttpStatusCode.NoContent);
            }
            finally
            {
                Interlocked.Decrement(ref active);
            }
        });
        var client = CloudServerTestFactory.ApiClient(handler, CloudServerTestFactory.Options());

        var command = await client.PollAdminCommandAsync("silo-1");

        Assert.IsNull(command);
        Assert.AreEqual(2, attempts);
        Assert.AreEqual(1, maximumActive, "A retry must not overlap the cancelled or completed attempt.");
    }

    [TestMethod]
    public async Task ConnectPoll_CallerCancellation_StopsWithoutRetry()
    {
        var handler = new FakeCloudServerHandler(async (_, cancellationToken) =>
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return new HttpResponseMessage(HttpStatusCode.NoContent);
        });
        var client = CloudServerTestFactory.ApiClient(handler, CloudServerTestFactory.Options());
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));

        await Assert.ThrowsExactlyAsync<TaskCanceledException>(
            () => client.PollAdminCommandAsync("silo-1", cancellation.Token));

        Assert.AreEqual(1, handler.Requests.Count, "Caller cancellation must not be retried.");
    }

    [TestMethod]
    public async Task ConnectPoll_ConcurrentCallers_AreSerialized()
    {
        var active = 0;
        var maximumActive = 0;
        var firstStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var handler = new FakeCloudServerHandler(async (_, cancellationToken) =>
        {
            var currentActive = Interlocked.Increment(ref active);
            InterlockedExtensions.Max(ref maximumActive, currentActive);
            firstStarted.TrySetResult();
            try
            {
                await Task.Delay(TimeSpan.FromMilliseconds(100), cancellationToken);
                return new HttpResponseMessage(HttpStatusCode.NoContent);
            }
            finally
            {
                Interlocked.Decrement(ref active);
            }
        });
        var client = CloudServerTestFactory.ApiClient(handler, CloudServerTestFactory.Options());

        var first = client.PollAdminCommandAsync("silo-1");
        await firstStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
        var second = client.PollAdminCommandAsync("silo-1");
        await Task.WhenAll(first, second);

        Assert.AreEqual(2, handler.Requests.Count);
        Assert.AreEqual(1, maximumActive, "Only one connect poll may be active per Cloud Server client.");
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

internal static class InterlockedExtensions
{
    public static void Max(ref int location, int value)
    {
        var current = Volatile.Read(ref location);
        while (current < value)
        {
            var observed = Interlocked.CompareExchange(ref location, value, current);
            if (observed == current)
            {
                return;
            }
            current = observed;
        }
    }
}
