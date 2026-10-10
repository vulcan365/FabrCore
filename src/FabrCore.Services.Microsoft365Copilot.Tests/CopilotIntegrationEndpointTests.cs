using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using FabrCore.Core;
using FabrCore.Core.CloudServer;
using FabrCore.Host;
using FabrCore.Host.Services;
using FabrCore.Services.Contracts.Capabilities;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace FabrCore.Services.Microsoft365Copilot.Tests;

/// <summary>
/// Drives the administration routes over HTTP against a real FabrCore server, so authorization,
/// routing, and the wire shape are the shipped ones. Response bodies are written to the test
/// output; the cloud-server how-to quotes them.
/// </summary>
[TestClass, DoNotParallelize]
public sealed class CopilotIntegrationEndpointTests
{
    private const string AdminKey = "test-admin";
    private static readonly JsonSerializerOptions Indented = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    public TestContext TestContext { get; set; } = null!;

    [TestMethod]
    public async Task UnconfiguredHost_StartsWithTheChannelOff_AndStillAnswersAdministrationRoutes()
    {
        await using var app = await StartAsync(new());
        try
        {
            using var client = app.GetTestClient();

            using var anonymous = await client.GetAsync(IntegrationAdministration.Microsoft365Route);
            Assert.AreEqual(HttpStatusCode.Unauthorized, anonymous.StatusCode);
            using var anonymousDiagnostics = await client.PostAsync(
                IntegrationAdministration.Microsoft365Route + "/" + IntegrationAdministration.DiagnosticsSegment, null);
            Assert.AreEqual(HttpStatusCode.Unauthorized, anonymousDiagnostics.StatusCode);

            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", AdminKey);

            using var statusResponse = await client.GetAsync(IntegrationAdministration.Microsoft365Route);
            Assert.AreEqual(HttpStatusCode.OK, statusResponse.StatusCode);
            var status = await ReadAsync<Microsoft365IntegrationStatus>(statusResponse, "GET integrations/microsoft365 (channel off)");
            Assert.IsFalse(status.Enabled);
            Assert.IsFalse(status.Configured);
            Assert.IsNotNull(status.DisabledReason);
            Assert.IsTrue(status.Findings.All(f => f.Status == IntegrationAdministration.Skipped));

            foreach (var segment in new[] { IntegrationAdministration.ManifestSegment, IntegrationAdministration.AppPackageSegment })
            {
                using var conflict = await client.GetAsync(IntegrationAdministration.Microsoft365Route + "/" + segment);
                Assert.AreEqual(HttpStatusCode.Conflict, conflict.StatusCode, segment);
                await LogAsync(conflict, $"GET integrations/microsoft365/{segment} (channel off)");
            }

            using var diagnosticsResponse = await client.PostAsync(
                IntegrationAdministration.Microsoft365Route + "/" + IntegrationAdministration.DiagnosticsSegment, null);
            Assert.AreEqual(HttpStatusCode.OK, diagnosticsResponse.StatusCode);
            var diagnostics = await ReadAsync<IntegrationDiagnosticsReport>(diagnosticsResponse, "POST integrations/microsoft365/diagnostics (channel off)");
            Assert.AreEqual(IntegrationAdministration.Microsoft365, diagnostics.Integration);
            Assert.AreEqual(IntegrationAdministration.Skipped, diagnostics.Status);
            CollectionAssert.IsSubsetOf(
                new[] { "agent-type-registered", "bot-service-credential" }, diagnostics.Checks.Select(c => c.Id).ToArray());

            using var a2aResponse = await client.GetAsync(IntegrationAdministration.A2ARoute);
            Assert.AreEqual(HttpStatusCode.OK, a2aResponse.StatusCode);
            Assert.IsFalse((await ReadAsync<A2AIntegrationStatus>(a2aResponse, "GET integrations/a2a (A2A off)")).Enabled);

            using var spoofed = new HttpRequestMessage(HttpMethod.Get, IntegrationAdministration.Microsoft365Route);
            spoofed.Headers.Add("x-user-handle", "someone");
            using var rejected = await client.SendAsync(spoofed);
            Assert.AreEqual(HttpStatusCode.BadRequest, rejected.StatusCode);

            using var capabilitiesResponse = await client.GetAsync("/fabrcoreapi/capabilities");
            var capabilities = await ReadAsync<ClusterCapabilityDocument>(capabilitiesResponse, "GET capabilities (integrations off)");
            var channel = capabilities.Services.Single(s => s.Name == "microsoft365-copilot");
            Assert.IsFalse(channel.Available);
            Assert.IsFalse(capabilities.Services.Single(s => s.Name == "a2a").Available);

            var state = await client.GetFromJsonAsync<CloudConfigurationState>("/fabrcoreapi/admin/v1/settings/state");
            var enabledRow = state!.Settings.Single(s => s.Key == "Microsoft365Copilot:Enabled");
            Assert.AreEqual("code-default", enabledRow.Source);
            Assert.AreEqual("False", enabledRow.AppliedValue);
            Assert.IsTrue(enabledRow.AppliedKnown);
            Assert.IsFalse(state.Settings.Single(s => s.Key == "Microsoft365Copilot:AuthType").AppliedKnown);
        }
        finally { await app.StopAsync(); }
    }

