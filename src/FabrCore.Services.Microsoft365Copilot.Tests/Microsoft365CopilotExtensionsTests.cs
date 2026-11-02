using Microsoft.Agents.Builder;
using Microsoft.Agents.Hosting.AspNetCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using FabrCore.Core;

namespace FabrCore.Services.Microsoft365Copilot.Tests;

[TestClass]
public sealed class Microsoft365CopilotExtensionsTests
{
    private const string Agent0 = "Microsoft365Copilot:Agents:0:";
    private const string Agent1 = "Microsoft365Copilot:Agents:1:";

    private static HostApplicationBuilder CreateBuilder(Dictionary<string, string?> configuration)
    {
        var builder = Microsoft.Extensions.Hosting.Host.CreateEmptyApplicationBuilder(new HostApplicationBuilderSettings());
        builder.Configuration.AddInMemoryCollection(configuration);
        return builder;
    }

    private static Dictionary<string, string?> LocalDevelopment() => new()
    {
        ["Microsoft365Copilot:TokenValidation:Enabled"] = "false",
        [Agent0 + "Name"] = "assistant",
        [Agent0 + "AgentType"] = "chat-agent",
    };

    [TestMethod]
    public void RegistersBridgeAndAdapter()
    {
        var builder = CreateBuilder(new()
        {
            [Agent0 + "Name"] = "assistant",
            [Agent0 + "ClientId"] = CopilotTestAgents.ClientId,
            [Agent0 + "ClientSecret"] = "secret",
            [Agent0 + "AgentType"] = "chat-agent",
        });

        builder.AddMicrosoft365Copilot();

        Assert.IsTrue(builder.Services.Any(d => d.ServiceType == typeof(IAgent)));
        Assert.IsTrue(builder.Services.Any(d => d.ServiceType == typeof(FabrCoreCopilotAgent)));
        Assert.IsTrue(builder.Services.Any(d => d.ServiceType == typeof(ICopilotPrincipalResolver)));
        Assert.IsTrue(builder.Services.Any(d => d.ServiceType == typeof(ICopilotAgentProvisioner)));
        Assert.IsTrue(builder.Services.Any(d => d.ServiceType == typeof(CopilotAppPackageBuilder)));

        // Agents SDK configuration was synthesized from the Microsoft365Copilot section.
        Assert.AreEqual(CopilotTestAgents.ClientId,
            builder.Configuration["Connections:ServiceConnection:Settings:ClientId"]);
    }

    [TestMethod]
    public void AnAgentWithItsOwnSignIn_GetsItsOwnHandler_AndTheSdkAcceptsIt()
    {
        var builder = CreateBuilder(new()
        {
            ["Microsoft365Copilot:TenantId"] = "tenant",
            ["Microsoft365Copilot:AuthType"] = "FederatedCredentials",
            ["Microsoft365Copilot:FederatedClientId"] = "33333333-3333-3333-3333-333333333333",
            [Agent0 + "Name"] = "assistant",
            [Agent0 + "ClientId"] = CopilotTestAgents.ClientId,
            [Agent0 + "AgentType"] = "chat-agent",
            [Agent0 + "SignInConnectionName"] = "assistant-sso",
            [Agent1 + "Name"] = "sales",
            [Agent1 + "ClientId"] = CopilotTestAgents.SecondClientId,
            [Agent1 + "AgentType"] = "sales-agent",
        });

        builder.AddMicrosoft365Copilot();

        Assert.AreEqual("assistant-sso", builder.Configuration["AgentApplication:UserAuthorization:Handlers:assistant:Settings:AzureBotOAuthConnectionName"]);
        Assert.AreEqual("assistant", builder.Configuration["AgentApplication:UserAuthorization:Default"]);
        Assert.AreEqual("false", builder.Configuration["AgentApplication:UserAuthorization:AutoSignIn"]);
        Assert.IsNull(builder.Configuration["AgentApplication:UserAuthorization:Handlers:sales:Settings:AzureBotOAuthConnectionName"]);

        builder.Services.AddSingleton(FabrCoreCopilotAgentStreamingTests.ThrowingProxy.Create<FabrCore.Host.Services.IFabrCoreAgentService>());
        builder.Services.AddSingleton(FabrCoreCopilotAgentStreamingTests.ThrowingProxy.Create<Orleans.IGrainFactory>());
        builder.Services.AddSingleton(FabrCoreCopilotAgentStreamingTests.ThrowingProxy.Create<IPrincipalContextStore>());
        using var provider = builder.Services.BuildServiceProvider();

        // The SDK reads the handlers when it builds its options, and the bridge adds its routes in its constructor.
        Assert.IsNotNull(provider.GetRequiredService<FabrCoreCopilotAgent>());
        Assert.IsInstanceOfType<ICopilotUserAssertionSink>(provider.GetRequiredService<ICopilotUserAssertionSink>());
    }

