using System.IO.Compression;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Options;

namespace FabrCore.Services.Microsoft365Copilot;

/// <summary>
/// Generates the Microsoft 365 app manifest and the uploadable app package (zip) that makes the
/// agent visible in Microsoft 365 Copilot and Teams. Everything is derived from
/// <see cref="Microsoft365CopilotOptions"/> — no hand-authored manifest required.
/// </summary>
public sealed class CopilotAppPackageBuilder
{
    private const string ManifestSchemaVersion = "1.22";

    private readonly Microsoft365CopilotOptions _options;

    public CopilotAppPackageBuilder(IOptions<Microsoft365CopilotOptions> options)
    {
        _options = options.Value;
    }

    /// <summary>
    /// Canonical URL-safe name the manifest is addressable by on the
    /// <c>/manifests/{name}.json</c> endpoint — a slug of <c>Manifest:Name</c>
    /// (for example "My FabrCore Agent" → <c>my-fabrcore-agent</c>).
    /// </summary>
    public string ManifestName => Slugify(_options.Manifest.Name);

    /// <summary>
    /// Whether <paramref name="name"/> addresses this app's manifest. Accepts any value that
    /// slugs to <see cref="ManifestName"/> (so the raw display name and case variants match)
    /// as well as the Microsoft 365 app id (<c>Manifest:Id</c>, falling back to <c>ClientId</c>).
    /// </summary>
    public bool MatchesManifestName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return false;
        }

        var candidate = name.Trim();
        if (Slugify(candidate) == ManifestName)
        {
            return true;
        }

        var appId = _options.Manifest.Id ?? _options.ClientId;
        return appId is not null && string.Equals(candidate, appId, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Builds the manifest.json for the custom engine agent.</summary>
    public string BuildManifestJson()
    {
        var manifest = _options.Manifest;
        var botId = _options.ClientId
            ?? throw new InvalidOperationException(
                "Microsoft365Copilot:ClientId must be configured to generate an app manifest.");
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
            ["bots"] = new JsonArray(BuildBotEntry(botId)),
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
        // application id URI and the Bot Framework token domain.
        if (_options.UserAuthorizationConfigured)
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

    /// <summary>Builds the uploadable app package: manifest.json + color.png + outline.png.</summary>
    public byte[] BuildPackageZip()
    {
        using var buffer = new MemoryStream();
        using (var zip = new ZipArchive(buffer, ZipArchiveMode.Create, leaveOpen: true))
        {
            AddEntry(zip, "manifest.json", Encoding.UTF8.GetBytes(BuildManifestJson()));
            AddEntry(zip, "color.png", LoadIcon(_options.Manifest.ColorIconPath, DefaultIcons.Color));
            AddEntry(zip, "outline.png", LoadIcon(_options.Manifest.OutlineIconPath, DefaultIcons.Outline));
        }

        return buffer.ToArray();
    }

    private JsonObject BuildBotEntry(string botId)
    {
        var bot = new JsonObject
        {
            ["botId"] = botId,
            ["scopes"] = new JsonArray("personal"),
            ["supportsFiles"] = false,
            ["isNotificationOnly"] = false,
        };

        if (_options.Manifest.ConversationStarters.Count > 0)
        {
            var commands = new JsonArray();
            foreach (var starter in _options.Manifest.ConversationStarters.Take(10))
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
    private static string Slugify(string value)
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
