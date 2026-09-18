using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FabrCore.Core.CloudServer;
using FabrCore.ReferenceCloud;
using Microsoft.AspNetCore.TestHost;

namespace FabrCore.Host.Tests.CloudServer;

[TestClass]
public sealed class IndependentConfigurationServerTests
{
    private const string Key = "FabrCore:Orleans:ClusteringMode";

    [TestMethod]
    public async Task ReferenceHttpServer_EnforcesAuthentication_AndSupportsReportsDraftsAndPreviewCommands()
    {
        var file = Path.GetTempFileName();
        try
        {
            var original = """{"schemaVersion":1,"configurationVersion":"v1","configuration":{},"settings":{"FabrCore:Orleans:ClusteringMode":"Localhost"}}""";
            await File.WriteAllTextAsync(file, original);
            var configuration = new Dictionary<string, string?>
            {
                ["REFERENCE_CLUSTER_KEY"] = "cluster-test-credential",
                ["REFERENCE_OPERATOR_KEY"] = "operator-test-credential",
                ["REFERENCE_CLUSTER_ID"] = "independent-cluster",
                ["REFERENCE_ENVIRONMENT"] = "Test", ["REFERENCE_CONFIGURATION_FILE"] = file
            };
            await using var app = ReferenceCloudApplication.Build([], builder => builder.WebHost.UseTestServer(),
                name => configuration.GetValueOrDefault(name));
            await app.StartAsync();
            using var client = app.GetTestClient();
            Assert.AreEqual(HttpStatusCode.Unauthorized, (await client.GetAsync("/operator/configuration/reports")).StatusCode);
            client.DefaultRequestHeaders.Authorization = new("Bearer", "cluster-test-credential");
            client.DefaultRequestHeaders.Add(CloudServerProtocol.ClusterIdHeader, "independent-cluster");
            client.DefaultRequestHeaders.Add(CloudServerProtocol.EnvironmentHeader, "Test");
            Assert.AreEqual(HttpStatusCode.Unauthorized, (await client.GetAsync("/operator/configuration/reports")).StatusCode);
            var mismatch = Heartbeat(); mismatch.Environment = "Production";
            Assert.AreEqual(HttpStatusCode.BadRequest, (await client.PostAsJsonAsync(CloudServerProtocol.HeartbeatPath, mismatch)).StatusCode);
            Assert.AreEqual(HttpStatusCode.OK, (await client.PostAsJsonAsync(CloudServerProtocol.HeartbeatPath, Heartbeat())).StatusCode);
            client.DefaultRequestHeaders.Authorization = new("Bearer", "operator-test-credential");
            var reports = await client.GetFromJsonAsync<ConfigurationObservation[]>("/operator/configuration/reports");
            Assert.AreEqual("SqlServer", reports!.Single().ConfigurationState!.Settings[0].AppliedValue);
            var draftResponse = await client.PostAsJsonAsync("/operator/configuration/draft", new { hostInstanceId = "host-a", keys = new[] { Key } });
            draftResponse.EnsureSuccessStatusCode();
            var draft = await draftResponse.Content.ReadFromJsonAsync<JsonElement>();
            Assert.AreEqual("v1", draft.GetProperty("baseRevision").GetString());
            Assert.AreEqual("SqlServer", draft.GetProperty("settings").GetProperty(Key).GetString());
            Assert.AreEqual(original, await File.ReadAllTextAsync(file));

            var candidate = JsonSerializer.SerializeToUtf8Bytes(new Dictionary<string, string?> { [Key] = "SqlServer" });
            var queued = await client.PostAsJsonAsync("/operator/commands", new CloudAdminCommand
            { Method = "POST", PathAndQuery = "/fabrcoreapi/admin/v1/settings/preview", Body = candidate });
            Assert.AreEqual(HttpStatusCode.Accepted, queued.StatusCode);
            client.DefaultRequestHeaders.Authorization = new("Bearer", "cluster-test-credential");
            var command = await client.GetFromJsonAsync<CloudAdminCommand>(CloudServerProtocol.ConnectPath);
            Assert.AreEqual("/fabrcoreapi/admin/v1/settings/preview", command!.PathAndQuery);
            CollectionAssert.AreEqual(candidate, command.Body!);
            await app.StopAsync();
        }
        finally { File.Delete(file); }
    }
    private static CloudHeartbeatRequest Heartbeat(string host = "host-a", long sequence = 1) => new()
    {
        ClusterId = "independent-cluster", Environment = "Test", HostInstanceId = host,
        Capabilities = new() { ["configuration-state"] = "1" },
        ConfigurationState = new()
        {
            BootId = "f95a1c75c58c48caa8ee588b26a18705", Sequence = sequence, ObservedAt = DateTimeOffset.UtcNow,
            Started = true, DesiredRevision = "v1", Settings = [new()
            {
                Key = Key, HasDesiredValue = true, DesiredValue = "Localhost", ResolvedValue = "SqlServer",
                AppliedKnown = true, AppliedValue = "SqlServer", Source = "code-override", SourceId = "Example.Hosting",
                Overridden = true, CanAdopt = true
            }]
        }
    };