    [TestMethod]
    public void SeveralAgents_EachGetACatalogEntryAndAConnection()
    {
        var builder = CreateBuilder(new()
        {
            ["Microsoft365Copilot:TenantId"] = "tenant",
            [Agent0 + "Name"] = "assistant",
            [Agent0 + "Route"] = "/api/messages",
            [Agent0 + "ClientId"] = CopilotTestAgents.ClientId,
            [Agent0 + "ClientSecret"] = "secret-a",
            [Agent0 + "AgentType"] = "chat-agent",
            [Agent1 + "Name"] = "sales",
            [Agent1 + "ClientId"] = CopilotTestAgents.SecondClientId,
            [Agent1 + "ClientSecret"] = "secret-b",
            [Agent1 + "AgentType"] = "sales-agent",
            [Agent1 + "Handle"] = "sales-desk",
        });

        builder.AddMicrosoft365Copilot();

        using var provider = builder.Services.BuildServiceProvider();
        var catalog = provider.GetRequiredService<CopilotAgentCatalog>();
        CollectionAssert.AreEqual(new[] { "/api/messages", "/api/messages/sales" }, catalog.Agents.Select(a => a.Route).ToArray());
        Assert.AreEqual("sales-desk", catalog.Find("sales")!.Handle);
        Assert.AreEqual("secret-b", builder.Configuration["Connections:ServiceConnection-sales:Settings:ClientSecret"]);
        Assert.AreEqual(CopilotTestAgents.SecondClientId, builder.Configuration["ConnectionsMap:1:Audience"]);
    }

    [TestMethod]
    public void EnabledWithNoAgents_StartsAndExposesNothing()
    {
        var builder = CreateBuilder(new() { ["Microsoft365Copilot:Enabled"] = "true" });

        builder.AddMicrosoft365Copilot();

        using var provider = builder.Services.BuildServiceProvider();
        Assert.IsEmpty(provider.GetRequiredService<CopilotAgentCatalog>().Agents);
        Assert.IsNull(builder.Configuration["Connections:ServiceConnection:Settings:ClientId"]);
    }

    [TestMethod]
    public void DisabledAddon_RegistersOnlyMarkerAndOptions()
    {
        var builder = CreateBuilder(new()
        {
            ["Microsoft365Copilot:Enabled"] = "false",
        });

        builder.AddMicrosoft365Copilot();

        Assert.IsFalse(builder.Services.Any(d => d.ServiceType == typeof(IAgent)));
        Assert.IsNull(builder.Configuration["Connections:ServiceConnection:Settings:ClientId"]);
    }

    [TestMethod]
    public void DisabledAddon_DoesNotValidateWhatItWillNotServe()
    {
        var builder = CreateBuilder(new()
        {
            ["Microsoft365Copilot:Enabled"] = "false",
            ["Microsoft365Copilot:Agent:AgentType"] = "chat-agent",
            [Agent0 + "Name"] = "not a valid name",
        });

        builder.AddMicrosoft365Copilot();

        using var provider = builder.Services.BuildServiceProvider();
        Assert.IsEmpty(provider.GetRequiredService<CopilotAgentCatalog>().Agents);
    }

    [TestMethod]
    public void UnconfiguredAddon_StartsWithTheChannelOff_AndSaysWhy()
    {
        var builder = CreateBuilder(new());

        builder.AddMicrosoft365Copilot();

        var marker = (Microsoft365CopilotExtensions.Microsoft365CopilotMarker)builder.Services
            .Single(d => d.ServiceType == typeof(Microsoft365CopilotExtensions.Microsoft365CopilotMarker)).ImplementationInstance!;
        Assert.IsFalse(marker.Enabled);
        Assert.IsFalse(marker.Configured);
        StringAssert.Contains(marker.DisabledReason, "No Microsoft365Copilot configuration");
        Assert.IsFalse(builder.Services.Any(d => d.ServiceType == typeof(IAgent)));
        Assert.IsNull(builder.Configuration["Connections:ServiceConnection:Settings:ClientId"]);
    }

