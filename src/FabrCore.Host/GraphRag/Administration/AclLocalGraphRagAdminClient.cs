using System.Net;
using FabrCore.Core.Acl;
using FabrCore.Services.GraphRag.Administration.Models;
using FabrCore.Services.GraphRag.Services;
using Microsoft.Extensions.DependencyInjection;

namespace FabrCore.Services.GraphRag.Administration;

/// <summary>
/// Enforces FabrCore ACL for in-process administration when both an ACL enforcer
/// and a caller accessor are available. Otherwise it preserves legacy behavior.
/// </summary>
internal sealed class AclLocalGraphRagAdminClient(
    IGraphRagAdminClient inner,
    IServiceProvider services) : IGraphRagAdminClient
{
    private static readonly AclAction ReadAction = new("graphrag", "read");
    private static readonly AclAction ManageAction = new("graphrag", "manage");

    public Task<GraphRagAdminCapability> GetCapabilityAsync(CancellationToken ct = default) => ReadAsync(client => client.GetCapabilityAsync(ct), ct);
    public Task<AdminDashboardStats> GetDashboardStatsAsync(CancellationToken ct = default) => ReadAsync(client => client.GetDashboardStatsAsync(ct), ct);
    public Task<IReadOnlyList<AdminScopeDto>> ListScopesAsync(CancellationToken ct = default) => ReadAsync(client => client.ListScopesAsync(ct), ct);
    public Task<AdminScopeDto?> GetScopeAsync(string scopeKey, CancellationToken ct = default) => ReadAsync(client => client.GetScopeAsync(scopeKey, ct), ct);
    public Task<AdminScopeDto> CreateScopeAsync(string scopeKey, string? description, double defaultPriority = 1, string? metadata = null, CancellationToken ct = default) => ManageAsync(client => client.CreateScopeAsync(scopeKey, description, defaultPriority, metadata, ct), ct);
    public Task<AdminScopeDto> UpdateScopeAsync(string scopeKey, string? description, double defaultPriority = 1, string? metadata = null, CancellationToken ct = default) => ManageAsync(client => client.UpdateScopeAsync(scopeKey, description, defaultPriority, metadata, ct), ct);
    public Task<IReadOnlyList<AdminEntityDto>> ListEntitiesAsync(string? scopeFilter = null, string? entityTypeFilter = null, string? searchTerm = null, int page = 1, int pageSize = 25, CancellationToken ct = default) => ReadAsync(client => client.ListEntitiesAsync(scopeFilter, entityTypeFilter, searchTerm, page, pageSize, ct), ct);
    public Task<int> CountEntitiesAsync(string? scopeFilter = null, string? entityTypeFilter = null, string? searchTerm = null, CancellationToken ct = default) => ReadAsync(client => client.CountEntitiesAsync(scopeFilter, entityTypeFilter, searchTerm, ct), ct);
    public Task<AdminEntityDto?> GetEntityAsync(Guid entityId, CancellationToken ct = default) => ReadAsync(client => client.GetEntityAsync(entityId, ct), ct);
    public Task<AdminEntityDto> UpdateEntityAsync(Guid entityId, string? description, string? content, string? metadata, CancellationToken ct = default) => ManageAsync(client => client.UpdateEntityAsync(entityId, description, content, metadata, ct), ct);
    public Task DeleteEntityAsync(Guid entityId, CancellationToken ct = default) => ManageAsync(client => client.DeleteEntityAsync(entityId, ct), ct);
    public Task<IReadOnlyList<string>> ListEntityTypesAsync(CancellationToken ct = default) => ReadAsync(client => client.ListEntityTypesAsync(ct), ct);
    public Task<IReadOnlyList<AdminChunkDto>> ListChunksForEntityAsync(Guid entityId, CancellationToken ct = default) => ReadAsync(client => client.ListChunksForEntityAsync(entityId, ct), ct);
    public Task<IReadOnlyList<AdminRelationshipDto>> ListRelationshipsAsync(string? scopeFilter = null, string? entityNameFilter = null, string? relationshipTypeFilter = null, int page = 1, int pageSize = 25, CancellationToken ct = default) => ReadAsync(client => client.ListRelationshipsAsync(scopeFilter, entityNameFilter, relationshipTypeFilter, page, pageSize, ct), ct);
    public Task<int> CountRelationshipsAsync(string? scopeFilter = null, string? entityNameFilter = null, string? relationshipTypeFilter = null, CancellationToken ct = default) => ReadAsync(client => client.CountRelationshipsAsync(scopeFilter, entityNameFilter, relationshipTypeFilter, ct), ct);
    public Task DeleteRelationshipAsync(string fromEntityName, string fromEntityType, string toEntityName, string toEntityType, string relationshipType, string scopeKey, CancellationToken ct = default) => ManageAsync(client => client.DeleteRelationshipAsync(fromEntityName, fromEntityType, toEntityName, toEntityType, relationshipType, scopeKey, ct), ct);
    public Task<IReadOnlyList<string>> ListRelationshipTypesAsync(CancellationToken ct = default) => ReadAsync(client => client.ListRelationshipTypesAsync(ct), ct);
    public Task<IReadOnlyList<AdminDomainDto>> ListDomainsAsync(CancellationToken ct = default) => ReadAsync(client => client.ListDomainsAsync(ct), ct);
    public Task<AdminDomainDto> CreateDomainAsync(string name, string? description, double priorityWeight = 1, string? metadata = null, CancellationToken ct = default) => ManageAsync(client => client.CreateDomainAsync(name, description, priorityWeight, metadata, ct), ct);
    public Task<AdminDomainDto> UpdateDomainAsync(Guid domainId, string? description, double priorityWeight = 1, string? metadata = null, CancellationToken ct = default) => ManageAsync(client => client.UpdateDomainAsync(domainId, description, priorityWeight, metadata, ct), ct);
    public Task DeleteDomainAsync(Guid domainId, CancellationToken ct = default) => ManageAsync(client => client.DeleteDomainAsync(domainId, ct), ct);
    public Task<IReadOnlyList<AdminCategoryDto>> ListCategoriesAsync(string? domainNameFilter = null, CancellationToken ct = default) => ReadAsync(client => client.ListCategoriesAsync(domainNameFilter, ct), ct);
    public Task<AdminCategoryDto> CreateCategoryAsync(string name, string domainName, string? description, string? metadata = null, CancellationToken ct = default) => ManageAsync(client => client.CreateCategoryAsync(name, domainName, description, metadata, ct), ct);
    public Task<AdminCategoryDto> UpdateCategoryAsync(Guid categoryId, string? description, string? metadata = null, CancellationToken ct = default) => ManageAsync(client => client.UpdateCategoryAsync(categoryId, description, metadata, ct), ct);
    public Task DeleteCategoryAsync(Guid categoryId, CancellationToken ct = default) => ManageAsync(client => client.DeleteCategoryAsync(categoryId, ct), ct);
    public Task<GraphData> GetGraphDataAsync(string? scopeFilter = null, int maxNodes = 200, CancellationToken ct = default) => ReadAsync(client => client.GetGraphDataAsync(scopeFilter, maxNodes, ct), ct);
    public Task<AdminSearchResult> SearchAsync(string query, IReadOnlyList<string> scopes, string searchType, int limit = 10, string? entityTypeFilter = null, string? domainFilter = null, CancellationToken ct = default) => ReadAsync(client => client.SearchAsync(query, scopes, searchType, limit, entityTypeFilter, domainFilter, ct), ct);
    public Task<OrphanTaxonomyReport> GetOrphanTaxonomyAsync(CancellationToken ct = default) => ReadAsync(client => client.GetOrphanTaxonomyAsync(ct), ct);
    public Task PurgeOrphanTaxonomyAsync(IEnumerable<Guid> domainIds, IEnumerable<Guid> categoryIds, CancellationToken ct = default) => ManageAsync(client => client.PurgeOrphanTaxonomyAsync(domainIds, categoryIds, ct), ct);
    public Task<IngestionMetricsSummaryDto> GetMetricsSummaryAsync(string? scope, DateTime? since, int topN = 25, CancellationToken ct = default) => ReadAsync(client => client.GetMetricsSummaryAsync(scope, since, topN, ct), ct);
    public Task<IReadOnlyList<DocumentTokenSummaryDto>> GetDocumentTokenSummariesAsync(IReadOnlyList<Guid> documentIds, CancellationToken ct = default) => ReadAsync(client => client.GetDocumentTokenSummariesAsync(documentIds, ct), ct);
    public Task<IReadOnlyList<SourceDocumentDto>> ListDocumentsAsync(string? scopeFilter = null, int page = 1, int pageSize = 25, CancellationToken ct = default) => ReadAsync(client => client.ListDocumentsAsync(scopeFilter, page, pageSize, ct), ct);
    public Task<int> CountDocumentsAsync(string? scopeFilter = null, CancellationToken ct = default) => ReadAsync(client => client.CountDocumentsAsync(scopeFilter, ct), ct);
    public Task<SourceDocumentDto?> GetDocumentAsync(Guid documentId, CancellationToken ct = default) => ReadAsync(client => client.GetDocumentAsync(documentId, ct), ct);
    public Task<SourceDocumentDto> IngestDocumentAsync(GraphRagDocumentUpload upload, CancellationToken ct = default) => ManageAsync(client => client.IngestDocumentAsync(upload, ct), ct);
    public Task DeleteDocumentAsync(Guid documentId, CancellationToken ct = default) => ManageAsync(client => client.DeleteDocumentAsync(documentId, ct), ct);

    private Task<T> ReadAsync<T>(Func<IGraphRagAdminClient, Task<T>> operation, CancellationToken ct) => ExecuteAsync(ReadAction, operation, ct);
    private Task ReadAsync(Func<IGraphRagAdminClient, Task> operation, CancellationToken ct) => ExecuteAsync(ReadAction, operation, ct);
    private Task<T> ManageAsync<T>(Func<IGraphRagAdminClient, Task<T>> operation, CancellationToken ct) => ExecuteAsync(ManageAction, operation, ct);
    private Task ManageAsync(Func<IGraphRagAdminClient, Task> operation, CancellationToken ct) => ExecuteAsync(ManageAction, operation, ct);

    private async Task<T> ExecuteAsync<T>(AclAction action, Func<IGraphRagAdminClient, Task<T>> operation, CancellationToken ct)
    {
        await AuthorizeIfConfiguredAsync(action, ct);
        return await operation(inner);
    }

    private async Task ExecuteAsync(AclAction action, Func<IGraphRagAdminClient, Task> operation, CancellationToken ct)
    {
        await AuthorizeIfConfiguredAsync(action, ct);
        await operation(inner);
    }

    private async Task AuthorizeIfConfiguredAsync(AclAction action, CancellationToken ct)
    {
        var enforcer = services.GetService<AclEnforcer>();
        var principalAccessor = services.GetService<IGraphRagAdminPrincipalAccessor>();
        if (enforcer is null || principalAccessor is null) return;

        var principal = await principalAccessor.GetPrincipalIdAsync(ct);
        if (string.IsNullOrWhiteSpace(principal))
        {
            throw new GraphRagAdminClientException(
                "The current administration principal could not be resolved.",
                GraphRagAdminAvailability.Unauthorized,
                GraphRagAdminProblemCodes.Unauthorized,
                HttpStatusCode.Unauthorized);
        }

        try
        {
            var subject = new AclSubjectContext(principal, null);
            enforcer.Authorize(in subject, action, "*:*");
        }
        catch (AclDeniedException ex)
        {
            throw new GraphRagAdminClientException(
                "The current principal is not authorized for this GraphRAG administration operation.",
                GraphRagAdminAvailability.Unauthorized,
                GraphRagAdminProblemCodes.Unauthorized,
                HttpStatusCode.Forbidden,
                ex);
        }
    }
}
