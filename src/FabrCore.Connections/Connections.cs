using System.Net.Http.Json;
using System.Text.Json.Serialization;

namespace FabrCore.Connections;

public enum ConnectionAuthentication { AuthorizationCode, ClientCredentials, AgentIdOnBehalfOf, AgentIdApplication, OnBehalfOf }

/// <summary>Administrative metadata only. Secret values must never appear here.</summary>
public sealed class ConnectionProfile
{
    public string Name { get; set; } = "";
    public string Provider { get; set; } = "microsoft";
    public bool Enabled { get; set; }
    public ConnectionAuthentication Authentication { get; set; }
    public string Authority { get; set; } = "";
    public string ClientId { get; set; } = "";
    public string? CredentialReference { get; set; }
    public string? AgentIdentityClientId { get; set; }
    public string? BlueprintAudience { get; set; }
    public List<string> SignInScopes { get; set; } = [];
    public List<string> RedirectUris { get; set; } = [];
    public List<string> AllowedAgents { get; set; } = [];
    public Dictionary<string, ConnectionResource> Resources { get; set; } = new(StringComparer.Ordinal);
}

public sealed class ConnectionResource
{
    public string BaseUrl { get; set; } = "";
    public List<string> Scopes { get; set; } = [];
}

public sealed record ConnectionBinding(string OwnerPrincipal, string Name);
public sealed record ConnectionStatus(string Name, bool Enabled, string State, string Revision, string? ExternalSubject = null);
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
}

/// <summary>Host service. Principal and agent arguments must come from trusted host context.</summary>
public interface IConnectionService
{
    Task<string> GetSessionVersionAsync(string principal, string agent, ConnectionBinding binding, CancellationToken cancellationToken = default);
    Task<ConnectionAccessToken> GetAccessTokenAsync(string principal, string agent, ConnectionBinding binding, string resource, CancellationToken cancellationToken = default);
    Task<HttpClient> GetHttpClientAsync(string principal, string agent, ConnectionBinding binding, string resource, CancellationToken cancellationToken = default);
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
