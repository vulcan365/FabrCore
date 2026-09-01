using FabrCore.Host.A2A;
namespace FabrCore.Host.Configuration;

/// <summary>
/// Options for the A2A addon, bound from the <c>A2A</c> section of fabrcore.json or
/// appsettings.json.
/// </summary>
/// <remarks>
/// The smallest useful configuration publishes one registered agent type:
/// <code>
/// "A2A": {
///   "Enabled": true,
///   "PublicBaseUrl": "https://agents.contoso.com",
///   "AgentTypes": [ "chat-agent" ]
/// }
/// </code>
/// Every exposed agent is reachable at <c>{RoutePrefix}/{name}</c> and describes itself at
/// <c>{RoutePrefix}/{name}/.well-known/agent-card.json</c>.
/// </remarks>
public sealed class A2AOptions
{
    /// <summary>
    /// Master feature flag. When false the addon registers nothing and maps no routes, so the
    /// server exposes no A2A surface at all. Default false — A2A endpoints are public by design,
    /// so publishing them is an explicit decision.
    /// </summary>
    public bool Enabled { get; set; }

    /// <summary>Route prefix every agent is mounted under. Default <c>/a2a</c>.</summary>
    public string RoutePrefix { get; set; } = A2ADefaults.RoutePrefix;

    /// <summary>
    /// Absolute public base URL of this server, for example <c>https://agents.contoso.com</c>.
    /// Agent cards must advertise the URL clients can actually reach, which behind a reverse
    /// proxy is not the URL the request arrived on. When unset the addon derives it from the
    /// incoming request (honoring <c>X-Forwarded-*</c> if forwarded headers middleware is
    /// configured), which is convenient for local development and dev tunnels.
    /// </summary>
    public string? PublicBaseUrl { get; set; }

    /// <summary>
    /// Registered FabrCore agent type aliases (<c>[AgentAlias]</c> values) to publish. Each entry
    /// becomes one A2A agent whose instances are provisioned per calling principal. Shorthand for
    /// an <see cref="Agents"/> entry with only <c>AgentType</c> set.
    /// </summary>
    public List<string> AgentTypes { get; set; } = new();

    /// <summary>
    /// Existing fully-qualified agent handles to publish, for example <c>system:assistant</c>.
    /// The agent must already exist; the addon routes to it instead of provisioning. Shorthand
    /// for an <see cref="Agents"/> entry with only <c>AgentHandle</c> set. Cross-principal
    /// delivery requires an <c>agent.message.allow</c> ACL grant for the mapped principals.
    /// </summary>
    public List<string> AgentHandles { get; set; } = new();

    /// <summary>
    /// Explicitly configured agents, for full control over route name, card metadata, skills, and
    /// provisioning. Merged with the <see cref="AgentTypes"/> and <see cref="AgentHandles"/>
    /// shorthands; an explicit entry wins over a shorthand that resolves to the same route name.
    /// </summary>
    public List<A2AAgentOptions> Agents { get; set; } = new();

    /// <summary>
    /// Publish agents straight from the FabrCore registry and the live agent list, instead of
    /// naming each one here. This is the low-configuration path: turn it on and every agent type
    /// the host already advertises through <c>/fabrcoreapi/discovery</c> becomes an A2A agent,
    /// carrying its own <c>[Description]</c>, <c>[FabrCoreCapabilities]</c>, and
    /// <c>[FabrCoreNote]</c> across onto its agent card.
    /// </summary>
    public A2ADiscoveryOptions Discovery { get; set; } = new();

    /// <summary>
    /// Settings applied to every published agent that does not state its own. Lets a whole fleet
    /// share one model configuration, prompt, and plugin set without repeating them per agent.
    /// </summary>
    public A2AAgentDefaults Defaults { get; set; } = new();

    /// <summary>
    /// Route name of the agent served from the server-root well-known card
    /// (<c>/.well-known/agent-card.json</c>). Defaults to the only exposed agent when exactly one
    /// is configured, and to nothing when several are.
    /// </summary>
    public string? PrimaryAgent { get; set; }

    /// <summary>
    /// Serve <c>GET {RoutePrefix}</c> as a JSON catalog of every exposed agent and its endpoints.
    /// Useful when wiring up a client; it lists routes, not secrets. Default true.
    /// </summary>
    public bool EnableCatalogEndpoint { get; set; } = true;