    [TestMethod]
    public async Task ConfiguredHost_ReportsIdentityAndPosture_AndServesThePackage()
    {
        const string tenant = "11111111-1111-1111-1111-111111111111";
        const string client = "22222222-2222-2222-2222-222222222222";
        const string secret = "placeholder-secret-that-must-never-be-reported";
        await using var app = await StartAsync(new()
        {
            ["Microsoft365Copilot:TenantId"] = tenant,
            ["Microsoft365Copilot:ClientId"] = client,
            ["Microsoft365Copilot:ClientSecret"] = secret,
            ["Microsoft365Copilot:Principal:Strategy"] = "CanonicalEntra",
            ["Microsoft365Copilot:Agent:AgentType"] = "assistant-agent",
            ["Microsoft365Copilot:Manifest:Name"] = "Contoso Assistant",
            ["Microsoft365Copilot:Manifest:PublicHostName"] = "agents.contoso.com",
            ["A2A:Enabled"] = "true",
            ["A2A:PublicBaseUrl"] = "https://agents.contoso.com",
            ["A2A:AgentHandles:0"] = "system:assistant",
            ["A2A:Principal:Strategy"] = "CanonicalEntra",
            ["A2A:Authentication:Mode"] = "JwtBearer",
            ["A2A:Authentication:JwtBearer:Authority"] = $"https://login.microsoftonline.com/{tenant}/v2.0",
            ["A2A:Authentication:JwtBearer:Audience"] = client,
            ["A2A:Authentication:JwtBearer:RequiredScopes:0"] = "agent.invoke",
        });
        try
        {
            using var http = app.GetTestClient();
            http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", AdminKey);

            using var statusResponse = await http.GetAsync(IntegrationAdministration.Microsoft365Route);
            var body = await statusResponse.Content.ReadAsStringAsync();
            Assert.IsFalse(body.Contains(secret, StringComparison.Ordinal), "The client secret must never be reported.");
            var status = await ReadAsync<Microsoft365IntegrationStatus>(statusResponse, "GET integrations/microsoft365 (channel on)");
            Assert.IsTrue(status.Enabled);
            Assert.AreEqual(client, status.ClientId);
            Assert.AreEqual("CanonicalEntra", status.PrincipalStrategy);
            Assert.AreEqual(IntegrationAdministration.Warn, status.Status);
            Assert.AreEqual(IntegrationAdministration.Pass, status.Findings.Single(f => f.Id == "token-validation").Status);
            Assert.AreEqual(IntegrationAdministration.Warn, status.Findings.Single(f => f.Id == "credential-type").Status);
            Assert.AreEqual(IntegrationAdministration.Warn, status.Findings.Single(f => f.Id == "turn-state-storage").Status);

            using var manifestResponse = await http.GetAsync(IntegrationAdministration.Microsoft365Route + "/" + IntegrationAdministration.ManifestSegment);
            Assert.AreEqual(HttpStatusCode.OK, manifestResponse.StatusCode);
            Assert.IsTrue(manifestResponse.Headers.CacheControl?.NoStore);
            var manifest = await ReadAsync<JsonElement>(manifestResponse, "GET integrations/microsoft365/manifest");
            Assert.AreEqual("Contoso Assistant", manifest.GetProperty("name").GetProperty("short").GetString());
            Assert.AreEqual(client, manifest.GetProperty("copilotAgents").GetProperty("customEngineAgents")[0].GetProperty("id").GetString());

            using var packageResponse = await http.GetAsync(IntegrationAdministration.Microsoft365Route + "/" + IntegrationAdministration.AppPackageSegment);
            Assert.AreEqual(HttpStatusCode.OK, packageResponse.StatusCode);
            Assert.AreEqual("application/zip", packageResponse.Content.Headers.ContentType?.MediaType);
            Assert.AreEqual("appPackage.zip", packageResponse.Content.Headers.ContentDisposition?.FileNameStar ?? packageResponse.Content.Headers.ContentDisposition?.FileName);
            var package = await packageResponse.Content.ReadAsByteArrayAsync();
            Assert.IsTrue(package.Length > 4 && package[0] == (byte)'P' && package[1] == (byte)'K', "The package must be a zip archive.");
            TestContext.WriteLine($"--- GET integrations/microsoft365/app-package: 200 application/zip, {package.Length} bytes");

            using var a2aResponse = await http.GetAsync(IntegrationAdministration.A2ARoute);
            var a2a = await ReadAsync<A2AIntegrationStatus>(a2aResponse, "GET integrations/a2a (A2A on)");
            Assert.IsTrue(a2a.Enabled);
            Assert.AreEqual("JwtBearer", a2a.AuthenticationMode);
            CollectionAssert.AreEqual(new[] { "agent.invoke" }, a2a.RequiredScopes);
            Assert.AreEqual("system:assistant", a2a.Agents.Single().AgentHandle);

            using var capabilitiesResponse = await http.GetAsync("/fabrcoreapi/capabilities");
            var capabilities = await ReadAsync<ClusterCapabilityDocument>(capabilitiesResponse, "GET capabilities (integrations on)");
            var channel = capabilities.Services.Single(s => s.Name == "microsoft365-copilot");
            Assert.IsTrue(channel.Available);
            CollectionAssert.Contains(channel.Features, "activity-protocol");
            CollectionAssert.Contains(channel.Features, "principal-canonicalentra");
            var a2aService = capabilities.Services.Single(s => s.Name == "a2a");
            Assert.IsTrue(a2aService.Available);
            CollectionAssert.Contains(a2aService.Features, "auth-jwtbearer");

            var state = await http.GetFromJsonAsync<CloudConfigurationState>("/fabrcoreapi/admin/v1/settings/state");
            var rows = state!.Settings.ToDictionary(s => s.Key);
            TestContext.WriteLine("--- GET settings/state (Microsoft365Copilot and A2A rows)");
            TestContext.WriteLine(JsonSerializer.Serialize(
                state.Settings.Where(s => s.Key.Contains("Microsoft365Copilot", StringComparison.Ordinal) || s.Key.StartsWith("A2A:", StringComparison.Ordinal)), Indented));
            Assert.AreEqual(client, rows["Microsoft365Copilot:ClientId"].AppliedValue);
            Assert.AreEqual("CanonicalEntra", rows["Microsoft365Copilot:Principal:Strategy"].AppliedValue);
            Assert.AreEqual("code-default", rows["Microsoft365Copilot:MessagesEndpoint"].Source);
            Assert.AreEqual("False", rows["Runtime:Microsoft365Copilot:ForwardsUserCredential"].AppliedValue);
            Assert.IsFalse(rows.ContainsKey("Microsoft365Copilot:ClientSecret") && rows["Microsoft365Copilot:ClientSecret"].AppliedValue is not null);
            Assert.AreEqual("JwtBearer", rows["A2A:Authentication:Mode"].AppliedValue);
        }
        finally { await app.StopAsync(); }
    }

