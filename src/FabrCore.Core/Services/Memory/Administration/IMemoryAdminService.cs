using FabrCore.Services.Memory.Administration.Models;
using FabrCore.Services.Memory.Audit;
using FabrCore.Services.Memory.Models;

namespace FabrCore.Services.Memory.Administration;

/// <summary>
/// Administration surface over agent memory — dashboards, scope management, memory
/// CRUD, consolidation, and audit review. Intended for admin UI and maintenance
/// tooling (e.g. the FabrCore.Surface.Admin memory page), not for agents.
///
/// <para>
/// Reads query the <c>mem</c> schema directly; all mutations route through the
/// scope-bound <c>IAgentMemoryService</c> / <c>IMemoryScopeService</c> so hot-index
/// maintenance, embedding generation, taxonomy validation, and audit stay consistent.
/// </para>
/// </summary>
public interface IMemoryAdminService
{
    // ─── Dashboard ──────────────────────────────────────────────────────

    Task<AdminMemoryDashboardStats> GetDashboardStatsAsync(CancellationToken ct = default);

    // ─── Scopes ─────────────────────────────────────────────────────────

    /// <summary>
    /// List all scopes: registered rows from <c>mem.MemoryScope</c> plus any scope keys
    /// that only exist implicitly through memory rows (<c>IsRegistered = false</c>).
    /// </summary>
    Task<IReadOnlyList<AdminMemoryScopeDto>> ListScopesAsync(CancellationToken ct = default);

    /// <summary>Create a shared scope (e.g. "bank-recon"). Throws when the key exists.</summary>
    Task<AdminMemoryScopeDto> CreateSharedScopeAsync(
        string scopeKey, string? description, string? actorId = null, CancellationToken ct = default);

    /// <summary>
    /// Destructive: delete a scope and every memory, chunk, relationship, and summary
    /// node in it. Returns the deleted counts. Writes a <c>ScopeDeleted</c> audit entry.
    /// </summary>
    Task<AdminScopeDeleteResult> DeleteScopeAsync(
        string scopeKey, string? actorId = null, CancellationToken ct = default);

    // ─── Memories ───────────────────────────────────────────────────────

    /// <summary>List memories in a scope, paged, newest first.</summary>
    /// <param name="searchTerm">Case-insensitive match on title, description, or primary content.</param>
    Task<IReadOnlyList<AdminMemoryDto>> ListMemoriesAsync(
        string scopeKey,
        MemoryType? typeFilter = null,
        MemoryTemperature? temperatureFilter = null,
        string? searchTerm = null,
        int page = 1, int pageSize = 25,
        CancellationToken ct = default);

    /// <summary>Count memories matching the same filters as <see cref="ListMemoriesAsync"/>.</summary>
    Task<int> CountMemoriesAsync(
        string scopeKey,
        MemoryType? typeFilter = null,
        MemoryTemperature? temperatureFilter = null,
        string? searchTerm = null,
        CancellationToken ct = default);

    /// <summary>Get full detail for one memory (content, chunks, relationships), or null.</summary>
    Task<AdminMemoryDetailDto?> GetMemoryAsync(Guid memoryId, CancellationToken ct = default);

    /// <summary>
    /// Create a memory in a scope — the admin "teach" path (e.g. save the Rule
    /// "Habitat line items are business meal expenses" into scope "bank-recon").
    /// Runs through the full save pipeline: taxonomy validation, embedding,
    /// entity matching, hot index.
    /// </summary>
    Task<AdminMemoryDto> CreateMemoryAsync(
        string scopeKey, string title, MemoryType type, string content,
        string? description = null,
        MemoryTemperature temperature = MemoryTemperature.Warm,
        bool isPointInTime = false,
        Dictionary<string, string>? metadata = null,
        string? actorId = null,
        CancellationToken ct = default);

    /// <summary>Update a memory's title, type, content, description, and temperature.</summary>
    Task<AdminMemoryDetailDto> UpdateMemoryAsync(
        Guid memoryId, string title, MemoryType type, string content,
        string? description, MemoryTemperature temperature,
        string? actorId = null,
        CancellationToken ct = default);

    /// <summary>Delete a memory (store + hot index). Returns false when not found.</summary>
    Task<bool> DeleteMemoryAsync(Guid memoryId, string? actorId = null, CancellationToken ct = default);

    // ─── Maintenance ────────────────────────────────────────────────────

    /// <summary>Run consolidation (dedup, prune, contradictions, index truncation) for a scope.</summary>
    Task<MemoryConsolidationResult> ConsolidateScopeAsync(
        string scopeKey, string? actorId = null, CancellationToken ct = default);

    // ─── Audit ──────────────────────────────────────────────────────────

    /// <summary>List audit entries, newest first, optionally filtered to one scope.</summary>
    Task<IReadOnlyList<MemoryAuditEntry>> ListAuditEntriesAsync(
        string? scopeKey = null, int page = 1, int pageSize = 50, CancellationToken ct = default);
}
