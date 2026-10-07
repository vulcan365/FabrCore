using FabrCore.Core.CloudServer;
using FabrCore.Host.Configuration.Cloud;
using FabrCore.Sdk;
using FabrCore.Services.Contracts.Capabilities;
using Microsoft.Agents.Storage;
using Microsoft.Extensions.Options;
using System.Text.Json;
using Marker = FabrCore.Services.Microsoft365Copilot.Microsoft365CopilotExtensions.Microsoft365CopilotMarker;

namespace FabrCore.Services.Microsoft365Copilot.Tests;

[TestClass]
public sealed class CopilotAdministrationTests
{
    private static readonly Marker On = new(Enabled: true);
    private static readonly Marker Unconfigured = new(Enabled: false, Configured: false, DisabledReason: "not configured");

    private static Microsoft365CopilotOptions ProductionOptions()
    {
        var options = new Microsoft365CopilotOptions
        {
            TenantId = "11111111-1111-1111-1111-111111111111",
            ClientId = "22222222-2222-2222-2222-222222222222",
            AuthType = "WorkloadIdentity",
        };
        options.Principal.Strategy = CopilotPrincipalStrategy.CanonicalEntra;
        options.Agent.Binding = "assistant";
        options.Agent.AgentType = "crm-agent";
        options.Manifest.PublicHostName = "agents.contoso.com";
        return options;
    }

    private static string StatusOf(IEnumerable<IntegrationCheck> checks, string id) => checks.Single(c => c.Id == id).Status;

    // ----- Status and findings -----

    [TestMethod]
    public void ChannelOff_ReportsWhy_AndSkipsEveryFinding()
    {
        var status = CopilotIntegrationStatusBuilder.Build(new Microsoft365CopilotOptions(), Unconfigured, null, false);

        Assert.IsFalse(status.Enabled);
        Assert.IsFalse(status.Configured);
        Assert.AreEqual("not configured", status.DisabledReason);
        Assert.AreEqual(IntegrationAdministration.Skipped, status.Status);
        CollectionAssert.AreEqual(CopilotIntegrationStatusBuilder.FindingIds, status.Findings.Select(f => f.Id).ToArray());
        Assert.IsTrue(status.Findings.All(f => f.Status == IntegrationAdministration.Skipped));
    }

    [TestMethod]
    public void ProductionConfiguration_Passes()
    {
        var status = CopilotIntegrationStatusBuilder.Build(ProductionOptions(), On, typeof(DurableStorage), false);

        Assert.AreEqual(IntegrationAdministration.Pass, status.Status);
        Assert.IsNull(status.DisabledReason);
        Assert.AreEqual("WorkloadIdentity", status.AuthType);
        Assert.AreEqual("CanonicalEntra", status.PrincipalStrategy);
        Assert.AreEqual("assistant", status.AgentBinding);
        Assert.AreEqual("22222222-2222-2222-2222-222222222222", status.ManifestId);
        Assert.AreEqual(nameof(DurableStorage), status.TurnStateStorage);
        Assert.AreEqual(IntegrationAdministration.Skipped, StatusOf(status.Findings, "proactive-scopes"));
    }

    [TestMethod]
    public void DefaultsOutOfTheBox_AreFlaggedForReview()
    {
        var options = new Microsoft365CopilotOptions { ClientId = "client", ClientSecret = "do-not-report-me" };
        options.Agent.AgentType = "chat-agent";

        var status = CopilotIntegrationStatusBuilder.Build(options, On, typeof(MemoryStorage), false);

        Assert.AreEqual(IntegrationAdministration.Warn, status.Status);
        Assert.AreEqual(IntegrationAdministration.Pass, StatusOf(status.Findings, "token-validation"));
        Assert.AreEqual(IntegrationAdministration.Warn, StatusOf(status.Findings, "principal-strategy"));
        Assert.AreEqual(IntegrationAdministration.Warn, StatusOf(status.Findings, "credential-type"));
        Assert.AreEqual(IntegrationAdministration.Warn, StatusOf(status.Findings, "turn-state-storage"));
        Assert.AreEqual(IntegrationAdministration.Pass, StatusOf(status.Findings, "user-credential-forwarding"));
        Assert.AreEqual(IntegrationAdministration.Warn, StatusOf(status.Findings, "public-host"));
        Assert.IsFalse(JsonSerializer.Serialize(status, JsonSerializerOptions.Web).Contains("do-not-report-me", StringComparison.Ordinal),
            "The client secret must never appear in the status document.");
    }

