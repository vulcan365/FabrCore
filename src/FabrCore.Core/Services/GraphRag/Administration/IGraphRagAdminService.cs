using FabrCore.Services.GraphRag.Administration.Models;

namespace FabrCore.Services.GraphRag.Administration;

public interface IGraphRagAdminService
{
    // ─── Dashboard ───────────────────────────────────────────────────────
    Task<AdminDashboardStats> GetDashboardStatsAsync(CancellationToken ct = default);

    // ─── Scopes ──────────────────────────────────────────────────────────
    Task<IReadOnlyList<AdminScopeDto>> ListScopesAsync(CancellationToken ct = default);
    Task<AdminScopeDto?> GetScopeAsync(string scopeKey, CancellationToken ct = default);
    Task<AdminScopeDto> CreateScopeAsync(string scopeKey, string? description, double defaultPriority = 1.0, string? metadata = null, CancellationToken ct = default);
    Task<AdminScopeDto> UpdateScopeAsync(string scopeKey, string? description, double defaultPriority = 1.0, string? metadata = null, CancellationToken ct = default);

    // ─── Entities ────────────────────────────────────────────────────────
    Task<IReadOnlyList<AdminEntityDto>> ListEntitiesAsync(string? scopeFilter = null, string? entityTypeFilter = null, string? searchTerm = null, int page = 1, int pageSize = 25, CancellationToken ct = default);
    Task<int> CountEntitiesAsync(string? scopeFilter = null, string? entityTypeFilter = null, string? searchTerm = null, CancellationToken ct = default);
    Task<AdminEntityDto?> GetEntityAsync(Guid entityId, CancellationToken ct = default);
    Task<AdminEntityDto> UpdateEntityAsync(Guid entityId, string? description, string? content, string? metadata, CancellationToken ct = default);
    Task DeleteEntityAsync(Guid entityId, CancellationToken ct = default);
    Task<IReadOnlyList<string>> ListEntityTypesAsync(CancellationToken ct = default);

    // ─── Chunks ──────────────────────────────────────────────────────────
    Task<IReadOnlyList<AdminChunkDto>> ListChunksForEntityAsync(Guid entityId, CancellationToken ct = default);

    // ─── Relationships ───────────────────────────────────────────────────
    Task<IReadOnlyList<AdminRelationshipDto>> ListRelationshipsAsync(string? scopeFilter = null, string? entityNameFilter = null, string? relationshipTypeFilter = null, int page = 1, int pageSize = 25, CancellationToken ct = default);
    Task<int> CountRelationshipsAsync(string? scopeFilter = null, string? entityNameFilter = null, string? relationshipTypeFilter = null, CancellationToken ct = default);
    Task DeleteRelationshipAsync(string fromEntityName, string fromEntityType, string toEntityName, string toEntityType, string relationshipType, string scopeKey, CancellationToken ct = default);
    Task<IReadOnlyList<string>> ListRelationshipTypesAsync(CancellationToken ct = default);

    // ─── Domains ─────────────────────────────────────────────────────────
    Task<IReadOnlyList<AdminDomainDto>> ListDomainsAsync(CancellationToken ct = default);
    Task<AdminDomainDto> CreateDomainAsync(string name, string? description, double priorityWeight = 1.0, string? metadata = null, CancellationToken ct = default);
    Task<AdminDomainDto> UpdateDomainAsync(Guid domainId, string? description, double priorityWeight = 1.0, string? metadata = null, CancellationToken ct = default);
    Task DeleteDomainAsync(Guid domainId, CancellationToken ct = default);

    // ─── Categories ──────────────────────────────────────────────────────
    Task<IReadOnlyList<AdminCategoryDto>> ListCategoriesAsync(string? domainNameFilter = null, CancellationToken ct = default);
    Task<AdminCategoryDto> CreateCategoryAsync(string name, string domainName, string? description, string? metadata = null, CancellationToken ct = default);
    Task<AdminCategoryDto> UpdateCategoryAsync(Guid categoryId, string? description, string? metadata = null, CancellationToken ct = default);
    Task DeleteCategoryAsync(Guid categoryId, CancellationToken ct = default);

    // ─── Graph Visualization ─────────────────────────────────────────────
    Task<GraphData> GetGraphDataAsync(string? scopeFilter = null, int maxNodes = 200, CancellationToken ct = default);

    // ─── Search ──────────────────────────────────────────────────────────
    Task<AdminSearchResult> SearchAsync(string query, IReadOnlyList<string> scopes, string searchType, int limit = 10, string? entityTypeFilter = null, string? domainFilter = null, CancellationToken ct = default);

    // ─── Orphan Taxonomy ─────────────────────────────────────────────────
    Task<OrphanTaxonomyReport> GetOrphanTaxonomyAsync(CancellationToken ct = default);
    Task PurgeOrphanTaxonomyAsync(IEnumerable<Guid> domainIds, IEnumerable<Guid> categoryIds, CancellationToken ct = default);

    // ─── Ingestion Metrics ───────────────────────────────────────────────
    Task<IngestionMetricsSummaryDto> GetMetricsSummaryAsync(string? scope, DateTime? since, int topN = 25, CancellationToken ct = default);
    Task<IReadOnlyList<DocumentTokenSummaryDto>> GetDocumentTokenSummariesAsync(IReadOnlyList<Guid> documentIds, CancellationToken ct = default);
}

public sealed class OrphanTaxonomyReport
{
    public IReadOnlyList<AdminDomainDto> Domains { get; init; } = Array.Empty<AdminDomainDto>();
    public IReadOnlyList<AdminCategoryDto> Categories { get; init; } = Array.Empty<AdminCategoryDto>();
}