    /// <summary>
    /// Origins allowed to read agent cards from a browser, written as
    /// <c>Access-Control-Allow-Origin</c> on every card route. Default <c>*</c>.
    /// </summary>
    /// <remarks>
    /// Copilot Studio fetches the card with a cross-origin <c>fetch()</c> from its own page, not
    /// from its service: the request carries <c>Origin: https://copilotstudio.microsoft.com</c> and
    /// <c>Sec-Fetch-Mode: cors</c>. Without this header the browser discards a perfectly good 200
    /// before the page sees it, and the operator is told the card could not be found while the
    /// server log shows the request succeeding. A card is public, anonymous, non-secret metadata
    /// that the protocol expects any client to fetch, so <c>*</c> is the useful default.
    /// This applies to card routes only; the call endpoints are never opened cross-origin by it.
    /// Set to an empty list to send no header at all.
    /// </remarks>
    public List<string> AgentCardCorsOrigins { get; set; } = new() { "*" };

    /// <summary>How A2A clients authenticate.</summary>
    public A2AAuthenticationOptions Authentication { get; set; } = new();

    /// <summary>How an authenticated A2A caller maps to a FabrCore principal.</summary>
    public A2APrincipalOptions Principal { get; set; } = new();

    /// <summary>Task lifetime, retention, and execution limits.</summary>
    public A2ATaskOptions Tasks { get; set; } = new();

    /// <summary>Tolerances for clients that deviate from the strict binding split.</summary>
    public A2AInteropOptions Interop { get; set; } = new();

    /// <summary>Publisher identity written to every agent card.</summary>
    public A2AProviderOptions Provider { get; set; } = new();
}

/// <summary>One agent published over A2A.</summary>
public sealed class A2AAgentOptions
{
    /// <summary>
    /// Route segment and card identity, for example <c>support</c> gives <c>/a2a/support</c>.
    /// Defaults to a slug of <see cref="AgentType"/> or <see cref="AgentHandle"/>.
    /// </summary>
    public string? Name { get; set; }

    /// <summary>Display name on the agent card. Defaults to a title-cased <see cref="Name"/>.</summary>
    public string? DisplayName { get; set; }

    /// <summary>
    /// Card description. This is what an orchestrator reads to decide when to call the agent, so
    /// make it say what the agent is for. Defaults to the registered agent type's description.
    /// </summary>
    public string? Description { get; set; }

    /// <summary>Agent version reported on the card. Falls back to <c>Defaults:Version</c>.</summary>
    public string? Version { get; set; }

    /// <summary>Absolute URL of an icon for the agent card.</summary>
    public string? IconUrl { get; set; }

    /// <summary>Absolute URL of human-readable documentation for the agent card.</summary>
    public string? DocumentationUrl { get; set; }

    /// <summary>
    /// FabrCore agent type alias to provision per calling principal. Mutually exclusive with
    /// <see cref="AgentHandle"/>.
    /// </summary>
    public string? AgentType { get; set; }

    /// <summary>
    /// Fully-qualified handle of an existing agent (<c>principal:handle</c>) to route to instead
    /// of provisioning. Mutually exclusive with <see cref="AgentType"/>.
    /// </summary>
    public string? AgentHandle { get; set; }

    /// <summary>Handle given to provisioned instances. Defaults to <c>a2a-{Name}</c>.</summary>
    public string? Handle { get; set; }

    /// <summary>Model configuration name used by provisioned instances. Falls back to <c>Defaults:Models</c>.</summary>
    public string? Models { get; set; }

    /// <summary>System prompt applied to provisioned instances. Falls back to <c>Defaults:SystemPrompt</c>.</summary>
    public string? SystemPrompt { get; set; }

    /// <summary>Plugin aliases enabled on provisioned instances. Empty falls back to <c>Defaults:Plugins</c>.</summary>
    public List<string> Plugins { get; set; } = new();

    /// <summary>Standalone tool aliases enabled on provisioned instances. Empty falls back to <c>Defaults:Tools</c>.</summary>
    public List<string> Tools { get; set; } = new();

    /// <summary>Args passed to provisioned instances. Merged over <c>Defaults:Args</c>.</summary>
    public Dictionary<string, string> Args { get; set; } = new();

