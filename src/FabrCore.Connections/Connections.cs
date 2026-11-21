using System.Net.Http.Json;
using System.Text.Json.Serialization;

namespace FabrCore.Connections;

public enum ConnectionAuthentication { AuthorizationCode, ClientCredentials, AgentIdOnBehalfOf, AgentIdApplication, OnBehalfOf }

/// <summary>Administrative metadata only. Secret values must never appear here.</summary>
public sealed class ConnectionProfile
{
    public string Name { get; set; } = "";
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public ManagedAgentIdentity? ManagedIdentity { get; set; }
    public string Provider { get; set; } = "microsoft";
    public bool Enabled { get; set; }
    public ConnectionAuthentication Authentication { get; set; }
    public string Authority { get; set; } = "";
    public string ClientId { get; set; } = "";
    public string? CredentialReference { get; set; }
    public string? AgentIdentityClientId { get; set; }
    public string? BlueprintAudience { get; set; }
    /// <summary>Token audiences a user assertion may also have, such as a bot's <c>api://botid-{appId}</c>.</summary>
    public List<string> AssertionAudiences { get; set; } = [];
    /// <summary>Token issuers a user assertion may also have, such as Entra's v1 issuer, beside the authority's own.</summary>
    public List<string> AssertionIssuers { get; set; } = [];
    public List<string> SignInScopes { get; set; } = [];
    public List<string> RedirectUris { get; set; } = [];
    public List<string> AllowedAgents { get; set; } = [];
    /// <summary>
    /// Agent types every instance of which may use this application connection, whoever owns the instance, beside the exact
    /// handles in <see cref="AllowedAgents"/>. Only for <see cref="ConnectionAuthentication.ClientCredentials"/> and
    /// <see cref="ConnectionAuthentication.AgentIdApplication"/>. The type comes from the host, so who may create an agent of
    /// a type is the host's access control.
    /// </summary>
    public List<string> AllowedAgentTypes { get; set; } = [];
    public Dictionary<string, ConnectionResource> Resources { get; set; } = new(StringComparer.Ordinal);
}

/// <summary>Administrative link to a directory child and the exact Host registrations allowed to act as it.</summary>
public sealed record ManagedAgentIdentity(Guid BindingId, Guid TenantId, Guid ObjectId, bool Shared,
    Dictionary<string, Guid> Members, string ManagementUrl);

public sealed class ConnectionResource
{
    public string BaseUrl { get; set; } = "";
    public List<string> Scopes { get; set; } = [];
}

public sealed record ConnectionBinding(string OwnerPrincipal, string Name);

/// <summary>The agent asking for a connection, taken from trusted host context: never from a prompt or a message.</summary>
public sealed record ConnectionCaller(string Principal, string Agent, string? AgentType = null);

/// <summary>
/// Host service, optional: the connection an agent gets for a name its arguments don't bind. Return null to fall back to the
/// agent's own principal's connection of that name. The connection's own allowed agents and agent types still decide.
/// </summary>
public interface IConnectionBindingResolver
{
    ConnectionBinding? Resolve(ConnectionCaller caller, string connection);
}
public sealed record ConnectionStatus(string Name, bool Enabled, string State, string Revision, string? ExternalSubject = null)
{
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public ManagedAgentIdentity? ManagedIdentity { get; init; }
}
public sealed record ConnectionTransaction(string State, string AuthorizationUrl, DateTimeOffset ExpiresAt);
public sealed record BeginConnectionRequest(string Connection, string RedirectUri);
public sealed record CompleteConnectionRequest(string Connection, string State, string? AuthorizationCode, string? ProviderError = null)
{
    public override string ToString() => "[protected authorization completion]";
}
public sealed record ConnectionAssertionRequest(string Connection, string Assertion)
{
    public override string ToString() => "[protected user assertion]";
}
public sealed record ConnectionCapabilities(bool EntraAgentIdEnabled, bool ClientHandoffEnabled);

/// <summary>For trusted application code only. Never return this object from an AI tool.</summary>
public sealed class ConnectionAccessToken(string accessToken, DateTimeOffset expiresAt)
{
    [JsonIgnore] public string AccessToken { get; } = accessToken;
    public DateTimeOffset ExpiresAt { get; } = expiresAt;
    public override string ToString() => "[protected access token]";
}

public sealed class ConnectionException(string code, string message) : Exception(message)
{
    public string Code { get; } = code;

    /// <summary>
    /// What to tell the person, set once when their connection lapses. The agent that hit it sends it to them, so a background
    /// agent doesn't fail quietly.
    /// </summary>
    public string? Notice { get; init; }
}

