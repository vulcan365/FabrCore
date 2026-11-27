using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FabrCore.Connections;
using FabrCore.Core;
using FabrCore.Host;
using FabrCore.Host.Configuration;
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
public sealed class ManagedAgentIdentityTests
{
    [TestMethod]
    public async Task BindingsRequireCurrentRegistrationAndSurviveSiblingRemoval()
    {
        var appBuilder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = "Development",
            ApplicationName = typeof(FabrCoreHostExtensions).Assembly.GetName().Name });
        appBuilder.Configuration.Sources.Clear();
        appBuilder.Configuration.AddInMemoryCollection(new Dictionary<string, string?> { ["FabrCore:AdminAuthentication:ApiKey"] = "integration-admin" });
        appBuilder.Logging.ClearProviders();
        appBuilder.WebHost.UseTestServer();
        appBuilder.AddFabrCoreServer(new FabrCoreServerOptions { AdditionalAssemblies =
            [typeof(ConnectionsExtensions).Assembly, typeof(RegistrationTestAgent).Assembly] }
            .UseConfigurationStore<ConnectionIntegrationTests.TestConfigurationStore>());
        appBuilder.Services.AddFabrCoreConnections(o => { o.Enabled = true; o.EntraAgentIdEnabled = true; });
        var wire = new OAuthProviderTests.IdentityProvider();
        using var tokenEndpoint = new HttpClient(wire);
        appBuilder.Services.AddSingleton(new OAuthProvider(new OAuthProviderTests.CredentialProvider(), tokenEndpoint));
        await using var app = appBuilder.Build();
        app.UseFabrCoreServer(); app.MapFabrCoreConnections();
        await app.StartAsync();
        try
        {
            using var client = app.GetTestClient();
            client.DefaultRequestHeaders.Authorization = new("Bearer", "integration-admin");
            var runtime = app.Services.GetRequiredService<IFabrCoreAgentService>();
            var connections = app.Services.GetRequiredService<IConnectionService>();
            const string owner = "pilot";
            async Task Register(string agent) => await runtime.ConfigureAgentsAsync(owner, [new() { Handle = agent, AgentType = "registration-test" }]);
            async Task<Dictionary<string, Guid>> Registrations()
            {
                using var response = await client.GetAsync("/fabrcoreapi/admin/v1/principals/pilot/agents/registrations");
                response.EnsureSuccessStatusCode();
                var page = await response.Content.ReadFromJsonAsync<JsonElement>();
                return page.GetProperty("items").EnumerateArray().ToDictionary(a => a.GetProperty("handle").GetString()!, a => a.GetProperty("registrationId").GetGuid());
            }
            var binding = new ConnectionBinding(owner, "$agent-identity");
            Task<string> Session(string agent) => connections.GetSessionVersionAsync(owner, owner + ":" + agent, binding);
            await Register("a"); await Register("b");
            var members = await Registrations();
            Assert.AreNotEqual(members["pilot:a"], members["pilot:b"]);
            var id = Guid.NewGuid(); var tenant = Guid.NewGuid();
            var profile = new ConnectionProfile { Name = $"identity-{id:N}", Enabled = true,
                Authentication = ConnectionAuthentication.AgentIdApplication, ClientId = Guid.NewGuid().ToString(),
                AgentIdentityClientId = Guid.NewGuid().ToString(), CredentialReference = "forge-host",
                Authority = $"https://login.microsoftonline.com/{tenant}/v2.0", AllowedAgents = [..members.Keys],
                ManagedIdentity = new(id, tenant, Guid.NewGuid(), true, members, "https://example.invalid/manage"),
                Resources = new() { ["graph"] = new() { BaseUrl = "https://graph.microsoft.com/", Scopes = ["https://graph.microsoft.com/.default"] } } };
            var root = "/fabrcoreapi/admin/v1/principals/pilot/connections/" + profile.Name;
            async Task<HttpResponseMessage> Save(string path, string revision)
            {
                using var request = new HttpRequestMessage(HttpMethod.Put, root + path) { Content = JsonContent.Create(profile) };
                request.Headers.TryAddWithoutValidation("If-Match", revision);
                return await client.SendAsync(request);
            }
            // Set the enrollment-key authentication options on this isolated in-memory Host.
            // No sync service or remote endpoint is needed to exercise the actual HTTP policy.
            var cloud = app.Services.GetRequiredService<Microsoft.Extensions.Options.IOptionsMonitor<CloudServerOptions>>().CurrentValue;
            var remote = app.Services.GetRequiredService<Microsoft.Extensions.Options.IOptionsMonitor<RemoteAdministrationOptions>>().CurrentValue;
            cloud.ApiKey = "integration-enrollment";
            cloud.Enabled = remote.Enabled = true;
            client.DefaultRequestHeaders.Authorization = new("Bearer", cloud.ApiKey);
            using var inventory = await client.GetAsync("/fabrcoreapi/admin/v1/principals/pilot/agents/registrations");
            Assert.AreEqual(HttpStatusCode.OK, inventory.StatusCode);
            using var forbidden = await Save("/managed-identity", "*");
            Assert.AreEqual(HttpStatusCode.Forbidden, forbidden.StatusCode);
            client.DefaultRequestHeaders.Authorization = new("Bearer", "integration-admin");
            using var created = await Save("/managed-identity", "*");
            Assert.IsTrue(created.IsSuccessStatusCode, await created.Content.ReadAsStringAsync());
            var revision = (await created.Content.ReadFromJsonAsync<ConnectionStatus>())!.Revision;
            Assert.AreEqual(await Session("a"), await Session("b"));
            await Assert.ThrowsExactlyAsync<ConnectionException>(() => Session("c"));
            using var stale = await Save("/managed-identity", "*");
            Assert.AreEqual(HttpStatusCode.PreconditionFailed, stale.StatusCode);
            using var ordinary = await Save("", revision);
            Assert.IsFalse(ordinary.IsSuccessStatusCode);
            // An older Insights form can omit all management metadata. The stored profile still determines ownership.
            client.DefaultRequestHeaders.Authorization = new("Bearer", cloud.ApiKey);
            var original = await client.GetStringAsync(root);
            var ordinaryProfile = JsonSerializer.SerializeToNode(profile, JsonSerializerOptions.Web)!.AsObject();
            ordinaryProfile.Remove("managedIdentity");
            ordinaryProfile["enabled"] = false;
            foreach (var expectedRevision in new[] { "*", revision })
            {
                using var edit = new HttpRequestMessage(HttpMethod.Put, root) { Content = JsonContent.Create(ordinaryProfile) };
                edit.Headers.TryAddWithoutValidation("If-Match", expectedRevision);
                using var rejected = await client.SendAsync(edit);
                Assert.IsFalse(rejected.IsSuccessStatusCode);
            }
            using var clear = await client.DeleteAsync(root + "/authorization");
            Assert.IsFalse(clear.IsSuccessStatusCode);
            ordinaryProfile["name"] = "independent";
            ordinaryProfile["agentIdentityClientId"] = Guid.NewGuid().ToString();
            ordinaryProfile["enabled"] = true;
            using var add = new HttpRequestMessage(HttpMethod.Put, root[..(root.LastIndexOf('/') + 1)] + "independent")
                { Content = JsonContent.Create(ordinaryProfile) };
            add.Headers.TryAddWithoutValidation("If-Match", "*");
            using var added = await client.SendAsync(add);
            Assert.IsTrue(added.IsSuccessStatusCode, await added.Content.ReadAsStringAsync());
            await Register("a");
            Assert.AreEqual(members["pilot:a"], (await Registrations())["pilot:a"]);
            Assert.AreEqual(original, await client.GetStringAsync(root));
            client.DefaultRequestHeaders.Authorization = new("Bearer", "integration-admin");
            var cachedVersion = await Session("a");
            using var renew = new HttpRequestMessage(HttpMethod.Post, root + "/managed-identity/renew");
            renew.Headers.TryAddWithoutValidation("If-Match", revision);
            using var renewed = await client.SendAsync(renew);
            Assert.IsTrue(renewed.IsSuccessStatusCode);
            Assert.AreNotEqual(cachedVersion, await Session("a"));
            await runtime.EvictAgentAsync(owner, "pilot:a");
            await Register("a");
            Assert.AreNotEqual(members["pilot:a"], (await Registrations())["pilot:a"]);
            await Assert.ThrowsExactlyAsync<ConnectionException>(() => Session("a"));
            await Session("b");
            members.Remove("pilot:a");
            profile.AllowedAgents = ["pilot:b"];
            using var removed = await Save("/managed-identity", revision);
            Assert.IsTrue(removed.IsSuccessStatusCode, await removed.Content.ReadAsStringAsync());
            await Session("b");
            await Assert.ThrowsExactlyAsync<ConnectionException>(() => Session("a"));

            // Link the replacement A to its own child. The in-process identity provider records both exchanges.
            var childB = profile.AgentIdentityClientId;
            var childA = Guid.NewGuid().ToString();
            var secondId = Guid.NewGuid();
            var registrationA = (await Registrations())["pilot:a"];
            profile.Name = $"identity-{secondId:N}";
            profile.AgentIdentityClientId = childA;
            profile.AllowedAgents = ["pilot:a"];
            profile.ManagedIdentity = new(secondId, tenant, Guid.NewGuid(), false, new() { ["pilot:a"] = registrationA }, "https://example.invalid/manage");
            root = "/fabrcoreapi/admin/v1/principals/pilot/connections/" + profile.Name;
            using var dedicated = await Save("/managed-identity", "*");
            Assert.IsTrue(dedicated.IsSuccessStatusCode, await dedicated.Content.ReadAsStringAsync());
            var secondRevision = (await dedicated.Content.ReadFromJsonAsync<ConnectionStatus>())!.Revision;
            async Task<string> Token(string agent) =>
                (await connections.GetAccessTokenAsync(owner, owner + ":" + agent, binding, "graph")).AccessToken;
            var aToken = await Token("a");
            var bToken = await Token("b");
            Assert.AreNotEqual(aToken, bToken);
            CollectionAssert.AreEqual(new[] { childA, childB }, wire.Forms.Where(f => f.ContainsKey("fmi_path")).Select(f => f["fmi_path"]).ToArray());
            CollectionAssert.AreEqual(new[] { childA, childB }, wire.Forms.Where(f => !f.ContainsKey("fmi_path")).Select(f => f["client_id"]).ToArray());
            using var renewA = new HttpRequestMessage(HttpMethod.Post, root + "/managed-identity/renew");
            renewA.Headers.TryAddWithoutValidation("If-Match", secondRevision);
            using var refresh = await client.SendAsync(renewA);
            refresh.EnsureSuccessStatusCode();
            Assert.AreNotEqual(aToken, await Token("a"));
            Assert.AreEqual(bToken, await Token("b"));
            profile.Enabled = false;
            using var disabled = await Save("/managed-identity", secondRevision);
            disabled.EnsureSuccessStatusCode();
            await Assert.ThrowsExactlyAsync<ConnectionException>(() => Token("a"));
            Assert.AreEqual(bToken, await Token("b"));
        }
        finally { await app.StopAsync(); }
    }
}

[AgentAlias("registration-test")]
public sealed class RegistrationTestAgent(AgentConfiguration config, IServiceProvider services, IFabrCoreAgentHost host)
    : FabrCoreAgentProxy(config, services, host)
{
    public override Task OnInitialize() => Task.CompletedTask;
    public override Task<AgentMessage> OnMessage(AgentMessage message) => Task.FromResult(message.Response());
}