    [TestMethod]
    public void DisabledAddon_StillAdvertisesAndReportsItself()
    {
        foreach (var configuration in new Dictionary<string, string?>[] { new(), new() { ["Microsoft365Copilot:Enabled"] = "false" } })
        {
            var builder = CreateBuilder(configuration);

            builder.AddMicrosoft365Copilot();

            Assert.IsTrue(builder.Services.Any(d =>
                d.ServiceType == typeof(FabrCore.Services.Contracts.Capabilities.IFabrCoreCapabilityContributor)));
            Assert.IsTrue(builder.Services.Any(d =>
                d.ServiceType == typeof(FabrCore.Host.Configuration.Cloud.IFabrCoreRuntimeSettingsContributor)));
        }
    }

    [TestMethod]
    public void ExplicitlyDisabledAddon_RecordsThatItWasConfiguredOff()
    {
        var builder = CreateBuilder(new() { ["Microsoft365Copilot:Enabled"] = "false" });

        builder.AddMicrosoft365Copilot();

        var marker = (Microsoft365CopilotExtensions.Microsoft365CopilotMarker)builder.Services
            .Single(d => d.ServiceType == typeof(Microsoft365CopilotExtensions.Microsoft365CopilotMarker)).ImplementationInstance!;
        Assert.IsFalse(marker.Enabled);
        Assert.IsTrue(marker.Configured);
        Assert.AreEqual("Microsoft365Copilot:Enabled is false.", marker.DisabledReason);
    }

    [TestMethod]
    public void CodeDelegateAlone_CountsAsConfiguration_AndIsStillValidated()
    {
        // Supplying options in code is a decision to run the channel; an incomplete one must
        // fail loudly rather than be mistaken for an untouched add-on.
        var incomplete = CreateBuilder(new());
        Assert.ThrowsExactly<InvalidOperationException>(() =>
            incomplete.AddMicrosoft365Copilot(o => o.Agents.Add(new CopilotAgentEntryOptions { Name = "assistant" })));

        var complete = CreateBuilder(new());
        complete.AddMicrosoft365Copilot(o =>
        {
            o.TokenValidation.Enabled = false;
            o.Agents.Add(CopilotTestAgents.Entry(clientId: null));
        });
        Assert.IsTrue(complete.Services.Any(d => d.ServiceType == typeof(IAgent)));
    }

    [TestMethod]
    public void NativeAgentsSdkConnections_AreRecordedBeforeTheBridgeRuns()
    {
        var native = CreateBuilder(new()
        {
            ["Connections:ServiceConnection:Settings:ClientId"] = "host-owned",
            [Agent0 + "Name"] = "assistant",
            [Agent0 + "ClientId"] = "client",
            [Agent0 + "AgentType"] = "chat-agent",
        });
        native.AddMicrosoft365Copilot();
        var synthesized = CreateBuilder(new()
        {
            [Agent0 + "Name"] = "assistant",
            [Agent0 + "ClientId"] = "client",
            [Agent0 + "ClientSecret"] = "secret",
            [Agent0 + "AgentType"] = "chat-agent",
        });
        synthesized.AddMicrosoft365Copilot();

        static bool Native(HostApplicationBuilder builder) => ((Microsoft365CopilotExtensions.Microsoft365CopilotMarker)builder.Services
            .Single(d => d.ServiceType == typeof(Microsoft365CopilotExtensions.Microsoft365CopilotMarker)).ImplementationInstance!).NativeServiceConnection;
        Assert.IsTrue(Native(native));
        Assert.IsFalse(Native(synthesized));
    }

    [TestMethod]
    [DataRow("Microsoft365Copilot:Agent:AgentType", "chat-agent", "'Agent'")]
    [DataRow("Microsoft365Copilot:Agent:Binding", "assistant", "'Agent'")]
    [DataRow("Microsoft365Copilot:MessagesEndpoint", "/api/messages", "'MessagesEndpoint'")]
    [DataRow("Microsoft365Copilot:ClientId", "client", "'ClientId'")]
    public void Throws_OnTheSingleAgentSettingsItNoLongerReads(string key, string value, string named)
    {
        var configuration = new Dictionary<string, string?>
        {
            ["Microsoft365Copilot:TokenValidation:Enabled"] = "false",
            [key] = value,
        };

        var error = Assert.ThrowsExactly<InvalidOperationException>(() => CreateBuilder(configuration).AddMicrosoft365Copilot());

        StringAssert.Contains(error.Message, named);
        StringAssert.Contains(error.Message, "Microsoft365Copilot:Agents");
    }

