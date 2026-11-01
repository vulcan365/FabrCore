namespace FabrCore.Services.Microsoft365Copilot;

/// <summary>
/// Options for the Microsoft 365 Copilot addon, bound from the
/// <c>Microsoft365Copilot</c> section of fabrcore.json or appsettings.json.
/// </summary>
/// <remarks>
/// Minimal production configuration, one entry per agent listed in Microsoft 365 Copilot:
/// <code>
/// "Microsoft365Copilot": {
///   "TenantId": "&lt;entra-tenant-id&gt;",
///   "Agents": [
///     { "Name": "assistant", "Route": "/api/messages", "AgentType": "chat-agent",
///       "ClientId": "&lt;bot-app-registration-client-id&gt;", "ClientSecret": "&lt;secret&gt;" },
///     { "Name": "sales", "AgentType": "sales-agent",
///       "ClientId": "&lt;second-bot-client-id&gt;", "ClientSecret": "&lt;secret&gt;" }
///   ]
/// }
/// </code>
/// Minimal local-dev configuration (Microsoft 365 Agents Playground, no Azure resources):
/// <code>
/// "Microsoft365Copilot": {
///   "TokenValidation": { "Enabled": false },
///   "Agents": [ { "Name": "assistant", "Route": "/api/messages", "AgentType": "chat-agent" } ]
/// }
/// </code>
/// </remarks>
public sealed class Microsoft365CopilotOptions
{
    /// <summary>
    /// Master switch. When false the channel registers no bridge and maps no messaging endpoint;
    /// only its capability and settings reporters and its administration routes remain, so a
    /// management console can see that it is installed and why it is off. A host with no
    /// <c>Microsoft365Copilot</c> configuration at all starts with the channel off.
    /// </summary>
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// Entra tenant id of the bot app registrations (SingleTenant apps). Default for every entry
    /// in <see cref="Agents"/>; an entry may override it.
    /// </summary>
    public string? TenantId { get; set; }

    /// <summary>
    /// How the addon authenticates outbound calls to Azure Bot Service. Default for every entry in
    /// <see cref="Agents"/>, as are the certificate, federation, and authority settings below.
    /// One of the Agents SDK MSAL auth types:
    /// <c>ClientSecret</c>, <c>Certificate</c>, <c>CertificateSubjectName</c>, <c>UserManagedIdentity</c>,
    /// <c>SystemManagedIdentity</c>, <c>FederatedCredentials</c>, <c>WorkloadIdentity</c>.
    /// </summary>
    public string AuthType { get; set; } = "ClientSecret";

    /// <summary>Certificate thumbprint for <c>AuthType = Certificate</c>.</summary>
    public string? CertificateThumbprint { get; set; }

    /// <summary>Certificate subject name for <c>AuthType = CertificateSubjectName</c>.</summary>
    public string? CertificateSubjectName { get; set; }

    /// <summary>Certificate store name (default <c>My</c>) for certificate auth types.</summary>
    public string? CertificateStoreName { get; set; }

    /// <summary>Managed identity client id for <c>AuthType = FederatedCredentials</c> or <c>UserManagedIdentity</c>.</summary>
    public string? FederatedClientId { get; set; }

    /// <summary>Federated token file path for <c>AuthType = WorkloadIdentity</c> (AKS).</summary>
    public string? FederatedTokenFile { get; set; }

    /// <summary>
    /// Override for the token authority. Defaults to
    /// <c>https://login.microsoftonline.com/{TenantId}</c> (or the botframework.com common
    /// authority when no tenant id is configured, for MultiTenant apps).
    /// </summary>
    public string? AuthorityEndpoint { get; set; }

    /// <summary>
    /// Message sent when the user (or the agent) is added to a conversation.
    /// Set to an empty string to disable.
    /// </summary>
    public string? WelcomeMessage { get; set; } =
        "Hello! I'm ready to help — ask me anything.";

    /// <summary>Message shown to the user when the bridged FabrCore agent fails.</summary>
    public string ErrorMessage { get; set; } =
        "Something went wrong while processing your request. Please try again.";