    [TestMethod]
    public async Task HostClient_SendsOpenReportContract_ToIndependentServer()
    {
        var store = new ConfigurationReportStore("independent-cluster", "Test");
        var handler = new FakeCloudServerHandler(async request =>
        {
            Assert.AreEqual(CloudServerProtocol.HeartbeatPath, request.RequestUri!.AbsolutePath);
            var json = await request.Content!.ReadAsStringAsync();
            StringAssert.Contains(json, "\"configurationState\"");
            var heartbeat = JsonSerializer.Deserialize<CloudHeartbeatRequest>(json, JsonSerializerOptions.Web)!;
            Assert.AreEqual("1", heartbeat.Capabilities["configuration-state"]);
            store.Accept(heartbeat);
            return FakeCloudServerHandler.Json(HttpStatusCode.OK, new CloudHeartbeatResponse());
        });
        var client = CloudServerTestFactory.ApiClient(handler,
            CloudServerTestFactory.Options(o => o.ClusterId = "independent-cluster"), environmentName: "Test");
        await client.SendHeartbeatAsync(Heartbeat());
        Assert.AreEqual("SqlServer", store.Snapshot().Single().ConfigurationState!.Settings.Single().AppliedValue);
    }

    [TestMethod]
    public void ReferenceServer_RetainsNewestPerProcess_AndAcceptsLegacyHeartbeats()
    {
        var store = new ConfigurationReportStore("independent-cluster", "Test");
        store.Accept(Heartbeat(sequence: 2));
        var late = Heartbeat(); late.ConfigurationState!.Settings[0].AppliedValue = "Localhost";
        store.Accept(late);
        late.ConfigurationState = null; store.Accept(late);
        var otherBoot = Heartbeat(sequence: 99); otherBoot.ConfigurationState!.BootId = Guid.NewGuid().ToString("N");
        store.Accept(otherBoot);
        Assert.AreEqual(2L, store.Snapshot().Single().ConfigurationState!.Sequence);
        store.Accept(Heartbeat("host-b"));
        Assert.HasCount(2, store.Snapshot());
        var snapshot = store.Snapshot(); snapshot[0].ConfigurationState!.Settings.Clear();
        Assert.HasCount(1, store.Snapshot()[0].ConfigurationState!.Settings);
    }

    [TestMethod]
    public void ReferenceServer_RejectsScopeAndMalformedReports_AndRedactsSecrets()
    {
        var store = new ConfigurationReportStore("independent-cluster", "Test");
        var wrongScope = Heartbeat(); wrongScope.Environment = "Production";
        Assert.ThrowsExactly<ArgumentException>(() => store.Accept(wrongScope));
        var invalid = Heartbeat(); invalid.ConfigurationState!.Settings.Add(invalid.ConfigurationState.Settings[0]);
        Assert.ThrowsExactly<ArgumentException>(() => store.Accept(invalid));
        var report = Heartbeat();
        report.ConfigurationState!.Settings.Add(new() { Key = "ConnectionStrings:Database", DesiredValue = "private-value", ResolvedValue = "private-value", AppliedValue = "private-value", AppliedKnown = true, CanAdopt = true });
        store.Accept(report);
        Assert.DoesNotContain("private-value", JsonSerializer.Serialize(store.Snapshot()));
        Assert.ThrowsExactly<ArgumentException>(() => store.CreateDraft("host-a", ["ConnectionStrings:Database"], new Dictionary<string, string?>()));
    }

    [TestMethod]
    public void ReferenceServer_AdoptsIntoSeparateDraft_WithoutChangingIntentOrOwnership()
    {
        var store = new ConfigurationReportStore("independent-cluster", "Test"); store.Accept(Heartbeat());
        var desired = new Dictionary<string, string?> { [Key] = "Localhost", ["A2A:Enabled"] = "false" };
        var draft = store.CreateDraft("host-a", [Key], desired);
        Assert.AreEqual("SqlServer", draft[Key]);
        Assert.AreEqual("Localhost", desired[Key]);
        Assert.AreEqual("false", draft["A2A:Enabled"]);
        Assert.AreEqual("Example.Hosting", store.Snapshot().Single().ConfigurationState!.Settings[0].SourceId);
        Assert.ThrowsExactly<ArgumentException>(() => store.CreateDraft("host-a", ["Runtime:Unknown"], desired));
    }
}
