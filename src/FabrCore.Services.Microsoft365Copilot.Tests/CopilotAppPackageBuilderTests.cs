using System.IO.Compression;
using System.Text.Json;
using Microsoft.Extensions.Options;

namespace FabrCore.Services.Microsoft365Copilot.Tests;

[TestClass]
public sealed class CopilotAppPackageBuilderTests
{
    /// <summary>One configured agent, with the builder that packages it.</summary>
    private sealed record Packaged(CopilotAppPackageBuilder Builder, CopilotAgent Agent)
    {
        public string ManifestJson() => Builder.BuildManifestJson(Agent);
        public byte[] Zip() => Builder.BuildPackageZip(Agent);
        public string ManifestName => Builder.ManifestName(Agent);
        public bool Matches(string? name) => Builder.FindByManifestName(name) is not null;
    }

    private static Packaged CreateBuilder(Action<Microsoft365CopilotOptions>? configure = null)
    {
        var options = CopilotTestAgents.Single(o =>
        {
            o.Manifest.Name = "Test Agent";
            o.Manifest.PublicHostName = "agents.contoso.com";
        });
        configure?.Invoke(options);
        var catalog = CopilotTestAgents.Catalog(options);
        return new Packaged(new CopilotAppPackageBuilder(Options.Create(options), catalog), catalog.Agents[0]);
    }

    [TestMethod]
    public void OnlyAnAgentWithItsOwnSignIn_DeclaresSingleSignOn()
    {
        var options = CopilotTestAgents.Single(o => o.Manifest.PublicHostName = "agents.contoso.com");
        options.Agents[0].SignInConnectionName = "assistant-sso";
        options.Agents.Add(CopilotTestAgents.Entry("sales", CopilotTestAgents.SecondClientId));
        options.UserAuthorizationConfigured = true;
        var catalog = CopilotTestAgents.Catalog(options);
        var builder = new CopilotAppPackageBuilder(Options.Create(options), catalog);

        var signedIn = JsonDocument.Parse(builder.BuildManifestJson(catalog.Agents[0])).RootElement;
        Assert.AreEqual($"api://botid-{CopilotTestAgents.ClientId}", signedIn.GetProperty("webApplicationInfo").GetProperty("resource").GetString());
        Assert.AreEqual(CopilotTestAgents.ClientId, signedIn.GetProperty("webApplicationInfo").GetProperty("id").GetString());

        var other = JsonDocument.Parse(builder.BuildManifestJson(catalog.Agents[1])).RootElement;
        Assert.IsFalse(other.TryGetProperty("webApplicationInfo", out _));
    }

    [TestMethod]
    public void Manifest_DeclaresCustomEngineAgent_BoundToBot()
    {
        var manifest = JsonDocument.Parse(CreateBuilder().ManifestJson()).RootElement;

        var bot = manifest.GetProperty("bots")[0];
        Assert.AreEqual("11111111-2222-3333-4444-555555555555", bot.GetProperty("botId").GetString());
        Assert.AreEqual("personal", bot.GetProperty("scopes")[0].GetString());

        var cea = manifest.GetProperty("copilotAgents").GetProperty("customEngineAgents")[0];
        Assert.AreEqual("11111111-2222-3333-4444-555555555555", cea.GetProperty("id").GetString());
        Assert.AreEqual("bot", cea.GetProperty("type").GetString());

        Assert.AreEqual("1.25", manifest.GetProperty("manifestVersion").GetString());
        Assert.AreEqual("Test Agent", manifest.GetProperty("name").GetProperty("short").GetString());
        Assert.AreEqual("agents.contoso.com", manifest.GetProperty("validDomains")[0].GetString());

        // Removed from manifest schema v1.17+; the store rejects manifests that still carry it
        // because the schema sets additionalProperties: false.
        Assert.IsFalse(manifest.TryGetProperty("packageName", out _));
    }

    [TestMethod]
    public void Manifest_NormalizesPublicHostName_FromUrl()
    {
        var builder = CreateBuilder(o => o.Manifest.PublicHostName = "https://surfaceapp.fabrcore.ai/some/path");
        var manifest = JsonDocument.Parse(builder.ManifestJson()).RootElement;

        Assert.AreEqual("surfaceapp.fabrcore.ai", manifest.GetProperty("validDomains")[0].GetString());
    }

    [TestMethod]
    public void Manifest_NormalizesPublicHostName_FromHostWithPort()
    {
        var builder = CreateBuilder(o => o.Manifest.PublicHostName = "surfaceapp.fabrcore.ai:443");
        var manifest = JsonDocument.Parse(builder.ManifestJson()).RootElement;

        Assert.AreEqual("surfaceapp.fabrcore.ai", manifest.GetProperty("validDomains")[0].GetString());
    }

    [TestMethod]
    public void Manifest_OmitsSsoSection_WithoutUserAuthorization()
    {
        var manifest = JsonDocument.Parse(CreateBuilder().ManifestJson()).RootElement;

        Assert.IsFalse(manifest.TryGetProperty("webApplicationInfo", out _));
    }

    [TestMethod]
    public void Manifest_AddsSsoSection_WhenUserAuthorizationConfigured()
    {
        var builder = CreateBuilder(o => o.UserAuthorizationConfigured = true);
        var manifest = JsonDocument.Parse(builder.ManifestJson()).RootElement;

        var sso = manifest.GetProperty("webApplicationInfo");
        Assert.AreEqual("api://botid-11111111-2222-3333-4444-555555555555", sso.GetProperty("resource").GetString());

        var domains = manifest.GetProperty("validDomains").EnumerateArray().Select(d => d.GetString()).ToList();
        CollectionAssert.Contains(domains, "token.botframework.com");
    }