    /// <summary>
    /// Give each A2A <c>contextId</c> its own agent instance (<c>{Handle}-{contextId}</c>)
    /// instead of one continuous instance per principal. Use when callers expect conversations to
    /// be isolated from each other. Falls back to <c>Defaults:AgentPerContext</c>.
    /// </summary>
    public bool? AgentPerContext { get; set; }

    /// <summary>
    /// Skills advertised on the card. When empty a single skill is synthesized from the agent's
    /// name and description, which is enough for Copilot Studio to route to it.
    /// </summary>
    public List<A2ASkillOptions> Skills { get; set; } = new();

    /// <summary>Media types the agent accepts. Empty falls back to <c>Defaults:InputModes</c>.</summary>
    public List<string> InputModes { get; set; } = new();

    /// <summary>Media types the agent produces. Empty falls back to <c>Defaults:OutputModes</c>.</summary>
    public List<string> OutputModes { get; set; } = new();

    /// <summary>Advertise streaming support on the card. Falls back to <c>Defaults:Streaming</c>.</summary>
    public bool? Streaming { get; set; }
}

/// <summary>A capability advertised on an agent card.</summary>
public sealed class A2ASkillOptions
{
    /// <summary>Stable identifier. Defaults to a slug of <see cref="Name"/>.</summary>
    public string? Id { get; set; }

    /// <summary>Short human-readable skill name.</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>What the skill does, in the words an orchestrator should match against.</summary>
    public string Description { get; set; } = string.Empty;

    /// <summary>Keywords that help an orchestrator classify the skill.</summary>
    public List<string> Tags { get; set; } = new();

    /// <summary>Example prompts that should route to this skill.</summary>
    public List<string> Examples { get; set; } = new();
}

/// <summary>How A2A clients authenticate against the published endpoints.</summary>
public sealed class A2AAuthenticationOptions
{
    /// <summary>Authentication mode. Default <see cref="A2AAuthenticationMode.ApiKey"/>.</summary>
    public A2AAuthenticationMode Mode { get; set; } = A2AAuthenticationMode.ApiKey;

    /// <summary>API key settings, used when <see cref="Mode"/> is <c>ApiKey</c>.</summary>
    public A2AApiKeyOptions ApiKey { get; set; } = new();

    /// <summary>JWT bearer settings, used when <see cref="Mode"/> is <c>JwtBearer</c>.</summary>
    public A2AJwtBearerOptions JwtBearer { get; set; } = new();

    /// <summary>
    /// Leave agent cards readable without credentials even when calls require them. A2A clients
    /// (Copilot Studio included) fetch the card before they hold a credential, so this defaults
    /// to true. The card carries route and capability metadata only.
    /// </summary>
    public bool AllowAnonymousAgentCard { get; set; } = true;
}

/// <summary>Supported A2A authentication modes.</summary>
public enum A2AAuthenticationMode
{
    /// <summary>
    /// No credential required. Only appropriate when the endpoint is not publicly reachable or
    /// the reverse proxy in front of it authenticates callers.
    /// </summary>
    None,

    /// <summary>A shared secret in a header or query parameter. Matches Copilot Studio's "API key".</summary>
    ApiKey,

    /// <summary>An OAuth 2.0 / OIDC access token. Matches Copilot Studio's "OAuth 2.0".</summary>
    JwtBearer,
}

/// <summary>Shared-secret authentication settings.</summary>
public sealed class A2AApiKeyOptions
{
    /// <summary>Header carrying the key. Default <c>x-api-key</c>. Set empty to disable header keys.</summary>
    public string HeaderName { get; set; } = A2ADefaults.ApiKeyHeader;

    /// <summary>
    /// Query parameter carrying the key, for clients that cannot set headers. Unset by default:
    /// keys in query strings end up in access logs and proxy traces.
    /// </summary>
    public string? QueryParameterName { get; set; }

    /// <summary>Accepted keys. At least one is required in <c>ApiKey</c> mode.</summary>
    public List<A2AApiKeyEntry> Keys { get; set; } = new();
}

/// <summary>One accepted API key and the identity it grants.</summary>
public sealed class A2AApiKeyEntry
{
    /// <summary>Label for logs and diagnostics. Never the secret itself.</summary>
    public string? Name { get; set; }

