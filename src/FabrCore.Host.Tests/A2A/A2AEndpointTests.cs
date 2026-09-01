using System.Net;
using System.Text.Json;

using FabrCore.Host.A2A;
using FabrCore.Host.Configuration;
using FabrCore.Host.Testing;
namespace FabrCore.Host.Tests.A2A;

[TestClass]
public sealed class A2AEndpointTests
{
    private static Dictionary<string, string?> OpenConfig() => new()
    {
        ["A2A:Enabled"] = "true",
        ["A2A:PublicBaseUrl"] = "https://agents.contoso.com",
        ["A2A:Authentication:Mode"] = "None",
        ["A2A:AgentTypes:0"] = "botanical-agent",
    };

    private static string SendEnvelope(string text, string? contextId = null, string method = "message/send")
        => JsonSerializer.Serialize(new
        {
            jsonrpc = "2.0",
            id = 7,
            method,
            @params = new
            {
                message = new
                {
                    kind = "message",
                    role = "user",
                    messageId = "m-1",
                    contextId,
                    parts = new[] { new { kind = "text", text } },
                },
            },
        });

    // ── Agent card ─────────────────────────────────────────────────────────────────────────

    [TestMethod]
    public async Task AgentCard_IsServedOnBothWellKnownPathsAndTheRestCardRoute()
    {
        await using var host = await A2ATestHost.StartAsync(OpenConfig());

        foreach (var path in new[]
                 {
                     "/a2a/botanical-agent/.well-known/agent-card.json",
                     "/a2a/botanical-agent/.well-known/agent.json",
                     "/a2a/botanical-agent/v1/card",
                 })
        {
            using var card = await host.GetJsonAsync(path);
            var root = card.RootElement;

            Assert.AreEqual("0.3.0", root.GetProperty("protocolVersion").GetString(), path);
            Assert.AreEqual("Botanical Agent", root.GetProperty("name").GetString(), path);
            Assert.AreEqual(
                "Answers questions about plants and botany.",
                root.GetProperty("description").GetString(),
                path);
            Assert.AreEqual(
                "https://agents.contoso.com/a2a/botanical-agent",
                root.GetProperty("url").GetString(),
                path);
            Assert.AreEqual("JSONRPC", root.GetProperty("preferredTransport").GetString(), path);
            Assert.IsTrue(root.GetProperty("capabilities").GetProperty("streaming").GetBoolean(), path);
            Assert.AreEqual(1, root.GetProperty("skills").GetArrayLength(), path);
        }
    }

    [TestMethod]
    public async Task AgentCard_IsServedOnEveryPathACopilotStudioStyleClientProbes()
    {
        // Copilot Studio is configured with the message endpoint and appends the well-known path
        // to it verbatim rather than resolving against the agent's base, then falls back to the
        // server root, then to a bare GET on the endpoint. This is the exact probe sequence
        // captured from a live connection attempt; every entry must answer with the card or the
        // client reports "we couldn't find an agent card at this URL" while the card sits at a
        // path it never asks for.
        await using var host = await A2ATestHost.StartAsync(OpenConfig());

        var probes = new List<string>();
        foreach (var segment in new[] { "", "/v1", "/v1/message:stream", "/v1/message:send" })
        {
            foreach (var fileName in new[]
                     {
                         "agent-card.json",
                         "agent.json",
                         "agentcard.json",
                         "agentCard.json",
                         "agent_card.json",
                     })
            {
                probes.Add($"/a2a/botanical-agent{segment}/.well-known/{fileName}");
            }
        }

        // Server root, and the bare-GET last resort on each endpoint the client may hold.
        probes.Add("/.well-known/agent-card.json");
        probes.Add("/.well-known/agentCard.json");
        probes.Add("/.well-known/agent_card.json");
        probes.Add("/a2a/botanical-agent");
        probes.Add("/a2a/botanical-agent/v1/message:stream");
        probes.Add("/a2a/botanical-agent/v1/message:send");

        foreach (var path in probes)
        {
            using var card = await host.GetJsonAsync(path);
            Assert.AreEqual("Botanical Agent", card.RootElement.GetProperty("name").GetString(), path);
        }
    }

