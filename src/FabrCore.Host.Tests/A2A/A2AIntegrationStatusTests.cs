using FabrCore.Core.CloudServer;
using FabrCore.Host.A2A;
using FabrCore.Host.Configuration;
using FabrCore.Host.Security;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;

namespace FabrCore.Host.Tests.A2A;

[TestClass]
public sealed class A2AIntegrationStatusTests
{
    private static A2AOptions JwtOptions()
    {
        var options = new A2AOptions { Enabled = true, PublicBaseUrl = "https://agents.example.com" };
        options.Authentication.Mode = A2AAuthenticationMode.JwtBearer;
        options.Authentication.JwtBearer.Authority = "https://login.microsoftonline.com/tenant/v2.0";
        options.Authentication.JwtBearer.Audience = "api-client-id";
        options.Authentication.JwtBearer.RequiredScopes.Add("agent.invoke");
        options.Principal.Strategy = A2APrincipalStrategy.CanonicalEntra;
        return options;
    }

    private static string StatusOf(A2AIntegrationStatus status, string id) => status.Findings.Single(f => f.Id == id).Status;

    [TestMethod]
    public void DisabledEndpoint_SkipsEveryFinding()
    {
        var status = A2AIntegrationStatusBuilder.Build(new A2AOptions(), [], taskStore: null);

        Assert.IsFalse(status.Enabled);
        Assert.AreEqual(IntegrationAdministration.Skipped, status.Status);
        CollectionAssert.AreEqual(
            new[] { "authentication", "api-key-query", "required-scopes", "principal-strategy", "publication", "public-base-url", "task-store" },
            status.Findings.Select(f => f.Id).ToArray());
        Assert.IsTrue(status.Findings.All(f => f.Status == IntegrationAdministration.Skipped));
    }

    [TestMethod]
    public void DelegatedTokenConfiguration_PassesIdentityFindings()
    {
        var status = A2AIntegrationStatusBuilder.Build(JwtOptions(), [Agent("assistant")], typeof(DurableStore));

        Assert.AreEqual(IntegrationAdministration.Pass, status.Status);
        Assert.AreEqual(IntegrationAdministration.Skipped, StatusOf(status, "api-key-query"));
        Assert.AreEqual("JwtBearer", status.AuthenticationMode);
        Assert.AreEqual("CanonicalEntra", status.PrincipalStrategy);
        CollectionAssert.AreEqual(new[] { "api-client-id" }, status.Audiences);
        CollectionAssert.AreEqual(new[] { "agent.invoke" }, status.RequiredScopes);
        Assert.AreEqual(nameof(DurableStore), status.TaskStore);
        Assert.AreEqual(0, status.ApiKeyNames.Count);
    }

    [TestMethod]
    public void ApiKeys_AreReportedByNameOnly()
    {
        const string secret = "s3cr3t-key-value-that-must-not-leak";
        var options = new A2AOptions { Enabled = true };
        options.Authentication.ApiKey.Keys.Add(new() { Name = "copilot-studio", Value = secret });
        options.Authentication.ApiKey.Keys.Add(new() { Value = secret + "-2" });
        options.Authentication.ApiKey.QueryParameterName = "key";

        var status = A2AIntegrationStatusBuilder.Build(options, [], typeof(InMemoryA2ATaskStore));
        var json = JsonSerializer.Serialize(status, JsonSerializerOptions.Web);

        CollectionAssert.AreEqual(new[] { "copilot-studio", "(unnamed)" }, status.ApiKeyNames);
        Assert.IsFalse(json.Contains(secret, StringComparison.Ordinal), "A key value must never appear in the status document.");
        Assert.AreEqual(IntegrationAdministration.Warn, StatusOf(status, "authentication"));
        Assert.AreEqual(IntegrationAdministration.Warn, StatusOf(status, "api-key-query"));
        Assert.AreEqual(IntegrationAdministration.Skipped, StatusOf(status, "required-scopes"));
        Assert.AreEqual(IntegrationAdministration.Warn, StatusOf(status, "principal-strategy"));
        Assert.AreEqual(IntegrationAdministration.Warn, StatusOf(status, "publication"));
        Assert.AreEqual(IntegrationAdministration.Warn, StatusOf(status, "public-base-url"));
        Assert.AreEqual(IntegrationAdministration.Warn, StatusOf(status, "task-store"));
    }

