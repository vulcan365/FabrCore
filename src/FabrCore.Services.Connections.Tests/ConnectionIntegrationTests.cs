using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using FabrCore.Connections;
using FabrCore.Core;
using FabrCore.Host;
using FabrCore.Host.Configuration;
using FabrCore.Host.Services;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace FabrCore.Services.Connections.Tests;

[TestClass, DoNotParallelize]
public sealed class ConnectionIntegrationTests
{
    [TestMethod]
    public async Task ClusterPersistsProtectedProfilesAndConsumesOwnerBoundHandoffsOnce()
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = "Development", ApplicationName = typeof(FabrCoreHostExtensions).Assembly.GetName().Name });
        builder.Configuration.Sources.Clear();
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?> { ["FabrCore:AdminAuthentication:ApiKey"] = "test-admin" });
        builder.Logging.ClearProviders(); builder.WebHost.UseTestServer();
        builder.AddFabrCoreServer(new FabrCoreServerOptions { AdditionalAssemblies = [typeof(ConnectionsExtensions).Assembly] }.UseConfigurationStore<TestConfigurationStore>());
        builder.Services.AddSingleton<IConnectionHandoffPrincipalValidator, TestProofValidator>();
        // Default Localhost protection needs no application setup or assertion flag.
        builder.Services.AddFabrCoreConnections(o => { o.Enabled = true; o.ClientHandoffEnabled = true; });
        await using var app = builder.Build(); app.UseFabrCoreServer(); app.MapFabrCoreConnections();
        await app.StartAsync();
        try
        {
            using var client = app.GetTestClient();
            const string root = "/fabrcoreapi/admin/v1/principals/alice/connections/work";
            Assert.AreEqual(HttpStatusCode.Unauthorized, (await client.GetAsync(root)).StatusCode);
            client.DefaultRequestHeaders.Authorization = new("Bearer", "test-admin");
            var profile = new ConnectionProfile {
                Name = "work", Enabled = true, Authentication = ConnectionAuthentication.ClientCredentials,
                Authority = "https://login.microsoftonline.com/test/v2.0", ClientId = "test-client", CredentialReference = "mail-app",
                AllowedAgents = ["alice:mail"], Resources = new() { ["graph"] = new() { BaseUrl = "https://graph.microsoft.com/v1.0/", Scopes = ["https://graph.microsoft.com/.default"] } }
            };
            async Task<HttpResponseMessage> Save(string revision)
            {
                using var request = new HttpRequestMessage(HttpMethod.Put, root) { Content = JsonContent.Create(profile) };
                request.Headers.IfMatch.Add(revision == "*" ? EntityTagHeaderValue.Any : new EntityTagHeaderValue($"\"{revision}\""));
                return await client.SendAsync(request);
            }
            using var saved = await Save("*"); Assert.AreEqual(HttpStatusCode.OK, saved.StatusCode, await saved.Content.ReadAsStringAsync());
            var status = (await saved.Content.ReadFromJsonAsync<ConnectionStatus>())!;
            using var conflict = await Save("*"); Assert.AreEqual(HttpStatusCode.PreconditionFailed, conflict.StatusCode);
            var stored = await app.Services.GetRequiredService<IUserScopedFabrCoreStorageProvider>().GetAsync<string>("alice", "fabrcore.protected-connections", "catalog");
            Assert.IsNotNull(stored); Assert.IsFalse(stored.Contains("test-client")); Assert.IsFalse(stored.Contains("mail-app"));
            var service = app.Services.GetRequiredService<IConnectionService>();
            var version = await service.GetSessionVersionAsync("alice", "alice:mail", new("alice", "work"));
            using var oldClient = await service.GetHttpClientAsync("alice", "alice:mail", new("alice", "work"), "graph");
            async Task<ConnectionHandoffEnvelope> Envelope(string proof)
            {
                using var response = await client.PostAsync(root + "/handoff", null);
                Assert.AreEqual(HttpStatusCode.OK, response.StatusCode, await response.Content.ReadAsStringAsync());
                return ConnectionHandoff.Encrypt((await response.Content.ReadFromJsonAsync<ConnectionHandoffChallenge>())!, new() { UserProof = proof, Operation = "disconnect" });
            }
            var wrongOwner = await Envelope("bob-proof");
            using var denied = await client.PostAsJsonAsync(root + "/handoff/complete", wrongOwner);
            Assert.AreEqual(HttpStatusCode.Forbidden, denied.StatusCode);
            Assert.AreEqual(version, await service.GetSessionVersionAsync("alice", "alice:mail", new("alice", "work")));
            var envelope = await Envelope("alice-proof");
            Assert.IsFalse(JsonSerializer.Serialize(envelope).Contains("alice-proof"));
            using var completed = await client.PostAsJsonAsync(root + "/handoff/complete", envelope);
            Assert.AreEqual(HttpStatusCode.OK, completed.StatusCode, await completed.Content.ReadAsStringAsync());
            Assert.AreNotEqual(version, await service.GetSessionVersionAsync("alice", "alice:mail", new("alice", "work")));
            var changed = await Assert.ThrowsExactlyAsync<ConnectionException>(() => oldClient.GetAsync("users"));
            Assert.AreEqual("connection-changed", changed.Code);
            using var replay = await client.PostAsJsonAsync(root + "/handoff/complete", envelope);
            Assert.AreEqual(HttpStatusCode.BadRequest, replay.StatusCode);
            using var updated = await Save(status.Revision); Assert.AreEqual(HttpStatusCode.OK, updated.StatusCode);
            StringAssert.Contains(await client.GetStringAsync("/fabrcoreapi/admin/v1/capabilities"), "connections");
            var openApi = await client.GetStringAsync("/fabrcoreapi/admin/v1/openapi.json");
            StringAssert.Contains(openApi, "/connections/{name}/handoff");
            StringAssert.Contains(openApi, "If-Match");
        }
        finally { await app.StopAsync(); }
    }

    [TestMethod]
    public void HandoffRejectsTamperingAndAnotherChallenge()
    {
        using var rsa = RSA.Create(2048); var expires = DateTimeOffset.UtcNow.AddMinutes(1);
        var pending = new PendingHandoff(Convert.ToBase64String(rsa.ExportPkcs8PrivateKey()), expires);
        var challenge = new ConnectionHandoffChallenge("id", Convert.ToBase64String(rsa.ExportSubjectPublicKeyInfo()), expires);
        var envelope = ConnectionHandoff.Encrypt(challenge, new() { UserProof = "proof", Operation = "complete", AuthorizationCode = "sensitive" });
        Assert.AreEqual("sensitive", ConnectionGrain.DecryptHandoff(envelope, pending).AuthorizationCode);
        Assert.ThrowsExactly<ConnectionException>(() => ConnectionGrain.DecryptHandoff(envelope with { Id = "another" }, pending));
        Assert.ThrowsExactly<ConnectionException>(() => ConnectionGrain.DecryptHandoff(envelope with { Tag = Convert.ToBase64String(new byte[16]) }, pending));
    }

    public sealed class TestProofValidator : IConnectionHandoffPrincipalValidator
    {
        public Task<string?> ValidateAsync(string userProof, CancellationToken cancellationToken) => Task.FromResult<string?>(userProof == "alice-proof" ? "alice" : "bob");
    }
    public sealed class TestConfigurationStore : IFabrCoreConfigurationStore
    {
        public bool SupportsWrites => false;
        public Task<FabrCoreConfiguration> GetConfigurationAsync(CancellationToken cancellationToken = default) => Task.FromResult(new FabrCoreConfiguration { ModelConfigurations = [new() { Name = "default", Provider = "OpenAI", Uri = "https://example.invalid", Model = "test", ApiKeyAlias = "" }, new() { Name = "embeddings", Provider = "OpenAI", Uri = "https://example.invalid", Model = "test", ApiKeyAlias = "" }] });
        public Task SaveConfigurationAsync(FabrCoreConfiguration configuration, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }
}
