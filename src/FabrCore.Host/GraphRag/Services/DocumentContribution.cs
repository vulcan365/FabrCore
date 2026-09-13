namespace FabrCore.Services.GraphRag.Services;

/// <summary>
/// Kinds of graph items a document can contribute. Stored as TINYINT in
/// <c>grag.DocumentContribution.ItemKind</c>.
/// </summary>
public enum ContributionKind : byte
{
    Entity = 1,
    Relationship = 2,
    Domain = 3,
    Category = 4,
    BelongsTo = 5,
    ExtractedFromEdge = 6
}

/// <summary>
/// Shape of a BelongsTo edge contribution. Only valid when <see cref="ContributionKind.BelongsTo"/>.
/// </summary>
public enum BelongsToShape : byte
{
    EntityToCategory = 1,
    EntityToDomain = 2,
    CategoryToDomain = 3
}

/// <summary>
/// In-memory identity for a single contribution. Used to diff the old set against
/// the new set during re-ingest. Equality is value-based across all populated
/// fields; unpopulated fields are <c>null</c> / <c>default</c>.
/// </summary>
public readonly record struct ContributionKey(
    ContributionKind Kind,
    Guid? EntityId,
    Guid? RelFromEntityId,
    Guid? RelToEntityId,
    string? RelationshipType,
    Guid? DomainId,
    Guid? CategoryId,
    BelongsToShape? Shape)
{
    public static ContributionKey ForEntity(Guid entityId) =>
        new(ContributionKind.Entity, entityId, null, null, null, null, null, null);

    public static ContributionKey ForRelationship(Guid fromId, Guid toId, string type) =>
        new(ContributionKind.Relationship, null, fromId, toId, type, null, null, null);

    public static ContributionKey ForExtractedFromEdge(Guid fromEntityId, Guid toEntityId) =>
        new(ContributionKind.ExtractedFromEdge, null, fromEntityId, toEntityId, "EXTRACTED_FROM", null, null, null);

    public static ContributionKey ForDomain(Guid domainId) =>
        new(ContributionKind.Domain, null, null, null, null, domainId, null, null);

    public static ContributionKey ForCategory(Guid categoryId) =>
        new(ContributionKind.Category, null, null, null, null, null, categoryId, null);

    public static ContributionKey ForBelongsToEntityCategory(Guid entityId, Guid categoryId) =>
        new(ContributionKind.BelongsTo, null, entityId, null, null, null, categoryId, BelongsToShape.EntityToCategory);

    public static ContributionKey ForBelongsToEntityDomain(Guid entityId, Guid domainId) =>
        new(ContributionKind.BelongsTo, null, entityId, null, null, domainId, null, BelongsToShape.EntityToDomain);

    public static ContributionKey ForBelongsToCategoryDomain(Guid categoryId, Guid domainId) =>
        new(ContributionKind.BelongsTo, null, null, null, null, domainId, categoryId, BelongsToShape.CategoryToDomain);
}

/// <summary>
/// Thrown when a concurrent ingest of the same <c>(ScopeKey, FileName)</c> pair is
/// already in progress. The caller should retry after a short delay; the lock
/// auto-releases when the prior transaction commits or is deemed stale.
/// </summary>
public sealed class ConcurrentIngestionException : InvalidOperationException
{
    public ConcurrentIngestionException(string fileName, string scopeKey)
        : base($"Another ingest of '{fileName}' under scope '{scopeKey}' is already in progress.")
    {
        FileName = fileName;
        ScopeKey = scopeKey;
    }

    public string FileName { get; }
    public string ScopeKey { get; }
}
