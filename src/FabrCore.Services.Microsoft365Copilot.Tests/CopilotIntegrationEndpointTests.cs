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

            Assert.IsEmpty(status.Agents);

            foreach (var segment in new[] { IntegrationAdministration.ManifestSegment, IntegrationAdministration.AppPackageSegment })
            {
                using var conflict = await client.GetAsync(AgentRoute("assistant", segment));
                Assert.AreEqual(HttpStatusCode.Conflict, conflict.StatusCode, segment);
                await LogAsync(conflict, $"GET integrations/microsoft365/agents/assistant/{segment} (channel off)");
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
        const string salesClient = "33333333-3333-3333-3333-333333333333";
        const string secret = "placeholder-secret-that-must-never-be-reported";
        await using var app = await StartAsync(new()
        {
            ["Microsoft365Copilot:TenantId"] = tenant,
            ["Microsoft365Copilot:Principal:Strategy"] = "CanonicalEntra",
            ["Microsoft365Copilot:Agents:0:Name"] = "assistant",
            ["Microsoft365Copilot:Agents:0:Route"] = "/api/messages",
            ["Microsoft365Copilot:Agents:0:ClientId"] = client,
            ["Microsoft365Copilot:Agents:0:ClientSecret"] = secret,
            ["Microsoft365Copilot:Agents:0:AgentType"] = "assistant-agent",
            ["Microsoft365Copilot:Agents:0:Manifest:Name"] = "Contoso Assistant",
            ["Microsoft365Copilot:Agents:1:Name"] = "sales",
            ["Microsoft365Copilot:Agents:1:ClientId"] = salesClient,
            ["Microsoft365Copilot:Agents:1:ClientSecret"] = secret,
            ["Microsoft365Copilot:Agents:1:AgentType"] = "sales-agent",
            ["Microsoft365Copilot:Agents:1:Manifest:Name"] = "Contoso Sales",
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
            Assert.AreEqual(IntegrationAdministration.Microsoft365ApiVersion, status.ApiVersion);
            CollectionAssert.AreEqual(new[] { "assistant", "sales" }, status.Agents.Select(a => a.Name).ToArray());
            CollectionAssert.AreEqual(new[] { "/api/messages", "/api/messages/sales" }, status.Agents.Select(a => a.MessagesEndpoint).ToArray());
            CollectionAssert.AreEqual(new[] { client, salesClient }, status.Agents.Select(a => a.ClientId).ToArray());
            Assert.AreEqual("sales", status.Agents[1].AgentHandle);
            Assert.AreEqual("CanonicalEntra", status.PrincipalStrategy);
            Assert.AreEqual(IntegrationAdministration.Warn, status.Status);
            Assert.AreEqual(IntegrationAdministration.Pass, status.Findings.Single(f => f.Id == "token-validation").Status);
            Assert.AreEqual(IntegrationAdministration.Warn, status.Findings.Single(f => f.Id == "credential-type").Status);
            Assert.AreEqual(IntegrationAdministration.Warn, status.Findings.Single(f => f.Id == "turn-state-storage").Status);

            using var manifestResponse = await http.GetAsync(AgentRoute("assistant", IntegrationAdministration.ManifestSegment));
            Assert.AreEqual(HttpStatusCode.OK, manifestResponse.StatusCode);
            Assert.IsTrue(manifestResponse.Headers.CacheControl?.NoStore);
            var manifest = await ReadAsync<JsonElement>(manifestResponse, "GET integrations/microsoft365/agents/assistant/manifest");
            Assert.AreEqual("Contoso Assistant", manifest.GetProperty("name").GetProperty("short").GetString());
            Assert.AreEqual(client, manifest.GetProperty("copilotAgents").GetProperty("customEngineAgents")[0].GetProperty("id").GetString());

            using var salesManifestResponse = await http.GetAsync(AgentRoute("sales", IntegrationAdministration.ManifestSegment));
            var salesManifest = await ReadAsync<JsonElement>(salesManifestResponse, "GET integrations/microsoft365/agents/sales/manifest");
            Assert.AreEqual("Contoso Sales", salesManifest.GetProperty("name").GetProperty("short").GetString());
            Assert.AreEqual(salesClient, salesManifest.GetProperty("bots")[0].GetProperty("botId").GetString());

            using var unknown = await http.GetAsync(AgentRoute("nobody", IntegrationAdministration.AppPackageSegment));
            Assert.AreEqual(HttpStatusCode.NotFound, unknown.StatusCode);
            await LogAsync(unknown, "GET integrations/microsoft365/agents/nobody/app-package");

            using var packageResponse = await http.GetAsync(AgentRoute("sales", IntegrationAdministration.AppPackageSegment));
            Assert.AreEqual(HttpStatusCode.OK, packageResponse.StatusCode);
            Assert.AreEqual("application/zip", packageResponse.Content.Headers.ContentType?.MediaType);
            Assert.AreEqual("appPackage.zip", packageResponse.Content.Headers.ContentDisposition?.FileNameStar ?? packageResponse.Content.Headers.ContentDisposition?.FileName);
            var package = await packageResponse.Content.ReadAsByteArrayAsync();
            Assert.IsTrue(package.Length > 4 && package[0] == (byte)'P' && package[1] == (byte)'K', "The package must be a zip archive.");
            TestContext.WriteLine($"--- GET integrations/microsoft365/agents/sales/app-package: 200 application/zip, {package.Length} bytes");
            using (var zip = new System.IO.Compression.ZipArchive(new MemoryStream(package)))
            using (var packaged = JsonDocument.Parse(zip.GetEntry("manifest.json")!.Open()))
            {
                Assert.AreEqual(salesClient, packaged.RootElement.GetProperty("id").GetString());
            }

            // The developer routes are per agent too. Both messaging routes exist and refuse a caller without a token.
            using var devManifest = await http.GetAsync("/m365copilot/sales/manifest.json");
            Assert.AreEqual(HttpStatusCode.OK, devManifest.StatusCode);
            using var named = await http.GetAsync("/manifests/contoso-sales.json");
            Assert.AreEqual(HttpStatusCode.OK, named.StatusCode);
            using var anonymous = new HttpClient(app.GetTestServer().CreateHandler()) { BaseAddress = http.BaseAddress };
            foreach (var route in new[] { "/api/messages", "/api/messages/sales" })
            {
                using var refused = await anonymous.PostAsync(route, JsonContent.Create(new { type = "message" }));
                Assert.AreEqual(HttpStatusCode.Unauthorized, refused.StatusCode, route);
            }

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
            CollectionAssert.Contains(channel.Features, "multi-agent");
            Assert.AreEqual(IntegrationAdministration.Microsoft365ApiVersion, channel.ApiVersion);
            var a2aService = capabilities.Services.Single(s => s.Name == "a2a");
            Assert.IsTrue(a2aService.Available);
            CollectionAssert.Contains(a2aService.Features, "auth-jwtbearer");

            var state = await http.GetFromJsonAsync<CloudConfigurationState>("/fabrcoreapi/admin/v1/settings/state");
            var rows = state!.Settings.ToDictionary(s => s.Key);
            TestContext.WriteLine("--- GET settings/state (Microsoft365Copilot and A2A rows)");
            TestContext.WriteLine(JsonSerializer.Serialize(
                state.Settings.Where(s => s.Key.Contains("Microsoft365Copilot", StringComparison.Ordinal) || s.Key.StartsWith("A2A:", StringComparison.Ordinal)), Indented));
            Assert.AreEqual(client, rows["Microsoft365Copilot:Agents:0:ClientId"].AppliedValue);
            Assert.AreEqual(salesClient, rows["Microsoft365Copilot:Agents:1:ClientId"].AppliedValue);
            Assert.AreEqual("CanonicalEntra", rows["Microsoft365Copilot:Principal:Strategy"].AppliedValue);
            Assert.AreEqual("/api/messages/sales", rows["Microsoft365Copilot:Agents:1:Route"].AppliedValue);
            Assert.AreEqual("False", rows["Runtime:Microsoft365Copilot:ForwardsUserCredential"].AppliedValue);
            Assert.IsFalse(state.Settings.Any(row => row.AppliedValue == secret), "A client secret must never be reported as an applied value.");
            Assert.AreEqual("JwtBearer", rows["A2A:Authentication:Mode"].AppliedValue);
        }
        finally { await app.StopAsync(); }
    }

    [TestMethod]
    public async Task EachMessagingRoute_ReachesItsOwnAgent_UnderTheCallersPrincipal()
    {
        var provisioner = new RecordingProvisioner();
        await using var app = await StartAsync(new()
        {
            ["Microsoft365Copilot:TokenValidation:Enabled"] = "false",
            ["Microsoft365Copilot:Agents:0:Name"] = "assistant",
            ["Microsoft365Copilot:Agents:0:Route"] = "/api/messages",
            ["Microsoft365Copilot:Agents:0:AgentType"] = "assistant-agent",
            ["Microsoft365Copilot:Agents:1:Name"] = "sales",
            ["Microsoft365Copilot:Agents:1:AgentType"] = "sales-agent",
            ["Microsoft365Copilot:Agents:1:Handle"] = "sales-desk",
        }, services => services.AddSingleton<ICopilotAgentProvisioner>(provisioner));
        try
        {
            using var http = app.GetTestClient();

            static object Activity(string user) => new
            {
                type = "message", id = Guid.NewGuid().ToString("N"), channelId = "msteams", text = "hello",
                serviceUrl = "https://smba.example.invalid/", from = new { id = user }, recipient = new { id = "bot" },
                conversation = new { id = "conversation-" + user },
            };

            using var toAssistant = await http.PostAsync("/api/messages", JsonContent.Create(Activity("ada")));
            Assert.AreEqual(HttpStatusCode.Accepted, toAssistant.StatusCode);
            var first = await provisioner.NextAsync();
            Assert.AreEqual("assistant", first.Agent.Name);
            Assert.AreEqual("assistant", first.Agent.Handle);
            Assert.AreEqual("assistant-agent", first.Agent.AgentType);
            Assert.AreEqual("msteams-ada", first.Principal, "The principal comes from the activity, not from configuration.");

            using var toSales = await http.PostAsync("/api/messages/sales", JsonContent.Create(Activity("grace")));
            Assert.AreEqual(HttpStatusCode.Accepted, toSales.StatusCode);
            var second = await provisioner.NextAsync();
            Assert.AreEqual("sales", second.Agent.Name);
            Assert.AreEqual("sales-desk", second.Agent.Handle);
            Assert.AreEqual("sales-agent", second.Agent.AgentType);
            Assert.AreEqual("msteams-grace", second.Principal);

            using var nowhere = await http.PostAsync("/api/messages/nobody", JsonContent.Create(Activity("ada")));
            Assert.AreEqual(HttpStatusCode.NotFound, nowhere.StatusCode);
        }
        finally { await app.StopAsync(); }
    }

    /// <summary>Records which agent each turn asked for, then stops the turn before it reaches a real agent.</summary>
    private sealed class RecordingProvisioner : ICopilotAgentProvisioner
    {
        private readonly System.Threading.Channels.Channel<(CopilotAgent Agent, string Principal)> _seen =
            System.Threading.Channels.Channel.CreateUnbounded<(CopilotAgent, string)>();

        public Task<string> EnsureAgentAsync(CopilotAgent agent, string principalHandle, CancellationToken cancellationToken)
        {
            _seen.Writer.TryWrite((agent, principalHandle));
            throw new InvalidOperationException("Recorded; the test does not run an agent.");
        }

        public void Invalidate(string principalHandle, string agentHandle)
        {
        }

        public async Task<(CopilotAgent Agent, string Principal)> NextAsync()
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            return await _seen.Reader.ReadAsync(timeout.Token);
        }
    }

    private static string AgentRoute(string agent, string segment) =>
        $"{IntegrationAdministration.Microsoft365Route}/{IntegrationAdministration.AgentsSegment}/{agent}/{segment}";

    private static async Task<WebApplication> StartAsync(
        Dictionary<string, string?> settings, Action<IServiceCollection>? services = null)
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
        services?.Invoke(builder.Services);
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
