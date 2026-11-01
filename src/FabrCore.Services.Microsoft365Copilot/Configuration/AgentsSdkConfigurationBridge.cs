using Microsoft.Extensions.Configuration;

namespace FabrCore.Services.Microsoft365Copilot;

/// <summary>
/// Translates the single <c>Microsoft365Copilot</c> configuration section into the configuration
/// shape the Microsoft 365 Agents SDK reads (<c>Connections</c>, <c>ConnectionsMap</c>,
/// <c>AgentApplication</c>), so hosts only maintain one section in fabrcore.json however many
/// agents they expose.
/// Sections the host has already defined natively are left untouched, which lets advanced users
/// drop down to raw Agents SDK configuration at any time.
/// </summary>
internal static class AgentsSdkConfigurationBridge
{
    /// <summary>Keys under <c>Microsoft365Copilot:UserAuthorization</c> that are FabrCore-specific and must not be forwarded to the SDK.</summary>
    private static readonly string[] FabrCoreUserAuthKeys = ["PassUserTokenToAgent", "UserTokenArgName"];

    public static void Inject(
        IConfigurationManager configuration,
        IReadOnlyList<CopilotAgent> agents,
        IConfigurationSection copilotSection)
    {
        var values = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);

        InjectServiceConnections(configuration, agents, values);
        InjectAgentApplication(configuration, values);
        InjectUserAuthorization(configuration, copilotSection, values);

        if (values.Count > 0)
        {
            ((IConfigurationBuilder)configuration).AddInMemoryCollection(values);
        }
    }

    /// <summary>
    /// One connection per bot registration, selected by the audience of the inbound token, so
    /// each agent's replies and proactive messages go out under its own credential.
    /// </summary>
    private static void InjectServiceConnections(
        IConfigurationManager configuration,
        IReadOnlyList<CopilotAgent> agents,
        Dictionary<string, string?> values)
    {
        if (configuration.GetSection("Connections").Exists())
        {
            return;
        }

        // Without a client id there is nothing to connect as (pure local-dev mode);
        // the Agents SDK tolerates a missing Connections section in anonymous scenarios.
        var identified = agents.Where(agent => !string.IsNullOrWhiteSpace(agent.ClientId)).ToList();
        if (identified.Count == 0)
        {
            return;
        }

        foreach (var agent in identified)
        {
            InjectServiceConnection(agent, values);
        }

        if (configuration.GetSection("ConnectionsMap").Exists())
        {
            return;
        }

        var index = 0;
        foreach (var agent in identified)
        {
            values[$"ConnectionsMap:{index}:Audience"] = agent.ClientId;
            values[$"ConnectionsMap:{index}:ServiceUrl"] = "*";
            values[$"ConnectionsMap:{index}:Connection"] = agent.ConnectionName;
            index++;
        }

        // Inbound validation already rejects a token for any other audience, so this entry is
        // reached only where no audience exists: anonymous local development, and SDK calls
        // that ask for the default connection.
        values[$"ConnectionsMap:{index}:ServiceUrl"] = "*";
        values[$"ConnectionsMap:{index}:Connection"] = identified[0].ConnectionName;
    }

    private static void InjectServiceConnection(CopilotAgent agent, Dictionary<string, string?> values)
    {
        var prefix = $"Connections:{agent.ConnectionName}:Settings:";
        values[prefix + "AuthType"] = agent.AuthType;
        values[prefix + "ClientId"] = agent.ClientId;
        values[prefix + "AuthorityEndpoint"] = agent.AuthorityEndpoint
            ?? $"https://login.microsoftonline.com/{(string.IsNullOrWhiteSpace(agent.TenantId) ? "botframework.com" : agent.TenantId)}";
        values[prefix + "Scopes:0"] = "https://api.botframework.com/.default";

        Set("TenantId", agent.TenantId);
        Set("ClientSecret", agent.ClientSecret);
        Set("CertThumbprint", agent.CertificateThumbprint);
        Set("CertSubjectName", agent.CertificateSubjectName);
        Set("CertStoreName", agent.CertificateStoreName);
        Set("FederatedClientId", agent.FederatedClientId);
        Set("FederatedTokenFile", agent.FederatedTokenFile);

        void Set(string key, string? value)
        {
            if (!string.IsNullOrWhiteSpace(value))
            {
                values[prefix + key] = value;
            }
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
