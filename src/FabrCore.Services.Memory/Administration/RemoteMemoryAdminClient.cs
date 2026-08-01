using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using FabrCore.Services.Memory.Administration.Models;
using FabrCore.Services.Memory.Audit;
using FabrCore.Services.Memory.Configuration;
using FabrCore.Services.Memory.Models;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace FabrCore.Services.Memory.Administration;

public sealed class RemoteMemoryAdminClient(
    HttpClient httpClient,
    IOptions<MemoryAdminClientOptions> options,
    IMemoryAdminPrincipalAccessor principalAccessor,
    ILogger<RemoteMemoryAdminClient> logger) : IMemoryAdminClient
{
    private const string ApiPath = "/fabrcoreapi/memory/admin/v1";
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public async Task<MemoryAdminCapability> GetCapabilityAsync(CancellationToken ct = default)
    {
        try
        {
            return await GetAsync<MemoryAdminCapability>("capabilities", ct);
        }
        catch (MemoryAdminClientException ex)
        {
            return new MemoryAdminCapability
            {
                Availability = ex.Availability,
                Message = ex.Message
            };
        }
    }

    public Task<AdminMemoryDashboardStats> GetDashboardStatsAsync(CancellationToken ct = default) =>
        GetAsync<AdminMemoryDashboardStats>("dashboard", ct);

    public async Task<IReadOnlyList<AdminMemoryScopeDto>> ListScopesAsync(CancellationToken ct = default) =>
        await GetAsync<List<AdminMemoryScopeDto>>("scopes", ct);

    public Task<AdminMemoryScopeDto> CreateSharedScopeAsync(
        string scopeKey, string? description, string? actorId = null, CancellationToken ct = default) =>
        SendAsync<AdminMemoryScopeDto>(
            HttpMethod.Post, $"scopes/{Escape(scopeKey)}", new MemoryScopeCreateRequest(description), ct);

    public Task<AdminScopeDeleteResult> DeleteScopeAsync(
        string scopeKey, string? actorId = null, CancellationToken ct = default) =>
        SendAsync<AdminScopeDeleteResult>(HttpMethod.Delete, $"scopes/{Escape(scopeKey)}", null, ct);

    public async Task<IReadOnlyList<AdminMemoryDto>> ListMemoriesAsync(
        string scopeKey,
        MemoryType? typeFilter = null,
        MemoryTemperature? temperatureFilter = null,
        string? searchTerm = null,
        int page = 1,
        int pageSize = 25,
        CancellationToken ct = default) =>
        await GetAsync<List<AdminMemoryDto>>(
            $"memories{Query(("scope", scopeKey), ("type", typeFilter), ("temperature", temperatureFilter), ("search", searchTerm), ("page", page), ("pageSize", pageSize))}",
            ct);

    public Task<int> CountMemoriesAsync(
        string scopeKey,
        MemoryType? typeFilter = null,
        MemoryTemperature? temperatureFilter = null,
        string? searchTerm = null,
        CancellationToken ct = default) =>
        GetAsync<int>(
            $"memories/count{Query(("scope", scopeKey), ("type", typeFilter), ("temperature", temperatureFilter), ("search", searchTerm))}",
            ct);

    public Task<AdminMemoryDetailDto?> GetMemoryAsync(Guid memoryId, CancellationToken ct = default) =>
        GetNullableAsync<AdminMemoryDetailDto>($"memories/{memoryId:D}", ct);

    public Task<AdminMemoryDto> CreateMemoryAsync(
        string scopeKey,
        string title,
        MemoryType type,
        string content,
        string? description = null,
        MemoryTemperature temperature = MemoryTemperature.Warm,
        bool isPointInTime = false,
        Dictionary<string, string>? metadata = null,
        string? actorId = null,
        CancellationToken ct = default) =>
        SendAsync<AdminMemoryDto>(
            HttpMethod.Post,
            $"scopes/{Escape(scopeKey)}/memories",
            new MemoryCreateRequest(title, type, content, description, temperature, isPointInTime, metadata),
            ct);

    public Task<AdminMemoryDetailDto> UpdateMemoryAsync(
        Guid memoryId,
        string title,
        MemoryType type,
        string content,
        string? description,
        MemoryTemperature temperature,
        string? actorId = null,
        CancellationToken ct = default) =>
        SendAsync<AdminMemoryDetailDto>(
            HttpMethod.Put,
            $"memories/{memoryId:D}",
            new MemoryUpdateRequest(title, type, content, description, temperature),
            ct);

    public Task<bool> DeleteMemoryAsync(
        Guid memoryId, string? actorId = null, CancellationToken ct = default) =>
        SendAsync<bool>(HttpMethod.Delete, $"memories/{memoryId:D}", null, ct);

    public Task<MemoryConsolidationResult> ConsolidateScopeAsync(
        string scopeKey, string? actorId = null, CancellationToken ct = default) =>
        SendAsync<MemoryConsolidationResult>(
            HttpMethod.Post, $"scopes/{Escape(scopeKey)}/consolidate", null, ct);

    public async Task<IReadOnlyList<MemoryAuditEntry>> ListAuditEntriesAsync(
        string? scopeKey = null,
        int page = 1,
        int pageSize = 50,
        CancellationToken ct = default) =>
        await GetAsync<List<MemoryAuditEntry>>(
            $"audit{Query(("scope", scopeKey), ("page", page), ("pageSize", pageSize))}",
            ct);

    private Task<T> GetAsync<T>(string path, CancellationToken ct) =>
        SendAsync<T>(HttpMethod.Get, path, null, ct);

    private async Task<T?> GetNullableAsync<T>(string path, CancellationToken ct) where T : class
    {
        using var request = await CreateRequestAsync(HttpMethod.Get, path, ct);
        using var response = await SendCoreAsync(request, ct);
        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            return null;
        }

        await ThrowForFailureAsync(response, path == "capabilities", ct);
        return await response.Content.ReadFromJsonAsync<T>(JsonOptions, ct);
    }

    private async Task<T> SendAsync<T>(
        HttpMethod method, string path, object? body, CancellationToken ct)
    {
        using var request = await CreateRequestAsync(method, path, ct);
        if (body is not null)
        {
            request.Content = JsonContent.Create(body, options: JsonOptions);
        }

        using var response = await SendCoreAsync(request, ct);
        await ThrowForFailureAsync(response, path == "capabilities", ct);
        return await response.Content.ReadFromJsonAsync<T>(JsonOptions, ct)
            ?? throw new MemoryAdminClientException(
                "The Memory host returned an empty response.",
                MemoryAdminAvailability.Unavailable,
                MemoryAdminProblemCodes.Unavailable,
                response.StatusCode);
    }

    private async Task<HttpRequestMessage> CreateRequestAsync(
        HttpMethod method, string path, CancellationToken ct)
    {
        var principal = await principalAccessor.GetPrincipalIdAsync(ct);
        if (string.IsNullOrWhiteSpace(principal))
        {
            throw new MemoryAdminClientException(
                "The current administration principal could not be resolved.",
                MemoryAdminAvailability.Unauthorized,
                MemoryAdminProblemCodes.Unauthorized,
                HttpStatusCode.Unauthorized);
        }

        var request = new HttpRequestMessage(method, $"{BuildBaseUrl()}/{path}");
        if (string.IsNullOrWhiteSpace(options.Value.ApiKey))
        {
            throw new MemoryAdminClientException(
                "MemoryAdminClientOptions.ApiKey is not configured.",
                MemoryAdminAvailability.Unauthorized,
                MemoryAdminProblemCodes.Unauthorized);
        }

        request.Headers.Authorization =
            new AuthenticationHeaderValue("Bearer", options.Value.ApiKey);
        request.Headers.TryAddWithoutValidation("x-user-handle", principal.Trim());
        return request;
    }

    private async Task<HttpResponseMessage> SendCoreAsync(
        HttpRequestMessage request, CancellationToken ct)
    {
        try
        {
            return await httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
        }
        catch (OperationCanceledException ex) when (!ct.IsCancellationRequested)
        {
            throw new MemoryAdminClientException(
                "The Memory host did not respond before the request timed out.",
                MemoryAdminAvailability.Unreachable,
                MemoryAdminProblemCodes.Unreachable,
                innerException: ex);
        }
        catch (HttpRequestException ex)
        {
            logger.LogWarning(ex, "The remote Memory administration host is unreachable.");
            throw new MemoryAdminClientException(
                "The remote Memory administration host is unreachable.",
                MemoryAdminAvailability.Unreachable,
                MemoryAdminProblemCodes.Unreachable,
                innerException: ex);
        }
    }

    private static async Task ThrowForFailureAsync(
        HttpResponseMessage response, bool capabilityRequest, CancellationToken ct)
    {
        if (response.IsSuccessStatusCode)
        {
            return;
        }

        var body = await response.Content.ReadAsStringAsync(ct);
        var code = ReadProblemValue(body, "code") ?? response.StatusCode switch
        {
            HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden => MemoryAdminProblemCodes.Unauthorized,
            HttpStatusCode.NotFound when capabilityRequest => MemoryAdminProblemCodes.Unregistered,
            HttpStatusCode.Conflict => MemoryAdminProblemCodes.Conflict,
            HttpStatusCode.BadRequest => MemoryAdminProblemCodes.ValidationFailed,
            _ => MemoryAdminProblemCodes.Unavailable
        };
        var availability = response.StatusCode switch
        {
            HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden => MemoryAdminAvailability.Unauthorized,
            HttpStatusCode.NotFound when capabilityRequest => MemoryAdminAvailability.Unregistered,
            _ => MemoryAdminAvailability.Unavailable
        };
        var message =
            ReadProblemValue(body, "detail")
            ?? ReadProblemValue(body, "title")
            ?? $"Memory administration request failed with HTTP {(int)response.StatusCode}.";
        throw new MemoryAdminClientException(message, availability, code, response.StatusCode);
    }

    private string BuildBaseUrl()
    {
        var baseAddress = options.Value.BaseAddress;
        if (string.IsNullOrWhiteSpace(baseAddress))
        {
            throw new MemoryAdminClientException(
                "MemoryAdminClientOptions.BaseAddress is not configured.",
                MemoryAdminAvailability.Unreachable,
                MemoryAdminProblemCodes.Unreachable);
        }

        return $"{baseAddress.TrimEnd('/')}{ApiPath}";
    }

    private static string? ReadProblemValue(string json, string name)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return null;
        }

        try
        {
            using var document = JsonDocument.Parse(json);
            foreach (var property in document.RootElement.EnumerateObject())
            {
                if (property.Name.Equals(name, StringComparison.OrdinalIgnoreCase)
                    && property.Value.ValueKind == JsonValueKind.String)
                {
                    return property.Value.GetString();
                }
            }
        }
        catch (JsonException)
        {
        }

        return null;
    }

    private static string Escape(string value) => Uri.EscapeDataString(value);

    private static string Query(params (string Name, object? Value)[] values)
    {
        var parts = values
            .Where(item => item.Value is not null && !string.IsNullOrWhiteSpace(item.Value.ToString()))
            .Select(item => $"{Escape(item.Name)}={Escape(item.Value!.ToString()!)}");
        var query = string.Join("&", parts);
        return query.Length == 0 ? string.Empty : $"?{query}";
    }
}