    [TestMethod]
    public async Task AgentCard_IsReadableByABrowserFromAnotherOrigin()
    {
        // Copilot Studio fetches the card with a cross-origin fetch() from its own page, not from
        // its service - the captured request carries Origin: https://copilotstudio.microsoft.com
        // and Sec-Fetch-Mode: cors. Without Access-Control-Allow-Origin the browser discards a
        // perfectly good 200 before the page sees it, and the operator is told the card could not
        // be found while the server log shows every probe succeeding.
        await using var host = await A2ATestHost.StartAsync(OpenConfig());

        foreach (var path in new[]
                 {
                     "/a2a/botanical-agent/.well-known/agent-card.json",
                     "/a2a/botanical-agent/v1/message:stream/.well-known/agent-card.json",
                     "/a2a/botanical-agent/v1/card",
                     "/a2a/.well-known/agent-card.json",
                     "/.well-known/agent-card.json",
                     "/a2a/botanical-agent",
                 })
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, path);
            request.Headers.Add("Origin", "https://copilotstudio.microsoft.com");

            var response = await host.Client.SendAsync(request);

            Assert.AreEqual(HttpStatusCode.OK, response.StatusCode, path);
            Assert.IsTrue(
                response.Headers.TryGetValues("Access-Control-Allow-Origin", out var values),
                $"{path} sent no Access-Control-Allow-Origin, so a browser cannot read it.");
            Assert.AreEqual("*", values!.Single(), path);
        }
    }

    [TestMethod]
    public async Task CallEndpoints_AreNotOpenedCrossOriginByTheAgentCardHeader()
    {
        // The card is public metadata; the call endpoints are not. Widening one must not widen
        // the other.
        await using var host = await A2ATestHost.StartAsync(OpenConfig());

        using var request = new HttpRequestMessage(
            HttpMethod.Post, "/a2a/botanical-agent/v1/message:send");
        request.Headers.Add("Origin", "https://evil.example");
        request.Content = new StringContent(
            SendEnvelope("hello"), System.Text.Encoding.UTF8, "application/json");

        var response = await host.Client.SendAsync(request);

        Assert.IsFalse(
            response.Headers.Contains("Access-Control-Allow-Origin"),
            "A call endpoint must not advertise cross-origin access.");
    }

    [TestMethod]
    public async Task AgentCard_IsServedUnderTheRoutePrefixTheProductTooltipDocuments()
    {
        // Copilot Studio's in-product tooltip says to enter the base URI ("https://your-domain.com/a2a")
        // and that the card is discovered at "https://your-domain.com/a2a/.well-known/agent-card.json"
        // — a different contract from the Learn article, which says to enter the message endpoint.
        // Serve the primary agent's card under the route prefix so both readings work.
        await using var host = await A2ATestHost.StartAsync(OpenConfig());

        foreach (var path in new[]
                 {
                     "/a2a/.well-known/agent-card.json",
                     "/a2a/.well-known/agent.json",
                     "/a2a/.well-known/agentcard.json",
                     "/a2a/.well-known/agent_card.json",
                 })
        {
            using var card = await host.GetJsonAsync(path);
            Assert.AreEqual("Botanical Agent", card.RootElement.GetProperty("name").GetString(), path);
        }
    }

    [TestMethod]
    public async Task AgentCard_SpellingVariantsDoNotCollideIntoAnAmbiguousRoute()
    {
        // ASP.NET route matching is case-insensitive, so mapping both "agentcard.json" and
        // "agentCard.json" would make either request an ambiguous match and answer 500 — the
        // failure mode that a naive "serve every spelling the client asks for" introduces.
        await using var host = await A2ATestHost.StartAsync(OpenConfig());

        foreach (var path in new[]
                 {
                     "/a2a/botanical-agent/.well-known/agentcard.json",
                     "/a2a/botanical-agent/.well-known/agentCard.json",
                     "/a2a/botanical-agent/.well-known/AGENTCARD.JSON",
                 })
        {
            var response = await host.Client.GetAsync(path);
            Assert.AreEqual(HttpStatusCode.OK, response.StatusCode, path);
        }
    }

    [TestMethod]
    public async Task AgentCard_AdvertisesTheHttpJsonInterfaceAlongsideJsonRpc()
    {
        await using var host = await A2ATestHost.StartAsync(OpenConfig());
        using var card = await host.GetJsonAsync("/a2a/botanical-agent/.well-known/agent-card.json");

        var interfaces = card.RootElement.GetProperty("additionalInterfaces")
            .EnumerateArray()
            .Select(i => (i.GetProperty("transport").GetString(), i.GetProperty("url").GetString()))
            .ToList();

        CollectionAssert.Contains(
            interfaces, ("JSONRPC", "https://agents.contoso.com/a2a/botanical-agent"));
        CollectionAssert.Contains(
            interfaces, ("HTTP+JSON", "https://agents.contoso.com/a2a/botanical-agent/v1"));
    }

    [TestMethod]
    public async Task AgentCard_IsServedFromTheServerRootWhenOnlyOneAgentIsExposed()
    {
        await using var host = await A2ATestHost.StartAsync(OpenConfig());

        using var card = await host.GetJsonAsync("/.well-known/agent-card.json");
        Assert.AreEqual("Botanical Agent", card.RootElement.GetProperty("name").GetString());
    }

    [TestMethod]
    public async Task AgentCard_DescribesApiKeyAuthentication()
    {
        var config = OpenConfig();
        config["A2A:Authentication:Mode"] = "ApiKey";
        config["A2A:Authentication:ApiKey:Keys:0:Value"] = "secret-key";

        await using var host = await A2ATestHost.StartAsync(config);

        // The card must stay readable without the key, because a client fetches it first.
        using var card = await host.GetJsonAsync("/a2a/botanical-agent/.well-known/agent-card.json");
        var scheme = card.RootElement.GetProperty("securitySchemes").GetProperty("apiKey");

        Assert.AreEqual("apiKey", scheme.GetProperty("type").GetString());
        Assert.AreEqual("x-api-key", scheme.GetProperty("name").GetString());
        Assert.AreEqual("header", scheme.GetProperty("in").GetString());
        Assert.AreEqual(1, card.RootElement.GetProperty("security").GetArrayLength());
    }

    [TestMethod]
    public async Task AgentCard_FallsBackToTheRequestHostWhenNoPublicBaseUrlIsConfigured()
    {
        var config = OpenConfig();
        config.Remove("A2A:PublicBaseUrl");

        await using var host = await A2ATestHost.StartAsync(config);
        using var card = await host.GetJsonAsync("/a2a/botanical-agent/.well-known/agent-card.json");

        StringAssert.EndsWith(card.RootElement.GetProperty("url").GetString(), "/a2a/botanical-agent");
        StringAssert.StartsWith(card.RootElement.GetProperty("url").GetString(), "http://");
    }

    // ── JSON-RPC binding ───────────────────────────────────────────────────────────────────

    [TestMethod]
    public async Task JsonRpc_MessageSend_ReturnsACompletedTaskWithTheReplyAsAnArtifact()
    {
        var agentService = new FakeFabrCoreAgentService
        {
            Reply = "Tomatoes need more light than strawberries.",
        };
        await using var host = await A2ATestHost.StartAsync(OpenConfig(), agentService);

        var response = await host.PostJsonAsync("/a2a/botanical-agent", SendEnvelope("Which plant needs more light?"));
        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);

        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var root = body.RootElement;

        Assert.AreEqual("2.0", root.GetProperty("jsonrpc").GetString());
        Assert.AreEqual(7, root.GetProperty("id").GetInt32());

        var task = root.GetProperty("result");
        Assert.AreEqual("task", task.GetProperty("kind").GetString());
        Assert.AreEqual("completed", task.GetProperty("status").GetProperty("state").GetString());
        Assert.AreEqual(
            "Tomatoes need more light than strawberries.",
            task.GetProperty("artifacts")[0].GetProperty("parts")[0].GetProperty("text").GetString());

        // The user turn and the agent turn are both recorded on the task.
        Assert.AreEqual(2, task.GetProperty("history").GetArrayLength());
    }

    [TestMethod]
    public async Task JsonRpc_MessageStream_EmitsTaskThenStatusThenArtifactThenFinalStatus()
    {
        await using var host = await A2ATestHost.StartAsync(OpenConfig());

        var events = await ReadSseAsync(host, "/a2a/botanical-agent", SendEnvelope("hello", method: "message/stream"));

        var kinds = events.Select(e => e.GetProperty("result").GetProperty("kind").GetString()).ToList();
        CollectionAssert.AreEqual(
            new[] { "task", "status-update", "artifact-update", "status-update" }, kinds);

        var final = events[^1].GetProperty("result");
        Assert.IsTrue(final.GetProperty("final").GetBoolean());
        Assert.AreEqual("completed", final.GetProperty("status").GetProperty("state").GetString());

        // Streaming frames stay inside the JSON-RPC envelope, echoing the request id.
        Assert.AreEqual(7, events[0].GetProperty("id").GetInt32());
    }

    [TestMethod]
    public async Task JsonRpc_UnknownMethod_ReturnsMethodNotFoundInTheEnvelope()
    {
        await using var host = await A2ATestHost.StartAsync(OpenConfig());

        var response = await host.PostJsonAsync(
            "/a2a/botanical-agent",
            """{"jsonrpc":"2.0","id":1,"method":"agent/doTheThing","params":{}}""");

        // JSON-RPC reports application errors with HTTP 200 and an error member.
        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.AreEqual(-32601, body.RootElement.GetProperty("error").GetProperty("code").GetInt32());
    }

    [TestMethod]
    public async Task JsonRpc_PushNotificationConfig_ReportsThatItIsNotSupported()
    {
        await using var host = await A2ATestHost.StartAsync(OpenConfig());

        var response = await host.PostJsonAsync(
            "/a2a/botanical-agent",
            """{"jsonrpc":"2.0","id":1,"method":"tasks/pushNotificationConfig/set","params":{}}""");

        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.AreEqual(-32003, body.RootElement.GetProperty("error").GetProperty("code").GetInt32());
    }

    // ── HTTP+JSON binding ──────────────────────────────────────────────────────────────────

    [TestMethod]
    public async Task Http_MessageSend_TakesABarePayloadAndReturnsABareTask()
    {
        await using var host = await A2ATestHost.StartAsync(OpenConfig());

        var response = await host.PostJsonAsync(
            "/a2a/botanical-agent/v1/message:send",
            """{"message":{"kind":"message","role":"user","messageId":"m-1","parts":[{"kind":"text","text":"hi"}]}}""");

        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());

        // No JSON-RPC envelope on the REST binding.
        Assert.IsFalse(body.RootElement.TryGetProperty("jsonrpc", out _));
        Assert.AreEqual("task", body.RootElement.GetProperty("kind").GetString());
        Assert.AreEqual("completed", body.RootElement.GetProperty("status").GetProperty("state").GetString());
    }

    [TestMethod]
    public async Task Http_MessageStream_StreamsBareEventsForABarePayload()
    {
        await using var host = await A2ATestHost.StartAsync(OpenConfig());

        var events = await ReadSseAsync(
            host,
            "/a2a/botanical-agent/v1/message:stream",
            """{"message":{"kind":"message","role":"user","messageId":"m-1","parts":[{"kind":"text","text":"hi"}]}}""");

        var kinds = events.Select(e => e.GetProperty("kind").GetString()).ToList();
        CollectionAssert.AreEqual(new[] { "task", "status-update", "artifact-update", "status-update" }, kinds);
    }

    [TestMethod]
    public async Task Http_GetTask_ReadsBackACompletedTask()
    {
        await using var host = await A2ATestHost.StartAsync(OpenConfig());

        var send = await host.PostJsonAsync("/a2a/botanical-agent", SendEnvelope("hi"));
        using var sent = JsonDocument.Parse(await send.Content.ReadAsStringAsync());
        var taskId = sent.RootElement.GetProperty("result").GetProperty("id").GetString();

        using var fetched = await host.GetJsonAsync($"/a2a/botanical-agent/v1/tasks/{taskId}");
        Assert.AreEqual(taskId, fetched.RootElement.GetProperty("id").GetString());
        Assert.AreEqual("completed", fetched.RootElement.GetProperty("status").GetProperty("state").GetString());
    }

    [TestMethod]
    public async Task Http_GetTask_ReportsAnUnknownTaskAsANotFoundWithTheA2AErrorCode()
    {
        await using var host = await A2ATestHost.StartAsync(OpenConfig());

        var response = await host.Client.GetAsync("/a2a/botanical-agent/v1/tasks/does-not-exist");

        Assert.AreEqual(HttpStatusCode.NotFound, response.StatusCode);
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.AreEqual(-32001, body.RootElement.GetProperty("error").GetProperty("code").GetInt32());
    }

    // ── Copilot Studio compatibility ───────────────────────────────────────────────────────

    [TestMethod]
    public async Task CopilotStudioShape_JsonRpcPostedToTheStreamRoute_AnswersWithOneBufferedEnvelope()
    {
        var agentService = new FakeFabrCoreAgentService
        {
            Reply = "Tomatoes need more light than strawberries.",
        };
        await using var host = await A2ATestHost.StartAsync(OpenConfig(), agentService);

        // Copilot Studio is configured with the /v1/message:stream URL but posts JSON-RPC bodies
        // and reads a single JSON response.
        var response = await host.PostJsonAsync(
            "/a2a/botanical-agent/v1/message:stream",
            SendEnvelope("Which plant needs more light?"));

        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
        Assert.AreEqual("application/json", response.Content.Headers.ContentType?.MediaType);

        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var root = body.RootElement;

        Assert.AreEqual("2.0", root.GetProperty("jsonrpc").GetString());
        Assert.AreEqual(7, root.GetProperty("id").GetInt32());

        // The compatibility shape is a flat agent Message, so a connector that reads one answer finds it.
        var result = root.GetProperty("result");
        Assert.AreEqual("message", result.GetProperty("kind").GetString());
        Assert.AreEqual("agent", result.GetProperty("role").GetString());
        Assert.AreEqual(
            "Tomatoes need more light than strawberries.",
            result.GetProperty("parts")[0].GetProperty("text").GetString());
    }

    [TestMethod]
    public async Task CopilotStudioShape_CanBeConfiguredToReturnTheFullTask()
    {
        var config = OpenConfig();
        config["A2A:Interop:CompatibilityResultShape"] = "Task";

        await using var host = await A2ATestHost.StartAsync(config);

        var response = await host.PostJsonAsync("/a2a/botanical-agent/v1/message:stream", SendEnvelope("hi"));
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());

        Assert.AreEqual("task", body.RootElement.GetProperty("result").GetProperty("kind").GetString());
    }

    [TestMethod]
    public async Task CopilotStudioShape_MessageMetadataReachesTheAgentAsAnArg()
    {
        await using var host = await A2ATestHost.StartAsync(OpenConfig());

        var payload = JsonSerializer.Serialize(new
        {
            jsonrpc = "2.0",
            id = 1,
            method = "message/send",
            @params = new
            {
                message = new
                {
                    kind = "message",
                    role = "user",
                    messageId = "m-1",
                    contextId = "ee1e68ee-75fc-42bb-83d7-25fd26e559c3",
                    metadata = new Dictionary<string, object>
                    {
                        ["copilotstudio.microsoft.com/a2a/chathistory"] = new[]
                        {
                            new { From = "user", Text = "Which plant needs more sunlight?" },
                        },
                    },
                    parts = new[] { new { kind = "text", text = "Which plant needs more sunlight?" } },
                },
            },
        });

        await host.PostJsonAsync("/a2a/botanical-agent/v1/message:stream", payload);

        var args = host.AgentService.Sends.Single().Message.Args!;
        Assert.AreEqual("ee1e68ee-75fc-42bb-83d7-25fd26e559c3", args["A2A:ContextId"]);
        Assert.AreEqual("m-1", args["A2A:MessageId"]);
        Assert.AreEqual("botanical-agent", args["A2A:AgentName"]);
        StringAssert.Contains(args["A2A:Metadata"], "chathistory");
    }

    [TestMethod]
    public async Task JsonRpcOnHttpRoutes_CanBeRejectedWhenInteropIsTurnedOff()
    {
        var config = OpenConfig();
        config["A2A:Interop:AcceptJsonRpcOnHttpRoutes"] = "false";

        await using var host = await A2ATestHost.StartAsync(config);

        var response = await host.PostJsonAsync("/a2a/botanical-agent/v1/message:send", SendEnvelope("hi"));
        Assert.AreEqual(HttpStatusCode.BadRequest, response.StatusCode);
    }

    // ── Agent routing ──────────────────────────────────────────────────────────────────────

    [TestMethod]
    public async Task AgentTypes_ProvisionAnAgentPerCallingPrincipal()
    {
        await using var host = await A2ATestHost.StartAsync(OpenConfig());

        await host.PostJsonAsync("/a2a/botanical-agent", SendEnvelope("hi"));

        var (principal, configs) = host.AgentService.Ensured.Single();
        Assert.AreEqual("a2a", principal);
        Assert.AreEqual("botanical-agent", configs.Single().AgentType);
        Assert.AreEqual("a2a-botanical-agent", configs.Single().Handle);

        var send = host.AgentService.Sends.Single();
        Assert.AreEqual("a2a", send.Principal);
        Assert.AreEqual("a2a-botanical-agent", send.Handle);
    }

    [TestMethod]
    public async Task AgentHandles_RouteToTheExistingAgentWithoutProvisioning()
    {
        var config = OpenConfig();
        config.Remove("A2A:AgentTypes:0");
        config["A2A:AgentHandles:0"] = "system:assistant";

        await using var host = await A2ATestHost.StartAsync(config);

        await host.PostJsonAsync("/a2a/assistant", SendEnvelope("hi"));

        Assert.AreEqual(0, host.AgentService.Ensured.Count);
        Assert.AreEqual("system:assistant", host.AgentService.Sends.Single().Handle);
    }

    [TestMethod]
    public async Task AgentPerContext_GivesEachConversationItsOwnAgentInstance()
    {
        var config = OpenConfig();
        config.Remove("A2A:AgentTypes:0");
        config["A2A:Agents:0:AgentType"] = "botanical-agent";
        config["A2A:Agents:0:AgentPerContext"] = "true";

        await using var host = await A2ATestHost.StartAsync(config);

        await host.PostJsonAsync("/a2a/botanical-agent", SendEnvelope("hi", contextId: "conv-A"));
        await host.PostJsonAsync("/a2a/botanical-agent", SendEnvelope("hi", contextId: "conv-B"));

        CollectionAssert.AreEquivalent(
            new[] { "a2a-botanical-agent-conv-a", "a2a-botanical-agent-conv-b" },
            host.AgentService.Sends.Select(s => s.Handle).ToArray());
    }

    [TestMethod]
    public async Task PrincipalStrategy_ContextId_IsolatesCallersByConversation()
    {
        var config = OpenConfig();
        config["A2A:Principal:Strategy"] = "ContextId";
        config["A2A:Principal:Prefix"] = "a2a-";

        await using var host = await A2ATestHost.StartAsync(config);

        await host.PostJsonAsync("/a2a/botanical-agent", SendEnvelope("hi", contextId: "conv-A"));

        Assert.AreEqual("a2a-conv-a", host.AgentService.Sends.Single().Principal);
    }

    [TestMethod]
    public async Task AgentError_IsReportedAsAFailedTask()
    {
        var agentService = new FakeFabrCoreAgentService
        {
            ReplyFactory = _ => Task.FromResult(new FabrCore.Core.AgentMessage
            {
                MessageType = FabrCore.Core.SystemMessageTypes.Error,
                Message = "the model refused",
            }),
        };

        await using var host = await A2ATestHost.StartAsync(OpenConfig(), agentService);

        var response = await host.PostJsonAsync("/a2a/botanical-agent", SendEnvelope("hi"));
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var task = body.RootElement.GetProperty("result");

        Assert.AreEqual("failed", task.GetProperty("status").GetProperty("state").GetString());
        Assert.AreEqual(
            "the model refused",
            task.GetProperty("status").GetProperty("message").GetProperty("parts")[0].GetProperty("text").GetString());
    }

    // ── Catalog ────────────────────────────────────────────────────────────────────────────

    [TestMethod]
    public async Task Catalog_ListsEveryExposedAgentAndItsEndpoints()
    {
        await using var host = await A2ATestHost.StartAsync(OpenConfig());

        using var catalog = await host.GetJsonAsync("/a2a");
        var agent = catalog.RootElement.GetProperty("agents")[0];

        Assert.AreEqual("botanical-agent", agent.GetProperty("name").GetString());
        Assert.AreEqual(
            "https://agents.contoso.com/a2a/botanical-agent/.well-known/agent-card.json",
            agent.GetProperty("agentCard").GetString());
        Assert.AreEqual(
            "https://agents.contoso.com/a2a/botanical-agent/v1/message:stream",
            agent.GetProperty("httpJson").GetProperty("stream").GetString());
    }

    // ── Helpers ────────────────────────────────────────────────────────────────────────────

    private static async Task<List<JsonElement>> ReadSseAsync(FabrCoreA2ATestHost host, string path, string body)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, path)
        {
            Content = new StringContent(body, System.Text.Encoding.UTF8, "application/json"),
        };
        request.Headers.Accept.ParseAdd("text/event-stream");

        using var response = await host.Client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead);
        response.EnsureSuccessStatusCode();
        Assert.AreEqual("text/event-stream", response.Content.Headers.ContentType?.MediaType);

        var events = new List<JsonElement>();
        await using var stream = await response.Content.ReadAsStreamAsync();
        using var reader = new StreamReader(stream);

        while (await reader.ReadLineAsync() is { } line)
        {
            if (line.StartsWith("data: ", StringComparison.Ordinal))
            {
                events.Add(JsonDocument.Parse(line[6..]).RootElement.Clone());
            }
        }

        return events;
    }
}
