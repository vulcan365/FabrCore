using System.Net;
using System.Text.Json;

using FabrCore.Host.A2A;
using FabrCore.Host.Configuration;
using FabrCore.Host.Testing;
namespace FabrCore.Host.Tests.A2A;

[TestClass]
public sealed class A2ADiscoveryTests
{
    private static Dictionary<string, string?> MultiAgentConfig() => new()
    {
        ["A2A:Enabled"] = "true",
        ["A2A:PublicBaseUrl"] = "https://agents.contoso.com",
        ["A2A:Authentication:Mode"] = "None",
        ["A2A:AgentTypes:0"] = "botanical-agent",
        ["A2A:AgentTypes:1"] = "support-agent",
    };

    [TestMethod]
    public async Task AgentCard_IsAlsoServedUnderV1_ForClientsThatResolveTheWellKnownPathAgainstTheRestUrl()
    {
        await using var host = await A2ATestHost.StartAsync(MultiAgentConfig());

        foreach (var path in new[]
                 {
                     "/a2a/botanical-agent/v1/.well-known/agent-card.json",
                     "/a2a/botanical-agent/v1/.well-known/agent.json",
                 })
        {
            using var card = await host.GetJsonAsync(path);
            Assert.AreEqual("Botanical Agent", card.RootElement.GetProperty("name").GetString(), path);
        }
    }

    [TestMethod]
    public async Task RootAgentCard_WithSeveralAgentsAndNoPrimary_ExplainsWhereTheCardsAre()
    {
        await using var host = await A2ATestHost.StartAsync(MultiAgentConfig());

        var response = await host.Client.GetAsync("/.well-known/agent-card.json");

        Assert.AreEqual(HttpStatusCode.NotFound, response.StatusCode);
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());

        StringAssert.Contains(body.RootElement.GetProperty("message").GetString(), "A2A:PrimaryAgent");
        Assert.AreEqual(
            "https://agents.contoso.com/a2a/support-agent/.well-known/agent-card.json",
            body.RootElement.GetProperty("agentCards").GetProperty("support-agent").GetString());
    }

    [TestMethod]
    public async Task RootAgentCard_ServesThePrimaryAgentWhenOneIsDesignated()
    {
        var config = MultiAgentConfig();
        config["A2A:PrimaryAgent"] = "support-agent";

        await using var host = await A2ATestHost.StartAsync(config);

        using var card = await host.GetJsonAsync("/.well-known/agent.json");
        Assert.AreEqual("Support Agent", card.RootElement.GetProperty("name").GetString());
    }

    [TestMethod]
    public async Task UnregisteredAgentType_StillGetsAUsableCard()
    {
        // "support-agent" is not in the fake registry, so the card falls back to a title-cased
        // name and a generated description rather than failing to publish.
        await using var host = await A2ATestHost.StartAsync(MultiAgentConfig());

        using var card = await host.GetJsonAsync("/a2a/support-agent/.well-known/agent-card.json");
        Assert.AreEqual("Support Agent", card.RootElement.GetProperty("name").GetString());
        Assert.AreEqual(
            "Support Agent, a FabrCore agent.", card.RootElement.GetProperty("description").GetString());
        Assert.AreEqual(1, card.RootElement.GetProperty("skills").GetArrayLength());
    }
}
