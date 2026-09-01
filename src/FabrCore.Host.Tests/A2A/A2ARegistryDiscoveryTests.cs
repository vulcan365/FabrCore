using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;

using FabrCore.Host.A2A;
using FabrCore.Host.Configuration;
using FabrCore.Host.Testing;
namespace FabrCore.Host.Tests.A2A;

/// <summary>
/// Covers the low-configuration path: publishing agents from the FabrCore registry and the live
/// agent list instead of naming each one in configuration.
/// </summary>
[TestClass]
public sealed class A2ARegistryDiscoveryTests
{
    /// <summary>The whole A2A section needed to publish a fleet — no agent is named.</summary>
    private static Dictionary<string, string?> DiscoveryConfig() => new()
    {
        ["A2A:Enabled"] = "true",
        ["A2A:PublicBaseUrl"] = "https://agents.contoso.com",
        ["A2A:Authentication:Mode"] = "None",
        ["A2A:Discovery:AgentTypes"] = "Described",
    };

    private static async Task<List<string>> PublishedNamesAsync(FabrCoreA2ATestHost host)
    {
        using var catalog = await host.GetJsonAsync("/a2a");
        return catalog.RootElement.GetProperty("agents")
            .EnumerateArray()
            .Select(a => a.GetProperty("name").GetString()!)
            .ToList();
    }

    [TestMethod]
    public async Task Described_PublishesEveryRegisteredAgentTypeThatHasADescription()
    {
        await using var host = await A2ATestHost.StartAsync(DiscoveryConfig(), useRealRegistry: true);

        var names = await PublishedNamesAsync(host);

        CollectionAssert.Contains(names, "botanical-agent");
        CollectionAssert.Contains(names, "support-agent");
    }

    [TestMethod]
    public async Task Described_SkipsAgentTypesWithNoDescription()
    {
        await using var host = await A2ATestHost.StartAsync(DiscoveryConfig(), useRealRegistry: true);

        // An agent with nothing to say about itself is not one a remote orchestrator should be
        // choosing, so [Description] is the opt-in.
        CollectionAssert.DoesNotContain(await PublishedNamesAsync(host), "internal-worker-agent");
    }

    [TestMethod]
    public async Task All_PublishesUndescribedAgentTypesToo()
    {
        var config = DiscoveryConfig();
        config["A2A:Discovery:AgentTypes"] = "All";

        await using var host = await A2ATestHost.StartAsync(config, useRealRegistry: true);

        CollectionAssert.Contains(await PublishedNamesAsync(host), "internal-worker-agent");
    }

    [TestMethod]
    public async Task HiddenAgentTypesAreNeverPublished()
    {
        // [FabrCoreHidden] keeps an agent out of /fabrcoreapi/discovery, and the registry applies
        // that filter before A2A sees the type — so hiding it once hides it everywhere.
        var config = DiscoveryConfig();
        config["A2A:Discovery:AgentTypes"] = "All";

        await using var host = await A2ATestHost.StartAsync(config, useRealRegistry: true);

        CollectionAssert.DoesNotContain(await PublishedNamesAsync(host), "secret-agent");

        var response = await host.Client.GetAsync("/a2a/secret-agent/.well-known/agent-card.json");
        Assert.AreEqual(System.Net.HttpStatusCode.NotFound, response.StatusCode);
    }

    [TestMethod]
    public async Task DiscoveryIsOffByDefault()
    {
        var config = DiscoveryConfig();
        config.Remove("A2A:Discovery:AgentTypes");

        await using var host = await A2ATestHost.StartAsync(config, useRealRegistry: true);

        Assert.AreEqual(0, (await PublishedNamesAsync(host)).Count);
    }

    [TestMethod]
    public async Task ExcludeGlobsWithholdMatchingAgentTypes()
    {
        var config = DiscoveryConfig();
        config["A2A:Discovery:AgentTypes"] = "All";
        config["A2A:Discovery:ExcludeAgentTypes:0"] = "internal-*";

        await using var host = await A2ATestHost.StartAsync(config, useRealRegistry: true);
        var names = await PublishedNamesAsync(host);

        CollectionAssert.DoesNotContain(names, "internal-worker-agent");
        CollectionAssert.Contains(names, "botanical-agent");
    }

    [TestMethod]
    public async Task IncludeGlobsNarrowToMatchingAgentTypes()
    {
        var config = DiscoveryConfig();
        config["A2A:Discovery:IncludeAgentTypes:0"] = "*-agent";
        config["A2A:Discovery:ExcludeAgentTypes:0"] = "support-*";

        await using var host = await A2ATestHost.StartAsync(config, useRealRegistry: true);
        var names = await PublishedNamesAsync(host);

        CollectionAssert.Contains(names, "botanical-agent");
        CollectionAssert.DoesNotContain(names, "support-agent");
    }