    /// <summary>Inbound channel-token validation settings.</summary>
    public CopilotTokenValidationOptions TokenValidation { get; set; } = new();

    /// <summary>
    /// The agents this host exposes, zero or more. Each entry is one agent in the Microsoft 365
    /// Copilot agent list: its own Azure Bot registration, its own messaging route, and its own
    /// app package. With no entries the channel is on but serves no messaging endpoint.
    /// </summary>
    public List<CopilotAgentEntryOptions> Agents { get; set; } = new();

    /// <summary>Response streaming behavior on streaming-capable channels (Teams, M365 Copilot).</summary>
    public CopilotStreamingOptions Streaming { get; set; } = new();

    /// <summary>
    /// Out-of-turn delivery through stored Teams/Copilot conversation endpoints.
    /// Disabled by default and independent from normal turn replies.
    /// </summary>
    public CopilotProactiveOptions Proactive { get; set; } = new();

    /// <summary>How the inbound Microsoft 365 user is mapped to a FabrCore principal handle.</summary>
    public CopilotPrincipalOptions Principal { get; set; } = new();

    /// <summary>
    /// Entra user authorization (SSO / OBO). The <c>Handlers</c> subsection uses the Microsoft 365
    /// Agents SDK handler schema and is forwarded to the SDK verbatim; the remaining properties
    /// control FabrCore-specific behavior.
    /// </summary>
    public CopilotUserAuthorizationOptions UserAuthorization { get; set; } = new();

    /// <summary>
    /// Metadata used to generate the Microsoft 365 app manifests / app packages. Default for every
    /// entry in <see cref="Agents"/>; an entry overrides individual values in its own <c>Manifest</c>.
    /// </summary>
    public CopilotManifestOptions Manifest { get; set; } = new();

    /// <summary>
    /// Set by <c>AddMicrosoft365Copilot</c> when an SDK user-authorization handler section was
    /// found, so downstream components know SSO is in play without re-reading configuration.
    /// </summary>
    internal bool UserAuthorizationConfigured { get; set; }
}

/// <summary>Validation settings for tokens Azure Bot Service presents to the messages endpoint.</summary>
public sealed class CopilotTokenValidationOptions
{
    /// <summary>
    /// When false the messages endpoint accepts anonymous requests. Only use this for local
    /// development against the Microsoft 365 Agents Playground — never in production.
    /// </summary>
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// Token audiences accepted on every messaging route, in addition to each agent's own
    /// <c>ClientId</c>, which is always accepted on that agent's route and on no other.
    /// </summary>
    public List<string> Audiences { get; set; } = new();

    /// <summary>Additional valid issuers, appended to the built-in Bot Framework / Entra issuer list.</summary>
    public List<string> ValidIssuers { get; set; } = new();

    /// <summary>Override for the Azure Bot Service OpenID metadata document.</summary>
    public string? AzureBotServiceOpenIdMetadataUrl { get; set; }

    /// <summary>Override for the Entra OpenID metadata document.</summary>
    public string? OpenIdMetadataUrl { get; set; }
}

/// <summary>
/// One agent exposed to Microsoft 365 Copilot. Microsoft allows one bot per app manifest and one
/// messaging endpoint per Azure Bot resource, so each entry carries its own bot registration,
/// its own route, and its own app package.
/// </summary>
/// <remarks>
/// The entry names the FabrCore agent and nothing more: plugins, tools, and prompts belong to the
/// agent type or to the blueprint that provisions it. An agent that already exists for the
/// calling principal under <see cref="Handle"/> is used as it is; a missing one is created from
/// <see cref="AgentType"/> and <see cref="Models"/> alone.
/// </remarks>
public sealed class CopilotAgentEntryOptions
{
    /// <summary>
    /// Stable name of the entry: letters, digits, <c>-</c> and <c>_</c>. Used in the default
    /// route, the app-package routes, and the administration API.
    /// </summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>
    /// Route Azure Bot Service posts to for this agent. Defaults to <c>/api/messages/{Name}</c>.
    /// One entry may claim the bare <c>/api/messages</c>.
    /// </summary>
    public string? Route { get; set; }

