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

`AddGraphRagAdministration()` registers the versioned controller at
`/fabrcoreapi/graphrag/admin/v1`. It requires the Host's `FabrCoreAdmin` bearer policy and uses
`x-user-handle` for ACL and audit attribution. Prefer this open protocol for remote admin UIs.

Use app-owned endpoints only for consumer-specific operations that are not part of the GraphRAG
admin contract. Those endpoints must still resolve allowed scopes and enforce authorization
before calling lower-level services.

## Security Guidance

- Never trust browser-provided scopes directly.
- Never trust LLM-provided scopes.
- Never expose the admin controller without a configured cluster-scoped admin key.
- Never treat `x-user-handle` as authentication; it is authorization/audit context after bearer
  authentication succeeds.
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
