namespace FabrCore.Services.GraphRag.Administration;

public sealed record GraphRagScopeWriteRequest(string? Description, double DefaultPriority = 1.0, string? Metadata = null);
public sealed record GraphRagEntityUpdateRequest(string? Description, string? Content, string? Metadata);
public sealed record GraphRagRelationshipDeleteRequest(string FromEntityName, string FromEntityType, string ToEntityName, string ToEntityType, string RelationshipType, string ScopeKey);
public sealed record GraphRagDomainCreateRequest(string Name, string? Description, double PriorityWeight = 1.0, string? Metadata = null);
public sealed record GraphRagDomainUpdateRequest(string? Description, double PriorityWeight = 1.0, string? Metadata = null);
public sealed record GraphRagCategoryCreateRequest(string Name, string DomainName, string? Description, string? Metadata = null);
public sealed record GraphRagCategoryUpdateRequest(string? Description, string? Metadata = null);
public sealed record GraphRagSearchRequest(string Query, IReadOnlyList<string> Scopes, string SearchType, int Limit = 10, string? EntityTypeFilter = null, string? DomainFilter = null);
public sealed record GraphRagPurgeTaxonomyRequest(IReadOnlyList<Guid> DomainIds, IReadOnlyList<Guid> CategoryIds);
public sealed record GraphRagDocumentIdsRequest(IReadOnlyList<Guid> DocumentIds);

public static class GraphRagAdminClientKeys
{
    public const string Local = "FabrCore.GraphRag.Admin.Local";
}
