using FabrCore.Host.Configuration;
namespace FabrCore.Host.A2A;

/// <summary>
/// Well-known names, routes, and message keys used by the A2A addon.
/// </summary>
public static class A2ADefaults
{
    /// <summary>Configuration section name in fabrcore.json / appsettings.json.</summary>
    public const string SectionName = "A2A";

    /// <summary>Default route prefix every exposed agent is mounted under.</summary>
    public const string RoutePrefix = "/a2a";

    /// <summary>Authentication scheme registered for API key callers.</summary>
    public const string ApiKeyScheme = "FabrCoreA2AApiKey";

    /// <summary>Authentication scheme registered for OAuth 2.0 / OIDC bearer callers.</summary>
    public const string JwtBearerScheme = "FabrCoreA2ABearer";

    /// <summary>Authorization policy applied to the A2A call endpoints.</summary>
    public const string AuthorizationPolicy = "FabrCoreA2A";

    /// <summary>Default header carrying the API key.</summary>
    public const string ApiKeyHeader = "x-api-key";

    /// <summary>Default FabrCore principal handle A2A callers act as.</summary>
    public const string PrincipalHandle = "a2a";

    /// <summary>Value stamped on <c>AgentMessage.Channel</c> for traffic arriving through this addon.</summary>
    public const string ChannelName = "a2a";

    /// <summary>Security scheme name written to agent cards for API key authentication.</summary>
    public const string ApiKeySchemeName = "apiKey";

    /// <summary>Security scheme name written to agent cards for bearer authentication.</summary>
    public const string BearerSchemeName = "oauth2";

    /// <summary>Well-known agent card path introduced in A2A 0.3.</summary>
    public const string WellKnownAgentCardPath = "/.well-known/agent-card.json";

    /// <summary>
    /// Well-known agent card path used before A2A 0.3, and the path Copilot Studio documents.
    /// Served alongside <see cref="WellKnownAgentCardPath"/> with identical content.
    /// </summary>
    public const string LegacyWellKnownAgentCardPath = "/.well-known/agent.json";

    /// <summary>
    /// Every well-known card file name a client may ask for, served with identical content.
    /// The spec settled on <c>agent-card.json</c> (0.3) after <c>agent.json</c> (pre-0.3), but
    /// clients probe further spellings: Microsoft Copilot Studio asks for five, of which
    /// <c>agentCard.json</c> is only a recasing of <c>agentcard.json</c> and needs no route of
    /// its own — ASP.NET route matching is case-insensitive, and mapping both spellings makes
    /// every request to either one an ambiguous match. Serving the rest costs four routes and
    /// removes a whole class of "the card is right there and the client still cannot see it"
    /// failures.
    /// </summary>
    public static readonly string[] WellKnownAgentCardFileNames =
    {
        "agent-card.json",
        "agent.json",
        "agentcard.json",
        "agent_card.json",
    };

    /// <summary>
    /// Path segments a card is served under, relative to an agent's base path, so that a client
    /// which appends <c>/.well-known/...</c> to whatever URL it was configured with finds it.
    /// Copilot Studio is configured with the message endpoint and appends to it verbatim rather
    /// than resolving against the agent's base, so the method segments are included.
    /// </summary>
    public static readonly string[] AgentCardBaseSegments =
    {
        "",
        "/v1",
        "/v1/message:stream",
        "/v1/message:send",
    };

    // Keys stamped onto AgentMessage.Args so FabrCore agents and plugins can see the A2A context
    // the request arrived with.

    /// <summary>A2A conversation identifier grouping related turns.</summary>
    public const string ArgContextId = "A2A:ContextId";

    /// <summary>Identifier of the A2A task this turn belongs to.</summary>
    public const string ArgTaskId = "A2A:TaskId";

    /// <summary>Identifier of the inbound A2A message.</summary>
    public const string ArgMessageId = "A2A:MessageId";

    /// <summary>Route name of the exposed agent that was called.</summary>
    public const string ArgAgentName = "A2A:AgentName";

    /// <summary>Caller identity as resolved by authentication (API key name or token subject).</summary>
    public const string ArgCaller = "A2A:Caller";

    /// <summary>Raw JSON of the inbound message's <c>metadata</c> object, when the caller sent one.</summary>
    public const string ArgMetadata = "A2A:Metadata";

    /// <summary>JSON array of the inbound message's non-text parts, when the caller sent any.</summary>
    public const string ArgNonTextParts = "A2A:NonTextParts";
}