/// <summary>Host service. Principal and agent arguments must come from trusted host context.</summary>
public interface IConnectionService
{
    Task<string> GetSessionVersionAsync(string principal, string agent, ConnectionBinding binding, CancellationToken cancellationToken = default);
    Task<ConnectionAccessToken> GetAccessTokenAsync(string principal, string agent, ConnectionBinding binding, string resource, CancellationToken cancellationToken = default);
    Task<HttpClient> GetHttpClientAsync(string principal, string agent, ConnectionBinding binding, string resource, CancellationToken cancellationToken = default);

    /// <summary>
    /// As <see cref="GetHttpClientAsync(string, string, ConnectionBinding, string, CancellationToken)"/>, calling
    /// <paramref name="notify"/> with a lapse's <see cref="ConnectionException.Notice"/>. The client fetches its token as it
    /// sends each request, which is where a lapse shows up.
    /// </summary>
    Task<HttpClient> GetHttpClientAsync(string principal, string agent, ConnectionBinding binding, string resource, Func<string, Task> notify, CancellationToken cancellationToken = default)
        => GetHttpClientAsync(principal, agent, binding, resource, cancellationToken);

    /// <summary>As the principal-and-agent overload, also passing the caller's agent type for <see cref="ConnectionProfile.AllowedAgentTypes"/>.</summary>
    Task<string> GetSessionVersionAsync(ConnectionCaller caller, ConnectionBinding binding, CancellationToken cancellationToken = default)
        => GetSessionVersionAsync(caller.Principal, caller.Agent, binding, cancellationToken);

    /// <summary>As the principal-and-agent overload, also passing the caller's agent type for <see cref="ConnectionProfile.AllowedAgentTypes"/>.</summary>
    Task<ConnectionAccessToken> GetAccessTokenAsync(ConnectionCaller caller, ConnectionBinding binding, string resource, CancellationToken cancellationToken = default)
        => GetAccessTokenAsync(caller.Principal, caller.Agent, binding, resource, cancellationToken);

    /// <summary>As the principal-and-agent overload, also passing the caller's agent type for <see cref="ConnectionProfile.AllowedAgentTypes"/>.</summary>
    Task<HttpClient> GetHttpClientAsync(ConnectionCaller caller, ConnectionBinding binding, string resource, Func<string, Task> notify, CancellationToken cancellationToken = default)
        => GetHttpClientAsync(caller.Principal, caller.Agent, binding, resource, notify, cancellationToken);
}

/// <summary>
/// Host service for a channel that signed a person in. Hands their token to the on-behalf-of connections this agent may use, so
/// the agent never sees it. A connection keeps a token that isn't close to expiring, since replacing one clears its cached tokens.
/// </summary>
public interface IConnectionUserAssertions
{
    Task SubmitAsync(string principal, string agent, string assertion, CancellationToken cancellationToken = default);
}

/// <summary>Agent-bound facade; does not permit model-selected principal or actor overrides.</summary>
public interface IAgentConnections
{
    Task<string> GetSessionVersionAsync(string connection, CancellationToken cancellationToken = default);
    Task<ConnectionAccessToken> GetAccessTokenAsync(string connection, string resource, CancellationToken cancellationToken = default);
    Task<HttpClient> GetHttpClientAsync(string connection, string resource, CancellationToken cancellationToken = default);
}

public sealed class FabrCoreConnectionsClient(HttpClient http)
{
    private const string Root = "fabrcoreapi/connections/v1/";
    public Task<ConnectionStatus[]> ListAsync(CancellationToken cancellationToken = default)
        => Read<ConnectionStatus[]>(HttpMethod.Get, "", null, cancellationToken);
    public Task<ConnectionTransaction> BeginAsync(BeginConnectionRequest request, CancellationToken cancellationToken = default)
        => Read<ConnectionTransaction>(HttpMethod.Post, "begin", request, cancellationToken);
    public Task<ConnectionStatus> CompleteAsync(CompleteConnectionRequest request, CancellationToken cancellationToken = default)
        => Read<ConnectionStatus>(HttpMethod.Post, "complete", request, cancellationToken);
    public Task<ConnectionStatus> ConnectAssertionAsync(ConnectionAssertionRequest request, CancellationToken cancellationToken = default)
        => Read<ConnectionStatus>(HttpMethod.Post, "assertion", request, cancellationToken);
    public Task<ConnectionStatus> DisconnectAsync(string connection, CancellationToken cancellationToken = default)
        => Read<ConnectionStatus>(HttpMethod.Delete, Uri.EscapeDataString(connection), null, cancellationToken);
    private async Task<T> Read<T>(HttpMethod method, string path, object? body, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(method, Root + path);
        if (body is not null) request.Content = JsonContent.Create(body);
        using var response = await http.SendAsync(request, ct);
        return await ConnectionResponse.Read<T>(response, ct);
    }
}
