using FabrCore.Services.Memory.Administration.Models;
using FabrCore.Services.Memory.Audit;
using FabrCore.Services.Memory.Models;

namespace FabrCore.Services.Memory.Administration;

internal sealed class LocalMemoryAdminClient(IMemoryAdminService admin) : IMemoryAdminClient
{
    private static readonly string[] Features =
        ["dashboard", "scopes", "memories", "consolidation", "audit"];

    public Task<MemoryAdminCapability> GetCapabilityAsync(CancellationToken ct = default)
        => Task.FromResult(new MemoryAdminCapability
        {
            Availability = MemoryAdminAvailability.Available,
            Features = Features
        });

    public Task<AdminMemoryDashboardStats> GetDashboardStatsAsync(CancellationToken ct = default) => admin.GetDashboardStatsAsync(ct);
    public Task<IReadOnlyList<AdminMemoryScopeDto>> ListScopesAsync(CancellationToken ct = default) => admin.ListScopesAsync(ct);
    public Task<AdminMemoryScopeDto> CreateSharedScopeAsync(string scopeKey, string? description, string? actorId = null, CancellationToken ct = default) => admin.CreateSharedScopeAsync(scopeKey, description, actorId, ct);
    public Task<AdminScopeDeleteResult> DeleteScopeAsync(string scopeKey, string? actorId = null, CancellationToken ct = default) => admin.DeleteScopeAsync(scopeKey, actorId, ct);
    public Task<IReadOnlyList<AdminMemoryDto>> ListMemoriesAsync(string scopeKey, MemoryType? typeFilter = null, MemoryTemperature? temperatureFilter = null, string? searchTerm = null, int page = 1, int pageSize = 25, CancellationToken ct = default) => admin.ListMemoriesAsync(scopeKey, typeFilter, temperatureFilter, searchTerm, page, pageSize, ct);
    public Task<int> CountMemoriesAsync(string scopeKey, MemoryType? typeFilter = null, MemoryTemperature? temperatureFilter = null, string? searchTerm = null, CancellationToken ct = default) => admin.CountMemoriesAsync(scopeKey, typeFilter, temperatureFilter, searchTerm, ct);
    public Task<AdminMemoryDetailDto?> GetMemoryAsync(Guid memoryId, CancellationToken ct = default) => admin.GetMemoryAsync(memoryId, ct);
    public Task<AdminMemoryDto> CreateMemoryAsync(string scopeKey, string title, MemoryType type, string content, string? description = null, MemoryTemperature temperature = MemoryTemperature.Warm, bool isPointInTime = false, Dictionary<string, string>? metadata = null, string? actorId = null, CancellationToken ct = default) => admin.CreateMemoryAsync(scopeKey, title, type, content, description, temperature, isPointInTime, metadata, actorId, ct);
    public Task<AdminMemoryDetailDto> UpdateMemoryAsync(Guid memoryId, string title, MemoryType type, string content, string? description, MemoryTemperature temperature, string? actorId = null, CancellationToken ct = default) => admin.UpdateMemoryAsync(memoryId, title, type, content, description, temperature, actorId, ct);
    public Task<bool> DeleteMemoryAsync(Guid memoryId, string? actorId = null, CancellationToken ct = default) => admin.DeleteMemoryAsync(memoryId, actorId, ct);
    public Task<MemoryConsolidationResult> ConsolidateScopeAsync(string scopeKey, string? actorId = null, CancellationToken ct = default) => admin.ConsolidateScopeAsync(scopeKey, actorId, ct);
    public Task<IReadOnlyList<MemoryAuditEntry>> ListAuditEntriesAsync(string? scopeKey = null, int page = 1, int pageSize = 50, CancellationToken ct = default) => admin.ListAuditEntriesAsync(scopeKey, page, pageSize, ct);
}
