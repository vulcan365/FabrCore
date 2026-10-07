using FabrCore.Core.CloudServer;
using Microsoft.Agents.Storage;
using static FabrCore.Core.CloudServer.IntegrationAdministration;
using Marker = FabrCore.Services.Microsoft365Copilot.Microsoft365CopilotExtensions.Microsoft365CopilotMarker;

namespace FabrCore.Services.Microsoft365Copilot;

/// <summary>
/// Turns the options the channel actually runs with into the status document and posture findings
/// served by the integrations administration API. Pure: identifiers and settings only, never a
/// secret, certificate, or token.
/// </summary>
internal static class CopilotIntegrationStatusBuilder
{
    internal static readonly string[] FindingIds =
    [
        "token-validation", "principal-strategy", "credential-type", "turn-state-storage",
        "user-credential-forwarding", "public-host", "proactive-scopes"
    ];

    internal static Microsoft365IntegrationStatus Build(
        Microsoft365CopilotOptions options, Marker marker, Type? turnStateStorage, bool customPrincipalResolver)
    {
        var status = new Microsoft365IntegrationStatus
        {
            Enabled = marker.Enabled,
            Configured = marker.Configured,
            DisabledReason = marker.Enabled ? null : marker.DisabledReason,
            TenantId = options.TenantId,
            ClientId = options.ClientId,
            // With a natively configured Agents SDK connection this option is not what authenticates.
            AuthType = marker.NativeServiceConnection ? null : options.AuthType,
            MessagesEndpoint = options.MessagesEndpoint,
            TokenValidationEnabled = options.TokenValidation.Enabled,
            PrincipalStrategy = options.Principal.Strategy.ToString(),
            AgentBinding = options.Agent.Binding,
            AgentType = options.Agent.AgentType,
            AgentHandle = options.Agent.Handle,
            SharedAgentHandle = options.Agent.SharedAgentHandle,
            SingleSignOn = options.UserAuthorizationConfigured,
            ForwardsUserCredential = ForwardsUserCredential(options),
            ProactiveEnabled = options.Proactive.Enabled,
            ProactiveConversationTypes = [.. options.Proactive.AllowedConversationTypes],
            StreamingEnabled = options.Streaming.Enabled,
            PublicHostName = options.Manifest.PublicHostName,
            ManifestId = options.Manifest.Id ?? options.ClientId,
            ManifestName = options.Manifest.Name,
            ManifestVersion = options.Manifest.Version,
            TurnStateStorage = turnStateStorage?.Name
        };
        status.Findings = Findings(options, marker, turnStateStorage, customPrincipalResolver);
        status.Status = Summarize(status.Findings);
        return status;
    }

    /// <summary>The token reaches agent messages only when it is both requested and obtainable.</summary>
    internal static bool ForwardsUserCredential(Microsoft365CopilotOptions options) =>
        options.UserAuthorization.PassUserTokenToAgent && options.UserAuthorizationConfigured;