    /// <summary>The secret value. Prefer a secret store or environment variable over fabrcore.json.</summary>
    public string Value { get; set; } = string.Empty;

    /// <summary>
    /// FabrCore principal this key acts as, used when
    /// <see cref="A2APrincipalStrategy.ApiKey"/> is selected.
    /// </summary>
    public string? PrincipalHandle { get; set; }

    /// <summary>Route names this key may call. Empty means every exposed agent.</summary>
    public List<string> Agents { get; set; } = new();
}

/// <summary>OAuth 2.0 / OIDC bearer token settings.</summary>
public sealed class A2AJwtBearerOptions
{
    /// <summary>Token issuer / OIDC authority, for example <c>https://login.microsoftonline.com/{tenant}/v2.0</c>.</summary>
    public string? Authority { get; set; }

    /// <summary>Expected token audience.</summary>
    public string? Audience { get; set; }

    /// <summary>Additional accepted audiences.</summary>
    public List<string> ValidAudiences { get; set; } = new();

    /// <summary>Additional accepted issuers.</summary>
    public List<string> ValidIssuers { get; set; } = new();

    /// <summary>Require HTTPS metadata retrieval. Default true; turn off only for local testing.</summary>
    public bool RequireHttpsMetadata { get; set; } = true;

    /// <summary>Claim carrying the caller identity used by <see cref="A2APrincipalStrategy.Claim"/>.</summary>
    public string PrincipalClaimType { get; set; } = "oid";

    /// <summary>Scopes advertised on the agent card's OAuth security scheme.</summary>
    public Dictionary<string, string> Scopes { get; set; } = new();

    /// <summary>Authorization endpoint advertised on the agent card. Optional.</summary>
    public string? AuthorizationUrl { get; set; }

    /// <summary>Token endpoint advertised on the agent card. Optional.</summary>
    public string? TokenUrl { get; set; }

    /// <summary>Refresh endpoint advertised on the agent card. Optional.</summary>
    public string? RefreshUrl { get; set; }
}

/// <summary>Strategy for deriving the FabrCore principal handle of an A2A caller.</summary>
public enum A2APrincipalStrategy
{
    /// <summary>Every A2A caller shares one principal (<see cref="A2APrincipalOptions.Handle"/>). Default.</summary>
    Fixed,

    /// <summary>One principal per A2A <c>contextId</c>, isolating conversations from each other.</summary>
    ContextId,

    /// <summary>The <c>PrincipalHandle</c> of the matched API key.</summary>
    ApiKey,

    /// <summary>A claim from the validated bearer token.</summary>
    Claim,
}

/// <summary>How A2A callers map onto FabrCore principals.</summary>
public sealed class A2APrincipalOptions
{
    /// <summary>Mapping strategy. Default <see cref="A2APrincipalStrategy.Fixed"/>.</summary>
    public A2APrincipalStrategy Strategy { get; set; } = A2APrincipalStrategy.Fixed;

    /// <summary>Principal handle used by <see cref="A2APrincipalStrategy.Fixed"/>. Default <c>a2a</c>.</summary>
    public string Handle { get; set; } = A2ADefaults.PrincipalHandle;

    /// <summary>Prefix prepended to derived handles, for example <c>a2a-</c>. Must not contain <c>:</c>.</summary>
    public string? Prefix { get; set; }
}

/// <summary>Task store and execution limits.</summary>
public sealed class A2ATaskOptions
{
    /// <summary>How long a terminal task stays queryable through <c>tasks/get</c>. Default 1 hour.</summary>
    public TimeSpan Retention { get; set; } = TimeSpan.FromHours(1);

    /// <summary>Maximum tasks held in the in-memory store. Oldest terminal tasks evict first. Default 1000.</summary>
    public int MaxRetainedTasks { get; set; } = 1000;

    /// <summary>Time an agent gets to answer before the task fails. Default 5 minutes.</summary>
    public TimeSpan ExecutionTimeout { get; set; } = TimeSpan.FromMinutes(5);

    /// <summary>Turns of <c>history</c> returned on a task when the client asks for no specific length. Default 10.</summary>
    public int DefaultHistoryLength { get; set; } = 10;

