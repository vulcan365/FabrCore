using System.IO.Compression;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Options;

namespace FabrCore.Services.Microsoft365Copilot;

/// <summary>
/// Generates the Microsoft 365 app manifest and the uploadable app package (zip) that makes an
/// agent visible in Microsoft 365 Copilot and Teams. Microsoft allows one bot per manifest, so
/// every agent in the <see cref="CopilotAgentCatalog"/> gets its own package. Everything is
/// derived from <see cref="Microsoft365CopilotOptions"/> — no hand-authored manifest required.
/// </summary>
public sealed class CopilotAppPackageBuilder
{
    private const string ManifestSchemaVersion = "1.25";

    private readonly Microsoft365CopilotOptions _options;
    private readonly CopilotAgentCatalog _catalog;

    public CopilotAppPackageBuilder(IOptions<Microsoft365CopilotOptions> options, CopilotAgentCatalog catalog)
    {
        _options = options.Value;
        _catalog = catalog;
    }

    /// <summary>
    /// Canonical URL-safe name an agent's manifest is addressable by on the
    /// <c>/manifests/{name}.json</c> endpoint — a slug of its manifest name
    /// (for example "My FabrCore Agent" → <c>my-fabrcore-agent</c>).
    /// </summary>
    public string ManifestName(CopilotAgent agent) => Slugify(agent.Manifest.Name);

