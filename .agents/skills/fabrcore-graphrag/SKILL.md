---
name: fabrcore-graphrag
description: Build with FabrCore.Services.GraphRag, the service-only GraphRAG package for FabrCore. Use when adding GraphRAG to a .NET 10 app, configuring AddGraphRagServices or AddGraphRagAdministration, creating scopes, ingesting documents, searching with scoped GraphRAG services, building admin UI/API endpoints, using GraphRAG from FabrCore agents or plugins, troubleshooting grag schema/database issues, or migrating consumers from FabrCore.Agents.GraphRagAgent to FabrCore.Services.GraphRag.
---

# FabrCore GraphRAG Service Skill

Use this skill to integrate `FabrCore.Services.GraphRag` into .NET 10 / FabrCore
applications. Treat this as a service-first package: it provides GraphRAG
contracts and operations, not UI components.

## Core Rule

Prefer the service package:

```csharp
using FabrCore.Services.GraphRag;
using FabrCore.Services.GraphRag.Services;
using FabrCore.Services.GraphRag.Administration;
using FabrCore.Services.GraphRag.Administration.Models;
```

Do not use the old `FabrCore.Agents.GraphRagAgent` namespace for new work unless
the user explicitly asks to maintain legacy code.

## What This Package Is

`FabrCore.Services.GraphRag` provides:

- SQL Server GraphRAG schema and migrations under the preserved `grag` schema.
- DI registration with `AddGraphRagServices`.
- Optional administration service registration with `AddGraphRagAdministration`.
- Scope management through `IKnowledgeScopeService`.
- Document ingestion through `IKnowledgeIngestionService`.
- Scope-enforced search through `IKnowledgeSearchService`.
- Admin/dashboard/data-management operations through `IGraphRagAdminService`.
- Optional plugin and agent adapters for FabrCore agent/tool-call scenarios.

It intentionally does not provide Razor components, pages, JavaScript, or static
assets. Build UI, controllers, pages, and API endpoints in the consuming app.

## First Steps In A Consumer App

1. Add a package or project reference to `FabrCore.Services.GraphRag`.
2. Add a SQL Server connection string, usually named `GraphRagDb`.
3. Register `AddGraphRagServices("GraphRagDb")`.
4. Register `AddGraphRagAdministration()` only when building admin screens,
   dashboards, maintenance endpoints, or graph visualization APIs.
5. Inject the service interfaces needed by the user-facing feature.
6. Keep scope keys trusted: derive them from config, claims, tenant context, or
   another authoritative source. Never let an LLM choose scopes.

Use the assets as copyable templates:

- `assets/appsettings.graphrag.json` for configuration shape.
- `assets/service-registration.cs` for DI setup.
- `assets/minimal-api-endpoints.cs` for app-owned API endpoints.
- `assets/background-ingestion-worker.cs` for queued ingestion patterns.
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

## Service Registration Pattern

Use this in the consuming app startup:

```csharp
builder.Services.AddGraphRagServices(
    connectionStringName: "GraphRagDb",
    extractionModelName: "graph-extraction");

builder.Services.AddGraphRagAdministration();
```

`extractionModelName` is optional. Use it when ingestion should perform
LLM-assisted entity, relationship, domain, and category extraction. Omit it when
the app only needs source documents/chunks or when extraction will be added later.

`AddGraphRagServices` registers the schema hosted service. On app startup, the
service resolves the configured connection string and runs schema/migration
initialization.

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

1. Register `AddGraphRagAdministration`.
2. Inject `IGraphRagAdminService`.
3. Build app-owned pages/components/controllers around the admin DTOs.
4. Load `references/ui-and-admin.md` for method mapping.
5. Use `assets/minimal-api-endpoints.cs` if exposing a backend API for a SPA or
   Blazor client.

### Ingest Documents

1. Ensure a scope exists with `IKnowledgeScopeService`.
2. Inject `IKnowledgeIngestionService`.
3. Call `IngestDocumentAsync(fileName, scopeKey, markdownContent, ct)`.
4. Store/display `SourceDocumentDto.DocumentId`, `Status`, `ChunkCount`,
   `ExtractedEntityCount`, `ExtractedRelationshipCount`, and `Reused`.
5. For bulk or async ingestion, adapt `assets/background-ingestion-worker.cs`.

### Search Knowledge

1. Derive allowed scopes from the authenticated user or agent configuration.
2. Construct `ScopedSearchRequest` or `ScopedRelationshipRequest`.
3. Use `SearchEntitiesAsync`, `SearchChunksAsync`, `SearchRelationshipsAsync`,
   `HybridSearchAsync`, or `DeepSearchAsync`.
4. Treat returned strings as JSON. Pass them to an LLM, API response, or UI JSON
   parser as needed.

### Use GraphRAG In Agents Or Plugins

1. Register `AddGraphRagServices` in the host app.
2. Configure plugin/agent `ConnectionStringName`.
3. Configure `AllowedScopes` for search-capable tools and agents.
4. Read `references/agents-and-plugins.md`.
5. Adapt `assets/plugin-agent-config.json`.

## Implementation Guardrails

- Keep UI out of `FabrCore.Services.GraphRag`.
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

For repositories using Microsoft.Testing.Platform, run tests from the directory
containing the relevant `global.json`, and use `--project` when needed:

```powershell
dotnet test --project path\to\Tests.csproj --filter "FullyQualifiedName~GraphRag"
```

For a smoke check, verify:

- App starts without GraphRAG schema initialization errors.
- `IKnowledgeScopeService` resolves from DI.
- A test scope can be created or listed.
- A scoped search without scopes fails fast.
- A scoped search with valid scopes reaches embeddings or the host API fallback.