    /// <summary>Client id (app id) of this agent's Azure Bot Entra app registration.</summary>
    public string? ClientId { get; set; }

    /// <summary>Client secret for <c>AuthType = ClientSecret</c>. Prefer certificates or managed identity in production.</summary>
    public string? ClientSecret { get; set; }

    /// <summary>Entra tenant id of this registration. Defaults to the top-level <c>TenantId</c>.</summary>
    public string? TenantId { get; set; }

    /// <summary>Outbound credential type. Defaults to the top-level <c>AuthType</c>.</summary>
    public string? AuthType { get; set; }

    /// <summary>Certificate thumbprint. Defaults to the top-level value.</summary>
    public string? CertificateThumbprint { get; set; }

    /// <summary>Certificate subject name. Defaults to the top-level value.</summary>
    public string? CertificateSubjectName { get; set; }

    /// <summary>Certificate store name. Defaults to the top-level value.</summary>
    public string? CertificateStoreName { get; set; }

    /// <summary>Managed identity client id. Defaults to the top-level value.</summary>
    public string? FederatedClientId { get; set; }

    /// <summary>Federated token file path. Defaults to the top-level value.</summary>
    public string? FederatedTokenFile { get; set; }

    /// <summary>Token authority override. Defaults to the top-level value.</summary>
    public string? AuthorityEndpoint { get; set; }

    /// <summary>FabrCore agent type alias (the <c>[AgentAlias]</c> value) that answers on this route.</summary>
    public string? AgentType { get; set; }

    /// <summary>
    /// Handle of the agent under each calling user's principal. Defaults to <see cref="Name"/>.
    /// The principal itself always comes from the inbound activity.
    /// </summary>
    public string? Handle { get; set; }

    /// <summary>Model configuration name used when the agent has to be created. Default <c>default</c>.</summary>
    public string Models { get; set; } = "default";

    /// <summary>Greeting for this agent. Defaults to the top-level <c>WelcomeMessage</c>.</summary>
    public string? WelcomeMessage { get; set; }

    /// <summary>App-manifest values for this agent, layered over the top-level <c>Manifest</c>.</summary>
    public CopilotAgentManifestOptions Manifest { get; set; } = new();
}

/// <summary>
/// Per-agent app-manifest overrides. Every value left unset falls back to the top-level
/// <see cref="CopilotManifestOptions"/>.
/// </summary>
public sealed class CopilotAgentManifestOptions
{
    /// <summary>Microsoft 365 app id (GUID). Defaults to the agent's <c>ClientId</c>.</summary>
    public string? Id { get; set; }

    /// <summary>App version (semver).</summary>
    public string? Version { get; set; }

    /// <summary>
    /// Short display name (max 30 characters). Defaults to the top-level name when the host
    /// exposes one agent, and to the entry's <c>Name</c> when it exposes several.
    /// </summary>
    public string? Name { get; set; }

    /// <summary>Full display name.</summary>
    public string? FullName { get; set; }

    /// <summary>Short description (max 80 characters).</summary>
    public string? Description { get; set; }

    /// <summary>Full description.</summary>
    public string? FullDescription { get; set; }

    /// <summary>Developer/company name shown in the store listing.</summary>
    public string? DeveloperName { get; set; }

    /// <summary>Developer website (https).</summary>
    public string? WebsiteUrl { get; set; }

    /// <summary>Privacy statement URL (https).</summary>
    public string? PrivacyUrl { get; set; }

    /// <summary>Terms of use URL (https).</summary>
    public string? TermsOfUseUrl { get; set; }

    /// <summary>Accent color used behind the outline icon (hex).</summary>
    public string? AccentColor { get; set; }

    /// <summary>Conversation starters for this agent. Empty means the top-level starters.</summary>
    public List<CopilotConversationStarter> ConversationStarters { get; set; } = new();

    /// <summary>Path to a 192x192 PNG color icon for this agent.</summary>
    public string? ColorIconPath { get; set; }

    /// <summary>Path to a 32x32 transparent PNG outline icon for this agent.</summary>
    public string? OutlineIconPath { get; set; }
}

