using System.Text.Json.Serialization;

using FabrCore.Host.Configuration;
namespace FabrCore.Host.A2A.Protocol;

/// <summary>
/// The discovery document an A2A client fetches to learn what an agent can do and how to reach it.
/// Served from <c>/.well-known/agent-card.json</c>, <c>/.well-known/agent.json</c>, and
/// <c>{base}/v1/card</c>.
/// </summary>
public sealed class A2AAgentCard
{
    /// <summary>A2A specification version this card conforms to.</summary>
    public string ProtocolVersion { get; set; } = A2AProtocol.Version;

    public string Name { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;

    /// <summary>Absolute URL of the agent's preferred transport endpoint.</summary>
    public string Url { get; set; } = string.Empty;

    /// <summary>One of <c>JSONRPC</c>, <c>GRPC</c>, <c>HTTP+JSON</c>.</summary>
    public string PreferredTransport { get; set; } = A2ATransports.JsonRpc;

    /// <summary>Other transports the same agent is reachable on.</summary>
    public List<A2AAgentInterface>? AdditionalInterfaces { get; set; }

    public A2AAgentProvider? Provider { get; set; }

    /// <summary>Version of the agent itself (not of the protocol).</summary>
    public string Version { get; set; } = "1.0.0";

    public string? DocumentationUrl { get; set; }
    public string? IconUrl { get; set; }

    public A2AAgentCapabilities Capabilities { get; set; } = new();

    /// <summary>Security schemes by name, using the OpenAPI 3 security scheme shape.</summary>
    public Dictionary<string, A2ASecurityScheme>? SecuritySchemes { get; set; }

    /// <summary>Which of <see cref="SecuritySchemes"/> apply, as OpenAPI security requirements.</summary>
    public List<Dictionary<string, List<string>>>? Security { get; set; }

    public List<string> DefaultInputModes { get; set; } = new() { "text/plain" };
    public List<string> DefaultOutputModes { get; set; } = new() { "text/plain" };

    public List<A2AAgentSkill> Skills { get; set; } = new();

    public bool SupportsAuthenticatedExtendedCard { get; set; }
}

/// <summary>An additional transport binding for the same agent.</summary>
public sealed class A2AAgentInterface
{
    public string Url { get; set; } = string.Empty;
    public string Transport { get; set; } = A2ATransports.JsonRpc;
}

/// <summary>Transport identifiers used by <see cref="A2AAgentCard.PreferredTransport"/>.</summary>
public static class A2ATransports
{
    public const string JsonRpc = "JSONRPC";
    public const string HttpJson = "HTTP+JSON";
    public const string Grpc = "GRPC";
}

/// <summary>Who publishes the agent.</summary>
public sealed class A2AAgentProvider
{
    public string Organization { get; set; } = string.Empty;
    public string Url { get; set; } = string.Empty;
}

/// <summary>Optional protocol features the agent implements.</summary>
public sealed class A2AAgentCapabilities
{
    public bool Streaming { get; set; } = true;
    public bool PushNotifications { get; set; }
    public bool StateTransitionHistory { get; set; }
    public List<A2AAgentExtension>? Extensions { get; set; }
}

/// <summary>A protocol extension the agent understands.</summary>
public sealed class A2AAgentExtension
{
    public string Uri { get; set; } = string.Empty;
    public string? Description { get; set; }
    public bool Required { get; set; }
}

/// <summary>
/// A capability advertised on the card. Orchestrators such as Copilot Studio read the name,
/// description, and tags to decide when to route work to this agent.
/// </summary>
public sealed class A2AAgentSkill
{
    public string Id { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public List<string> Tags { get; set; } = new();
    public List<string>? Examples { get; set; }
    public List<string>? InputModes { get; set; }
    public List<string>? OutputModes { get; set; }
}

/// <summary>
/// OpenAPI-shaped security scheme. Only the members A2A uses are modelled; unset members are
/// omitted so a single class can express the <c>apiKey</c>, <c>http</c>, and <c>oauth2</c> variants.
/// </summary>
public sealed class A2ASecurityScheme
{
    [JsonPropertyName("type")]
    public string Type { get; set; } = "http";

    public string? Description { get; set; }

    /// <summary><c>apiKey</c>: the header, query, or cookie name.</summary>
    public string? Name { get; set; }

    /// <summary><c>apiKey</c>: <c>header</c>, <c>query</c>, or <c>cookie</c>.</summary>
    [JsonPropertyName("in")]
    public string? In { get; set; }

    /// <summary><c>http</c>: the HTTP auth scheme, e.g. <c>bearer</c>.</summary>
    public string? Scheme { get; set; }

    /// <summary><c>http</c>: hint for the bearer token format, e.g. <c>JWT</c>.</summary>
    public string? BearerFormat { get; set; }

    /// <summary><c>oauth2</c>: the supported flows.</summary>
    public A2AOAuthFlows? Flows { get; set; }

    /// <summary><c>oauth2</c>: authorization server metadata document.</summary>
    public string? Oauth2MetadataUrl { get; set; }

    /// <summary><c>openIdConnect</c>: discovery document.</summary>
    public string? OpenIdConnectUrl { get; set; }
}

/// <summary>OAuth 2.0 flows advertised by an <c>oauth2</c> security scheme.</summary>
public sealed class A2AOAuthFlows
{
    public A2AOAuthFlow? ClientCredentials { get; set; }
    public A2AOAuthFlow? AuthorizationCode { get; set; }
}

/// <summary>A single OAuth 2.0 flow definition.</summary>
public sealed class A2AOAuthFlow
{
    public string? AuthorizationUrl { get; set; }
    public string? TokenUrl { get; set; }
    public string? RefreshUrl { get; set; }
    public Dictionary<string, string> Scopes { get; set; } = new();
}
