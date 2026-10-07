namespace FabrCore.Core.CloudServer;

/// <summary>
/// Routes and well-known values of the integrations administration API. The API reports how a
/// host's external channels are configured and whether that configuration is safe; it never
/// returns a credential.
/// </summary>
public static class IntegrationAdministration
{
    public const string ApiVersion = "1";

    /// <summary>Route prefix, relative to the host root.</summary>
    public const string RoutePrefix = "/fabrcoreapi/admin/v1/integrations";

    /// <summary>Microsoft 365 Copilot and Teams channel. Served by the Copilot add-on.</summary>
    public const string Microsoft365 = "microsoft365";

    /// <summary>Agent2Agent endpoint. Served by the host.</summary>
    public const string A2A = "a2a";

    public const string Microsoft365Route = RoutePrefix + "/" + Microsoft365;
    public const string A2ARoute = RoutePrefix + "/" + A2A;

    public const string ManifestSegment = "manifest";
    public const string AppPackageSegment = "app-package";
    public const string DiagnosticsSegment = "diagnostics";

    /// <summary>The check ran and found nothing to act on.</summary>
    public const string Pass = "pass";

    /// <summary>The integration works but an operator should review this.</summary>
    public const string Warn = "warn";

    /// <summary>The integration is unsafe or cannot work as configured.</summary>
    public const string Fail = "fail";

    /// <summary>The check does not apply to the current configuration, or could not run.</summary>
    public const string Skipped = "skipped";

    /// <summary>
    /// The worst status in a set: <see cref="Fail"/>, then <see cref="Warn"/>, then
    /// <see cref="Pass"/>. <see cref="Skipped"/> when no check ran.
    /// </summary>
    public static string Summarize(IEnumerable<IntegrationCheck> checks)
    {
        var result = Skipped;
        foreach (var check in checks)
        {
            if (check.Status == Fail) return Fail;
            if (check.Status == Warn) result = Warn;
            else if (check.Status == Pass && result == Skipped) result = Pass;
        }
        return result;
    }
}

/// <summary>One named posture finding or diagnostic step.</summary>
public sealed class IntegrationCheck
{
    /// <summary>Stable identifier, for example <c>token-validation</c>. Consoles key their own text on it.</summary>
    public string Id { get; set; } = "";

    /// <summary><c>pass</c>, <c>warn</c>, <c>fail</c>, or <c>skipped</c>.</summary>
    public string Status { get; set; } = IntegrationAdministration.Skipped;

    /// <summary>Operator-readable explanation. Never contains a credential.</summary>
    public string Message { get; set; } = "";
}

/// <summary>Result of an on-demand diagnostic run against one host.</summary>
public sealed class IntegrationDiagnosticsReport
{
    public string ApiVersion { get; set; } = IntegrationAdministration.ApiVersion;

    /// <summary>Which integration ran, for example <c>microsoft365</c>.</summary>
    public string Integration { get; set; } = "";

    public DateTimeOffset ObservedAt { get; set; }

    /// <summary>Worst status across <see cref="Checks"/>.</summary>
    public string Status { get; set; } = IntegrationAdministration.Skipped;

    public List<IntegrationCheck> Checks { get; set; } = [];
}

/// <summary>
/// How the Microsoft 365 Copilot and Teams channel is configured on this host. Identifiers and
/// posture only: no secret, certificate, or token is ever included.
/// </summary>
public sealed class Microsoft365IntegrationStatus
{
    public string ApiVersion { get; set; } = IntegrationAdministration.ApiVersion;

    /// <summary>Whether the channel is serving its messaging endpoint.</summary>
    public bool Enabled { get; set; }

    /// <summary>Whether any configuration was supplied. False means the add-on is installed but untouched.</summary>
    public bool Configured { get; set; }

    /// <summary>Why the channel is off. Null while it is on.</summary>
    public string? DisabledReason { get; set; }

    public string? TenantId { get; set; }

    /// <summary>Client id of the bot's app registration.</summary>
    public string? ClientId { get; set; }

