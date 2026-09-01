using System.Text.Json;
using FabrCore.Core.Skills;
using FabrCore.Host.Services;

using FabrCore.Host.A2A;
using FabrCore.Host.Configuration;
using FabrCore.Host.Testing;
namespace FabrCore.Host.Tests.A2A;

/// <summary>
/// FabrCore harness skills (<c>_HarnessSkills</c>, stored per principal through the FabrCore API)
/// and A2A card skills are different things sharing a word. These cover the bridge between them.
/// </summary>
[TestClass]
public sealed class A2AHarnessSkillTests
{
    private static Dictionary<string, string?> Config() => new()
    {
        ["A2A:Enabled"] = "true",
        ["A2A:PublicBaseUrl"] = "https://agents.contoso.com",
        ["A2A:Authentication:Mode"] = "None",
        ["A2A:AgentTypes:0"] = "botanical-agent",
        ["A2A:Defaults:Args:_HarnessSkills"] = "order-lookup@1.2.0,returns-policy@2.0.0",
    };

    private static FakeSkillCatalog Catalog() => new FakeSkillCatalog()
        .With("a2a", "order-lookup", "1.2.0", "Looks up Contoso order status from an order number.")
        .With("a2a", "returns-policy", "2.0.0", "Explains Contoso return eligibility and windows.");

    private static async Task<List<(string Name, string Description)>> SkillsAsync(FabrCoreA2ATestHost host, string path)
    {
        using var card = await host.GetJsonAsync(path);
        return card.RootElement.GetProperty("skills")
            .EnumerateArray()
            .Select(s => (s.GetProperty("name").GetString()!, s.GetProperty("description").GetString()!))
            .ToList();
    }

    [TestMethod]
    public async Task HarnessSkillsTheAgentLoadsAreAdvertisedOnItsCard()
    {
        await using var host = await A2ATestHost.StartAsync(Config(), skillCatalog: Catalog());

        var skills = await SkillsAsync(host, "/a2a/botanical-agent/.well-known/agent-card.json");

        // The agent's own description stays first, with each loaded harness skill appended.
        Assert.AreEqual("Botanical Agent", skills[0].Name);
        CollectionAssert.Contains(skills.Select(s => s.Name).ToList(), "order-lookup");
        CollectionAssert.Contains(skills.Select(s => s.Name).ToList(), "returns-policy");
        Assert.AreEqual(
            "Looks up Contoso order status from an order number.",
            skills.Single(s => s.Name == "order-lookup").Description);
    }

    [TestMethod]
    public async Task HarnessSkillsAreTaggedSoAnOrchestratorCanTellThemApart()
    {
        await using var host = await A2ATestHost.StartAsync(Config(), skillCatalog: Catalog());

        using var card = await host.GetJsonAsync("/a2a/botanical-agent/.well-known/agent-card.json");
        var skill = card.RootElement.GetProperty("skills")
            .EnumerateArray()
            .Single(s => s.GetProperty("name").GetString() == "order-lookup");

        var tags = skill.GetProperty("tags").EnumerateArray().Select(t => t.GetString()).ToList();
        CollectionAssert.Contains(tags, "harness-skill");
    }

    [TestMethod]
    public async Task ADeclaredSkillThePrincipalNeverPublishedIsOmittedRatherThanFaked()
    {
        var catalog = new FakeSkillCatalog()
            .With("a2a", "order-lookup", "1.2.0", "Looks up Contoso order status.");

        await using var host = await A2ATestHost.StartAsync(Config(), skillCatalog: catalog);

        var names = (await SkillsAsync(host, "/a2a/botanical-agent/.well-known/agent-card.json"))
            .Select(s => s.Name).ToList();

        CollectionAssert.Contains(names, "order-lookup");
        CollectionAssert.DoesNotContain(names, "returns-policy");
    }

    [TestMethod]
    public async Task AWrongVersionIsNotSilentlySubstituted()
    {
        // Harness skill references are exact-version by design; a card must not imply otherwise.
        var catalog = new FakeSkillCatalog()
            .With("a2a", "order-lookup", "9.9.9", "A different version entirely.");

        await using var host = await A2ATestHost.StartAsync(Config(), skillCatalog: catalog);

        var names = (await SkillsAsync(host, "/a2a/botanical-agent/.well-known/agent-card.json"))
            .Select(s => s.Name).ToList();

        CollectionAssert.DoesNotContain(names, "order-lookup");
    }

    [TestMethod]
    public async Task AnAgentPublishedByHandleReadsTheSkillsOfItsOwnPrincipal()
    {
        var config = Config();
        config.Remove("A2A:AgentTypes:0");
        config["A2A:AgentHandles:0"] = "contoso:assistant";

        // The principal is in the handle, so it is knowable regardless of the caller strategy.
        config["A2A:Principal:Strategy"] = "ContextId";

        var catalog = new FakeSkillCatalog()
            .With("contoso", "order-lookup", "1.2.0", "Contoso order status lookups.")
            .With("a2a", "order-lookup", "1.2.0", "The wrong principal's copy.");

        await using var host = await A2ATestHost.StartAsync(config, skillCatalog: catalog);

        var skills = await SkillsAsync(host, "/a2a/assistant/.well-known/agent-card.json");
        Assert.AreEqual(
            "Contoso order status lookups.",
            skills.Single(s => s.Name == "order-lookup").Description);
    }

