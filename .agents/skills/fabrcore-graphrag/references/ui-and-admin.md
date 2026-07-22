# UI And Administration Reference

## Table Of Contents

- Project boundary
- Registration
- Admin service mapping
- Suggested UI screens
- API endpoint guidance
- Security guidance
- Graph visualization data

## Project Boundary

`FabrCore.Services.GraphRag` does not ship UI. Consumers should build UI in their
own app using:

```csharp
using FabrCore.Services.GraphRag.Administration;
using FabrCore.Services.GraphRag.Administration.Models;
```

Do not add Razor components or static web assets to the service package.

## Registration

```csharp
builder.Services.AddGraphRagServices("GraphRagDb");
builder.Services.AddGraphRagAdministration();
```

Then inject:

```csharp
IGraphRagAdminService admin
```

## Admin Service Mapping

Dashboard:

- Use `GetDashboardStatsAsync`.

Scopes:

- Use `ListScopesAsync`, `GetScopeAsync`, `CreateScopeAsync`, `UpdateScopeAsync`.

Entities:

- Use `ListEntitiesAsync`, `CountEntitiesAsync`, `GetEntityAsync`,
  `UpdateEntityAsync`, `DeleteEntityAsync`, `ListEntityTypesAsync`.

Chunks:

- Use `ListChunksForEntityAsync`.

Relationships:

- Use `ListRelationshipsAsync`, `CountRelationshipsAsync`,
  `DeleteRelationshipAsync`, `ListRelationshipTypesAsync`.

Taxonomy:

- Use domain/category methods for curation.
- Use `GetOrphanTaxonomyAsync` and `PurgeOrphanTaxonomyAsync` for cleanup tools.

Graph:

- Use `GetGraphDataAsync(scopeFilter, maxNodes, ct)`.

Search:

- Use `SearchAsync(query, scopes, searchType, limit, entityTypeFilter, domainFilter, ct)`.

Metrics:

- Use `GetMetricsSummaryAsync` and `GetDocumentTokenSummariesAsync`.

## Suggested UI Screens

- Dashboard: total scopes, entities, relationships, documents, chunks, recent
  ingestion status.
- Scopes: list/create/edit scopes and show entity counts.
- Ingestion: upload/paste Markdown, select scope, show document status and
  metrics.
- Documents: list source documents, inspect metadata, delete/reingest.
- Entities: filter by scope/type/search, inspect chunks and metadata.
- Relationships: filter by scope/entity/type, delete incorrect relationships.
- Taxonomy: manage domains/categories and purge orphan taxonomy.
- Search: run scoped entity/chunk/hybrid/deep searches.
- Graph: visualize graph nodes/links from `GraphData`.
- Metrics: inspect ingestion token/call/duration summaries.

## API Endpoint Guidance

Use app-owned endpoints to enforce auth and scope checks before calling GraphRAG.

Endpoint responsibilities:

- Resolve authenticated user.
- Resolve allowed scopes for the user/tenant/workspace.
- Validate requested scope filters are within allowed scopes.
- Call `IGraphRagAdminService` or the lower-level services.
- Return DTOs or JSON to the UI.

Use `assets/minimal-api-endpoints.cs` as a starting point.

## Security Guidance

- Never trust browser-provided scopes directly.
- Never trust LLM-provided scopes.
- Treat domain/category filters as relevance filters only.
- Enforce authorization before calling admin mutation methods.
- Use audit logging for destructive admin operations.
- Restrict ingestion and deletion to elevated users or controlled automation.

## Graph Visualization Data

`GraphData` contains nodes and links intended for UI visualization. Use
`GetGraphDataAsync(scopeFilter, maxNodes, ct)` and keep `maxNodes` bounded.

For interactive graph UIs:

- Provide scope filter controls.
- Use server-side maximum node limits.
- Use progressive loading if the graph grows large.
- Show node type and relationship type in hover/details panels.

