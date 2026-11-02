using System.Security.Claims;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Options;

namespace FabrCore.Services.Microsoft365Copilot;

/// <summary>
/// One agent the channel exposes, with every default already applied: an entry of
/// <c>Microsoft365Copilot:Agents</c> merged over the top-level settings.
/// </summary>
public sealed class CopilotAgent
{
    internal CopilotAgent() { }

    /// <summary>Name of the configuration entry. Unique on the host.</summary>
    public string Name { get; internal init; } = string.Empty;

    /// <summary>Route Azure Bot Service posts to for this agent. Unique on the host.</summary>
    public string Route { get; internal init; } = string.Empty;

    /// <summary>Client id of this agent's bot registration. Null only in anonymous local development.</summary>
    public string? ClientId { get; internal init; }

    /// <summary>Entra tenant of the bot registration.</summary>
    public string? TenantId { get; internal init; }

    /// <summary>How the host authenticates to Azure Bot Service as this bot.</summary>
    public string AuthType { get; internal init; } = "ClientSecret";

    /// <summary>FabrCore agent type alias that answers on this route.</summary>
    public string AgentType { get; internal init; } = string.Empty;

    /// <summary>Handle of the agent under each calling user's principal.</summary>
    public string Handle { get; internal init; } = string.Empty;

    /// <summary>Model configuration used when the agent has to be created.</summary>
    public string Models { get; internal init; } = "default";

    /// <summary>Greeting sent when a conversation starts. Null or empty for none.</summary>
    public string? WelcomeMessage { get; internal init; }

    /// <summary>App-manifest values for this agent, top-level defaults already applied.</summary>
    public CopilotManifestOptions Manifest { get; internal init; } = new();

    /// <summary>The Azure Bot OAuth connection that signs people in to this agent, or null when it has no sign-in of its own.</summary>
    public string? SignInConnectionName { get; internal init; }

    /// <summary>Name of the Agents SDK connection the host authenticates this bot with.</summary>
    public string ConnectionName { get; internal init; } = Microsoft365CopilotDefaults.ServiceConnectionName;

    internal string? ClientSecret { get; init; }
    internal string? CertificateThumbprint { get; init; }
    internal string? CertificateSubjectName { get; init; }
    internal string? CertificateStoreName { get; init; }
    internal string? FederatedClientId { get; init; }
    internal string? FederatedTokenFile { get; init; }
    internal string? AuthorityEndpoint { get; init; }
}

/// <summary>
/// The agents this host exposes to Microsoft 365 Copilot, resolved once at startup. The channel
/// settings require a restart to change, so the set never changes while the host runs.
/// </summary>
public sealed partial class CopilotAgentCatalog
{
    /// <summary>
    /// Claim the messaging route stamps on the request identity to name the agent it belongs to.
    /// The Agents SDK queues most activities and builds the bridge again from the container, so
    /// the identity is the one thing that travels from the route to the turn.
    /// </summary>
    internal const string AgentClaimType = "fabrcore:m365copilot:agent";

    private readonly Dictionary<string, CopilotAgent> _byName;
    private readonly Dictionary<string, CopilotAgent> _byClientId;

    public CopilotAgentCatalog(IOptions<Microsoft365CopilotOptions> options)
        : this(Resolve(options.Value))
    {
    }

