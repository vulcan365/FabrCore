using System.Text;
using FabrCore.Services.GraphRag.Administration.Models;
using FabrCore.Services.GraphRag.Services;

namespace FabrCore.Services.GraphRag.Administration;

internal sealed class LocalGraphRagAdminClient(
    IGraphRagAdminService admin,
    IKnowledgeIngestionService ingestion,
    IKnowledgeScopeService scopes,
    IMarkdownConversionService markdownConverter) : IGraphRagAdminClient
{
    private static readonly string[] Features =
    [
        "dashboard", "scopes", "documents", "entities", "relationships",
        "taxonomy", "graph", "search", "metrics", "maintenance", "upload"
    ];

    public Task<GraphRagAdminCapability> GetCapabilityAsync(CancellationToken ct = default)
        => Task.FromResult(new GraphRagAdminCapability
        {
            Availability = GraphRagAdminAvailability.Available,
            Features = Features
        });

    public Task<AdminDashboardStats> GetDashboardStatsAsync(CancellationToken ct = default) => admin.GetDashboardStatsAsync(ct);
    public Task<IReadOnlyList<AdminScopeDto>> ListScopesAsync(CancellationToken ct = default) => admin.ListScopesAsync(ct);
    public Task<AdminScopeDto?> GetScopeAsync(string scopeKey, CancellationToken ct = default) => admin.GetScopeAsync(scopeKey, ct);
    public Task<AdminScopeDto> CreateScopeAsync(string scopeKey, string? description, double defaultPriority = 1, string? metadata = null, CancellationToken ct = default) => admin.CreateScopeAsync(scopeKey, description, defaultPriority, metadata, ct);
    public Task<AdminScopeDto> UpdateScopeAsync(string scopeKey, string? description, double defaultPriority = 1, string? metadata = null, CancellationToken ct = default) => admin.UpdateScopeAsync(scopeKey, description, defaultPriority, metadata, ct);
    public Task<IReadOnlyList<AdminEntityDto>> ListEntitiesAsync(string? scopeFilter = null, string? entityTypeFilter = null, string? searchTerm = null, int page = 1, int pageSize = 25, CancellationToken ct = default) => admin.ListEntitiesAsync(scopeFilter, entityTypeFilter, searchTerm, page, pageSize, ct);
    public Task<int> CountEntitiesAsync(string? scopeFilter = null, string? entityTypeFilter = null, string? searchTerm = null, CancellationToken ct = default) => admin.CountEntitiesAsync(scopeFilter, entityTypeFilter, searchTerm, ct);
    public Task<AdminEntityDto?> GetEntityAsync(Guid entityId, CancellationToken ct = default) => admin.GetEntityAsync(entityId, ct);
    public Task<AdminEntityDto> UpdateEntityAsync(Guid entityId, string? description, string? content, string? metadata, CancellationToken ct = default) => admin.UpdateEntityAsync(entityId, description, content, metadata, ct);
    public Task DeleteEntityAsync(Guid entityId, CancellationToken ct = default) => admin.DeleteEntityAsync(entityId, ct);
    public Task<IReadOnlyList<string>> ListEntityTypesAsync(CancellationToken ct = default) => admin.ListEntityTypesAsync(ct);
    public Task<IReadOnlyList<AdminChunkDto>> ListChunksForEntityAsync(Guid entityId, CancellationToken ct = default) => admin.ListChunksForEntityAsync(entityId, ct);
    public Task<IReadOnlyList<AdminRelationshipDto>> ListRelationshipsAsync(string? scopeFilter = null, string? entityNameFilter = null, string? relationshipTypeFilter = null, int page = 1, int pageSize = 25, CancellationToken ct = default) => admin.ListRelationshipsAsync(scopeFilter, entityNameFilter, relationshipTypeFilter, page, pageSize, ct);
    public Task<int> CountRelationshipsAsync(string? scopeFilter = null, string? entityNameFilter = null, string? relationshipTypeFilter = null, CancellationToken ct = default) => admin.CountRelationshipsAsync(scopeFilter, entityNameFilter, relationshipTypeFilter, ct);
    public Task DeleteRelationshipAsync(string fromEntityName, string fromEntityType, string toEntityName, string toEntityType, string relationshipType, string scopeKey, CancellationToken ct = default) => admin.DeleteRelationshipAsync(fromEntityName, fromEntityType, toEntityName, toEntityType, relationshipType, scopeKey, ct);
    public Task<IReadOnlyList<string>> ListRelationshipTypesAsync(CancellationToken ct = default) => admin.ListRelationshipTypesAsync(ct);
    public Task<IReadOnlyList<AdminDomainDto>> ListDomainsAsync(CancellationToken ct = default) => admin.ListDomainsAsync(ct);
    public Task<AdminDomainDto> CreateDomainAsync(string name, string? description, double priorityWeight = 1, string? metadata = null, CancellationToken ct = default) => admin.CreateDomainAsync(name, description, priorityWeight, metadata, ct);
    public Task<AdminDomainDto> UpdateDomainAsync(Guid domainId, string? description, double priorityWeight = 1, string? metadata = null, CancellationToken ct = default) => admin.UpdateDomainAsync(domainId, description, priorityWeight, metadata, ct);
    public Task DeleteDomainAsync(Guid domainId, CancellationToken ct = default) => admin.DeleteDomainAsync(domainId, ct);
    public Task<IReadOnlyList<AdminCategoryDto>> ListCategoriesAsync(string? domainNameFilter = null, CancellationToken ct = default) => admin.ListCategoriesAsync(domainNameFilter, ct);
    public Task<AdminCategoryDto> CreateCategoryAsync(string name, string domainName, string? description, string? metadata = null, CancellationToken ct = default) => admin.CreateCategoryAsync(name, domainName, description, metadata, ct);
    public Task<AdminCategoryDto> UpdateCategoryAsync(Guid categoryId, string? description, string? metadata = null, CancellationToken ct = default) => admin.UpdateCategoryAsync(categoryId, description, metadata, ct);
    public Task DeleteCategoryAsync(Guid categoryId, CancellationToken ct = default) => admin.DeleteCategoryAsync(categoryId, ct);
    public Task<GraphData> GetGraphDataAsync(string? scopeFilter = null, int maxNodes = 200, CancellationToken ct = default) => admin.GetGraphDataAsync(scopeFilter, maxNodes, ct);
    public Task<AdminSearchResult> SearchAsync(string query, IReadOnlyList<string> scopes, string searchType, int limit = 10, string? entityTypeFilter = null, string? domainFilter = null, CancellationToken ct = default) => admin.SearchAsync(query, scopes, searchType, limit, entityTypeFilter, domainFilter, ct);
    public Task<OrphanTaxonomyReport> GetOrphanTaxonomyAsync(CancellationToken ct = default) => admin.GetOrphanTaxonomyAsync(ct);
    public Task PurgeOrphanTaxonomyAsync(IEnumerable<Guid> domainIds, IEnumerable<Guid> categoryIds, CancellationToken ct = default) => admin.PurgeOrphanTaxonomyAsync(domainIds, categoryIds, ct);
    public Task<IngestionMetricsSummaryDto> GetMetricsSummaryAsync(string? scope, DateTime? since, int topN = 25, CancellationToken ct = default) => admin.GetMetricsSummaryAsync(scope, since, topN, ct);
    public Task<IReadOnlyList<DocumentTokenSummaryDto>> GetDocumentTokenSummariesAsync(IReadOnlyList<Guid> documentIds, CancellationToken ct = default) => admin.GetDocumentTokenSummariesAsync(documentIds, ct);
    public Task<IReadOnlyList<SourceDocumentDto>> ListDocumentsAsync(string? scopeFilter = null, int page = 1, int pageSize = 25, CancellationToken ct = default) => ingestion.ListDocumentsAsync(scopeFilter, page, pageSize, ct);
    public Task<int> CountDocumentsAsync(string? scopeFilter = null, CancellationToken ct = default) => ingestion.CountDocumentsAsync(scopeFilter, ct);
    public Task<SourceDocumentDto?> GetDocumentAsync(Guid documentId, CancellationToken ct = default) => ingestion.GetDocumentAsync(documentId, ct);
    public Task DeleteDocumentAsync(Guid documentId, CancellationToken ct = default) => ingestion.DeleteDocumentAsync(documentId, ct);

    public async Task<SourceDocumentDto> IngestDocumentAsync(GraphRagDocumentUpload upload, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(upload);
        if (!await scopes.ScopeExistsAsync(upload.ScopeKey, ct))
        {
            throw new ArgumentException($"GraphRAG scope '{upload.ScopeKey}' is not registered.", nameof(upload));
        }

        string markdown;
        if (IsMarkdown(upload.FileName))
        {
            using var reader = new StreamReader(upload.Content, Encoding.UTF8, detectEncodingFromByteOrderMarks: true, leaveOpen: true);
            markdown = await reader.ReadToEndAsync(ct);
        }
        else
        {
            markdown = await markdownConverter.ConvertAsync(upload.Content, upload.FileName, upload.ContentType, ct);
        }

        return await ingestion.IngestDocumentAsync(
            new KnowledgeIngestionRequest(upload.FileName, upload.ScopeKey, markdown, upload.ExtractionInstructions),
            ct);
    }

    private static bool IsMarkdown(string fileName)
        => Path.GetExtension(fileName).Equals(".md", StringComparison.OrdinalIgnoreCase)
           || Path.GetExtension(fileName).Equals(".markdown", StringComparison.OrdinalIgnoreCase)
           || Path.GetExtension(fileName).Equals(".txt", StringComparison.OrdinalIgnoreCase);
}
