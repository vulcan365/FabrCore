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
            AuthType = "WorkloadIdentity",
            Agents =
            {
                new CopilotAgentEntryOptions
                {
                    Name = "assistant", Route = "/api/messages", AgentType = "crm-agent",
                    ClientId = "22222222-2222-2222-2222-222222222222",
                },
            },
        };
        options.Principal.Strategy = CopilotPrincipalStrategy.CanonicalEntra;
        options.Manifest.PublicHostName = "agents.contoso.com";
        return options;
    }

    private static Microsoft365IntegrationStatus Build(
        Microsoft365CopilotOptions options, Marker marker, Type? storage, bool customPrincipalResolver) =>
        CopilotIntegrationStatusBuilder.Build(options, CopilotTestAgents.Resolve(options), marker, storage, customPrincipalResolver);

    private static CopilotCapabilityContributor Capabilities(Microsoft365CopilotOptions options, Marker marker) =>
        new(Options.Create(options), CopilotTestAgents.Catalog(options), marker);

    private static CopilotRuntimeSettingsContributor Settings(Microsoft365CopilotOptions options, Marker marker) =>
        new(Options.Create(options), CopilotTestAgents.Catalog(options), marker);

    private static string StatusOf(IEnumerable<IntegrationCheck> checks, string id) => checks.Single(c => c.Id == id).Status;

    // ----- Status and findings -----

    [TestMethod]
    public void ChannelOff_ReportsWhy_AndSkipsEveryFinding()
    {
        var status = Build(new Microsoft365CopilotOptions(), Unconfigured, null, false);

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
        var status = Build(ProductionOptions(), On, typeof(DurableStorage), false);

        Assert.AreEqual(IntegrationAdministration.Pass, status.Status);
        Assert.IsNull(status.DisabledReason);
        Assert.AreEqual("CanonicalEntra", status.PrincipalStrategy);
        Assert.AreEqual(IntegrationAdministration.Microsoft365ApiVersion, status.ApiVersion);
        var agent = status.Agents.Single();
        Assert.AreEqual("assistant", agent.Name);
        Assert.AreEqual("/api/messages", agent.MessagesEndpoint);
        Assert.AreEqual("WorkloadIdentity", agent.AuthType);
        Assert.AreEqual("crm-agent", agent.AgentType);
        Assert.AreEqual("assistant", agent.AgentHandle);
        Assert.AreEqual("11111111-1111-1111-1111-111111111111", agent.TenantId);
        Assert.AreEqual("22222222-2222-2222-2222-222222222222", agent.ManifestId);
        Assert.AreEqual(nameof(DurableStorage), status.TurnStateStorage);
        Assert.AreEqual(IntegrationAdministration.Skipped, StatusOf(status.Findings, "proactive-scopes"));
    }

    [TestMethod]
    public void DefaultsOutOfTheBox_AreFlaggedForReview()
    {
        var options = new Microsoft365CopilotOptions { Agents = { CopilotTestAgents.Entry(clientId: "client") } };
        options.Agents[0].ClientSecret = "do-not-report-me";

        var status = Build(options, On, typeof(MemoryStorage), false);

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

        var status = Build(options, On, typeof(DurableStorage), false);

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

        var status = Build(options, On, typeof(DurableStorage), false);

        Assert.IsFalse(status.ForwardsUserCredential);
        Assert.AreEqual(IntegrationAdministration.Warn, StatusOf(status.Findings, "user-credential-forwarding"));
    }

    [TestMethod]
    public void HostOwnedDecisions_AreSkippedRatherThanGuessed()
    {
        var marker = new Marker(Enabled: true, NativeServiceConnection: true);

        var status = Build(ProductionOptions(), marker, typeof(DurableStorage), customPrincipalResolver: true);

        Assert.IsNull(status.Agents.Single().AuthType, "With a native Agents SDK connection the add-on's AuthType is not what authenticates.");
        Assert.AreEqual(IntegrationAdministration.Skipped, StatusOf(status.Findings, "credential-type"));
        Assert.AreEqual(IntegrationAdministration.Skipped, StatusOf(status.Findings, "principal-strategy"));
    }

    [TestMethod]
    public void ChannelIdFallback_IsFlaggedEvenWithCanonicalIdentity()
    {
        var options = ProductionOptions();
        options.Principal.AllowChannelIdFallback = true;

        var status = Build(options, On, typeof(DurableStorage), false);

        Assert.AreEqual(IntegrationAdministration.Warn, StatusOf(status.Findings, "principal-strategy"));
    }

    [TestMethod]
    public void SeveralAgents_AreEachReported_AndASecretOnAnyOfThemIsFlagged()
    {
        var options = ProductionOptions();
        options.Agents.Add(new CopilotAgentEntryOptions
        {
            Name = "sales", AgentType = "sales-agent", Handle = "sales-desk", AuthType = "ClientSecret",
            ClientId = "33333333-3333-3333-3333-333333333333", ClientSecret = "do-not-report-me",
            Manifest = { Name = "Sales Desk", Version = "2.1.0" },
        });

        var status = Build(options, On, typeof(DurableStorage), false);

        CollectionAssert.AreEqual(new[] { "assistant", "sales" }, status.Agents.Select(a => a.Name).ToArray());
        var sales = status.Agents[1];
        Assert.AreEqual("/api/messages/sales", sales.MessagesEndpoint);
        Assert.AreEqual("33333333-3333-3333-3333-333333333333", sales.ClientId);
        Assert.AreEqual("sales-desk", sales.AgentHandle);
        Assert.AreEqual("Sales Desk", sales.ManifestName);
        Assert.AreEqual("2.1.0", sales.ManifestVersion);
        var credential = status.Findings.Single(f => f.Id == "credential-type");
        Assert.AreEqual(IntegrationAdministration.Warn, credential.Status);
        StringAssert.Contains(credential.Message, "sales");
        Assert.IsFalse(JsonSerializer.Serialize(status, JsonSerializerOptions.Web).Contains("do-not-report-me", StringComparison.Ordinal));
    }

    [TestMethod]
    public void NoAgents_IsReportedAsAnEmptyList()
    {
        var status = Build(new Microsoft365CopilotOptions(), On, typeof(DurableStorage), false);

        Assert.IsTrue(status.Enabled);
        Assert.IsEmpty(status.Agents);
        Assert.AreEqual(IntegrationAdministration.Skipped, StatusOf(status.Findings, "credential-type"));
    }

    // ----- Capability advertisement -----

    [TestMethod]
    public void Capabilities_AreAdvertisedWhileOff_AsUnavailable()
    {
        IFabrCoreCapabilityContributor contributor = Capabilities(new Microsoft365CopilotOptions(), Unconfigured);

        var service = contributor.GetServices().Single();
        var flags = contributor.GetHeartbeatCapabilities();

        Assert.AreEqual("microsoft365-copilot", service.Name);
        Assert.IsFalse(service.Available);
        Assert.AreEqual("not configured", service.UnavailableReason);
        CollectionAssert.AreEqual(new[] { "status", "app-package", "diagnostics", "multi-agent" }, service.Features);
        Assert.AreEqual(IntegrationAdministration.Microsoft365ApiVersion, service.ApiVersion);
        Assert.AreEqual("1", flags["m365copilot"]);
        Assert.AreEqual("false", flags["m365copilot.enabled"]);
        Assert.AreEqual("0", flags["m365copilot.agents"]);
    }

    [TestMethod]
    public void Capabilities_ListWhatIsEnabledAndConfigured()
    {
        var options = ProductionOptions();
        options.Proactive.Enabled = true;
        options.UserAuthorizationConfigured = true;
        options.TokenValidation.Enabled = false;
        IFabrCoreCapabilityContributor contributor = Capabilities(options, On);

        var service = contributor.GetServices().Single();

        Assert.IsTrue(service.Available);
        Assert.IsNull(service.UnavailableReason);
        CollectionAssert.AreEqual(
            new[]
            {
                "status", "app-package", "diagnostics", "multi-agent", "activity-protocol", "streaming", "adaptive-cards",
                "principal-canonicalentra", "proactive", "sso", "token-validation-off"
            },
            service.Features);
        Assert.AreEqual("true", contributor.GetHeartbeatCapabilities()["m365copilot.enabled"]);
        Assert.AreEqual("1", contributor.GetHeartbeatCapabilities()["m365copilot.agents"]);
    }

    [TestMethod]
    public void Capabilities_OmitOptionalFeaturesThatAreOff()
    {
        var options = new Microsoft365CopilotOptions();
        options.Streaming.Enabled = false;
        IFabrCoreCapabilityContributor contributor = Capabilities(options, On);

        var features = contributor.GetServices().Single().Features;

        CollectionAssert.AreEqual(
            new[] { "status", "app-package", "diagnostics", "multi-agent", "activity-protocol", "adaptive-cards", "principal-entraobjectid" },
            features);
    }

    // ----- Applied-settings reporting -----

    [TestMethod]
    public void AppliedSettings_ReportIdentityAndPosture_WithDefaults()
    {
        var contributor = Settings(ProductionOptions(), On);

        var rows = contributor.Observe().ToDictionary(o => o.Key);

        CollectionAssert.AreEquivalent(
            new[]
            {
                "Microsoft365Copilot:Enabled", "Microsoft365Copilot:TenantId",
                "Microsoft365Copilot:AuthType", "Microsoft365Copilot:TokenValidation:Enabled",
                "Microsoft365Copilot:Principal:Strategy", "Microsoft365Copilot:Proactive:Enabled",
                "Microsoft365Copilot:Streaming:Enabled", "Microsoft365Copilot:Manifest:PublicHostName",
                "Microsoft365Copilot:Agents:0:Name", "Microsoft365Copilot:Agents:0:Route", "Microsoft365Copilot:Agents:0:ClientId",
                "Microsoft365Copilot:Agents:0:AgentType", "Microsoft365Copilot:Agents:0:Handle",
                "Runtime:Microsoft365Copilot:SingleSignOn", "Runtime:Microsoft365Copilot:ForwardsUserCredential"
            },
            rows.Keys.ToArray());
        Assert.IsTrue(rows.Values.All(o => o.AppliedKnown));
        Assert.AreEqual("True", rows["Microsoft365Copilot:Enabled"].Value);
        Assert.AreEqual("True", rows["Microsoft365Copilot:Enabled"].DefaultValue);
        Assert.AreEqual("WorkloadIdentity", rows["Microsoft365Copilot:AuthType"].Value);
        Assert.AreEqual("ClientSecret", rows["Microsoft365Copilot:AuthType"].DefaultValue);
        Assert.AreEqual("/api/messages", rows["Microsoft365Copilot:Agents:0:Route"].Value);
        Assert.AreEqual("assistant", rows["Microsoft365Copilot:Agents:0:Handle"].Value, "The handle the agent runs under, default applied.");
        Assert.AreEqual("CanonicalEntra", rows["Microsoft365Copilot:Principal:Strategy"].Value);
        Assert.AreEqual("EntraObjectId", rows["Microsoft365Copilot:Principal:Strategy"].DefaultValue);
        Assert.IsNull(rows["Microsoft365Copilot:TenantId"].DefaultValue, "Identity has no default to fall back on.");
        Assert.AreEqual("False", rows["Runtime:Microsoft365Copilot:ForwardsUserCredential"].Value);
    }

    [TestMethod]
    public void AppliedSettings_WhileOff_ClaimOnlyThatTheChannelIsOff()
    {
        var contributor = Settings(new Microsoft365CopilotOptions { Enabled = false }, Unconfigured);

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
        var contributor = Settings(ProductionOptions(), On);

        foreach (var fact in contributor.Observe().Where(o => o.Key.StartsWith("Runtime:", StringComparison.Ordinal)))
        {
            Assert.IsFalse(FabrCoreSettingsCatalog.IsSecret(fact.Key), $"{fact.Key} would be redacted, which tells an operator nothing.");
        }
    }

    [TestMethod]
    public void AuthType_IsNotClaimedAsApplied_WhenTheHostOwnsTheConnection()
    {
        var contributor = Settings(ProductionOptions(), new Marker(Enabled: true, NativeServiceConnection: true));

        var authType = contributor.Observe().Single(o => o.Key == "Microsoft365Copilot:AuthType");

        Assert.IsFalse(authType.AppliedKnown);
        Assert.IsNotNull(authType.Reason);
    }

    // ----- Diagnostics -----

    [TestMethod]
    public void AgentTypeCheck_ReportsWhetherEveryTypeIsRegistered()
    {
        var options = ProductionOptions();
        var registry = new Registry("crm-agent");
        var agents = CopilotTestAgents.Resolve(options);

        Assert.AreEqual(IntegrationAdministration.Pass, CopilotIntegrationEndpoints.AgentTypeCheck(agents, On, registry).Status);
        Assert.AreEqual(IntegrationAdministration.Skipped, CopilotIntegrationEndpoints.AgentTypeCheck(agents, On, registry: null).Status);
        Assert.AreEqual(IntegrationAdministration.Skipped, CopilotIntegrationEndpoints.AgentTypeCheck(agents, Unconfigured, registry).Status);
        Assert.AreEqual(IntegrationAdministration.Skipped, CopilotIntegrationEndpoints.AgentTypeCheck([], On, registry).Status);

        options.Agents.Add(CopilotTestAgents.Entry("sales", "33333333-3333-3333-3333-333333333333", "missing-agent"));
        var missing = CopilotIntegrationEndpoints.AgentTypeCheck(CopilotTestAgents.Resolve(options), On, registry);
        Assert.AreEqual(IntegrationAdministration.Fail, missing.Status);
        StringAssert.Contains(missing.Message, "'missing-agent' (sales)");
        Assert.IsFalse(missing.Message.Contains("crm-agent", StringComparison.Ordinal), "A registered type is not named as a problem.");
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