    private static async Task<WebApplication> StartAsync(Dictionary<string, string?> settings)
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            EnvironmentName = "Development",
            ApplicationName = typeof(FabrCoreHostExtensions).Assembly.GetName().Name
        });
        builder.Configuration.Sources.Clear();
        settings["FabrCore:AdminAuthentication:ApiKey"] = AdminKey;
        builder.Configuration.AddInMemoryCollection(settings);
        builder.Logging.ClearProviders();
        builder.WebHost.UseTestServer();
        builder.AddFabrCoreServer(new FabrCoreServerOptions().UseConfigurationStore<TestConfigurationStore>());
        builder.AddMicrosoft365Copilot();
        var app = builder.Build();
        app.UseFabrCoreServer();
        app.UseMicrosoft365Copilot();
        await app.StartAsync();
        return app;
    }

    private async Task<T> ReadAsync<T>(HttpResponseMessage response, string label)
    {
        var text = await response.Content.ReadAsStringAsync();
        Log(response, label, text);
        return JsonSerializer.Deserialize<T>(text, JsonSerializerOptions.Web)!;
    }

    private async Task LogAsync(HttpResponseMessage response, string label) =>
        Log(response, label, await response.Content.ReadAsStringAsync());

    private void Log(HttpResponseMessage response, string label, string text)
    {
        TestContext.WriteLine($"--- {label}: {(int)response.StatusCode}");
        try
        {
            using var document = JsonDocument.Parse(text);
            TestContext.WriteLine(JsonSerializer.Serialize(document.RootElement, Indented));
        }
        catch (JsonException) { TestContext.WriteLine(text); }
    }

    public sealed class TestConfigurationStore : IFabrCoreConfigurationStore
    {
        public bool SupportsWrites => false;

        public Task<FabrCoreConfiguration> GetConfigurationAsync(CancellationToken cancellationToken = default) => Task.FromResult(new FabrCoreConfiguration
        {
            ModelConfigurations =
            [
                new() { Name = "default", Provider = "OpenAI", Uri = "https://example.invalid", Model = "test", ApiKeyAlias = "" },
                new() { Name = "embeddings", Provider = "OpenAI", Uri = "https://example.invalid", Model = "test", ApiKeyAlias = "" }
            ]
        });

        public Task SaveConfigurationAsync(FabrCoreConfiguration configuration, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }
}
