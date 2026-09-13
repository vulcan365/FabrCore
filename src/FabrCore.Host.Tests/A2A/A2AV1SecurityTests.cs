using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Claims;
using System.Text;
using System.Text.Json;
using FabrCore.Host.A2A;
using FabrCore.Host.Testing;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using Microsoft.IdentityModel.Tokens;

namespace FabrCore.Host.Tests.A2A;

[TestClass]
public sealed class A2AV1SecurityTests
{
    private const string Tenant = "11111111-2222-3333-4444-555555555555";
    private const string Alice = "aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee";
    private const string Bob = "bbbbbbbb-bbbb-cccc-dddd-eeeeeeeeeeee";
    private const string Issuer = "https://issuer.example.test";
    private static readonly SymmetricSecurityKey Key = new(Encoding.UTF8.GetBytes("test-only-signing-key-with-at-least-32-bytes"));

    private static Task<FabrCoreA2ATestHost> StartAsync() => FabrCoreA2ATestHost.StartAsync(new Dictionary<string, string?>
    {
        ["A2A:Enabled"] = "true", ["A2A:Authentication:Mode"] = "JwtBearer",
        ["A2A:Authentication:JwtBearer:Authority"] = Issuer,
        ["A2A:Authentication:JwtBearer:Audience"] = "agents",
        ["A2A:Principal:Strategy"] = "CanonicalEntra",
        ["A2A:Agents:0:Name"] = "assistant", ["A2A:Agents:0:Binding"] = "assistant",
        ["A2A:Agents:1:Name"] = "other", ["A2A:Agents:1:AgentType"] = "botanical-agent",
        ["AgentBindings:assistant:Handle"] = "assistant", ["AgentBindings:assistant:AgentType"] = "botanical-agent",
        ["A2A:Defaults:Plugins:0"] = "must-not-leak-into-binding",
    }, registry: new FakeFabrCoreRegistry().WithAgentType("botanical-agent", "Plants"), configureServices: services =>
        services.PostConfigure<JwtBearerOptions>(A2ADefaults.JwtBearerScheme, options =>
        {
            options.Configuration = new OpenIdConnectConfiguration { Issuer = Issuer };
            options.Configuration.SigningKeys.Add(Key);
            options.TokenValidationParameters.IssuerSigningKey = Key;
            options.TokenValidationParameters.ValidIssuer = Issuer;
        }));

    private static string Token(string oid, string audience = "agents", bool app = false)
        => new JwtSecurityTokenHandler().WriteToken(new JwtSecurityToken(Issuer, audience,
            new[] { new Claim("tid", Tenant), new Claim("oid", oid), new Claim(app ? "idtyp" : "scp", app ? "app" : "agent.invoke") },
            expires: DateTime.UtcNow.AddMinutes(5), signingCredentials: new SigningCredentials(Key, SecurityAlgorithms.HmacSha256)));

    [TestMethod]
    public async Task ValidatedUser_RoutesThroughSharedBinding_AndCannotReadAnotherUsersOrAgentsTasks()
    {
        await using var host = await StartAsync();
        host.Client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", Token(Alice));
        var response = await host.PostJsonAsync("/a2a/assistant", FabrCoreA2ATestHost.MessageSendRequest("hi"));
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var id = body.RootElement.GetProperty("result").GetProperty("task").GetProperty("id").GetString();
        Assert.AreEqual($"entra-{Tenant}-{Alice}", host.AgentService.Sends.Single().Principal);
        Assert.AreEqual("assistant", host.AgentService.Sends.Single().Handle);
        Assert.AreEqual(0, host.AgentService.Ensured.Single().Configs.Single().Plugins.Count);
        Assert.AreEqual(HttpStatusCode.OK, (await host.Client.GetAsync($"/a2a/assistant/tasks/{id}")).StatusCode);
        Assert.AreEqual(HttpStatusCode.NotFound, (await host.Client.GetAsync($"/a2a/other/tasks/{id}")).StatusCode);
        host.Client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", Token(Bob));
        Assert.AreEqual(HttpStatusCode.NotFound, (await host.Client.GetAsync($"/a2a/assistant/tasks/{id}")).StatusCode);
        foreach (var method in new[] { "GetTask", "CancelTask", "SubscribeToTask" })
        {
            var denied = await host.PostJsonAsync("/a2a/assistant", JsonSerializer.Serialize(new { jsonrpc = "2.0", id = 2, method, @params = new { id } }));
            using var denial = JsonDocument.Parse(await denied.Content.ReadAsStringAsync());
            Assert.AreEqual(-32001, denial.RootElement.GetProperty("error").GetProperty("code").GetInt32());
        }
        using var list = await host.GetJsonAsync("/a2a/assistant/tasks");
        Assert.AreEqual(0, list.RootElement.GetProperty("tasks").GetArrayLength());
    }