    [TestMethod]
    public void SingleAgentSettings_BesideAnAgentsList_AreIgnoredAndRecorded()
    {
        // What a management console publishes while a cluster still has hosts on the older add-on.
        var configuration = LocalDevelopment();
        configuration["Microsoft365Copilot:ClientId"] = "legacy-client";
        configuration["Microsoft365Copilot:Agent:Binding"] = "assistant";
        var builder = CreateBuilder(configuration);

        builder.AddMicrosoft365Copilot();

        var marker = (Microsoft365CopilotExtensions.Microsoft365CopilotMarker)builder.Services
            .Single(d => d.ServiceType == typeof(Microsoft365CopilotExtensions.Microsoft365CopilotMarker)).ImplementationInstance!;
        CollectionAssert.AreEquivalent(new[] { "Agent", "ClientId" }, marker.IgnoredLegacyKeys.ToArray());
        using var provider = builder.Services.BuildServiceProvider();
        Assert.IsNull(provider.GetRequiredService<CopilotAgentCatalog>().Agents.Single().ClientId, "The legacy client id is not adopted.");
    }

    [TestMethod]
    public void Throws_WhenTokenValidationEnabled_WithoutClientId()
    {
        var builder = CreateBuilder(new()
        {
            [Agent0 + "Name"] = "assistant",
            [Agent0 + "AgentType"] = "chat-agent",
        });

        var error = Assert.ThrowsExactly<InvalidOperationException>(() => builder.AddMicrosoft365Copilot());

        StringAssert.Contains(error.Message, "'assistant' needs a ClientId");
    }

    [TestMethod]
    public void Throws_WhenASecretCredentialHasNoSecret()
    {
        var builder = CreateBuilder(new()
        {
            [Agent0 + "Name"] = "assistant",
            [Agent0 + "ClientId"] = "client",
            [Agent0 + "AgentType"] = "chat-agent",
        });

        var error = Assert.ThrowsExactly<InvalidOperationException>(() => builder.AddMicrosoft365Copilot());

        StringAssert.Contains(error.Message, "'assistant' needs a ClientSecret");
    }

    [TestMethod]
    public void AllowsMissingCredentials_ForLocalDevelopment()
    {
        var builder = CreateBuilder(LocalDevelopment());

        builder.AddMicrosoft365Copilot();

        Assert.IsTrue(builder.Services.Any(d => d.ServiceType == typeof(IAgent)));
    }

    [TestMethod]
    public void CodeOverride_WinsOverConfiguration()
    {
        var configuration = LocalDevelopment();
        configuration[Agent0 + "Handle"] = "from-config";
        var builder = CreateBuilder(configuration);

        builder.AddMicrosoft365Copilot(o => o.Agents[0].Handle = "from-code");

        using var provider = builder.Services.BuildServiceProvider();
        Assert.AreEqual("from-code", provider.GetRequiredService<IOptions<Microsoft365CopilotOptions>>().Value.Agents[0].Handle);
        Assert.AreEqual("from-code", provider.GetRequiredService<CopilotAgentCatalog>().Agents[0].Handle);
    }

    [TestMethod]
    public void ProactiveRelay_IsOptInAndUsesGenericRelayContract()
    {
        var disabled = CreateBuilder(LocalDevelopment());
        disabled.AddMicrosoft365Copilot();
        Assert.IsFalse(disabled.Services.Any(descriptor =>
            descriptor.ServiceType == typeof(IPrincipalMessageRelay)));

        var configuration = LocalDevelopment();
        configuration["Microsoft365Copilot:Proactive:Enabled"] = "true";
        var enabled = CreateBuilder(configuration);
        enabled.AddMicrosoft365Copilot();

        Assert.IsTrue(enabled.Services.Any(descriptor =>
            descriptor.ServiceType == typeof(IPrincipalMessageRelay) &&
            descriptor.ImplementationType == typeof(CopilotPrincipalMessageRelay)));
    }
}
