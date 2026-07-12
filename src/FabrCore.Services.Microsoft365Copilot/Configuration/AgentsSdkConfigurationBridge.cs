using Microsoft.Extensions.Configuration;

namespace FabrCore.Services.Microsoft365Copilot;

/// <summary>
/// Translates the single <c>Microsoft365Copilot</c> configuration section into the configuration
/// shape the Microsoft 365 Agents SDK reads (<c>Connections</c>, <c>ConnectionsMap</c>,
/// <c>AgentApplication</c>), so hosts only maintain one section in fabrcore.json.
/// Sections the host has already defined natively are left untouched, which lets advanced users
/// drop down to raw Agents SDK configuration at any time.
/// </summary>
internal static class AgentsSdkConfigurationBridge
{
    /// <summary>Keys under <c>Microsoft365Copilot:UserAuthorization</c> that are FabrCore-specific and must not be forwarded to the SDK.</summary>
    private static readonly string[] FabrCoreUserAuthKeys = ["PassUserTokenToAgent", "UserTokenArgName"];

    public static void Inject(
        IConfigurationManager configuration,
        Microsoft365CopilotOptions options,
        IConfigurationSection copilotSection)
    {
        var values = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);

        InjectServiceConnection(configuration, options, values);
        InjectAgentApplication(configuration, values);
        InjectUserAuthorization(configuration, copilotSection, values);

        if (values.Count > 0)
        {
            ((IConfigurationBuilder)configuration).AddInMemoryCollection(values);
        }
    }

    private static void InjectServiceConnection(
        IConfigurationManager configuration,
        Microsoft365CopilotOptions options,
        Dictionary<string, string?> values)
    {
        if (configuration.GetSection("Connections").Exists())
        {
            return;
        }

        // Without a client id there is nothing to connect as (pure local-dev mode);
        // the Agents SDK tolerates a missing Connections section in anonymous scenarios.
        if (string.IsNullOrWhiteSpace(options.ClientId))
        {
            return;
        }

        var prefix = $"Connections:{Microsoft365CopilotDefaults.ServiceConnectionName}:Settings:";
        values[prefix + "AuthType"] = options.AuthType;
        values[prefix + "ClientId"] = options.ClientId;
        values[prefix + "AuthorityEndpoint"] = options.AuthorityEndpoint
            ?? $"https://login.microsoftonline.com/{(string.IsNullOrWhiteSpace(options.TenantId) ? "botframework.com" : options.TenantId)}";
        values[prefix + "Scopes:0"] = "https://api.botframework.com/.default";

        if (!string.IsNullOrWhiteSpace(options.TenantId))
        {
            values[prefix + "TenantId"] = options.TenantId;
        }

        if (!string.IsNullOrWhiteSpace(options.ClientSecret))
        {
            values[prefix + "ClientSecret"] = options.ClientSecret;
        }

        if (!string.IsNullOrWhiteSpace(options.CertificateThumbprint))
        {
            values[prefix + "CertThumbprint"] = options.CertificateThumbprint;
        }

        if (!string.IsNullOrWhiteSpace(options.CertificateSubjectName))
        {
            values[prefix + "CertSubjectName"] = options.CertificateSubjectName;
        }

        if (!string.IsNullOrWhiteSpace(options.CertificateStoreName))
        {
            values[prefix + "CertStoreName"] = options.CertificateStoreName;
        }

        if (!string.IsNullOrWhiteSpace(options.FederatedClientId))
        {
            values[prefix + "FederatedClientId"] = options.FederatedClientId;
        }

        if (!string.IsNullOrWhiteSpace(options.FederatedTokenFile))
        {
            values[prefix + "FederatedTokenFile"] = options.FederatedTokenFile;
        }

        if (!configuration.GetSection("ConnectionsMap").Exists())
        {
            values["ConnectionsMap:0:ServiceUrl"] = "*";
            values["ConnectionsMap:0:Connection"] = Microsoft365CopilotDefaults.ServiceConnectionName;
        }
    }

    private static void InjectAgentApplication(
        IConfigurationManager configuration,
        Dictionary<string, string?> values)
    {
        if (configuration.GetSection("AgentApplication").Exists())
        {
            return;
        }

        // Typing indicator while the FabrCore agent works; mention normalization for Teams.
        values["AgentApplication:StartTypingTimer"] = "true";
        values["AgentApplication:RemoveRecipientMention"] = "true";
        values["AgentApplication:NormalizeMentions"] = "true";
    }

    private static void InjectUserAuthorization(
        IConfigurationManager configuration,
        IConfigurationSection copilotSection,
        Dictionary<string, string?> values)
    {
        var userAuth = copilotSection.GetSection("UserAuthorization");
        if (!userAuth.GetSection("Handlers").Exists()
            || configuration.GetSection("AgentApplication:UserAuthorization").Exists())
        {
            return;
        }

        foreach (var (key, value) in userAuth.AsEnumerable(makePathsRelative: true))
        {
            if (value is null || FabrCoreUserAuthKeys.Contains(key, StringComparer.OrdinalIgnoreCase))
            {
                continue;
            }

            values["AgentApplication:UserAuthorization:" + key] = value;
        }
    }
}
