namespace FabrCore.Services.Microsoft365Copilot;

/// <summary>
/// Options for the Microsoft 365 Copilot addon, bound from the
/// <c>Microsoft365Copilot</c> section of fabrcore.json or appsettings.json.
/// </summary>
/// <remarks>
/// Minimal production configuration:
/// <code>
/// "Microsoft365Copilot": {
///   "TenantId": "&lt;entra-tenant-id&gt;",
///   "ClientId": "&lt;bot-app-registration-client-id&gt;",
///   "ClientSecret": "&lt;bot-app-registration-secret&gt;",
///   "Agent": { "AgentType": "chat-agent" }
/// }
/// </code>
/// Minimal local-dev configuration (Microsoft 365 Agents Playground, no Azure resources):
/// <code>
/// "Microsoft365Copilot": {
///   "TokenValidation": { "Enabled": false },
///   "Agent": { "AgentType": "chat-agent" }
/// }
/// </code>
/// </remarks>
public sealed class Microsoft365CopilotOptions
{
    /// <summary>Master switch. When false the addon registers nothing and maps nothing.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>Entra tenant id of the bot's app registration (SingleTenant apps). Optional for MultiTenant registrations.</summary>
    public string? TenantId { get; set; }

    /// <summary>Client id (app id) of the Azure Bot's Entra app registration.</summary>
    public string? ClientId { get; set; }

    /// <summary>Client secret for <c>AuthType = ClientSecret</c>. Prefer certificates or managed identity in production.</summary>
    public string? ClientSecret { get; set; }

    /// <summary>
    /// How the addon authenticates outbound calls to Azure Bot Service. One of the Agents SDK MSAL auth types:
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

    /// <summary>Route the Azure Bot Service messaging endpoint is mapped at. Default <c>/api/messages</c>.</summary>
    public string MessagesEndpoint { get; set; } = Microsoft365CopilotDefaults.MessagesEndpoint;

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

    /// <summary>Which FabrCore agent handles Copilot conversations and how it is provisioned.</summary>
    public CopilotAgentOptions Agent { get; set; } = new();

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

    /// <summary>Metadata used to generate the Microsoft 365 app manifest / app package.</summary>
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

    /// <summary>Accepted token audiences. Defaults to the configured <c>ClientId</c>.</summary>
    public List<string> Audiences { get; set; } = new();

    /// <summary>Additional valid issuers, appended to the built-in Bot Framework / Entra issuer list.</summary>
    public List<string> ValidIssuers { get; set; } = new();

    /// <summary>Override for the Azure Bot Service OpenID metadata document.</summary>
    public string? AzureBotServiceOpenIdMetadataUrl { get; set; }

    /// <summary>Override for the Entra OpenID metadata document.</summary>
    public string? OpenIdMetadataUrl { get; set; }
}

/// <summary>Selects and provisions the FabrCore agent that answers Copilot conversations.</summary>
public sealed class CopilotAgentOptions
{
    /// <summary>
    /// FabrCore agent type alias (the <c>[AgentAlias]</c> value) provisioned for each Microsoft 365
    /// user. Required unless <see cref="SharedAgentHandle"/> is set.
    /// </summary>
    public string? AgentType { get; set; }

    /// <summary>Handle of the per-user agent instance. Default <c>copilot</c>.</summary>
    public string Handle { get; set; } = "copilot";

    /// <summary>
    /// Route every Copilot user to one existing shared agent instead of provisioning per-user
    /// agents. Must be a fully-qualified handle such as <c>system:assistant</c>. Cross-principal
    /// messaging requires an ACL grant (<c>agent.message.allow</c>) for the mapped principals.
    /// </summary>
    public string? SharedAgentHandle { get; set; }

    /// <summary>Model configuration name from fabrcore.json used by provisioned agents. Default <c>default</c>.</summary>
    public string Models { get; set; } = "default";

    /// <summary>System prompt applied to provisioned agents.</summary>
    public string? SystemPrompt { get; set; }

    /// <summary>Plugin aliases enabled on provisioned agents.</summary>
    public List<string> Plugins { get; set; } = new();

    /// <summary>Standalone tool aliases enabled on provisioned agents.</summary>
    public List<string> Tools { get; set; } = new();

    /// <summary>Args passed to provisioned agents (plugin configuration etc.).</summary>
    public Dictionary<string, string> Args { get; set; } = new();

    /// <summary>
    /// When true each Microsoft 365 conversation gets its own agent instance
    /// (<c>{Handle}-{conversationId}</c>) instead of one continuous agent per user.
    /// </summary>
    public bool AgentPerConversation { get; set; }
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
    /// <summary>Use the user's Entra object id (stable, tenant-scoped). Default.</summary>
    EntraObjectId,

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
    /// <summary>Microsoft 365 app id (GUID). Defaults to the bot's <c>ClientId</c>.</summary>
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
    /// (<c>/m365copilot/manifest.json</c>, <c>/m365copilot/appPackage.zip</c>, and the
    /// name-addressed <c>/manifests/{name}.json</c> where <c>{name}</c> is the slug of
    /// <see cref="Name"/>, for example <c>my-fabrcore-agent</c>).
    /// Defaults to enabled only in the Development environment.
    /// </summary>
    public bool? EnableAppPackageEndpoint { get; set; }
}