    [TestMethod]
    public async Task WrongAudience_IsRejected_AndAppTokenCannotAliasUser()
    {
        await using var host = await StartAsync();
        host.Client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", Token(Alice, "wrong"));
        Assert.AreEqual(HttpStatusCode.Unauthorized, (await host.PostJsonAsync("/a2a/assistant", FabrCoreA2ATestHost.MessageSendRequest("hi"))).StatusCode);
        host.Client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", Token(Alice, app: true));
        await host.PostJsonAsync("/a2a/assistant", FabrCoreA2ATestHost.MessageSendRequest("hi"));
        Assert.AreEqual($"app-{Tenant}-{Alice}", host.AgentService.Sends.Single().Principal);
    }

    [TestMethod]
    public async Task V1Rest_UsesNewEnvelopeAndRejectsOldVersionAndClientAssignedTaskIds()
    {
        await using var host = await StartAsync();
        host.Client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", Token(Alice));
        var response = await host.PostJsonAsync("/a2a/assistant/message:send", """{"message":{"messageId":"m1","role":"ROLE_USER","parts":[{"text":"hello"}]}}""");
        Assert.AreEqual("application/a2a+json", response.Content.Headers.ContentType!.MediaType);
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.AreEqual("TASK_STATE_COMPLETED", body.RootElement.GetProperty("task").GetProperty("status").GetProperty("state").GetString());
        var invalid = await host.PostJsonAsync("/a2a/assistant/message:send", """{"message":{"messageId":"m2","taskId":"chosen","role":"ROLE_USER","parts":[{"text":"hello"}]}}""");
        Assert.AreEqual(HttpStatusCode.NotFound, invalid.StatusCode);
        Assert.AreEqual(1, host.AgentService.Sends.Count);
        host.Client.DefaultRequestHeaders.Remove("A2A-Version");
        Assert.AreEqual(HttpStatusCode.BadRequest, (await host.Client.GetAsync("/a2a/assistant/tasks")).StatusCode);
        host.Client.DefaultRequestHeaders.Add("A2A-Version", "0.3");
        Assert.AreEqual(HttpStatusCode.BadRequest, (await host.Client.GetAsync("/a2a/assistant/tasks")).StatusCode);
    }

    [TestMethod]
    public async Task InvalidMessagesAndUnsupportedPush_DoNotStartAgentWork()
    {
        await using var host = await StartAsync();
        host.Client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", Token(Alice));
        foreach (var payload in new[]
        {
            """{"message":null}""",
            """{"message":{"parts":null}}""",
            """{"message":{"parts":[{"text":"hi"}]},"configuration":{"returnImmediately":"invalid"}}""",
            """{"message":{"parts":[{"text":"hi"}]},"configuration":{"taskPushNotificationConfig":{"url":"https://example.test"}}}""",
        })
        {
            var response = await host.PostJsonAsync("/a2a/assistant/message:send", payload);
            Assert.AreEqual(HttpStatusCode.BadRequest, response.StatusCode);
            Assert.AreEqual("application/problem+json", response.Content.Headers.ContentType!.MediaType);
        }
        Assert.AreEqual(0, host.AgentService.Sends.Count);
    }
}