    [TestMethod]
    public void UnsafeConfiguration_Fails()
    {
        var options = JwtOptions();
        options.Authentication.Mode = A2AAuthenticationMode.None;
        options.PublicBaseUrl = "http://agents.example.com";
        options.Discovery.AgentTypes = A2ADiscoveryMode.Described;

        var status = A2AIntegrationStatusBuilder.Build(options, [Agent("assistant")], typeof(DurableStore));

        Assert.AreEqual(IntegrationAdministration.Fail, status.Status);
        Assert.AreEqual(IntegrationAdministration.Fail, StatusOf(status, "authentication"));
        Assert.AreEqual(IntegrationAdministration.Fail, StatusOf(status, "public-base-url"));
        Assert.AreEqual(IntegrationAdministration.Warn, StatusOf(status, "publication"));
        Assert.AreEqual(0, status.Audiences.Count, "Bearer settings are not reported while bearer authentication is off.");
    }

    [TestMethod]
    public void BearerTokensWithoutAScopeOrRole_AreFlagged()
    {
        var options = JwtOptions();
        options.Authentication.JwtBearer.RequiredScopes.Clear();

        var status = A2AIntegrationStatusBuilder.Build(options, [Agent("assistant")], typeof(DurableStore));

        Assert.AreEqual(IntegrationAdministration.Warn, StatusOf(status, "required-scopes"));
    }

    [TestMethod]
    public void PublishedAgents_CarryTheirBindingName()
    {
        var options = JwtOptions();
        options.Agents.Add(new() { Name = "crm-assistant", Binding = "assistant", AgentType = "crm-agent" });

        var status = A2AIntegrationStatusBuilder.Build(options, [Agent("crm-assistant"), Agent("other")], typeof(DurableStore));

        Assert.AreEqual("assistant", status.Agents.Single(a => a.Name == "crm-assistant").Binding);
        Assert.IsNull(status.Agents.Single(a => a.Name == "other").Binding);
        Assert.AreEqual("/a2a/crm-assistant", status.Agents[0].BasePath);
        Assert.AreEqual("Configured", status.Agents[0].Source);
    }

    [TestMethod]
    public async Task Route_RequiresTheAdministrationKey_AndAnswersWhileA2AIsOff()
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = "Test" });
        builder.WebHost.UseTestServer();
        builder.Services.AddAuthentication()
            .AddScheme<FabrCoreAdminAuthenticationOptions, FabrCoreAdminAuthenticationHandler>(
                FabrCoreAdminAuthenticationDefaults.Scheme, options => options.ApiKey = "admin-key");
        builder.Services.Configure<CloudServerOptions>(_ => { });
        builder.Services.Configure<RemoteAdministrationOptions>(_ => { });
        builder.Services.AddAuthorization(options => options.AddPolicy(
            FabrCoreAdminAuthenticationDefaults.Policy,
            policy => policy.AddAuthenticationSchemes(FabrCoreAdminAuthenticationDefaults.Scheme).RequireAuthenticatedUser()));
        builder.Services.AddControllers().AddApplicationPart(typeof(FabrCore.Host.Api.Controllers.IntegrationsAdminController).Assembly);
        await using var app = builder.Build();
        app.UseAuthentication();
        app.UseAuthorization();
        app.MapControllers();
        await app.StartAsync();
        var client = app.GetTestClient();

        using var anonymous = await client.GetAsync(IntegrationAdministration.A2ARoute);
        Assert.AreEqual(HttpStatusCode.Unauthorized, anonymous.StatusCode);

        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "admin-key");
        var status = await client.GetFromJsonAsync<A2AIntegrationStatus>(IntegrationAdministration.A2ARoute);
        Assert.IsNotNull(status);
        Assert.IsFalse(status.Enabled);
        Assert.AreEqual("1", status.ApiVersion);

        using var spoofed = new HttpRequestMessage(HttpMethod.Get, IntegrationAdministration.A2ARoute);
        spoofed.Headers.Add("x-user-handle", "someone");
        using var rejected = await client.SendAsync(spoofed);
        Assert.AreEqual(HttpStatusCode.BadRequest, rejected.StatusCode);
    }

    private static A2AExposedAgent Agent(string name) => new()
    {
        Name = name, BasePath = "/a2a/" + name, DisplayName = name, Description = "test", Source = A2AExposureSource.Configured,
        AgentType = "crm-agent", Models = "default", Plugins = [], Tools = [], Args = new Dictionary<string, string>(),
        AgentPerContext = false, InputModes = [], OutputModes = [], Streaming = true, Version = "1.0.0", Skills = [], Notes = [],
        HarnessSkills = []
    };

    private sealed class DurableStore;
}
