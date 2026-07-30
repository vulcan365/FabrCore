using FabrCore.Services.Memory.Administration.Models;
using FabrCore.Services.Memory.Audit;
using FabrCore.Services.Memory.Configuration;
using FabrCore.Services.Memory.Models;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace FabrCore.Services.Memory.Administration;

internal sealed class MemoryAdminClientSelector(
    IServiceProvider services,
    IOptions<MemoryAdminClientOptions> options) : IMemoryAdminClient
{
    private IMemoryAdminClient? Local =>
        services.GetKeyedService<IMemoryAdminClient>(MemoryAdminClientKeys.Local);

    private RemoteMemoryAdminClient? Remote => services.GetService<RemoteMemoryAdminClient>();

    private IMemoryAdminClient Current => options.Value.Mode switch
    {
        MemoryAdminClientMode.Local => Local ?? throw MissingLocal(),
        MemoryAdminClientMode.Remote => Remote ?? throw MissingRemote(),
        _ => Local ?? Remote ?? throw MissingLocal()
    };

    public Task<MemoryAdminCapability> GetCapabilityAsync(CancellationToken ct = default)
    {
        try
        {
            return Current.GetCapabilityAsync(ct);
        }
        catch (MemoryAdminClientException ex)
        {
            return Task.FromResult(new MemoryAdminCapability
            {
                Availability = ex.Availability,
                Message = ex.Message
            });
        }
    }

    public Task<AdminMemoryDashboardStats> GetDashboardStatsAsync(CancellationToken ct = default) => Current.GetDashboardStatsAsync(ct);
    public Task<IReadOnlyList<AdminMemoryScopeDto>> ListScopesAsync(CancellationToken ct = default) => Current.ListScopesAsync(ct);
    public Task<AdminMemoryScopeDto> CreateSharedScopeAsync(string scopeKey, string? description, string? actorId = null, CancellationToken ct = default) => Current.CreateSharedScopeAsync(scopeKey, description, actorId, ct);
    public Task<AdminScopeDeleteResult> DeleteScopeAsync(string scopeKey, string? actorId = null, CancellationToken ct = default) => Current.DeleteScopeAsync(scopeKey, actorId, ct);
    public Task<IReadOnlyList<AdminMemoryDto>> ListMemoriesAsync(string scopeKey, MemoryType? typeFilter = null, MemoryTemperature? temperatureFilter = null, string? searchTerm = null, int page = 1, int pageSize = 25, CancellationToken ct = default) => Current.ListMemoriesAsync(scopeKey, typeFilter, temperatureFilter, searchTerm, page, pageSize, ct);
    public Task<int> CountMemoriesAsync(string scopeKey, MemoryType? typeFilter = null, MemoryTemperature? temperatureFilter = null, string? searchTerm = null, CancellationToken ct = default) => Current.CountMemoriesAsync(scopeKey, typeFilter, temperatureFilter, searchTerm, ct);
    public Task<AdminMemoryDetailDto?> GetMemoryAsync(Guid memoryId, CancellationToken ct = default) => Current.GetMemoryAsync(memoryId, ct);
    public Task<AdminMemoryDto> CreateMemoryAsync(string scopeKey, string title, MemoryType type, string content, string? description = null, MemoryTemperature temperature = MemoryTemperature.Warm, bool isPointInTime = false, Dictionary<string, string>? metadata = null, string? actorId = null, CancellationToken ct = default) => Current.CreateMemoryAsync(scopeKey, title, type, content, description, temperature, isPointInTime, metadata, actorId, ct);
    public Task<AdminMemoryDetailDto> UpdateMemoryAsync(Guid memoryId, string title, MemoryType type, string content, string? description, MemoryTemperature temperature, string? actorId = null, CancellationToken ct = default) => Current.UpdateMemoryAsync(memoryId, title, type, content, description, temperature, actorId, ct);
    public Task<bool> DeleteMemoryAsync(Guid memoryId, string? actorId = null, CancellationToken ct = default) => Current.DeleteMemoryAsync(memoryId, actorId, ct);
    public Task<MemoryConsolidationResult> ConsolidateScopeAsync(string scopeKey, string? actorId = null, CancellationToken ct = default) => Current.ConsolidateScopeAsync(scopeKey, actorId, ct);
    public Task<IReadOnlyList<MemoryAuditEntry>> ListAuditEntriesAsync(string? scopeKey = null, int page = 1, int pageSize = 50, CancellationToken ct = default) => Current.ListAuditEntriesAsync(scopeKey, page, pageSize, ct);

    private static MemoryAdminClientException MissingLocal() =>
        new("Local Memory administration services are not registered.",
            MemoryAdminAvailability.Unregistered, MemoryAdminProblemCodes.Unregistered);

    private static MemoryAdminClientException MissingRemote() =>
        new("Remote Memory administration services are not registered.",
            MemoryAdminAvailability.Unregistered, MemoryAdminProblemCodes.Unregistered);
}
