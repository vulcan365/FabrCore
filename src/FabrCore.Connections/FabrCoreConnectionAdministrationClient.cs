using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace FabrCore.Connections;

public sealed record ConnectionProfileSnapshot(ConnectionProfile Profile, string Revision);

/// <summary>Uses an authenticated administrator transport. User authorization is accepted only as a client-encrypted handoff.</summary>
public sealed class FabrCoreConnectionAdministrationClient(HttpClient http)
{
    public Task<ConnectionStatus[]> ListAsync(string principal, CancellationToken cancellationToken = default)
        => Send<ConnectionStatus[]>(HttpMethod.Get, Path(principal), null, null, cancellationToken);
    public Task<ConnectionProfileSnapshot> GetAsync(string principal, string name, CancellationToken cancellationToken = default)
        => Send<ConnectionProfileSnapshot>(HttpMethod.Get, Path(principal, name), null, null, cancellationToken);
    public Task<ConnectionStatus> SaveAsync(string principal, ConnectionProfile profile, string revision, CancellationToken cancellationToken = default)
        => Send<ConnectionStatus>(HttpMethod.Put, Path(principal, profile.Name), profile, revision, cancellationToken);
    public Task<ConnectionStatus> DisconnectAsync(string principal, string name, CancellationToken cancellationToken = default)
        => Send<ConnectionStatus>(HttpMethod.Delete, Path(principal, name) + "/authorization", null, null, cancellationToken);
    public Task<ConnectionHandoffChallenge> CreateHandoffAsync(string principal, string name, CancellationToken cancellationToken = default)
        => Send<ConnectionHandoffChallenge>(HttpMethod.Post, Path(principal, name) + "/handoff", null, null, cancellationToken);
    public Task<JsonElement> CompleteHandoffAsync(string principal, string name, ConnectionHandoffEnvelope envelope, CancellationToken cancellationToken = default)
        => Send<JsonElement>(HttpMethod.Post, Path(principal, name) + "/handoff/complete", envelope, null, cancellationToken);
    private static string Path(string principal, string? name = null) => "fabrcoreapi/admin/v1/principals/" + Uri.EscapeDataString(principal) + "/connections" + (name is null ? "" : "/" + Uri.EscapeDataString(name));
    private async Task<T> Send<T>(HttpMethod method, string path, object? body, string? revision, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(method, path);
        if (body is not null) request.Content = JsonContent.Create(body);
        if (revision is not null) request.Headers.TryAddWithoutValidation("If-Match", revision == "*" ? "*" : $"\"{revision}\"");
        using var response = await http.SendAsync(request, ct);
        if (response.StatusCode == HttpStatusCode.PreconditionFailed) throw new ConnectionException("revision-conflict", "Reload the connection before saving.");
        return await ConnectionResponse.Read<T>(response, ct);
    }
}