/// <summary>Streaming behavior for channels that support it (Teams, Microsoft 365 Copilot).</summary>
public sealed class CopilotStreamingOptions
{
    /// <summary>Use the channel's streaming protocol when available. Default true.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// Informative status update shown while the FabrCore agent is working.
    /// Set to an empty string to disable.
    /// </summary>
    public string? InformativeUpdate { get; set; } = "Working on it...";

    /// <summary>Attach the "AI generated" label to streamed responses. Default true.</summary>
    public bool EnableGeneratedByAILabel { get; set; } = true;

    /// <summary>Enable the Teams/Copilot feedback (thumbs up/down) buttons on responses.</summary>
    public bool EnableFeedbackLoop { get; set; }
}

/// <summary>Settings for the opt-in Microsoft 365 principal message relay.</summary>
public sealed class CopilotProactiveOptions
{
    /// <summary>Enable durable out-of-turn delivery. Default false.</summary>
    public bool Enabled { get; set; }

    /// <summary>Provider send attempts made before reporting a retryable failure to Core.</summary>
    public int MaxDeliveryAttempts { get; set; } = 3;

    /// <summary>Initial delay used by provider-local exponential backoff.</summary>
    public TimeSpan RetryBaseDelay { get; set; } = TimeSpan.FromSeconds(2);

    /// <summary>Timeout for each proactive send attempt.</summary>
    public TimeSpan SendTimeout { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>Number of independent worker queues.</summary>
    public int WorkerShards { get; set; } = 4;

    /// <summary>Bounded entries per worker shard.</summary>
    public int OutboundQueueCapacity { get; set; } = 64;

    /// <summary>Maximum stored conversation endpoints per principal.</summary>
    public int MaxStoredEndpoints { get; set; } = 8;

    /// <summary>
    /// Conversation types eligible for proactive delivery. The safe default is
    /// personal scope only; add channel/group types deliberately.
    /// </summary>
    public List<string> AllowedConversationTypes { get; set; } = ["personal"];
}

/// <summary>Strategy for deriving the FabrCore principal handle from the Microsoft 365 user.</summary>
public enum CopilotPrincipalStrategy
{
    /// <summary>Shared tenant/object identity compatible with A2A CanonicalEntra.</summary>
    CanonicalEntra = 4,
    /// <summary>Use the user's Entra object id (stable, tenant-scoped). Default.</summary>
    EntraObjectId = 0,

    /// <summary>Use <c>{tenantId}-{objectId}</c>. Recommended for MultiTenant bots.</summary>
    TenantAndObjectId,

    /// <summary>
    /// Use the user's UPN / preferred_username from the SSO token when user authorization is
    /// configured, falling back to the Entra object id.
    /// </summary>
    UserPrincipalName,

    /// <summary>Use <c>{channelId}-{channelUserId}</c>. Only useful for dev/test channels.</summary>
    ChannelUserId,
}

/// <summary>How inbound Microsoft 365 users map to FabrCore principals.</summary>
public sealed class CopilotPrincipalOptions
{
    /// <summary>Mapping strategy. Default <see cref="CopilotPrincipalStrategy.EntraObjectId"/>.</summary>
    public CopilotPrincipalStrategy Strategy { get; set; } = CopilotPrincipalStrategy.EntraObjectId;

    /// <summary>
    /// Optional prefix prepended to every mapped principal handle (for example <c>m365-</c>).
    /// Must not contain <c>:</c>.
    /// </summary>
    public string? Prefix { get; set; }

    /// <summary>
    /// Allow falling back to <c>{channelId}-{channelUserId}</c> when no Entra identity is present
    /// on the activity. Automatically allowed when token validation is disabled (local dev);
    /// otherwise unmapped users are rejected unless this is true.
    /// </summary>
    public bool AllowChannelIdFallback { get; set; }
}

/// <summary>
/// FabrCore-specific user-authorization behavior. SSO handlers themselves are configured in the
/// <c>UserAuthorization:Handlers</c> subsection using the Microsoft 365 Agents SDK schema
/// (<c>AzureBotOAuthConnectionName</c>, <c>OBOConnectionName</c>, <c>OBOScopes</c>, ...) and are
/// forwarded to the SDK verbatim.
/// </summary>
public sealed class CopilotUserAuthorizationOptions
{
    /// <summary>
    /// Stamp the signed-in user's Entra access token onto outgoing agent messages as
    /// <c>Args["Microsoft365Copilot:UserToken"]</c> so FabrCore agents/plugins can call downstream
    /// APIs (Microsoft Graph etc.) as the user. Off by default: the token then flows through
    /// FabrCore messaging and any configured monitors — enable deliberately.
    /// </summary>
    public bool PassUserTokenToAgent { get; set; }