    internal static List<IntegrationCheck> Findings(
        Microsoft365CopilotOptions options, Marker marker, Type? turnStateStorage, bool customPrincipalResolver)
    {
        if (!marker.Enabled)
        {
            return [.. FindingIds.Select(id => Check(id, Skipped, "The Microsoft 365 Copilot channel is off on this host."))];
        }

        return
        [
            options.TokenValidation.Enabled
                ? Check("token-validation", Pass, "Inbound activities must carry a valid Azure Bot Service token.")
                : Check("token-validation", Fail, "The messaging endpoint accepts anonymous requests. This is for local development only."),
            PrincipalStrategy(options, customPrincipalResolver),
            marker.NativeServiceConnection
                ? Check("credential-type", Skipped, "The host configures the Agents SDK Connections section itself, so the add-on does not choose the credential.")
                : string.IsNullOrWhiteSpace(options.ClientId)
                    ? Check("credential-type", Skipped, "No bot identity is configured.")
                    : string.Equals(options.AuthType, "ClientSecret", StringComparison.OrdinalIgnoreCase)
                        ? Check("credential-type", Warn, "The bot authenticates with a client secret held in host configuration. Prefer WorkloadIdentity, FederatedCredentials, a managed identity, or a certificate.")
                        : Check("credential-type", Pass, $"The bot authenticates with {options.AuthType}; no client secret is stored."),
            turnStateStorage is null
                ? Check("turn-state-storage", Skipped, "The turn-state store could not be inspected.")
                : turnStateStorage == typeof(MemoryStorage)
                    ? Check("turn-state-storage", Warn, options.UserAuthorizationConfigured
                        ? "Sign-in and turn state are held in process memory. Users sign in again after a restart, and state is not shared between host instances."
                        : "Turn state is held in process memory. It is lost on restart and is not shared between host instances.")
                    : Check("turn-state-storage", Pass, $"Turn state is stored by {turnStateStorage.Name}."),
            ForwardsUserCredential(options)
                ? Check("user-credential-forwarding", Fail, "The signed-in user's access token is copied onto agent messages, where the message monitor records it. Turn UserAuthorization:PassUserTokenToAgent off.")
                : options.UserAuthorization.PassUserTokenToAgent
                    ? Check("user-credential-forwarding", Warn, "UserAuthorization:PassUserTokenToAgent is set but no user-authorization handler is configured, so nothing is forwarded yet. Remove the setting.")
                    : Check("user-credential-forwarding", Pass, "User access tokens are not placed on agent messages."),
            string.IsNullOrWhiteSpace(options.Manifest.PublicHostName)
                ? Check("public-host", Warn, "Manifest:PublicHostName is not set, so the app package lists no valid domain for this host.")
                : Check("public-host", Pass, "The app package lists this host's public name as a valid domain."),
            !options.Proactive.Enabled
                ? Check("proactive-scopes", Skipped, "Proactive delivery is off.")
                : options.Proactive.AllowedConversationTypes.All(type => string.Equals(type, "personal", StringComparison.OrdinalIgnoreCase))
                    ? Check("proactive-scopes", Pass, "Proactive messages are delivered to personal conversations only.")
                    : Check("proactive-scopes", Warn, "Proactive messages may be delivered to shared conversations, where a message meant for one person is visible to others.")
        ];
    }

    private static IntegrationCheck PrincipalStrategy(Microsoft365CopilotOptions options, bool customResolver)
    {
        const string id = "principal-strategy";
        if (customResolver)
            return Check(id, Skipped, "A custom principal resolver is registered; its mapping is not assessed here.");
        if (options.Principal.Strategy == CopilotPrincipalStrategy.ChannelUserId)
            return Check(id, Fail, "Users are identified by a channel user id, which is not an Entra identity. This is for test channels only.");
        if (options.Principal.AllowChannelIdFallback && options.TokenValidation.Enabled)
            return Check(id, Warn, "Users without an Entra identity are admitted under a channel user id (Principal:AllowChannelIdFallback).");
        return options.Principal.Strategy switch
        {
            CopilotPrincipalStrategy.CanonicalEntra => Check(id, Pass,
                "Users map to the Entra tenant and object identity shared with the A2A endpoint."),
            CopilotPrincipalStrategy.EntraObjectId => Check(id, Warn,
                "The tenant is not part of the principal. Use CanonicalEntra so identity is tenant-qualified and matches the A2A endpoint."),
            CopilotPrincipalStrategy.TenantAndObjectId => Check(id, Warn,
                "The principal is tenant-qualified but does not match the one the A2A endpoint derives, so the same person reaches a different agent there. Use CanonicalEntra."),
            _ => Check(id, Warn,
                "User principal names can be renamed and reassigned. Use CanonicalEntra for a stable identity.")
        };
    }

    private static IntegrationCheck Check(string id, string status, string message) => new() { Id = id, Status = status, Message = message };
}
