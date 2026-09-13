using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using FabrCore.Core;
using FabrCore.Core.CloudServer;
using FabrCore.Sdk;
using FabrCore.Host.Services;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.AI;

namespace FabrCore.Host.Tests;

[TestClass, DoNotParallelize]
public sealed class CloudAdministrationIntegrationTests
{
    [TestMethod, TestCategory("SqlMode")]
    public async Task PrincipalDirectoryIncludesInactiveRegistrationsAndEnforcementChecksVersions()
    {
        var connection = Environment.GetEnvironmentVariable("FABRCORE_SQL_TEST_CONNECTION_STRING");
        if (string.IsNullOrEmpty(connection)) Assert.Inconclusive("An isolated SQL integration database is required.");
        await using var app = DatabaseModeTests.BuildHost("Development", connection);
        await app.StartAsync();
        try
        {
            var registry = app.Services.GetRequiredService<FabrCore.Core.Acl.IAclEntityStore>();
            await registry.UpsertPrincipalAsync(new() { Handle = "inactive-directory", DisplayName = "Inactive registration" });
            await app.Services.GetRequiredService<IFabrCoreAgentService>().ConfigureAgentsAsync("runtime-directory", [new() { Handle = "echo", AgentType = "database-mode-echo" }]);
            using var http = app.GetTestClient(); http.DefaultRequestHeaders.Authorization = new("Bearer", "test-admin");
            var client = new FabrCoreAdministrationClient(http);
            var page = await client.GetPrincipalPageAsync();
            var inactive = page.Items.Single(p => p.Handle == "inactive-directory");
            Assert.IsTrue(inactive.Registered); Assert.IsFalse(inactive.RuntimeDiscovered);
            Assert.IsTrue(page.Items.Single(p => p.Handle == "runtime-directory").RuntimeDiscovered);
            var version = (await registry.GetSnapshotAsync()).Version;
            await client.PutAclEntityAsync("enforcement", "mode", new { mode = (string?)null }, version);
            var stale = await Assert.ThrowsAsync<FabrCoreAdministrationException>(() => client.PutAclEntityAsync("enforcement", "mode", new { mode = (string?)null }, version));
            Assert.AreEqual(HttpStatusCode.PreconditionFailed, stale.StatusCode);
        }
        finally { await app.StopAsync(); }
    }
    [TestMethod]
    public async Task EvidenceExportIsStableAndChunksRespectSmallTransportLimits()
    {
        await using var app = DatabaseModeTests.BuildHost("Development", configureServices: services =>
            services.Configure<FabrCore.Host.Configuration.RemoteAdministrationOptions>(o => o.MaxBodyBytes = 1024));
        await app.StartAsync();
        try
        {
            var evidence = app.Services.GetRequiredService<FabrCore.Core.VerifiableExecution.IVerifiableExecutionStore>();
            await evidence.AppendRecordAsync(new() { TraceId = "export-test", AgentHandle = "owner:agent", Metadata = new() { ["payload"] = new string('x', 6000) } }, null, null);
            using var http = app.GetTestClient(); http.DefaultRequestHeaders.Authorization = new("Bearer", "test-admin");
            var client = new FabrCoreAdministrationClient(http);
            var manifest = await client.CreateEvidenceExportAsync("export-test");
            await evidence.AppendRecordAsync(new() { TraceId = "export-test", AgentHandle = "owner:agent", Sequence = 2 }, null, null);
            using var bytes = new MemoryStream();
            var id = manifest.GetProperty("id").GetString()!;
            for (var i = 0; i < manifest.GetProperty("chunks").GetInt32(); i++)
            {
                var response = await http.GetAsync($"/fabrcoreapi/admin/v1/observability/evidence/exports/{id}/{i}");
                var body = await response.Content.ReadAsByteArrayAsync();
                Assert.IsTrue(body.Length <= 1024);
                using var document = JsonDocument.Parse(body); bytes.Write(document.RootElement.GetProperty("data").GetBytesFromBase64());
            }
            Assert.AreEqual(manifest.GetProperty("revision").GetString(), Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(bytes.ToArray())));
            using var bundle = JsonDocument.Parse(bytes.ToArray()); Assert.AreEqual(1, bundle.RootElement.GetProperty("records").GetArrayLength());
        }
        finally { await app.StopAsync(); }
    }
    [TestMethod]
    public async Task AdminTurnOverlapsNormalFlowButRejectsAdminOverlapAndReset()
    {
        var model = new DiagnosticModel();
        await using var app = DatabaseModeTests.BuildHost("Development", configureServices: services => services.AddSingleton<IFabrCoreChatClientService>(model));
        await app.StartAsync();
        try
        {
            var service = app.Services.GetRequiredService<IFabrCoreAgentService>();
            await service.ConfigureAgentsAsync("diagnosis", [new() { Handle = "echo", AgentType = "database-mode-echo" }]);
            using var http = app.GetTestClient();
            http.DefaultRequestHeaders.Authorization = new("Bearer", "test-admin");
            var client = new FabrCoreAdministrationClient(http);
            var before = await client.GetAgentAsync("diagnosis", "echo");
            var first = await client.CreateAdminSessionAsync("diagnosis", "echo", new());
            var second = await client.CreateAdminSessionAsync("diagnosis", "echo", new());
            var input = new AdminTurnRequest { TurnId = "deduplicated", Message = "What evidence is available?" };
            await client.SubmitAdminTurnAsync("diagnosis", "echo", first.Id, input);
            await model.Started.Task.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.AreEqual("running", (await client.SubmitAdminTurnAsync("diagnosis", "echo", first.Id, input)).Status);
            var overlap = await Assert.ThrowsAsync<FabrCoreAdministrationException>(() => client.SubmitAdminTurnAsync("diagnosis", "echo", second.Id, new() { TurnId = "overlap", Message = "Second turn" }));
            Assert.AreEqual(HttpStatusCode.Conflict, overlap.StatusCode);
            var reset = await Assert.ThrowsAsync<FabrCoreAdministrationException>(() => client.ManageAgentAsync("diagnosis", "echo", "reset", new() { Revision = before.Revision }));
            Assert.AreEqual(HttpStatusCode.Conflict, reset.StatusCode);
            var reply = await service.SendAndReceiveMessageAsync("diagnosis", "echo", new AgentMessage { Message = "ordinary user" }).WaitAsync(TimeSpan.FromSeconds(10));
            Assert.AreEqual("ordinary user", reply.Message);
            model.Release.TrySetResult();
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            AdminConversationTurn turn;
            do { await Task.Delay(50, timeout.Token); turn = await client.GetAdminTurnAsync("diagnosis", "echo", first.Id, input.TurnId, timeout.Token); } while (turn.Status == "running");
            Assert.AreEqual("completed", turn.Status);
            Assert.AreEqual(1, model.Calls);
            var after = await client.GetAgentAsync("diagnosis", "echo");
            Assert.IsFalse(after.AdminProcessing);
            Assert.AreEqual(JsonSerializer.Serialize(before.State), JsonSerializer.Serialize(after.State));
            Assert.AreEqual(JsonSerializer.Serialize(before.Threads), JsonSerializer.Serialize(after.Threads));
        }
        finally { model.Release.TrySetResult(); await app.StopAsync(); }
    }

    private sealed class DiagnosticModel : IFabrCoreChatClientService, IChatClient
    {
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int Calls;
        public Task<IChatClient> GetChatClient(string name, int networkTimeoutSeconds = 100) => Task.FromResult<IChatClient>(this);
        public Task<ModelConfiguration> GetModelConfigurationAsync(string name) => Task.FromResult(new ModelConfiguration { Name = name, Model = "test", Provider = "Test", Uri = "http://localhost", ApiKeyAlias = "test" });
#pragma warning disable MEAI001
        public Task<ISpeechToTextClient> GetAudioClient(string name, int networkTimeoutSeconds = 100) => throw new NotSupportedException();
#pragma warning restore MEAI001
        public Task<IEmbeddingGenerator<string, Embedding<float>>> GetEmbeddingsClient(string name) => throw new NotSupportedException();
        public async Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref Calls);
            Assert.IsTrue(options!.Tools!.OfType<AIFunction>().All(t => new[] { "inspect_agent", "read_thread", "read_tool_errors", "read_execution_evidence", "inspect_application" }.Contains(t.Name)));
            Started.TrySetResult(); await Release.Task.WaitAsync(cancellationToken);
            return new(new ChatMessage(ChatRole.Assistant, "No captured evidence was requested."));
        }
        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public object? GetService(Type serviceType, object? serviceKey = null) => null;
        public void Dispose() { }
    }
    [TestMethod]
    public async Task RealHostEnforcesSessionOwnershipRevisionsAndPurePreview()
    {
        await using var app = DatabaseModeTests.BuildHost("Development");
        await app.StartAsync();
        try
        {
            var service = app.Services.GetRequiredService<IFabrCoreAgentService>();
            await service.ConfigureAgentsAsync("operator-test", [new() { Handle = "echo", AgentType = "database-mode-echo" }]);
            using var http = app.GetTestClient();
            var root = "/fabrcoreapi/admin/v1/principals/operator-test";
            Assert.AreEqual(HttpStatusCode.Unauthorized, (await http.GetAsync(root + "/agents/echo/admin-sessions")).StatusCode);
            http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "test-admin");
            http.DefaultRequestHeaders.Add("X-FabrCore-Admin-Actor", "alice");
            var client = new FabrCoreAdministrationClient(http);
            using var specification = JsonDocument.Parse(await http.GetStringAsync("/fabrcoreapi/admin/v1/openapi.json"));
            Assert.AreEqual("3.1.0", specification.RootElement.GetProperty("openapi").GetString());
            var session = await client.CreateAdminSessionAsync("operator-test", "echo", new());
            Assert.IsFalse(string.IsNullOrWhiteSpace(session.Id));
            Assert.AreEqual(1, (await client.ListAdminSessionsAsync("operator-test", "echo")).Count);
            http.DefaultRequestHeaders.Remove("X-FabrCore-Admin-Actor");
            http.DefaultRequestHeaders.Add("X-FabrCore-Admin-Actor", "bob");
            Assert.AreEqual(HttpStatusCode.NotFound, (await http.GetAsync(root + "/agents/echo/admin-sessions/" + session.Id)).StatusCode);
            var before = await client.GetAgentAsync("operator-test", "echo");
            using var stale = await http.PostAsJsonAsync(root + "/agents/echo/actions/state", new AgentManagementRequest { Revision = "stale" });
            Assert.AreEqual(HttpStatusCode.PreconditionFailed, stale.StatusCode);
            await client.SaveBlueprintAsync("operator-test", "preview-only", new() { Name = "preview-only", Agents = [new() { Handle = "never-created", AgentType = "database-mode-echo" }] }, "*");
            var preview = await client.PreviewBlueprintAsync("operator-test", "preview-only");
            Assert.AreEqual(1, preview.Agents.Count);
            Assert.IsFalse((await client.GetAgentsAsync("operator-test")).Any(a => a.Handle.EndsWith(":never-created")));
            var summaries = await client.GetBlueprintPageAsync("operator-test", limit: 1);
            Assert.AreEqual("preview-only", summaries.Items.Single().Name);
            Assert.AreEqual(before.Revision, (await client.GetAgentAsync("operator-test", "echo")).Revision);
            var deployment = new AdministrationOperationRequest { Kind = "deploy", BlueprintName = "preview-only",
                Deployment = new() { OperationId = "deploy-once", Revision = preview.Revision, ExpansionDigest = preview.ExpansionDigest } };
            await client.StartOperationAsync("operator-test", "first-delivery", deployment);
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            async Task<AdministrationOperation> Completed(string operationId)
            {
                AdministrationOperation operation;
                do { await Task.Delay(50, deadline.Token); operation = await client.GetOperationAsync("operator-test", operationId, deadline.Token); } while (operation.Status == "running");
                return operation;
            }
            var applied = await Completed("first-delivery");
            Assert.AreEqual("completed", applied.Status, applied.Error);
            await client.StartOperationAsync("operator-test", "duplicate-delivery", deployment);
            var duplicate = await Completed("duplicate-delivery");
            Assert.AreEqual("completed", duplicate.Status, duplicate.Error);
            Assert.AreEqual(JsonDocument.Parse(applied.ResultJson!).RootElement.GetProperty("body").GetProperty("completedAt").GetString(),
                JsonDocument.Parse(duplicate.ResultJson!).RootElement.GetProperty("body").GetProperty("completedAt").GetString());
            var deployedSummary = (await client.GetBlueprintPageAsync("operator-test")).Items.Single();
            Assert.AreEqual("applied", deployedSummary.DeploymentStatus);
            Assert.IsFalse(deployedSummary.DefinitionDrift!.Value);
        }
        finally { await app.StopAsync(); }
    }
}