    internal CopilotAgentCatalog(IReadOnlyList<CopilotAgent> agents)
    {
        Agents = agents;
        _byName = agents.ToDictionary(agent => agent.Name, StringComparer.OrdinalIgnoreCase);
        _byClientId = agents
            .Where(agent => !string.IsNullOrWhiteSpace(agent.ClientId))
            .ToDictionary(agent => agent.ClientId!, StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>Every exposed agent, in configuration order.</summary>
    public IReadOnlyList<CopilotAgent> Agents { get; }

    /// <summary>The agent with this entry name, or null.</summary>
    public CopilotAgent? Find(string? name) =>
        !string.IsNullOrWhiteSpace(name) && _byName.TryGetValue(name.Trim(), out var agent) ? agent : null;

    /// <summary>The agent whose bot registration has this client id, or null.</summary>
    public CopilotAgent? FindByClientId(string? clientId) =>
        !string.IsNullOrWhiteSpace(clientId) && _byClientId.TryGetValue(clientId.Trim(), out var agent) ? agent : null;

    /// <summary>
    /// The agent a turn belongs to: the one its route named, else the one whose bot the token was
    /// issued for, else the only agent there is.
    /// </summary>
    public CopilotAgent? Resolve(ClaimsIdentity? identity)
    {
        if (identity is not null)
        {
            var named = Find(identity.FindFirst(AgentClaimType)?.Value);
            if (named is not null)
            {
                return named;
            }

            var audience = FindByClientId(identity.FindFirst("aud")?.Value ?? identity.FindFirst("appid")?.Value);
            if (audience is not null)
            {
                return audience;
            }
        }

        return Agents.Count == 1 ? Agents[0] : null;
    }

    /// <summary>
    /// Applies the top-level defaults to each entry and checks the set is usable. Throws
    /// <see cref="InvalidOperationException"/> naming the offending setting otherwise.
    /// </summary>
    internal static IReadOnlyList<CopilotAgent> Resolve(Microsoft365CopilotOptions options)
    {
        var agents = new List<CopilotAgent>(options.Agents.Count);
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var routes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var clientIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var manifestNames = new HashSet<string>(StringComparer.Ordinal);

        for (var index = 0; index < options.Agents.Count; index++)
        {
            var entry = options.Agents[index];
            var at = $"{Microsoft365CopilotDefaults.SectionName}:Agents:{index}";

            var name = entry.Name?.Trim() ?? string.Empty;
            if (!NamePattern().IsMatch(name))
            {
                throw new InvalidOperationException(
                    $"{at}:Name is required and may contain only letters, digits, '-' and '_' (at most 63 characters).");
            }

            if (!names.Add(name))
            {
                throw new InvalidOperationException($"{at}:Name '{name}' is used by more than one agent.");
            }

            if (string.IsNullOrWhiteSpace(entry.AgentType))
            {
                throw new InvalidOperationException(
                    $"{at}:AgentType is required (a FabrCore [AgentAlias] value) so messages on this route have an agent to reach.");
            }

            var handle = string.IsNullOrWhiteSpace(entry.Handle) ? name : entry.Handle.Trim();
            if (handle.Contains(':'))
            {
                throw new InvalidOperationException(
                    $"{at}:Handle must be a bare agent handle without ':'. The principal comes from the inbound activity.");
            }

            var route = NormalizeRoute(entry.Route, name);
            if (route.Contains('{') || route.Contains('*'))
            {
                throw new InvalidOperationException($"{at}:Route must be a fixed path, not a route template.");
            }

            if (!routes.Add(route))
            {
                throw new InvalidOperationException(
                    $"{at}:Route '{route}' is used by more than one agent. Each Azure Bot needs its own messaging endpoint.");
            }

            var clientId = string.IsNullOrWhiteSpace(entry.ClientId) ? null : entry.ClientId.Trim();
            if (clientId is not null && !clientIds.Add(clientId))
            {
                throw new InvalidOperationException(
                    $"{at}:ClientId is used by more than one agent. Each agent needs its own Azure Bot registration.");
            }

            var manifest = ResolveManifest(options.Manifest, entry.Manifest, name, single: options.Agents.Count == 1);
            if (!manifestNames.Add(CopilotAppPackageBuilder.Slugify(manifest.Name)))
            {
                throw new InvalidOperationException(
                    $"{at}:Manifest:Name '{manifest.Name}' is used by more than one agent. Give each app package its own name.");
            }

            agents.Add(new CopilotAgent
            {
                Name = name,
                Route = route,
                ClientId = clientId,
                ClientSecret = entry.ClientSecret,
                TenantId = Coalesce(entry.TenantId, options.TenantId),
                AuthType = Coalesce(entry.AuthType, options.AuthType) ?? "ClientSecret",
                CertificateThumbprint = Coalesce(entry.CertificateThumbprint, options.CertificateThumbprint),
                CertificateSubjectName = Coalesce(entry.CertificateSubjectName, options.CertificateSubjectName),
                CertificateStoreName = Coalesce(entry.CertificateStoreName, options.CertificateStoreName),
                FederatedClientId = Coalesce(entry.FederatedClientId, options.FederatedClientId),
                FederatedTokenFile = Coalesce(entry.FederatedTokenFile, options.FederatedTokenFile),
                AuthorityEndpoint = Coalesce(entry.AuthorityEndpoint, options.AuthorityEndpoint),
                AgentType = entry.AgentType.Trim(),
                Handle = handle,
                Models = string.IsNullOrWhiteSpace(entry.Models) ? "default" : entry.Models.Trim(),
                // An explicit empty string turns the greeting off for this agent.
                WelcomeMessage = entry.WelcomeMessage ?? options.WelcomeMessage,
                SignInConnectionName = string.IsNullOrWhiteSpace(entry.SignInConnectionName) ? null : entry.SignInConnectionName.Trim(),
                Manifest = manifest,
                // The first agent keeps the historical connection name, so user-authorization
                // handlers written against "ServiceConnection" keep working on single-agent hosts.
                ConnectionName = index == 0
                    ? Microsoft365CopilotDefaults.ServiceConnectionName
                    : $"{Microsoft365CopilotDefaults.ServiceConnectionName}-{name}",
            });
        }

        return agents;
    }

    private static string NormalizeRoute(string? route, string name)
    {
        if (string.IsNullOrWhiteSpace(route))
        {
            return $"{Microsoft365CopilotDefaults.MessagesEndpoint}/{name}";
        }

        var trimmed = route.Trim().TrimEnd('/');
        return trimmed.StartsWith('/') ? trimmed : "/" + trimmed;
    }

    private static CopilotManifestOptions ResolveManifest(
        CopilotManifestOptions defaults, CopilotAgentManifestOptions entry, string agentName, bool single) => new()
    {
        // Never inherited: two app packages cannot share an app id.
        Id = Coalesce(entry.Id, single ? defaults.Id : null),
        Version = Coalesce(entry.Version, defaults.Version)!,
        Name = Coalesce(entry.Name, single ? defaults.Name : agentName)!,
        FullName = Coalesce(entry.FullName, single ? defaults.FullName : null),
        Description = Coalesce(entry.Description, defaults.Description)!,
        FullDescription = Coalesce(entry.FullDescription, defaults.FullDescription),
        DeveloperName = Coalesce(entry.DeveloperName, defaults.DeveloperName)!,
        WebsiteUrl = Coalesce(entry.WebsiteUrl, defaults.WebsiteUrl)!,
        PrivacyUrl = Coalesce(entry.PrivacyUrl, defaults.PrivacyUrl)!,
        TermsOfUseUrl = Coalesce(entry.TermsOfUseUrl, defaults.TermsOfUseUrl)!,
        AccentColor = Coalesce(entry.AccentColor, defaults.AccentColor)!,
        PublicHostName = defaults.PublicHostName,
        ConversationStarters = entry.ConversationStarters.Count > 0 ? entry.ConversationStarters : defaults.ConversationStarters,
        ColorIconPath = Coalesce(entry.ColorIconPath, defaults.ColorIconPath),
        OutlineIconPath = Coalesce(entry.OutlineIconPath, defaults.OutlineIconPath),
        EnableAppPackageEndpoint = defaults.EnableAppPackageEndpoint,
    };

    private static string? Coalesce(string? value, string? fallback) =>
        string.IsNullOrWhiteSpace(value) ? fallback : value.Trim();

    [GeneratedRegex("^[A-Za-z0-9][A-Za-z0-9_-]{0,62}$")]
    private static partial Regex NamePattern();
}