    /// <summary>Interval between keep-alive status events on a slow streaming response. Default 15 seconds.</summary>
    public TimeSpan StreamHeartbeatInterval { get; set; } = TimeSpan.FromSeconds(15);
}

/// <summary>Compatibility switches for clients that do not follow the binding split strictly.</summary>
public sealed class A2AInteropOptions
{
    /// <summary>
    /// Accept a JSON-RPC envelope posted to the HTTP+JSON routes and answer with a matching
    /// JSON-RPC envelope. Microsoft Copilot Studio does exactly this: it is configured with the
    /// REST-style <c>/v1/message:stream</c> URL but sends JSON-RPC bodies. Default true.
    /// </summary>
    public bool AcceptJsonRpcOnHttpRoutes { get; set; } = true;

    /// <summary>
    /// When a JSON-RPC envelope arrives on a streaming route, answer with one buffered JSON-RPC
    /// response holding the completed task instead of an SSE stream. Copilot Studio's connector
    /// reads a single JSON body, so streaming to it loses the result. Default true.
    /// </summary>
    public bool CollapseStreamForJsonRpcOnHttpRoutes { get; set; } = true;

    /// <summary>
    /// Shape of a non-streaming result on the standard A2A routes. <c>Task</c> is the richer
    /// answer and the protocol default: it carries the reply as an artifact, the terminal status
    /// message, and an id the client can follow up on. Default <c>Task</c>.
    /// </summary>
    public A2AResultShape ResultShape { get; set; } = A2AResultShape.Task;

    /// <summary>
    /// Shape of the result returned to a JSON-RPC envelope posted to an HTTP+JSON route — the
    /// Copilot Studio shape. Defaults to <c>Message</c>, which puts the reply text at the top
    /// level where a connector that expects a single flat answer will find it. Set to
    /// <c>Task</c> if your client understands the full task object.
    /// </summary>
    public A2AResultShape CompatibilityResultShape { get; set; } = A2AResultShape.Message;

    /// <summary>
    /// Copy the inbound message's <c>metadata</c> onto the FabrCore message as
    /// <c>Args["A2A:Metadata"]</c> so agents can read caller-supplied context — Copilot Studio
    /// puts the conversation history there. Default true.
    /// </summary>
    public bool PassMessageMetadataToAgent { get; set; } = true;
}

/// <summary>
/// Selects agents from what the host already knows about, so the <c>A2A</c> section does not have
/// to restate the agent catalog.
/// </summary>
/// <remarks>
/// Registry discovery reads the same source as <c>/fabrcoreapi/discovery</c>, which already omits
/// anything marked <c>[FabrCoreHidden]</c> — so hiding an agent from discovery hides it from A2A
/// too, with no second switch to remember.
/// </remarks>
public sealed class A2ADiscoveryOptions
{
    /// <summary>
    /// Which registered agent types to publish. Default <see cref="A2ADiscoveryMode.None"/>, so
    /// discovery is opt-in and an upgrade never widens what a server exposes.
    /// </summary>
    public A2ADiscoveryMode AgentTypes { get; set; } = A2ADiscoveryMode.None;

    /// <summary>
    /// Agent type aliases to publish, as exact names or <c>*</c> globs. Empty means every alias
    /// allowed by <see cref="AgentTypes"/>.
    /// </summary>
    public List<string> IncludeAgentTypes { get; set; } = new();

    /// <summary>
    /// Agent type aliases to withhold, as exact names or <c>*</c> globs. Applied after
    /// <see cref="IncludeAgentTypes"/>, so an exclude always wins.
    /// </summary>
    public List<string> ExcludeAgentTypes { get; set; } = new();

    /// <summary>
    /// Globs matched against the keys of live agents (<c>principal:handle</c>, for example
    /// <c>system:*</c>). Matching agents are published as they are, without provisioning. Unlike
    /// agent-type discovery this reads cluster state, so it is refreshed on
    /// <see cref="RefreshInterval"/> and agents created later appear without a restart.
    /// </summary>
    public List<string> IncludeAgentHandles { get; set; } = new();

    /// <summary>Globs of live agent keys to withhold. Applied after <see cref="IncludeAgentHandles"/>.</summary>
    public List<string> ExcludeAgentHandles { get; set; } = new();

