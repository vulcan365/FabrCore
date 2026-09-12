---
name: fabrcore-graphrag
description: "Integrate, configure, troubleshoot, and evaluate the GraphRAG feature in FabrCore.Host 2.0. Use for scoped ingestion and search, extraction performance and quality, SQL graph/vector setup, administration APIs, or GraphRAG agent and plugin adapters."
---

# FabrCore GraphRAG Service Skill

## FabrCore 2.0 baseline

In FabrCore 2.0 GA, GraphRAG implementation ships in FabrCore.Host and shared contracts in FabrCore.Core. Existing FabrCore.Services.GraphRag namespaces remain. Configure ConnectionStrings:FabrCore and call AddFabrCoreServer once; Host registers GraphRAG and its administration services. Do not install the retired service package or repeat manual registrations. Standalone does not expose SQL-only GraphRAG features.

Use this skill to integrate `FabrCore.Services.GraphRag` into .NET 10 / FabrCore
applications. The preserved namespaces expose Host services and Core contracts; UI belongs in the consuming application.

Release baseline: FabrCore 2.0.0 GA. Available experimental options are not
recommended defaults. Read [ingestion and evaluations](references/ingestion-and-evals.md)
when tuning performance or resuming evals; that reference distinguishes implemented
behavior from unfinished work. Console/Generic Host consumers are supported; Blazor
is not required.

## Core Rule

Use the preserved namespaces (implementation in Host, shared contracts in Core):

```csharp
using FabrCore.Services.GraphRag;
using FabrCore.Services.GraphRag.Services;
using FabrCore.Services.GraphRag.Administration;
using FabrCore.Services.GraphRag.Administration.Models;
```

Do not use the old `FabrCore.Agents.GraphRagAgent` namespace for new work unless
the user explicitly asks to maintain legacy code.

## Integrated GraphRAG services

The Host GraphRAG feature provides:

- SQL Server GraphRAG schema and migrations under the preserved `grag` schema.
- Integrated service and administration registration through `AddFabrCoreServer` in SQL mode.
- Scope management through `IKnowledgeScopeService`.
- Document ingestion through `IKnowledgeIngestionService`.
- Scope-enforced search through `IKnowledgeSearchService`.
- Admin/dashboard/data-management operations through `IGraphRagAdminService`.
- Open transport contracts and DTOs from `FabrCore.Core`.
- Vendor-neutral `IMarkdownConversionService`; OSS defaults to pass-through conversion.
- Optional plugin and agent adapters for FabrCore agent/tool-call scenarios.

It intentionally does not provide Razor components, pages, JavaScript, or static
assets. Build UI, controllers, pages, and API endpoints in the consuming app.

## First Steps In A Consumer App

1. Reference `FabrCore.Host` 2.0.0 and configure `ConnectionStrings:FabrCore` through secrets.
2. Call `builder.AddFabrCoreServer()` once. Host registers SQL knowledge and administration services.
3. Configure `default` chat and 1536-dimensional `embeddings` models.
4. For existing split storage, set `FabrCore:Database:GraphRagConnectionStringName`.
5. Inject the required service interfaces and derive allowed scopes from trusted application context.

Use the assets as copyable templates:

- `assets/appsettings.graphrag.json` for configuration shape.
- `assets/service-registration.cs` for DI setup.
- `assets/minimal-api-endpoints.cs` and `assets/background-ingestion-worker.cs` use the
  request-based ingestion API. Integrate application authorization and trusted scope selection
  before exposing the endpoint examples.
- `assets/plugin-agent-config.json` for plugin/agent configuration.

## Reference Map

Read these references only when needed:

- `references/service-setup.md`: registration, configuration, embeddings,
  startup/schema initialization, app integration checklist.
- `references/api-surface.md`: public interfaces, request contracts, DTOs, and
  common service usage patterns.
- `references/schema-and-migrations.md`: `grag` schema, tables, indexes,
  migrations, and database expectations.
- `references/agents-and-plugins.md`: using GraphRAG from FabrCore agents,
  plugins, and tool-call surfaces.
- `references/ui-and-admin.md`: building your own UI/API layer using
  `IGraphRagAdminService`.
- `references/troubleshooting.md`: common errors, causes, and fixes.
- `references/ingestion-and-evals.md`: pipeline, actual defaults, optional caches,
  experimental flags, measured recommendations, and the paused eval checkpoint.

## Service Registration Pattern

Configure SQL mode and call `builder.AddFabrCoreServer()`; do not repeat low-level
knowledge registrations. Set `FabrCore:GraphRag:ExtractionModelName` for an explicit alias.
When absent, extraction tries `graphrag`, then `default`; omission does not disable it.
An explicit invalid alias fails rather than silently falling back.

Ingestion tuning still binds from the legacy top-level `GraphRag:Ingestion` section,
including `EnableExtraction=false` for embedding-only ingestion. Do not move these keys
under `FabrCore` until the implementation supports that path.