    /// <summary>
    /// The agent whose manifest <paramref name="name"/> addresses, or null. Accepts any value
    /// that slugs to an agent's <see cref="ManifestName"/> (so the raw display name and case
    /// variants match) as well as its Microsoft 365 app id (<c>Manifest:Id</c>, falling back to
    /// <c>ClientId</c>).
    /// </summary>
    public CopilotAgent? FindByManifestName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return null;
        }

        var candidate = name.Trim();
        var slug = Slugify(candidate);
        foreach (var agent in _catalog.Agents)
        {
            if (slug == ManifestName(agent))
            {
                return agent;
            }

            var appId = agent.Manifest.Id ?? agent.ClientId;
            if (appId is not null && string.Equals(candidate, appId, StringComparison.OrdinalIgnoreCase))
            {
                return agent;
            }
        }

        return null;
    }

    /// <summary>Builds the manifest.json for one custom engine agent.</summary>
    public string BuildManifestJson(CopilotAgent agent)
    {
        var manifest = agent.Manifest;
        var botId = agent.ClientId
            ?? throw new InvalidOperationException(
                $"Microsoft365Copilot: agent '{agent.Name}' needs a ClientId to generate an app manifest.");
        var appId = manifest.Id ?? botId;

        var validDomains = new JsonArray();
        var publicHost = NormalizeHostName(manifest.PublicHostName);
        if (publicHost is not null)
        {
            validDomains.Add(publicHost);
        }

        var root = new JsonObject
        {
            ["$schema"] = $"https://developer.microsoft.com/json-schemas/teams/v{ManifestSchemaVersion}/MicrosoftTeams.schema.json",
            ["manifestVersion"] = ManifestSchemaVersion,
            ["version"] = manifest.Version,
            ["id"] = appId,
            ["developer"] = new JsonObject
            {
                ["name"] = manifest.DeveloperName,
                ["websiteUrl"] = manifest.WebsiteUrl,
                ["privacyUrl"] = manifest.PrivacyUrl,
                ["termsOfUseUrl"] = manifest.TermsOfUseUrl,
            },
            ["name"] = new JsonObject
            {
                ["short"] = manifest.Name,
                ["full"] = manifest.FullName ?? manifest.Name,
            },
            ["description"] = new JsonObject
            {
                ["short"] = manifest.Description,
                ["full"] = manifest.FullDescription ?? manifest.Description,
            },
            ["icons"] = new JsonObject
            {
                ["color"] = "color.png",
                ["outline"] = "outline.png",
            },
            ["accentColor"] = manifest.AccentColor,
            ["bots"] = new JsonArray(BuildBotEntry(botId, manifest)),
            ["copilotAgents"] = new JsonObject
            {
                ["customEngineAgents"] = new JsonArray(new JsonObject
                {
                    ["id"] = botId,
                    ["type"] = "bot",
                }),
            },
            ["permissions"] = new JsonArray("identity", "messageTeamMembers"),
            ["validDomains"] = validDomains,
        };

        // Single sign-on needs webApplicationInfo pointing at the api://botid-{clientId}
        // application id URI and the Bot Framework token domain. Where agents have sign-in of
        // their own, an agent without it mustn't claim it.
        var perAgentSignIn = _options.Agents.Any(entry => !string.IsNullOrWhiteSpace(entry.SignInConnectionName));
        if (perAgentSignIn ? agent.SignInConnectionName is not null : _options.UserAuthorizationConfigured)
        {
            root["webApplicationInfo"] = new JsonObject
            {
                ["id"] = botId,
                ["resource"] = $"api://botid-{botId}",
            };
            validDomains.Add("token.botframework.com");
        }

        return root.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
    }

    /// <summary>Builds one agent's uploadable app package: manifest.json + color.png + outline.png.</summary>
    public byte[] BuildPackageZip(CopilotAgent agent)
    {
        using var buffer = new MemoryStream();
        using (var zip = new ZipArchive(buffer, ZipArchiveMode.Create, leaveOpen: true))
        {
            AddEntry(zip, "manifest.json", Encoding.UTF8.GetBytes(BuildManifestJson(agent)));
            AddEntry(zip, "color.png", LoadIcon(agent.Manifest.ColorIconPath, DefaultIcons.Color));
            AddEntry(zip, "outline.png", LoadIcon(agent.Manifest.OutlineIconPath, DefaultIcons.Outline));
        }

        return buffer.ToArray();
    }

    private static JsonObject BuildBotEntry(string botId, CopilotManifestOptions manifest)
    {
        var bot = new JsonObject
        {
            ["botId"] = botId,
            ["scopes"] = new JsonArray("personal"),
            ["supportsFiles"] = false,
            ["isNotificationOnly"] = false,
        };

        if (manifest.ConversationStarters.Count > 0)
        {
            var commands = new JsonArray();
            foreach (var starter in manifest.ConversationStarters.Take(10))
            {
                commands.Add(new JsonObject
                {
                    ["title"] = starter.Title,
                    ["description"] = starter.Description,
                });
            }

            bot["commandLists"] = new JsonArray(new JsonObject
            {
                ["scopes"] = new JsonArray("personal"),
                ["commands"] = commands,
            });
        }

        return bot;
    }

    private static void AddEntry(ZipArchive zip, string name, byte[] content)
    {
        var entry = zip.CreateEntry(name, CompressionLevel.Optimal);
        using var stream = entry.Open();
        stream.Write(content);
    }

    private static byte[] LoadIcon(string? path, byte[] fallback)
        => string.IsNullOrWhiteSpace(path) ? fallback : File.ReadAllBytes(path);

    /// <summary>Lowercase ASCII letters and digits; every other run of characters becomes one dash.</summary>
    internal static string Slugify(string value)
    {
        var builder = new StringBuilder(value.Length);
        var lastWasDash = false;

        foreach (var ch in value)
        {
            if (char.IsAsciiLetterOrDigit(ch))
            {
                builder.Append(char.ToLowerInvariant(ch));
                lastWasDash = false;
            }
            else if (!lastWasDash && builder.Length > 0)
            {
                builder.Append('-');
                lastWasDash = true;
            }
        }

        return builder.ToString().TrimEnd('-');
    }

    /// <summary>
    /// Manifest validDomains entries must be bare host names. Accept full URLs
    /// ("https://host/path") and host:port forms and reduce them to the host.
    /// </summary>
    private static string? NormalizeHostName(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var trimmed = value.Trim();
        if (Uri.TryCreate(trimmed, UriKind.Absolute, out var absolute) && !string.IsNullOrEmpty(absolute.Host))
        {
            return absolute.Host;
        }

        if (Uri.TryCreate($"https://{trimmed}", UriKind.Absolute, out var hostOnly) && !string.IsNullOrEmpty(hostOnly.Host))
        {
            return hostOnly.Host;
        }

        return trimmed;
    }
}
