using System.Net.Http.Json;
using System.Text.Json;
using FabrCore.Connections;
using FabrCore.Core;
using FabrCore.Host;
using FabrCore.Host.Services;
using FabrCore.Sdk;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace FabrCore.Services.Connections.Tests;

[TestClass, DoNotParallelize]
public sealed class PrincipalTypeIdentityTests
{
    [TestMethod]
    public async Task OnePolicyCoversFutureAndRecreatedAgentsButRejectsOtherPrincipalsAndTypes()
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = "Development",
            ApplicationName = typeof(FabrCoreHostExtensions).Assembly.GetName().Name });
        builder.Configuration.Sources.Clear();
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?> { ["FabrCore:AdminAuthentication:ApiKey"] = "integration-admin" });
        builder.Logging.ClearProviders();
        builder.WebHost.UseTestServer();
        builder.AddFabrCoreServer(new FabrCoreServerOptions { AdditionalAssemblies =
            [typeof(ConnectionsExtensions).Assembly, typeof(RegistrationTestAgent).Assembly] }
            .UseConfigurationStore<ConnectionIntegrationTests.TestConfigurationStore>());
        builder.Services.AddFabrCoreConnections(o => { o.Enabled = true; o.EntraAgentIdEnabled = true; });
        using var tokenEndpoint = new HttpClient(new OAuthProviderTests.IdentityProvider());
        builder.Services.AddSingleton(new OAuthProvider(new OAuthProviderTests.CredentialProvider(), tokenEndpoint));
        await using var app = builder.Build();
        app.UseFabrCoreServer(); app.MapFabrCoreConnections();
        await app.StartAsync();
        try
        {
            using var client = app.GetTestClient();
            client.DefaultRequestHeaders.Authorization = new("Bearer", "integration-admin");
            var runtime = app.Services.GetRequiredService<IFabrCoreAgentService>();
            var connections = app.Services.GetRequiredService<IConnectionService>();
            const string owner = "pilot", type = "registration-test";
            var id = Guid.NewGuid(); var tenant = Guid.NewGuid();
            var profile = new ConnectionProfile { Name = $"identity-{id:N}", Enabled = true,
                Authentication = ConnectionAuthentication.AgentIdApplication, ClientId = Guid.NewGuid().ToString(),
                AgentIdentityClientId = Guid.NewGuid().ToString(), CredentialReference = "forge-host",
                Authority = $"https://login.microsoftonline.com/{tenant}/v2.0", AllowedAgentTypes = [type],
                ManagedIdentity = new(id, tenant, Guid.NewGuid(), true, [], "https://example.invalid/manage") { AgentType = type },
                Resources = new() { ["graph"] = new() { BaseUrl = "https://graph.microsoft.com/", Scopes = ["https://graph.microsoft.com/.default"] } } };
            string Root(ConnectionProfile p) => $"/fabrcoreapi/admin/v1/principals/{owner}/connections/{p.Name}";
            async Task<HttpResponseMessage> Save(ConnectionProfile p, string revision = "*")
            {
                using var request = new HttpRequestMessage(HttpMethod.Put, Root(p) + "/managed-identity") { Content = JsonContent.Create(p) };
                request.Headers.TryAddWithoutValidation("If-Match", revision);
                return await client.SendAsync(request);
            }
            async Task Register(string principal, string agent, string agentType = type)
                => await runtime.ConfigureAgentsAsync(principal, [new() { Handle = agent, AgentType = agentType }]);
            Task<string> Session(string principal, string handle, string agentType = type)
                => connections.GetSessionVersionAsync(new ConnectionCaller(principal, handle, agentType), new(owner, "$agent-identity"));
            // Configure before any instances exist; the binding has no registration membership.
            using var created = await Save(profile);
            Assert.IsTrue(created.IsSuccessStatusCode, await created.Content.ReadAsStringAsync());
            var revision = (await created.Content.ReadFromJsonAsync<ConnectionStatus>())!.Revision;
            var original = await client.GetStringAsync(Root(profile));
            await Register(owner, "a");
            var version = await Session(owner, "pilot:a");
            await Register(owner, "b"); await Register(owner, "c");
            Assert.AreEqual(version, await Session(owner, "pilot:b"));
            Assert.AreEqual(version, await Session(owner, "pilot:c"));
            var binding = new ConnectionBinding(owner, "$agent-identity");
            var firstToken = await connections.GetAccessTokenAsync(new ConnectionCaller(owner, "pilot:a", type), binding, "graph");
            var siblingToken = await connections.GetAccessTokenAsync(new ConnectionCaller(owner, "pilot:b", type), binding, "graph");
            Assert.AreEqual(firstToken.AccessToken, siblingToken.AccessToken);
            await runtime.EvictAgentAsync(owner, "pilot:b");
            await Assert.ThrowsExactlyAsync<ConnectionException>(() => Session(owner, "pilot:b"));
            await Register(owner, "b");
            Assert.AreEqual(version, await Session(owner, "pilot:b"));
            Assert.AreEqual(original, await client.GetStringAsync(Root(profile)));
            await Register("other", "a");
            await Assert.ThrowsExactlyAsync<ConnectionException>(() => Session("other", "other:a"));
            await Register(owner, "different", "identity-other-test");
            await Assert.ThrowsExactlyAsync<ConnectionException>(() => Session(owner, "pilot:different", "identity-other-test"));
            await Assert.ThrowsExactlyAsync<ConnectionException>(() => Session(owner, "pilot:different"));
            await Assert.ThrowsExactlyAsync<ConnectionException>(() => Session(owner, "pilot:unregistered"));
            await Assert.ThrowsExactlyAsync<ConnectionException>(() => Session(owner, "other:a"));
            await Assert.ThrowsExactlyAsync<ConnectionException>(() => Session(owner, "pilot:unregistered"));
            await Assert.ThrowsExactlyAsync<ConnectionException>(() => Session(owner, "pilot:a", "wrong-type"));
            await Register(owner, "wrong", "identity-other-test");
            await Assert.ThrowsExactlyAsync<ConnectionException>(() => Session(owner, "pilot:wrong"));
            // A second type policy and an overlapping exact policy are rejected before creating ambiguity.
            var secondId = Guid.NewGuid();
            var duplicate = JsonSerializer.Deserialize<ConnectionProfile>(JsonSerializer.Serialize(profile))!;
            duplicate.Name = $"identity-{secondId:N}";
            duplicate.AgentIdentityClientId = Guid.NewGuid().ToString();
            duplicate.ManagedIdentity = profile.ManagedIdentity with { BindingId = secondId, ObjectId = Guid.NewGuid() };
            using var overlappingType = await Save(duplicate);
            Assert.IsFalse(overlappingType.IsSuccessStatusCode);
            var registrations = await client.GetFromJsonAsync<JsonElement>("/fabrcoreapi/admin/v1/principals/pilot/agents/registrations");
            var registration = registrations.GetProperty("items").EnumerateArray().Single(a => a.GetProperty("handle").GetString() == "pilot:a").GetProperty("registrationId").GetGuid();
            duplicate.AllowedAgentTypes = [];
            duplicate.AllowedAgents = ["pilot:a"];
            duplicate.ManagedIdentity = duplicate.ManagedIdentity with { AgentType = null, Members = new() { ["pilot:a"] = registration } };
            using var overlappingMember = await Save(duplicate);
            Assert.IsFalse(overlappingMember.IsSuccessStatusCode);
            profile.Enabled = false;
            using var disabled = await Save(profile, revision);
            Assert.IsTrue(disabled.IsSuccessStatusCode);
            await Assert.ThrowsExactlyAsync<ConnectionException>(() => Session(owner, "pilot:a"));
        }
        finally { await app.StopAsync(); }
    }
}

[AgentAlias("identity-other-test")]
public sealed class OtherIdentityTestAgent(AgentConfiguration config, IServiceProvider services, IFabrCoreAgentHost host)
    : FabrCoreAgentProxy(config, services, host)
{
    public override Task OnInitialize() => Task.CompletedTask;
    public override Task<AgentMessage> OnMessage(AgentMessage message) => Task.FromResult(message.Response());
}