    [TestMethod]
    public async Task DiscoveredAgentsCarryTheirRegistryMetadataOntoTheCard()
    {
        await using var host = await A2ATestHost.StartAsync(DiscoveryConfig(), useRealRegistry: true);

        using var card = await host.GetJsonAsync("/a2a/botanical-agent/.well-known/agent-card.json");
        var root = card.RootElement;

        Assert.AreEqual("Answers questions about plants and botany.", root.GetProperty("description").GetString());

        var skill = root.GetProperty("skills")[0];
        var tags = skill.GetProperty("tags").EnumerateArray().Select(t => t.GetString()).ToList();
        CollectionAssert.Contains(tags, "plants");
        CollectionAssert.Contains(tags, "botany");

        // [FabrCoreNote] usually says when *not* to use an agent — exactly what an orchestrator
        // needs — so notes become skill examples.
        var examples = skill.GetProperty("examples").EnumerateArray().Select(e => e.GetString()!).ToList();
        Assert.IsTrue(examples.Any(e => e.Contains("quotes-agent")), string.Join(" | ", examples));
    }

    [TestMethod]
    public async Task NotesCanBeLeftOffTheCard()
    {
        var config = DiscoveryConfig();
        config["A2A:Discovery:IncludeNotes"] = "false";

        await using var host = await A2ATestHost.StartAsync(config, useRealRegistry: true);

        using var card = await host.GetJsonAsync("/a2a/botanical-agent/.well-known/agent-card.json");
        Assert.IsFalse(card.RootElement.GetProperty("skills")[0].TryGetProperty("examples", out _));
    }

    [TestMethod]
    public async Task DiscoveredAgentsAreCallableAndUseTheDefaultsBlock()
    {
        var config = DiscoveryConfig();
        config["A2A:Defaults:Models"] = "fleet-model";
        config["A2A:Defaults:SystemPrompt"] = "You are a Contoso specialist.";
        config["A2A:Defaults:Plugins:0"] = "orders-plugin";

        await using var host = await A2ATestHost.StartAsync(config, useRealRegistry: true);

        var response = await host.PostJsonAsync(
            "/a2a/botanical-agent",
            """{"jsonrpc":"2.0","id":1,"method":"message/send","params":{"message":{"kind":"message","role":"user","messageId":"m-1","parts":[{"kind":"text","text":"hi"}]}}}""");
        response.EnsureSuccessStatusCode();

        var config0 = host.AgentService.Ensured.Single().Configs.Single();
        Assert.AreEqual("botanical-agent", config0.AgentType);
        Assert.AreEqual("a2a-botanical-agent", config0.Handle);
        Assert.AreEqual("fleet-model", config0.Models);
        Assert.AreEqual("You are a Contoso specialist.", config0.SystemPrompt);
        Assert.AreEqual("orders-plugin", config0.Plugins.Single());
    }

    [TestMethod]
    public async Task AnExplicitAgentEntryOverridesTheDiscoveredOne()
    {
        var config = DiscoveryConfig();
        config["A2A:Defaults:Models"] = "fleet-model";
        config["A2A:Agents:0:Name"] = "botanical-agent";
        config["A2A:Agents:0:AgentType"] = "botanical-agent";
        config["A2A:Agents:0:Description"] = "The curated description.";
        config["A2A:Agents:0:Models"] = "special-model";

        await using var host = await A2ATestHost.StartAsync(config, useRealRegistry: true);

        using var card = await host.GetJsonAsync("/a2a/botanical-agent/.well-known/agent-card.json");
        Assert.AreEqual("The curated description.", card.RootElement.GetProperty("description").GetString());

        using var catalog = await host.GetJsonAsync("/a2a");
        var entry = catalog.RootElement.GetProperty("agents")
            .EnumerateArray()
            .Single(a => a.GetProperty("name").GetString() == "botanical-agent");
        Assert.AreEqual("Configured", entry.GetProperty("source").GetString());

        await host.PostJsonAsync(
            "/a2a/botanical-agent",
            """{"jsonrpc":"2.0","id":1,"method":"message/send","params":{"message":{"kind":"message","role":"user","messageId":"m-1","parts":[{"kind":"text","text":"hi"}]}}}""");
        Assert.AreEqual("special-model", host.AgentService.Ensured.Single().Configs.Single().Models);
    }

    // ── Live agent discovery ───────────────────────────────────────────────────────────────

    [TestMethod]
    public async Task LiveAgentGlobsPublishExistingAgentsWithoutProvisioning()
    {
        var config = DiscoveryConfig();
        config.Remove("A2A:Discovery:AgentTypes");
        config["A2A:Discovery:IncludeAgentHandles:0"] = "system:*";

        var agentService = new FakeFabrCoreAgentService()
            .WithLiveAgent("system:assistant")
            .WithLiveAgent("system:researcher")
            .WithLiveAgent("alice:private-notes");

        await using var host = await A2ATestHost.StartAsync(config, agentService);
        var names = await PublishedNamesAsync(host);

        CollectionAssert.AreEquivalent(new[] { "assistant", "researcher" }, names);

        await host.PostJsonAsync(
            "/a2a/assistant",
            """{"jsonrpc":"2.0","id":1,"method":"message/send","params":{"message":{"kind":"message","role":"user","messageId":"m-1","parts":[{"kind":"text","text":"hi"}]}}}""");

        Assert.AreEqual(0, host.AgentService.Ensured.Count, "An existing agent must not be reprovisioned.");
        Assert.AreEqual("system:assistant", host.AgentService.Sends.Single().Handle);
    }

