using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

using FabrCore.Host.A2A;
using FabrCore.Host.Configuration;
using FabrCore.Host.Testing;
namespace FabrCore.Host.Tests.A2A;

[TestClass]
public sealed class A2AAuthenticationTests
{
    private const string SendBody =
        """{"jsonrpc":"2.0","id":1,"method":"message/send","params":{"message":{"kind":"message","role":"user","messageId":"m-1","parts":[{"kind":"text","text":"hi"}]}}}""";

    private static Dictionary<string, string?> ApiKeyConfig() => new()
    {
        ["A2A:Enabled"] = "true",
        ["A2A:PublicBaseUrl"] = "https://agents.contoso.com",
        ["A2A:AgentTypes:0"] = "botanical-agent",
        ["A2A:Authentication:Mode"] = "ApiKey",
        ["A2A:Authentication:ApiKey:Keys:0:Name"] = "copilot-studio",
        ["A2A:Authentication:ApiKey:Keys:0:Value"] = "s3cret",
    };

    private static HttpRequestMessage Send(string? apiKey = null, string path = "/a2a/botanical-agent")
    {
        var request = new HttpRequestMessage(HttpMethod.Post, path)
        {
            Content = new StringContent(SendBody, Encoding.UTF8, "application/json"),
        };

        if (apiKey is not null)
        {
            request.Headers.Add("x-api-key", apiKey);
        }

        return request;
    }

    [TestMethod]
    public async Task ApiKey_MissingKey_IsRejected()
    {
        await using var host = await A2ATestHost.StartAsync(ApiKeyConfig());

        var response = await host.Client.SendAsync(Send());

        Assert.AreEqual(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.AreEqual(0, host.AgentService.Sends.Count);
    }

    [TestMethod]
    public async Task ApiKey_WrongKey_IsRejected()
    {
        await using var host = await A2ATestHost.StartAsync(ApiKeyConfig());

        var response = await host.Client.SendAsync(Send("not-the-key"));

        Assert.AreEqual(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.AreEqual(0, host.AgentService.Sends.Count);
    }

    [TestMethod]
    public async Task ApiKey_CorrectKey_IsAccepted()
    {
        await using var host = await A2ATestHost.StartAsync(ApiKeyConfig());

        var response = await host.Client.SendAsync(Send("s3cret"));

        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
        Assert.AreEqual(1, host.AgentService.Sends.Count);
    }

    [TestMethod]
    public async Task ApiKey_IsAlsoAcceptedAsABearerCredential()
    {
        await using var host = await A2ATestHost.StartAsync(ApiKeyConfig());

        var request = Send();
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", "s3cret");
        var response = await host.Client.SendAsync(request);

        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
    }

    [TestMethod]
    public async Task ApiKey_InAQueryParameter_IsAcceptedOnlyWhenConfigured()
    {
        var config = ApiKeyConfig();
        config["A2A:Authentication:ApiKey:QueryParameterName"] = "code";

        await using var host = await A2ATestHost.StartAsync(config);

        var accepted = await host.Client.SendAsync(Send(path: "/a2a/botanical-agent?code=s3cret"));
        Assert.AreEqual(HttpStatusCode.OK, accepted.StatusCode);

        var rejected = await host.Client.SendAsync(Send(path: "/a2a/botanical-agent?code=wrong"));
        Assert.AreEqual(HttpStatusCode.Unauthorized, rejected.StatusCode);
    }

    [TestMethod]
    public async Task ApiKey_ScopedToOtherAgents_CannotCallThisOne()
    {
        var config = ApiKeyConfig();
        config["A2A:AgentTypes:1"] = "support-agent";
        config["A2A:Authentication:ApiKey:Keys:0:Agents:0"] = "support-agent";

        await using var host = await A2ATestHost.StartAsync(config);

        var denied = await host.Client.SendAsync(Send("s3cret"));
        using var body = JsonDocument.Parse(await denied.Content.ReadAsStringAsync());
        Assert.AreEqual(-32600, body.RootElement.GetProperty("error").GetProperty("code").GetInt32());
        Assert.AreEqual(0, host.AgentService.Sends.Count);

        var allowed = await host.Client.SendAsync(Send("s3cret", "/a2a/support-agent"));
        Assert.AreEqual(HttpStatusCode.OK, allowed.StatusCode);
    }

    [TestMethod]
    public async Task ApiKey_MapsTheCallerToItsConfiguredPrincipal()
    {
        var config = ApiKeyConfig();
        config["A2A:Authentication:ApiKey:Keys:0:PrincipalHandle"] = "contoso-tenant";
        config["A2A:Principal:Strategy"] = "ApiKey";

        await using var host = await A2ATestHost.StartAsync(config);

        await host.Client.SendAsync(Send("s3cret"));

        Assert.AreEqual("contoso-tenant", host.AgentService.Sends.Single().Principal);
    }

    [TestMethod]
    public async Task AgentCard_StaysReadableWithoutCredentialsByDefault()
    {
        await using var host = await A2ATestHost.StartAsync(ApiKeyConfig());

        var response = await host.Client.GetAsync("/a2a/botanical-agent/.well-known/agent-card.json");
        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
    }

    [TestMethod]
    public async Task AgentCard_CanBeProtectedToo()
    {
        var config = ApiKeyConfig();
        config["A2A:Authentication:AllowAnonymousAgentCard"] = "false";

        await using var host = await A2ATestHost.StartAsync(config);

        var anonymous = await host.Client.GetAsync("/a2a/botanical-agent/.well-known/agent-card.json");
        Assert.AreEqual(HttpStatusCode.Unauthorized, anonymous.StatusCode);
    }
}
