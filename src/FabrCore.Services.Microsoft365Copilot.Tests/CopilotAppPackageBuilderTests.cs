using System.IO.Compression;
using System.Text.Json;
using Microsoft.Extensions.Options;

namespace FabrCore.Services.Microsoft365Copilot.Tests;

[TestClass]
public sealed class CopilotAppPackageBuilderTests
{
    private static CopilotAppPackageBuilder CreateBuilder(Action<Microsoft365CopilotOptions>? configure = null)
    {
        var options = new Microsoft365CopilotOptions
        {
            ClientId = "11111111-2222-3333-4444-555555555555",
            Manifest = { Name = "Test Agent", PublicHostName = "agents.contoso.com" },
        };
        configure?.Invoke(options);
        return new CopilotAppPackageBuilder(Options.Create(options));
    }

    [TestMethod]
    public void Manifest_DeclaresCustomEngineAgent_BoundToBot()
    {
        var manifest = JsonDocument.Parse(CreateBuilder().BuildManifestJson()).RootElement;

        var bot = manifest.GetProperty("bots")[0];
        Assert.AreEqual("11111111-2222-3333-4444-555555555555", bot.GetProperty("botId").GetString());
        Assert.AreEqual("personal", bot.GetProperty("scopes")[0].GetString());

        var cea = manifest.GetProperty("copilotAgents").GetProperty("customEngineAgents")[0];
        Assert.AreEqual("11111111-2222-3333-4444-555555555555", cea.GetProperty("id").GetString());
        Assert.AreEqual("bot", cea.GetProperty("type").GetString());

        Assert.AreEqual("1.22", manifest.GetProperty("manifestVersion").GetString());
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
        var manifest = JsonDocument.Parse(builder.BuildManifestJson()).RootElement;

        Assert.AreEqual("surfaceapp.fabrcore.ai", manifest.GetProperty("validDomains")[0].GetString());
    }

    [TestMethod]
    public void Manifest_NormalizesPublicHostName_FromHostWithPort()
    {
        var builder = CreateBuilder(o => o.Manifest.PublicHostName = "surfaceapp.fabrcore.ai:443");
        var manifest = JsonDocument.Parse(builder.BuildManifestJson()).RootElement;

        Assert.AreEqual("surfaceapp.fabrcore.ai", manifest.GetProperty("validDomains")[0].GetString());
    }

    [TestMethod]
    public void Manifest_OmitsSsoSection_WithoutUserAuthorization()
    {
        var manifest = JsonDocument.Parse(CreateBuilder().BuildManifestJson()).RootElement;

        Assert.IsFalse(manifest.TryGetProperty("webApplicationInfo", out _));
    }

    [TestMethod]
    public void Manifest_AddsSsoSection_WhenUserAuthorizationConfigured()
    {
        var builder = CreateBuilder(o => o.UserAuthorizationConfigured = true);
        var manifest = JsonDocument.Parse(builder.BuildManifestJson()).RootElement;

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
        var manifest = JsonDocument.Parse(builder.BuildManifestJson()).RootElement;

        var command = manifest.GetProperty("bots")[0].GetProperty("commandLists")[0].GetProperty("commands")[0];
        Assert.AreEqual("Status", command.GetProperty("title").GetString());
    }

    [TestMethod]
    public void Package_ContainsManifestAndIcons()
    {
        var zipBytes = CreateBuilder().BuildPackageZip();

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
        var builder = new CopilotAppPackageBuilder(Options.Create(new Microsoft365CopilotOptions()));

        Assert.ThrowsExactly<InvalidOperationException>(() => builder.BuildManifestJson());
    }
}