    [TestMethod]
    public void UnsafeConfiguration_Fails()
    {
        var options = ProductionOptions();
        options.TokenValidation.Enabled = false;
        options.Principal.Strategy = CopilotPrincipalStrategy.ChannelUserId;
        options.UserAuthorization.PassUserTokenToAgent = true;
        options.UserAuthorizationConfigured = true;
        options.Proactive.Enabled = true;
        options.Proactive.AllowedConversationTypes = ["personal", "groupChat"];

        var status = CopilotIntegrationStatusBuilder.Build(options, On, typeof(DurableStorage), false);

        Assert.AreEqual(IntegrationAdministration.Fail, status.Status);
        Assert.AreEqual(IntegrationAdministration.Fail, StatusOf(status.Findings, "token-validation"));
        Assert.AreEqual(IntegrationAdministration.Fail, StatusOf(status.Findings, "principal-strategy"));
        Assert.AreEqual(IntegrationAdministration.Fail, StatusOf(status.Findings, "user-credential-forwarding"));
        Assert.AreEqual(IntegrationAdministration.Warn, StatusOf(status.Findings, "proactive-scopes"));
        Assert.IsTrue(status.ForwardsUserCredential);
        Assert.IsTrue(status.SingleSignOn);
    }

    [TestMethod]
    public void UserCredentialForwarding_WithoutSignOn_IsInertAndSaysSo()
    {
        var options = ProductionOptions();
        options.UserAuthorization.PassUserTokenToAgent = true;

        var status = CopilotIntegrationStatusBuilder.Build(options, On, typeof(DurableStorage), false);

        Assert.IsFalse(status.ForwardsUserCredential);
        Assert.AreEqual(IntegrationAdministration.Warn, StatusOf(status.Findings, "user-credential-forwarding"));
    }

    [TestMethod]
    public void HostOwnedDecisions_AreSkippedRatherThanGuessed()
    {
        var marker = new Marker(Enabled: true, NativeServiceConnection: true);

        var status = CopilotIntegrationStatusBuilder.Build(ProductionOptions(), marker, typeof(DurableStorage), customPrincipalResolver: true);

        Assert.IsNull(status.AuthType, "With a native Agents SDK connection the add-on's AuthType is not what authenticates.");
        Assert.AreEqual(IntegrationAdministration.Skipped, StatusOf(status.Findings, "credential-type"));
        Assert.AreEqual(IntegrationAdministration.Skipped, StatusOf(status.Findings, "principal-strategy"));
    }

    [TestMethod]
    public void ChannelIdFallback_IsFlaggedEvenWithCanonicalIdentity()
    {
        var options = ProductionOptions();
        options.Principal.AllowChannelIdFallback = true;

        var status = CopilotIntegrationStatusBuilder.Build(options, On, typeof(DurableStorage), false);

        Assert.AreEqual(IntegrationAdministration.Warn, StatusOf(status.Findings, "principal-strategy"));
    }

    // ----- Capability advertisement -----

    [TestMethod]
    public void Capabilities_AreAdvertisedWhileOff_AsUnavailable()
    {
        IFabrCoreCapabilityContributor contributor = new CopilotCapabilityContributor(Options.Create(new Microsoft365CopilotOptions()), Unconfigured);

        var service = contributor.GetServices().Single();
        var flags = contributor.GetHeartbeatCapabilities();

        Assert.AreEqual("microsoft365-copilot", service.Name);
        Assert.IsFalse(service.Available);
        Assert.AreEqual("not configured", service.UnavailableReason);
        CollectionAssert.AreEqual(new[] { "status", "app-package", "diagnostics" }, service.Features);
        Assert.AreEqual("1", flags["m365copilot"]);
        Assert.AreEqual("false", flags["m365copilot.enabled"]);
    }

    [TestMethod]
    public void Capabilities_ListWhatIsEnabledAndConfigured()
    {
        var options = ProductionOptions();
        options.Proactive.Enabled = true;
        options.UserAuthorizationConfigured = true;
        options.TokenValidation.Enabled = false;
        IFabrCoreCapabilityContributor contributor = new CopilotCapabilityContributor(Options.Create(options), On);

        var service = contributor.GetServices().Single();

        Assert.IsTrue(service.Available);
        Assert.IsNull(service.UnavailableReason);
        CollectionAssert.AreEqual(
            new[]
            {
                "status", "app-package", "diagnostics", "activity-protocol", "streaming", "adaptive-cards",
                "principal-canonicalentra", "proactive", "sso", "agent-binding", "token-validation-off"
            },
            service.Features);
        Assert.AreEqual("true", contributor.GetHeartbeatCapabilities()["m365copilot.enabled"]);
    }