    /// <summary>How the host authenticates to Azure Bot Service, for example <c>WorkloadIdentity</c>.</summary>
    public string? AuthType { get; set; }

    public string? MessagesEndpoint { get; set; }

    /// <summary>Whether inbound activities must carry a valid Azure Bot Service token.</summary>
    public bool TokenValidationEnabled { get; set; }

    public string? PrincipalStrategy { get; set; }

    /// <summary>Named entry in <c>AgentBindings</c>, when the channel uses one.</summary>
    public string? AgentBinding { get; set; }

    public string? AgentType { get; set; }
    public string? AgentHandle { get; set; }
    public string? SharedAgentHandle { get; set; }

    /// <summary>Whether a user-authorization handler is configured.</summary>
    public bool SingleSignOn { get; set; }

    /// <summary>Whether the signed-in user's access token is copied onto agent messages.</summary>
    public bool ForwardsUserCredential { get; set; }

    public bool ProactiveEnabled { get; set; }
    public List<string> ProactiveConversationTypes { get; set; } = [];
    public bool StreamingEnabled { get; set; }
    public string? PublicHostName { get; set; }

    /// <summary>Microsoft 365 app id written to the manifest.</summary>
    public string? ManifestId { get; set; }

    public string? ManifestName { get; set; }
    public string? ManifestVersion { get; set; }

    /// <summary>Type name of the Agents SDK turn-state store in use.</summary>
    public string? TurnStateStorage { get; set; }

    /// <summary>Worst status across <see cref="Findings"/>.</summary>
    public string Status { get; set; } = IntegrationAdministration.Skipped;

    public List<IntegrationCheck> Findings { get; set; } = [];
}

/// <summary>
/// How the A2A endpoint is configured on this host. API keys are reported by name only.
/// </summary>
public sealed class A2AIntegrationStatus
{
    public string ApiVersion { get; set; } = IntegrationAdministration.ApiVersion;
    public bool Enabled { get; set; }
    public string? RoutePrefix { get; set; }
    public string? PublicBaseUrl { get; set; }

    /// <summary><c>None</c>, <c>ApiKey</c>, or <c>JwtBearer</c>.</summary>
    public string? AuthenticationMode { get; set; }

    /// <summary>Labels of the accepted API keys. Never the key values.</summary>
    public List<string> ApiKeyNames { get; set; } = [];

    public string? Authority { get; set; }
    public List<string> Audiences { get; set; } = [];
    public List<string> RequiredScopes { get; set; } = [];
    public List<string> RequiredRoles { get; set; } = [];
    public string? PrincipalStrategy { get; set; }

    /// <summary>Registry discovery mode: <c>None</c>, <c>Described</c>, or <c>All</c>.</summary>
    public string? DiscoveryMode { get; set; }

    /// <summary>Type name of the task store in use.</summary>
    public string? TaskStore { get; set; }

    /// <summary>Worst status across <see cref="Findings"/>.</summary>
    public string Status { get; set; } = IntegrationAdministration.Skipped;

    public List<A2AIntegrationAgent> Agents { get; set; } = [];
    public List<IntegrationCheck> Findings { get; set; } = [];
}

/// <summary>One agent published over A2A.</summary>
public sealed class A2AIntegrationAgent
{
    /// <summary>Route name, for example <c>support</c>.</summary>
    public string Name { get; set; } = "";

    public string DisplayName { get; set; } = "";

    /// <summary>Path the agent is served at, for example <c>/a2a/support</c>.</summary>
    public string BasePath { get; set; } = "";

    /// <summary><c>Configured</c>, <c>Registry</c>, or <c>LiveAgent</c>.</summary>
    public string Source { get; set; } = "";

    public string? AgentType { get; set; }

    /// <summary>Fully-qualified handle when the agent routes to an existing instance.</summary>
    public string? AgentHandle { get; set; }

    /// <summary>Named entry in <c>AgentBindings</c>, when the agent uses one.</summary>
    public string? Binding { get; set; }
}