    [TestMethod]
    public void Manifest_IncludesConversationStarters()
    {
        var builder = CreateBuilder(o => o.Manifest.ConversationStarters.Add(
            new CopilotConversationStarter { Title = "Status", Description = "What is my order status?" }));
        var manifest = JsonDocument.Parse(builder.ManifestJson()).RootElement;

        var command = manifest.GetProperty("bots")[0].GetProperty("commandLists")[0].GetProperty("commands")[0];
        Assert.AreEqual("Status", command.GetProperty("title").GetString());
    }

    [TestMethod]
    public void Package_ContainsManifestAndIcons()
    {
        var zipBytes = CreateBuilder().Zip();

        using var zip = new ZipArchive(new MemoryStream(zipBytes), ZipArchiveMode.Read);
        var names = zip.Entries.Select(e => e.Name).ToList();
        CollectionAssert.AreEquivalent(new[] { "manifest.json", "color.png", "outline.png" }, names);

        // Icons must be valid PNGs (magic header).
        foreach (var entry in zip.Entries.Where(e => e.Name.EndsWith(".png")))
        {
            using var stream = entry.Open();
            var header = new byte[8];
            stream.ReadExactly(header);
            CollectionAssert.AreEqual(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A }, header);
        }
    }

    [TestMethod]
    public void Manifest_Throws_WithoutClientId()
    {
        var builder = CreateBuilder(o =>
        {
            o.TokenValidation.Enabled = false;
            o.Agents[0].ClientId = null;
        });

        var error = Assert.ThrowsExactly<InvalidOperationException>(() => builder.ManifestJson());

        StringAssert.Contains(error.Message, "'assistant' needs a ClientId");
    }

    [TestMethod]
    public void EachAgent_GetsItsOwnPackage_BoundToItsOwnBot()
    {
        var options = new Microsoft365CopilotOptions
        {
            Manifest = { DeveloperName = "Contoso", PublicHostName = "agents.contoso.com" },
            Agents =
            {
                CopilotTestAgents.Entry("assistant", CopilotTestAgents.ClientId),
                CopilotTestAgents.Entry("sales", CopilotTestAgents.SecondClientId),
            },
        };
        options.Agents[1].Manifest.Name = "Sales Desk";
        options.Agents[1].Manifest.ConversationStarters.Add(new CopilotConversationStarter { Title = "Pipeline", Description = "Show my pipeline" });
        var catalog = CopilotTestAgents.Catalog(options);
        var builder = new CopilotAppPackageBuilder(Options.Create(options), catalog);

        var assistant = JsonDocument.Parse(builder.BuildManifestJson(catalog.Agents[0])).RootElement;
        var sales = JsonDocument.Parse(builder.BuildManifestJson(catalog.Agents[1])).RootElement;

        Assert.AreEqual(CopilotTestAgents.ClientId, assistant.GetProperty("id").GetString());
        Assert.AreEqual(CopilotTestAgents.ClientId, assistant.GetProperty("bots")[0].GetProperty("botId").GetString());
        Assert.IsFalse(assistant.GetProperty("bots")[0].TryGetProperty("commandLists", out _));

        Assert.AreEqual(CopilotTestAgents.SecondClientId, sales.GetProperty("id").GetString());
        Assert.AreEqual(CopilotTestAgents.SecondClientId, sales.GetProperty("bots")[0].GetProperty("botId").GetString());
        Assert.AreEqual(CopilotTestAgents.SecondClientId,
            sales.GetProperty("copilotAgents").GetProperty("customEngineAgents")[0].GetProperty("id").GetString());
        Assert.AreEqual("Sales Desk", sales.GetProperty("name").GetProperty("short").GetString());
        Assert.AreEqual("Contoso", sales.GetProperty("developer").GetProperty("name").GetString());
        Assert.AreEqual("Pipeline", sales.GetProperty("bots")[0].GetProperty("commandLists")[0].GetProperty("commands")[0].GetProperty("title").GetString());

        Assert.AreEqual("sales", builder.FindByManifestName("sales-desk")!.Name);
        Assert.AreEqual("sales", builder.FindByManifestName(CopilotTestAgents.SecondClientId)!.Name);
        Assert.AreEqual("assistant", builder.FindByManifestName("assistant")!.Name);
    }

    [TestMethod]
    public void ManifestName_IsSlugOfConfiguredName()
    {
        Assert.AreEqual("test-agent", CreateBuilder().ManifestName);
        Assert.AreEqual(
            "my-fabrcore-agent-v2",
            CreateBuilder(o => o.Manifest.Name = "  My FabrCore Agent (v2)! ").ManifestName);
    }

    [TestMethod]
    public void MatchesManifestName_AcceptsSlugNameVariantsAndAppId()
    {
        var builder = CreateBuilder();

        Assert.IsTrue(builder.Matches("test-agent"));
        Assert.IsTrue(builder.Matches("Test Agent"));
        Assert.IsTrue(builder.Matches("TEST-AGENT"));
        Assert.IsTrue(builder.Matches("11111111-2222-3333-4444-555555555555"));

        Assert.IsFalse(builder.Matches("other-agent"));
        Assert.IsFalse(builder.Matches(""));
        Assert.IsFalse(builder.Matches(null));
    }

    [TestMethod]
    public void MatchesManifestName_PrefersManifestIdOverClientId()
    {
        var builder = CreateBuilder(o => o.Manifest.Id = "99999999-8888-7777-6666-555555555555");

        Assert.IsTrue(builder.Matches("99999999-8888-7777-6666-555555555555"));
        Assert.IsFalse(builder.Matches("11111111-2222-3333-4444-555555555555"));
        Assert.AreEqual("99999999-8888-7777-6666-555555555555",
            JsonDocument.Parse(builder.ManifestJson()).RootElement.GetProperty("id").GetString());
    }
}