The integrated Host schema service initializes GraphRAG migrations. With
`FabrCore:Database:AutoInitialize=false`, it validates pre-provisioned schemas instead.

## Scope Rules

Scope is the access boundary.

- Every knowledge entity has one `ScopeKey`.
- Every search request must include at least one allowed scope.
- Scope order does not affect ranking.
- Domain/category filters are taxonomy filters, not authorization.
- Relationship traversal scope-checks both endpoints.
- User-selected scopes in UI must be validated against the authenticated user's
  allowed scopes before calling GraphRAG services.

Typical search request:

```csharp
var request = new ScopedSearchRequest(
    Query: query,
    Scopes: allowedScopes,
    Limit: 10,
    EntityTypeFilter: entityType,
    DomainFilter: domain);

var json = await search.SearchEntitiesAsync(request, ct);
```

## Common Tasks

### Add GraphRAG To A New App

1. Read `references/service-setup.md`.
2. Copy and adapt `assets/appsettings.graphrag.json`.
3. Copy and adapt `assets/service-registration.cs`.
4. Confirm SQL Server supports graph tables and `VECTOR(1536)`.
5. Build the app and check startup logs for GraphRAG schema initialization.
6. Add a small smoke test that resolves `IKnowledgeScopeService`.

### Build An Admin UI

1. Enable Host SQL mode; administration services register automatically.
2. Configure the Host's `FabrCoreAdmin` cluster key.
3. Use the secured `/fabrcoreapi/graphrag/admin/v1` controller or inject
   `IGraphRagAdminService` in-process.
4. Send `Authorization: Bearer` plus `x-user-handle` from remote admin clients.
5. Build app-owned pages/components around the open contracts.
6. Load `references/ui-and-admin.md` for method mapping.

### Ingest Documents

1. Ensure a scope exists with `IKnowledgeScopeService`.
2. Inject `IKnowledgeIngestionService`.
3. Call `IngestDocumentAsync(new KnowledgeIngestionRequest(fileName, scopeKey,
   markdownContent, extractionInstructions), ct)`. Instructions are not searchable source text.
4. Store/display `SourceDocumentDto.DocumentId`, `Status`, `ChunkCount`,
   `ExtractedEntityCount`, `ExtractedRelationshipCount`, and `Reused`.
5. For bulk ingestion, reuse the registered singleton and bound document concurrency;
   see `references/ingestion-and-evals.md`.

### Search Knowledge

1. Derive allowed scopes from the authenticated user or agent configuration.
2. Construct `ScopedSearchRequest` or `ScopedRelationshipRequest`.
3. Use `SearchEntitiesAsync`, `SearchChunksAsync`, `SearchRelationshipsAsync`,
   `HybridSearchAsync`, or `DeepSearchAsync`.
4. Treat returned strings as JSON. Pass them to an LLM, API response, or UI JSON
   parser as needed.

### Use GraphRAG In Agents Or Plugins

1. Enable SQL mode through `AddFabrCoreServer` in the host app.
2. Configure plugin/agent `ConnectionStringName`.
3. Configure `AllowedScopes` for search-capable tools and agents.
4. Read `references/agents-and-plugins.md`.
5. Adapt `assets/plugin-agent-config.json`.

## Implementation Guardrails

- Keep GraphRAG UI in the consuming application, outside Host service implementations.
- Keep vendor-specific document conversion out of OSS. The Vulcan365 adapter lives in the
  commercial `FabrCore.Services.GraphRag.Vulcan365` project.
- Keep shared admin interfaces/DTOs in `FabrCore.Core`; do not link-compile or
  type-forward them from the service package.
- Keep consumer-specific auth and tenant resolution in the consuming app.
- Use `IKnowledgeSearchService` as the authoritative search surface.
- Use `IKnowledgeIngestionService.DeleteDocumentAsync` for document deletion so
  document contribution cleanup remains consistent.
- Use `IGraphRagAdminService` for admin workflows instead of duplicating SQL in UI
  code.
- Do not bypass scope validation with direct SQL search endpoints.
- Do not rename the `grag` schema unless the service code is explicitly changed.
- Do not treat domains or categories as security boundaries.

## Validation

After modifying a consumer app:

```powershell
dotnet build
```

In the FabrCore repository, the GraphRAG tests are a Microsoft.Testing.Platform
executable. Use the verified unit-test invocation (not a solution-wide test assumption):

```powershell
dotnet run --project src/FabrCore.Services.GraphRag.Tests --no-restore -- --filter FullyQualifiedName~Unit
```

Do not run database/live-model evaluations as part of an unrelated documentation edit.
For ingestion changes, check persisted factual edges as well as retrieval smoke gates.

For a smoke check, verify:

- App starts without GraphRAG schema initialization errors.
- `IKnowledgeScopeService` resolves from DI.
- A test scope can be created or listed.
- A scoped search without scopes fails fast.
- A scoped search with valid scopes reaches embeddings or the host API fallback.