    [TestMethod]
    public async Task AgentsCreatedAfterStartupBecomeReachableWithoutARestart()
    {
        var config = DiscoveryConfig();
        config.Remove("A2A:Discovery:AgentTypes");
        config["A2A:Discovery:IncludeAgentHandles:0"] = "system:*";
        config["A2A:Discovery:RefreshInterval"] = "00:00:00.001";

        var agentService = new FakeFabrCoreAgentService().WithLiveAgent("system:assistant");
        await using var host = await A2ATestHost.StartAsync(config, agentService);

        Assert.AreEqual(
            System.Net.HttpStatusCode.NotFound,
            (await host.Client.GetAsync("/a2a/latecomer/.well-known/agent-card.json")).StatusCode);

        agentService.WithLiveAgent("system:latecomer");
        await Task.Delay(20);

        // Routes are parameterized, so a newly created agent is served without remapping.
        using var card = await host.GetJsonAsync("/a2a/latecomer/.well-known/agent-card.json");
        Assert.AreEqual("Latecomer", card.RootElement.GetProperty("name").GetString());
    }

    [TestMethod]
    public async Task LiveAgentLookupsAreCached()
    {
        var config = DiscoveryConfig();
        config.Remove("A2A:Discovery:AgentTypes");
        config["A2A:Discovery:IncludeAgentHandles:0"] = "system:*";
        config["A2A:Discovery:RefreshInterval"] = "00:05:00";

        var agentService = new FakeFabrCoreAgentService().WithLiveAgent("system:assistant");
        await using var host = await A2ATestHost.StartAsync(config, agentService);

        for (var i = 0; i < 5; i++)
        {
            await host.GetJsonAsync("/a2a/assistant/.well-known/agent-card.json");
        }

        Assert.AreEqual(1, agentService.GetAgentsCalls);
    }

    [TestMethod]
    public async Task RegistryDiscoveryCostsNoClusterCallsWhenNoHandleGlobsAreConfigured()
    {
        await using var host = await A2ATestHost.StartAsync(DiscoveryConfig(), useRealRegistry: true);

        await PublishedNamesAsync(host);
        await host.GetJsonAsync("/a2a/botanical-agent/.well-known/agent-card.json");

        Assert.AreEqual(0, host.AgentService.GetAgentsCalls);
    }

    [TestMethod]
    public async Task ConfiguredAgentsWinANameCollisionWithALiveAgent()
    {
        var config = DiscoveryConfig();
        config.Remove("A2A:Discovery:AgentTypes");
        config["A2A:Discovery:IncludeAgentHandles:0"] = "system:*";
        config["A2A:Agents:0:Name"] = "assistant";
        config["A2A:Agents:0:AgentType"] = "botanical-agent";
        config["A2A:Agents:0:Description"] = "The configured assistant.";

        var agentService = new FakeFabrCoreAgentService().WithLiveAgent("system:assistant");
        await using var host = await A2ATestHost.StartAsync(config, agentService);

        using var card = await host.GetJsonAsync("/a2a/assistant/.well-known/agent-card.json");
        Assert.AreEqual("The configured assistant.", card.RootElement.GetProperty("description").GetString());

        // The live system:assistant does not vanish — it matched the operator's glob, so it is
        // republished under its fully-qualified name instead of losing the route to the
        // configured entry.
        var names = await PublishedNamesAsync(host);
        Assert.AreEqual(1, names.Count(n => n == "assistant"));
        CollectionAssert.Contains(names, "system-assistant");

        using var liveCard = await host.GetJsonAsync("/a2a/system-assistant/.well-known/agent-card.json");
        StringAssert.Contains(liveCard.RootElement.GetProperty("description").GetString(), "system:assistant");
    }

    [TestMethod]
    public async Task EmptyRoutePrefixIsRejectedSoAgentRoutesCannotClaimTheWholeServer()
    {
        var builder = Microsoft.Extensions.Hosting.Host.CreateEmptyApplicationBuilder(
            new HostApplicationBuilderSettings());
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["A2A:Enabled"] = "true",
            ["A2A:Authentication:Mode"] = "None",
            ["A2A:RoutePrefix"] = "",
            ["A2A:Discovery:AgentTypes"] = "All",
        });

        var ex = Assert.ThrowsExactly<InvalidOperationException>(() => builder.AddA2A());
        StringAssert.Contains(ex.Message, "RoutePrefix");
    }
}