    /// <summary>Arg key used when <see cref="PassUserTokenToAgent"/> is enabled.</summary>
    public string UserTokenArgName { get; set; } = Microsoft365CopilotDefaults.ArgUserToken;
}

/// <summary>A Copilot conversation starter (surfaced from the app manifest command list).</summary>
public sealed class CopilotConversationStarter
{
    /// <summary>Short title shown to the user (max 32 characters per manifest schema).</summary>
    public string Title { get; set; } = string.Empty;

    /// <summary>The prompt sent when the starter is selected (max 128 characters).</summary>
    public string Description { get; set; } = string.Empty;
}

/// <summary>Metadata for the generated Microsoft 365 app manifest / package.</summary>
public sealed class CopilotManifestOptions
{
    /// <summary>
    /// Microsoft 365 app id (GUID). Defaults to the agent's <c>ClientId</c>. Set per agent in
    /// <c>Agents:{n}:Manifest:Id</c>; the top-level value is not inherited, because two apps
    /// cannot share an id.
    /// </summary>
    public string? Id { get; set; }

    /// <summary>App version (semver). Default <c>1.0.0</c>.</summary>
    public string Version { get; set; } = "1.0.0";

    /// <summary>Short display name (max 30 characters).</summary>
    public string Name { get; set; } = "FabrCore Agent";

    /// <summary>Full display name.</summary>
    public string? FullName { get; set; }

    /// <summary>Short description (max 80 characters).</summary>
    public string Description { get; set; } = "A FabrCore agent available in Microsoft 365 Copilot.";

    /// <summary>Full description.</summary>
    public string? FullDescription { get; set; }

    /// <summary>Developer/company name shown in the store listing.</summary>
    public string DeveloperName { get; set; } = "FabrCore";

    /// <summary>Developer website (https).</summary>
    public string WebsiteUrl { get; set; } = "https://github.com/vulcan365/FabrCore";

    /// <summary>Privacy statement URL (https).</summary>
    public string PrivacyUrl { get; set; } = "https://github.com/vulcan365/FabrCore";

    /// <summary>Terms of use URL (https).</summary>
    public string TermsOfUseUrl { get; set; } = "https://github.com/vulcan365/FabrCore";

    /// <summary>Accent color used behind the outline icon (hex). Default <c>#4B53C5</c>.</summary>
    public string AccentColor { get; set; } = "#4B53C5";

    /// <summary>
    /// Public host name of this server (for example <c>myagents.contoso.com</c>). Added to the
    /// manifest <c>validDomains</c>.
    /// </summary>
    public string? PublicHostName { get; set; }

    /// <summary>Conversation starters surfaced in Copilot and Teams.</summary>
    public List<CopilotConversationStarter> ConversationStarters { get; set; } = new();

    /// <summary>Path to a 192x192 PNG color icon. A generated placeholder is used when omitted.</summary>
    public string? ColorIconPath { get; set; }

    /// <summary>Path to a 32x32 transparent PNG outline icon. A generated placeholder is used when omitted.</summary>
    public string? OutlineIconPath { get; set; }

    /// <summary>
    /// Expose developer endpoints that serve the generated manifest and app package
    /// (<c>/m365copilot/{agent}/manifest.json</c>, <c>/m365copilot/{agent}/appPackage.zip</c>, and
    /// the name-addressed <c>/manifests/{name}.json</c> where <c>{name}</c> is the slug of an
    /// agent's manifest name, for example <c>my-fabrcore-agent</c>).
    /// Defaults to enabled only in the Development environment.
    /// </summary>
    public bool? EnableAppPackageEndpoint { get; set; }
}
