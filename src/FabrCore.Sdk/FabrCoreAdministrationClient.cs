using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using FabrCore.Core;
using FabrCore.Core.Blueprints;
using FabrCore.Core.CloudServer;
using FabrCore.Core.Monitoring;

namespace FabrCore.Sdk;

/// <summary>Vendor-neutral management client. Supply an authenticated HttpClient or a connect-channel message handler.</summary>
public sealed class FabrCoreAdministrationClient(HttpClient http)
{
    private static readonly JsonSerializerOptions Json = CreateJson();
    private static JsonSerializerOptions CreateJson() { var o = new JsonSerializerOptions(JsonSerializerDefaults.Web); o.Converters.Add(new JsonStringEnumConverter()); return o; }
    private static string E(string value) => Uri.EscapeDataString(value);
    private static string Principal(string principal) => "fabrcoreapi/admin/v1/principals/" + E(principal);
    private static string Agent(string principal, string agent) => Principal(principal) + "/agents/" + E(agent);

    public Task<JsonElement> GetCapabilitiesAsync(CancellationToken ct = default) => SendAsync<JsonElement>(HttpMethod.Get, "fabrcoreapi/admin/v1/capabilities", ct: ct);
    public Task<List<AgentInfo>> GetPrincipalsAsync(CancellationToken ct = default) => SendAsync<List<AgentInfo>>(HttpMethod.Get, "fabrcoreapi/admin/v1/runtime/principals", ct: ct);
    public Task<AdministrationPage<PrincipalSummary>> GetPrincipalPageAsync(int offset = 0, string? revision = null, int limit = 100, CancellationToken ct = default) =>
        SendAsync<AdministrationPage<PrincipalSummary>>(HttpMethod.Get, $"fabrcoreapi/admin/v1/principals?offset={offset}&limit={limit}" + (revision is null ? "" : "&revision=" + E(revision)), ct: ct);
    public Task<List<AgentInfo>> GetAgentsAsync(string principal, CancellationToken ct = default) => SendAsync<List<AgentInfo>>(HttpMethod.Get, $"fabrcoreapi/admin/v1/runtime/principals/{E(principal)}/agents", ct: ct);
    public Task<AgentManagementSnapshot> GetAgentAsync(string principal, string agent, CancellationToken ct = default) => SendAsync<AgentManagementSnapshot>(HttpMethod.Get, Agent(principal, agent) + "/configuration", ct: ct);
    public Task<JsonElement> ManageAgentAsync(string principal, string agent, string action, AgentManagementRequest request, CancellationToken ct = default) => SendAsync<JsonElement>(HttpMethod.Post, Agent(principal, agent) + "/actions/" + E(action), request, ct: ct);
    public Task<AdministrationOperation> StartOperationAsync(string principal, string operationId, AdministrationOperationRequest request, CancellationToken ct = default) => SendAsync<AdministrationOperation>(HttpMethod.Put, Principal(principal) + "/operations/" + E(operationId), request, ct: ct);
    public Task<AdministrationOperation> GetOperationAsync(string principal, string operationId, CancellationToken ct = default) => SendAsync<AdministrationOperation>(HttpMethod.Get, Principal(principal) + "/operations/" + E(operationId), ct: ct);

    public Task<List<BlueprintSummary>> GetBlueprintsAsync(string principal, CancellationToken ct = default) => SendAsync<List<BlueprintSummary>>(HttpMethod.Get, Principal(principal) + "/blueprint-management", ct: ct);
    public Task<AdministrationPage<BlueprintSummary>> GetBlueprintPageAsync(string principal, int offset = 0, string? revision = null, int limit = 100, CancellationToken ct = default) =>
        SendAsync<AdministrationPage<BlueprintSummary>>(HttpMethod.Get, Principal(principal) + $"/blueprint-management/summaries?offset={offset}&limit={limit}" + (revision is null ? "" : "&revision=" + E(revision)), ct: ct);
    public Task<FabrCoreBlueprint> GetBlueprintAsync(string principal, string name, CancellationToken ct = default) => SendAsync<FabrCoreBlueprint>(HttpMethod.Get, Principal(principal) + "/blueprints/" + E(name), ct: ct);
    public Task<JsonElement> SaveBlueprintAsync(string principal, string name, FabrCoreBlueprint blueprint, string revision, CancellationToken ct = default) => SendAsync<JsonElement>(HttpMethod.Put, Principal(principal) + "/blueprint-management/" + E(name), blueprint, revision, ct);
    public Task<BlueprintPreview> PreviewBlueprintAsync(string principal, string name, CancellationToken ct = default) => SendAsync<BlueprintPreview>(HttpMethod.Get, Principal(principal) + "/blueprint-management/" + E(name) + "/preview", ct: ct);
    public Task<BlueprintPreview> ValidateBlueprintAsync(string principal, FabrCoreBlueprint blueprint, CancellationToken ct = default) => SendAsync<BlueprintPreview>(HttpMethod.Post, Principal(principal) + "/blueprint-management/validate", blueprint, ct: ct);