    [TestMethod]
    public void Capabilities_OmitOptionalFeaturesThatAreOff()
    {
        var options = new Microsoft365CopilotOptions();
        options.Streaming.Enabled = false;
        IFabrCoreCapabilityContributor contributor = new CopilotCapabilityContributor(Options.Create(options), On);

        var features = contributor.GetServices().Single().Features;

        CollectionAssert.AreEqual(
            new[] { "status", "app-package", "diagnostics", "activity-protocol", "adaptive-cards", "principal-entraobjectid" },
            features);
    }

    // ----- Applied-settings reporting -----

    [TestMethod]
    public void AppliedSettings_ReportIdentityAndPosture_WithDefaults()
    {
        var contributor = new CopilotRuntimeSettingsContributor(Options.Create(ProductionOptions()), On);

        var rows = contributor.Observe().ToDictionary(o => o.Key);

        CollectionAssert.AreEquivalent(
            new[]
            {
                "Microsoft365Copilot:Enabled", "Microsoft365Copilot:TenantId", "Microsoft365Copilot:ClientId",
                "Microsoft365Copilot:AuthType", "Microsoft365Copilot:MessagesEndpoint", "Microsoft365Copilot:TokenValidation:Enabled",
                "Microsoft365Copilot:Principal:Strategy", "Microsoft365Copilot:Agent:Binding", "Microsoft365Copilot:Proactive:Enabled",
                "Microsoft365Copilot:Streaming:Enabled", "Microsoft365Copilot:Manifest:PublicHostName",
                "Runtime:Microsoft365Copilot:SingleSignOn", "Runtime:Microsoft365Copilot:ForwardsUserCredential"
            },
            rows.Keys.ToArray());
        Assert.IsTrue(rows.Values.All(o => o.AppliedKnown));
        Assert.AreEqual("True", rows["Microsoft365Copilot:Enabled"].Value);
        Assert.AreEqual("True", rows["Microsoft365Copilot:Enabled"].DefaultValue);
        Assert.AreEqual("WorkloadIdentity", rows["Microsoft365Copilot:AuthType"].Value);
        Assert.AreEqual("ClientSecret", rows["Microsoft365Copilot:AuthType"].DefaultValue);
        Assert.AreEqual("/api/messages", rows["Microsoft365Copilot:MessagesEndpoint"].DefaultValue);
        Assert.AreEqual("CanonicalEntra", rows["Microsoft365Copilot:Principal:Strategy"].Value);
        Assert.AreEqual("EntraObjectId", rows["Microsoft365Copilot:Principal:Strategy"].DefaultValue);
        Assert.IsNull(rows["Microsoft365Copilot:TenantId"].DefaultValue, "Identity has no default to fall back on.");
        Assert.AreEqual("False", rows["Runtime:Microsoft365Copilot:ForwardsUserCredential"].Value);
    }

    [TestMethod]
    public void AppliedSettings_WhileOff_ClaimOnlyThatTheChannelIsOff()
    {
        var contributor = new CopilotRuntimeSettingsContributor(Options.Create(new Microsoft365CopilotOptions { Enabled = false }), Unconfigured);

        var rows = contributor.Observe().ToDictionary(o => o.Key);

        Assert.IsTrue(rows["Microsoft365Copilot:Enabled"].AppliedKnown);
        Assert.AreEqual("False", rows["Microsoft365Copilot:Enabled"].Value);
        Assert.AreEqual("False", rows["Microsoft365Copilot:Enabled"].DefaultValue,
            "An untouched add-on starts off, whatever the options class default says.");
        Assert.IsTrue(rows.Values.Where(o => o.Key != "Microsoft365Copilot:Enabled").All(o => !o.AppliedKnown));
    }

    [TestMethod]
    public void RuntimeFacts_UseNamesTheCatalogDoesNotRedact()
    {
        var contributor = new CopilotRuntimeSettingsContributor(Options.Create(ProductionOptions()), On);

        foreach (var fact in contributor.Observe().Where(o => o.Key.StartsWith("Runtime:", StringComparison.Ordinal)))
        {
            Assert.IsFalse(FabrCoreSettingsCatalog.IsSecret(fact.Key), $"{fact.Key} would be redacted, which tells an operator nothing.");
        }
    }

    [TestMethod]
    public void AuthType_IsNotClaimedAsApplied_WhenTheHostOwnsTheConnection()
    {
        var contributor = new CopilotRuntimeSettingsContributor(
            Options.Create(ProductionOptions()), new Marker(Enabled: true, NativeServiceConnection: true));

        var authType = contributor.Observe().Single(o => o.Key == "Microsoft365Copilot:AuthType");

        Assert.IsFalse(authType.AppliedKnown);
        Assert.IsNotNull(authType.Reason);
    }