    /// <summary>
    /// How long a live-agent lookup is cached. Only used when <see cref="IncludeAgentHandles"/> is
    /// non-empty. Default 30 seconds.
    /// </summary>
    public TimeSpan RefreshInterval { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>
    /// When an agent loads FabrCore harness skills (the <c>_HarnessSkills</c> arg), resolve each
    /// <c>name@version</c> against the principal's stored skill catalog and advertise them as A2A
    /// skills on its card. A harness skill's name and description are a concrete statement of what
    /// the agent can do, which is exactly what a remote orchestrator routes on. Default true, and
    /// inert for agents that declare no harness skills.
    /// </summary>
    /// <remarks>
    /// Harness skills are principal-scoped, so this only applies where the principal is knowable
    /// before the caller authenticates: an agent published by handle (its principal is in the
    /// handle) or a provisioned agent under <see cref="A2APrincipalStrategy.Fixed"/>. Under the
    /// per-caller strategies the catalog differs by caller, so cards stay caller-independent and
    /// omit them.
    /// </remarks>
    public bool IncludeHarnessSkills { get; set; } = true;

    /// <summary>
    /// Carry <c>[FabrCoreNote]</c> values onto the agent card as extra skill examples and a
    /// "usage notes" line. Notes usually say when *not* to use an agent, which is exactly what an
    /// orchestrator needs. Default true.
    /// </summary>
    public bool IncludeNotes { get; set; } = true;
}

/// <summary>How much of the registry <see cref="A2ADiscoveryOptions.AgentTypes"/> publishes.</summary>
public enum A2ADiscoveryMode
{
    /// <summary>Publish nothing from the registry. Only explicit configuration applies. Default.</summary>
    None,

    /// <summary>
    /// Publish every registered agent type that carries a <c>[Description]</c>. One attribute is
    /// the opt-in, and it doubles as the card text an orchestrator routes on — an agent with
    /// nothing to say about itself is not one you want a remote agent choosing.
    /// </summary>
    Described,

    /// <summary>
    /// Publish every registered agent type that is not <c>[FabrCoreHidden]</c>, described or not.
    /// Undescribed agents get a generated placeholder description, which orchestrators route on
    /// poorly — prefer <see cref="Described"/>.
    /// </summary>
    All,
}

/// <summary>Baseline settings for published agents, overridable per agent.</summary>
public sealed class A2AAgentDefaults
{
    /// <summary>Model configuration name used by provisioned instances. Default <c>default</c>.</summary>
    public string Models { get; set; } = "default";

    /// <summary>System prompt applied to provisioned instances.</summary>
    public string? SystemPrompt { get; set; }

    /// <summary>Plugin aliases enabled on provisioned instances.</summary>
    public List<string> Plugins { get; set; } = new();

    /// <summary>Standalone tool aliases enabled on provisioned instances.</summary>
    public List<string> Tools { get; set; } = new();

    /// <summary>Args passed to provisioned instances.</summary>
    public Dictionary<string, string> Args { get; set; } = new();

    /// <summary>Give each A2A <c>contextId</c> its own agent instance.</summary>
    public bool AgentPerContext { get; set; }

    /// <summary>Media types published agents accept. Default <c>text/plain</c>.</summary>
    public List<string> InputModes { get; set; } = new() { "text/plain" };

    /// <summary>Media types published agents produce. Default <c>text/plain</c>.</summary>
    public List<string> OutputModes { get; set; } = new() { "text/plain" };

    /// <summary>Advertise streaming support. Default true.</summary>
    public bool Streaming { get; set; } = true;

    /// <summary>Agent version reported on cards. Default <c>1.0.0</c>.</summary>
    public string Version { get; set; } = "1.0.0";
}

/// <summary>Shape of a non-streaming A2A result.</summary>
public enum A2AResultShape
{
    /// <summary>Return the full <c>Task</c> object, with artifacts, status, and history.</summary>
    Task,

    /// <summary>Return a bare agent <c>Message</c> carrying the reply parts.</summary>
    Message,
}

/// <summary>Publisher identity written to every agent card.</summary>
public sealed class A2AProviderOptions
{
    /// <summary>Organization name. Omitted from the card when unset.</summary>
    public string? Organization { get; set; }

    /// <summary>Organization URL. Required by the card schema whenever an organization is set.</summary>
    public string? Url { get; set; }
}