    public Task<AdminConversationSession> CreateAdminSessionAsync(string principal, string agent, AdminSessionRequest request, CancellationToken ct = default) => SendAsync<AdminConversationSession>(HttpMethod.Post, Agent(principal, agent) + "/admin-sessions", request, ct: ct);
    public Task<List<AdminConversationSession>> ListAdminSessionsAsync(string principal, string agent, CancellationToken ct = default) => SendAsync<List<AdminConversationSession>>(HttpMethod.Get, Agent(principal, agent) + "/admin-sessions", ct: ct);
    public Task<AdminConversationSession> GetAdminSessionAsync(string principal, string agent, string sessionId, CancellationToken ct = default) => SendAsync<AdminConversationSession>(HttpMethod.Get, Agent(principal, agent) + "/admin-sessions/" + E(sessionId), ct: ct);
    public Task<AdminConversationTurn> SubmitAdminTurnAsync(string principal, string agent, string sessionId, AdminTurnRequest request, CancellationToken ct = default) => SendAsync<AdminConversationTurn>(HttpMethod.Post, Agent(principal, agent) + "/admin-sessions/" + E(sessionId) + "/turns", request, ct: ct);
    public Task<AdminConversationTurn> GetAdminTurnAsync(string principal, string agent, string sessionId, string turnId, CancellationToken ct = default) =>
        SendAsync<AdminConversationTurn>(HttpMethod.Get, Agent(principal, agent) + "/admin-sessions/" + E(sessionId) + "/turns/" + E(turnId), ct: ct);
    public Task<JsonElement> DeleteAdminSessionAsync(string principal, string agent, string sessionId, CancellationToken ct = default) => SendAsync<JsonElement>(HttpMethod.Delete, Agent(principal, agent) + "/admin-sessions/" + E(sessionId), ct: ct);

    public Task<AdministrationPage<T>> GetAclEntitiesAsync<T>(string kind, int offset = 0, long? version = null, int limit = 100, CancellationToken ct = default) =>
        SendAsync<AdministrationPage<T>>(HttpMethod.Get, $"fabrcoreapi/admin/v1/access/entities/{E(kind)}?offset={offset}&limit={limit}" + (version is null ? "" : "&version=" + version), ct: ct);
    public Task<JsonElement> PutAclEntityAsync<T>(string kind, string id, T value, long revision, CancellationToken ct = default) => SendAsync<JsonElement>(HttpMethod.Put, $"fabrcoreapi/admin/v1/access/entities/{E(kind)}/{E(id)}", value, revision.ToString(), ct);
    public Task<JsonElement> DeleteAclEntityAsync(string kind, string id, long revision, CancellationToken ct = default) => SendAsync<JsonElement>(HttpMethod.Delete, $"fabrcoreapi/admin/v1/access/entities/{E(kind)}/{E(id)}", revision: revision.ToString(), ct: ct);
    public Task<MonitorPage> QueryMonitorAsync(MonitorQuery query, CancellationToken ct = default)
    {
        var values = JsonSerializer.SerializeToElement(query, Json).EnumerateObject().Where(p => p.Value.ValueKind != JsonValueKind.Null)
            .Select(p => E(p.Name) + "=" + E(p.Value.ToString()));
        return SendAsync<MonitorPage>(HttpMethod.Get, "fabrcoreapi/admin/v1/observability/monitor?" + string.Join("&", values), ct: ct);
    }
    public Task<JsonElement> GetMonitorHealthAsync(CancellationToken ct = default) => SendAsync<JsonElement>(HttpMethod.Get, "fabrcoreapi/admin/v1/observability/monitor/health", ct: ct);
    public Task<AdministrationPage<string>> ListEvidenceTracesAsync(string? agentHandle = null, string? after = null, CancellationToken ct = default) =>
        SendAsync<AdministrationPage<string>>(HttpMethod.Get, "fabrcoreapi/admin/v1/observability/evidence/traces?limit=100" + (agentHandle is null ? "" : "&agentHandle=" + E(agentHandle)) + (after is null ? "" : "&after=" + E(after)), ct: ct);
    public Task<JsonElement> CreateEvidenceExportAsync(string traceId, CancellationToken ct = default) => SendAsync<JsonElement>(HttpMethod.Post, "fabrcoreapi/admin/v1/observability/evidence/" + E(traceId) + "/exports", ct: ct);
    public Task<JsonElement> GetEvidenceChunkAsync(string exportId, int chunk, CancellationToken ct = default) => SendAsync<JsonElement>(HttpMethod.Get, $"fabrcoreapi/admin/v1/observability/evidence/exports/{E(exportId)}/{chunk}", ct: ct);

    private async Task<T> SendAsync<T>(HttpMethod method, string path, object? body = null, string? revision = null, CancellationToken ct = default)
    {
        using var request = new HttpRequestMessage(method, path);
        if (body is not null) request.Content = JsonContent.Create(body, options: Json);
        if (revision is not null) request.Headers.TryAddWithoutValidation("If-Match", $"\"{revision}\"");
        using var response = await http.SendAsync(request, ct);
        if (!response.IsSuccessStatusCode) throw new FabrCoreAdministrationException(response.StatusCode, await response.Content.ReadAsStringAsync(ct));
        return (await response.Content.ReadFromJsonAsync<T>(Json, ct))!;
    }
}
public sealed class FabrCoreAdministrationException(HttpStatusCode statusCode, string responseBody) : Exception($"Administration request returned {(int)statusCode}: {responseBody}")
{
    public HttpStatusCode StatusCode { get; } = statusCode;
    public string ResponseBody { get; } = responseBody;
}