    // ----- Diagnostics -----

    [TestMethod]
    public void AgentTypeCheck_ReportsWhetherTheTypeIsRegistered()
    {
        var options = ProductionOptions();
        var registry = new Registry("crm-agent");

        Assert.AreEqual(IntegrationAdministration.Pass, CopilotIntegrationEndpoints.AgentTypeCheck(options, On, registry).Status);

        options.Agent.AgentType = "missing-agent";
        var missing = CopilotIntegrationEndpoints.AgentTypeCheck(options, On, registry);
        Assert.AreEqual(IntegrationAdministration.Fail, missing.Status);
        StringAssert.Contains(missing.Message, "missing-agent");

        Assert.AreEqual(IntegrationAdministration.Skipped, CopilotIntegrationEndpoints.AgentTypeCheck(options, On, registry: null).Status);
        Assert.AreEqual(IntegrationAdministration.Skipped, CopilotIntegrationEndpoints.AgentTypeCheck(options, Unconfigured, registry).Status);

        options.Agent.SharedAgentHandle = "system:helpdesk";
        Assert.AreEqual(IntegrationAdministration.Skipped, CopilotIntegrationEndpoints.AgentTypeCheck(options, On, registry).Status);
    }

    [TestMethod]
    public async Task CredentialCheck_Passes_WithoutEverReportingTheToken()
    {
        const string token = "eyJ0eXAiOiJKV1QifQ.super-secret-token-material";

        var check = await CopilotIntegrationEndpoints.CredentialCheckAsync(
            () => Task.FromResult(token), TimeSpan.FromSeconds(5), CancellationToken.None);

        Assert.AreEqual("bot-service-credential", check.Id);
        Assert.AreEqual(IntegrationAdministration.Pass, check.Status);
        Assert.IsFalse(check.Message.Contains(token, StringComparison.Ordinal));
        Assert.IsFalse(check.Message.Contains("eyJ", StringComparison.Ordinal));
    }

    [TestMethod]
    public async Task CredentialCheck_Failure_ReportsOnlyTheExceptionTypeAndFirstLine()
    {
        var check = await CopilotIntegrationEndpoints.CredentialCheckAsync(
            () => throw new InvalidOperationException("AADSTS700016: Application was not found.\r\nTrace ID: 123\r\nclient_assertion=leaked"),
            TimeSpan.FromSeconds(5), CancellationToken.None);

        Assert.AreEqual(IntegrationAdministration.Fail, check.Status);
        Assert.AreEqual("InvalidOperationException: AADSTS700016: Application was not found.", check.Message);
    }

    [TestMethod]
    public async Task CredentialCheck_BoundsLongMessages()
    {
        var check = await CopilotIntegrationEndpoints.CredentialCheckAsync(
            () => throw new InvalidOperationException(new string('x', 5000)), TimeSpan.FromSeconds(5), CancellationToken.None);

        Assert.IsTrue(check.Message.Length < 400, $"Message was {check.Message.Length} characters.");
    }

    [TestMethod]
    public async Task CredentialCheck_FailsWhenTheIdentityProviderDoesNotAnswerInTime()
    {
        var never = new TaskCompletionSource<string>();

        var check = await CopilotIntegrationEndpoints.CredentialCheckAsync(
            () => never.Task, TimeSpan.FromMilliseconds(50), CancellationToken.None);

        Assert.AreEqual(IntegrationAdministration.Fail, check.Status);
        StringAssert.Contains(check.Message, "No Bot Service token was acquired");
    }

    [TestMethod]
    public async Task CredentialCheck_FailsOnAnEmptyToken()
    {
        var check = await CopilotIntegrationEndpoints.CredentialCheckAsync(
            () => Task.FromResult(string.Empty), TimeSpan.FromSeconds(5), CancellationToken.None);

        Assert.AreEqual(IntegrationAdministration.Fail, check.Status);
    }

    [TestMethod]
    public void TheProductionTimeoutIsTwentySeconds()
    {
        Assert.AreEqual(TimeSpan.FromSeconds(20), CopilotIntegrationEndpoints.CredentialTimeout);
    }

    private sealed class DurableStorage;

    private sealed class Registry(params string[] agentTypes) : IFabrCoreRegistry
    {
        public List<RegistryEntry> GetAgentTypes() => [];
        public List<RegistryEntry> GetPlugins() => [];
        public List<RegistryEntry> GetTools() => [];
        public List<RegistryCollision> GetCollisions() => [];
        public Type? FindAgentType(string alias) => agentTypes.Contains(alias, StringComparer.OrdinalIgnoreCase) ? typeof(object) : null;
    }
}
