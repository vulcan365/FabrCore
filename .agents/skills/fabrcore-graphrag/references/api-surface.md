# API Surface Reference

## Table Of Contents

- Namespaces
- Service interfaces
- Scope service
- Ingestion service
- Search service
- Administration service
- Request contracts
- DTOs
- Return shape guidance

## Namespaces

Use these namespaces:

```csharp
using FabrCore.Services.GraphRag;
using FabrCore.Services.GraphRag.Services;
using FabrCore.Services.GraphRag.Administration;
using FabrCore.Services.GraphRag.Administration.Models;
using FabrCore.Services.GraphRag.Audit;
using FabrCore.Services.GraphRag.Models;
```

## Service Interfaces

Primary services:

- `IKnowledgeScopeService`
- `IKnowledgeIngestionService`
- `IKnowledgeSearchService`
- `IGraphRagAdminService`
- `IGraphRagAuditLog`

## Scope Service

`IKnowledgeScopeService` owns the `grag.KnowledgeScope` registry.

Methods:

- `CreateScopeAsync(scopeKey, description, defaultPriority, metadata, ct)`
- `GetScopeAsync(scopeKey, ct)`
- `ListScopesAsync(ct)`
- `ScopeExistsAsync(scopeKey, ct)`
- `CountEntitiesInScopeAsync(scopeKey, ct)`

Use it when bootstrapping tenants, workspaces, customers, projects, or any other
knowledge boundary.

## Ingestion Service

`IKnowledgeIngestionService` ingests source documents and manages source document
records.

Methods:

- `IngestDocumentAsync(fileName, scopeKey, markdownContent, ct)`
- `ListDocumentsAsync(scopeFilter, page, pageSize, ct)`
- `CountDocumentsAsync(scopeFilter, ct)`
- `GetDocumentAsync(documentId, ct)`
- `DeleteDocumentAsync(documentId, ct)`
- `GetContributionsAsync(documentId, ct)`

`IngestDocumentAsync` returns `SourceDocumentDto`.

Important `SourceDocumentDto` fields:

- `DocumentId`
- `FileName`
- `ScopeKey`
- `SourceKind`
- `SourceKey`
- `SourceTitle`
- `SourceOccurredAtUtc`
- `MetadataJson`
- `EntityId`
- `ChunkCount`
- `Status`
- `ErrorMessage`
- `ExtractedEntityCount`
- `ExtractedRelationshipCount`
- `ContentHash`
- `VersionNumber`
- `Reused`

Use `DeleteDocumentAsync` instead of direct SQL deletion so graph contribution
cleanup remains consistent.

## Search Service

`IKnowledgeSearchService` is the authoritative search surface. Use it for all
GraphRAG search paths, including UI, API, agent wrappers, and plugin tools.

Methods:

- `SearchEntitiesAsync(ScopedSearchRequest request, ct)`
- `SearchChunksAsync(ScopedSearchRequest request, ct)`
- `SearchRelationshipsAsync(ScopedRelationshipRequest request, ct)`
- `HybridSearchAsync(ScopedSearchRequest request, graphDepth, vectorLimit, ct)`
- `DeepSearchAsync(ScopedSearchRequest request, graphDepth, vectorLimit, maxIterations, ct)`

All methods return JSON strings.

## Administration Service

`IGraphRagAdminService` supports app-owned admin UI/API layers.

Dashboard:

- `GetDashboardStatsAsync(ct)`

Scopes:

- `ListScopesAsync(ct)`
- `GetScopeAsync(scopeKey, ct)`
- `CreateScopeAsync(scopeKey, description, defaultPriority, metadata, ct)`
- `UpdateScopeAsync(scopeKey, description, defaultPriority, metadata, ct)`

Entities:

- `ListEntitiesAsync(scopeFilter, entityTypeFilter, searchTerm, page, pageSize, ct)`
- `CountEntitiesAsync(scopeFilter, entityTypeFilter, searchTerm, ct)`
- `GetEntityAsync(entityId, ct)`
- `UpdateEntityAsync(entityId, description, content, metadata, ct)`
- `DeleteEntityAsync(entityId, ct)`
- `ListEntityTypesAsync(ct)`

Chunks:

- `ListChunksForEntityAsync(entityId, ct)`

Relationships:

- `ListRelationshipsAsync(scopeFilter, entityNameFilter, relationshipTypeFilter, page, pageSize, ct)`
- `CountRelationshipsAsync(scopeFilter, entityNameFilter, relationshipTypeFilter, ct)`
- `DeleteRelationshipAsync(fromEntityName, fromEntityType, toEntityName, toEntityType, relationshipType, ct)`
- `ListRelationshipTypesAsync(ct)`

Domains and categories:

- `ListDomainsAsync(ct)`
- `CreateDomainAsync(name, description, priorityWeight, metadata, ct)`
- `UpdateDomainAsync(domainId, description, priorityWeight, metadata, ct)`
- `DeleteDomainAsync(domainId, ct)`
- `ListCategoriesAsync(domainNameFilter, ct)`
- `CreateCategoryAsync(name, domainName, description, metadata, ct)`
- `UpdateCategoryAsync(categoryId, description, metadata, ct)`
- `DeleteCategoryAsync(categoryId, ct)`

Graph and search:

- `GetGraphDataAsync(scopeFilter, maxNodes, ct)`
- `SearchAsync(query, scopes, searchType, limit, entityTypeFilter, domainFilter, ct)`

Maintenance and metrics:

- `GetOrphanTaxonomyAsync(ct)`
- `PurgeOrphanTaxonomyAsync(domainIds, categoryIds, ct)`
- `GetMetricsSummaryAsync(scope, since, topN, ct)`
- `GetDocumentTokenSummariesAsync(documentIds, ct)`

## Request Contracts

`ScopedSearchRequest`:

```csharp
public sealed record ScopedSearchRequest(
    string Query,
    IReadOnlyList<string> Scopes,
    int Limit = 10,
    string? EntityTypeFilter = null,
    string? DomainFilter = null);
```

Validation:

- `Query` is required.
- `Scopes` must contain at least one non-whitespace value.
- `Limit` must be 1 through 200.

`ScopedRelationshipRequest`:

```csharp
public sealed record ScopedRelationshipRequest(
    string EntityName,
    string EntityType,
    IReadOnlyList<string> Scopes,
    string? RelationshipTypeFilter = null,
    int Depth = 1);
```

Validation:

- `EntityName` is required.
- `EntityType` is required.
- `Scopes` must contain at least one non-whitespace value.
- `Depth` must be 1, 2, or 3.

## Return Shape Guidance

Search methods return JSON strings because they are often passed directly to LLMs
or plugins. UI/API code can:

- Return the JSON string directly with `Content(..., "application/json")`.
- Parse the JSON into `JsonDocument` for shaping.
- Wrap the JSON in a typed API response with metadata such as query, scopes, and
duration.

Avoid parsing and reserializing unless the consumer needs a changed shape.