    [TestMethod]
    public async Task PerCallerPrincipalStrategiesLeaveHarnessSkillsOffTheSharedCard()
    {
        // Under a per-caller strategy the catalog differs by caller, but one card is served to
        // everyone — and usually before the caller has authenticated at all. Claiming skills we
        // cannot attribute would be worse than saying nothing.
        var config = Config();
        config["A2A:Principal:Strategy"] = "ContextId";

        var catalog = Catalog();
        await using var host = await A2ATestHost.StartAsync(config, skillCatalog: catalog);

        var names = (await SkillsAsync(host, "/a2a/botanical-agent/.well-known/agent-card.json"))
            .Select(s => s.Name).ToList();

        CollectionAssert.DoesNotContain(names, "order-lookup");
        Assert.AreEqual(0, catalog.ListCalls, "The catalog should not be read when it cannot be attributed.");
    }

    [TestMethod]
    public async Task TheFeatureCanBeTurnedOff()
    {
        var config = Config();
        config["A2A:Discovery:IncludeHarnessSkills"] = "false";

        var catalog = Catalog();
        await using var host = await A2ATestHost.StartAsync(config, skillCatalog: catalog);

        var names = (await SkillsAsync(host, "/a2a/botanical-agent/.well-known/agent-card.json"))
            .Select(s => s.Name).ToList();

        CollectionAssert.DoesNotContain(names, "order-lookup");
        Assert.AreEqual(0, catalog.ListCalls);
    }

    [TestMethod]
    public async Task AgentsThatLoadNoHarnessSkillsNeverTouchTheCatalog()
    {
        var config = Config();
        config.Remove("A2A:Defaults:Args:_HarnessSkills");

        var catalog = Catalog();
        await using var host = await A2ATestHost.StartAsync(config, skillCatalog: catalog);

        await host.GetJsonAsync("/a2a/botanical-agent/.well-known/agent-card.json");

        Assert.AreEqual(0, catalog.ListCalls);
    }

    [TestMethod]
    public async Task CatalogReadsAreCachedAcrossCardRequests()
    {
        var catalog = Catalog();
        await using var host = await A2ATestHost.StartAsync(Config(), skillCatalog: catalog);

        for (var i = 0; i < 5; i++)
        {
            await host.GetJsonAsync("/a2a/botanical-agent/.well-known/agent-card.json");
        }

        Assert.AreEqual(1, catalog.ListCalls);
    }

    [TestMethod]
    public async Task ACatalogFailureDegradesTheCardInsteadOfBreakingIt()
    {
        // A client fetches the card before it can do anything else, so a skill-store hiccup must
        // not take discovery down with it.
        var catalog = new FakeSkillCatalog { ThrowOnList = true };

        await using var host = await A2ATestHost.StartAsync(Config(), skillCatalog: catalog);

        using var card = await host.GetJsonAsync("/a2a/botanical-agent/.well-known/agent-card.json");
        Assert.AreEqual("Botanical Agent", card.RootElement.GetProperty("name").GetString());
        Assert.AreEqual(1, card.RootElement.GetProperty("skills").GetArrayLength());
    }

    [TestMethod]
    public async Task DeclaredHarnessSkillsReachTheProvisionedAgentAsAnArg()
    {
        await using var host = await A2ATestHost.StartAsync(Config(), skillCatalog: Catalog());

        await host.PostJsonAsync(
            "/a2a/botanical-agent",
            """{"jsonrpc":"2.0","id":1,"method":"message/send","params":{"message":{"kind":"message","role":"user","messageId":"m-1","parts":[{"kind":"text","text":"hi"}]}}}""");

        var config = host.AgentService.Ensured.Single().Configs.Single();
        Assert.AreEqual("order-lookup@1.2.0,returns-policy@2.0.0", config.Args["_HarnessSkills"]);
    }
}

/// <summary>Stand-in for the principal-scoped harness skill catalog.</summary>
internal sealed class FakeSkillCatalog : IFabrCoreSkillCatalogService
{
    private readonly Dictionary<string, List<FabrCoreSkillCatalogEntry>> _byPrincipal = new(StringComparer.OrdinalIgnoreCase);

    public int ListCalls { get; private set; }

    public bool ThrowOnList { get; set; }

    public FakeSkillCatalog With(string principal, string name, string version, string description)
    {
        if (!_byPrincipal.TryGetValue(principal, out var entries))
        {
            _byPrincipal[principal] = entries = new List<FabrCoreSkillCatalogEntry>();
        }

        entries.Add(new FabrCoreSkillCatalogEntry
        {
            Name = name,
            Version = version,
            Description = description,
            PublishedUtc = DateTimeOffset.UtcNow,
        });

        return this;
    }

    public Task<IReadOnlyList<FabrCoreSkillCatalogEntry>> ListAsync(
        string principalId, CancellationToken cancellationToken = default)
    {
        ListCalls++;
        if (ThrowOnList)
        {
            throw new InvalidOperationException("skill store unavailable");
        }

        return Task.FromResult<IReadOnlyList<FabrCoreSkillCatalogEntry>>(
            _byPrincipal.TryGetValue(principalId, out var entries) ? entries : []);
    }

    public Task<FabrCoreSkillManifest?> GetAsync(
        string principalId, string name, string version, CancellationToken cancellationToken = default)
        => throw new NotSupportedException();

    public Task<FabrCoreSkillPublishResult> PublishAsync(
        string principalId, string name, string version, Stream zipStream, CancellationToken cancellationToken = default)
        => throw new NotSupportedException();

    public Task<FabrCoreSkillManifest?> DeleteAsync(
        string principalId, string name, string version, CancellationToken cancellationToken = default)
        => throw new NotSupportedException();
}
