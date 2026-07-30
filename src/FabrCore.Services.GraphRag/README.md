# FabrCore.Services.GraphRag

`FabrCore.Services.GraphRag` is the service-only GraphRAG package for FabrCore.
It contains the GraphRAG database schema, migrations, ingestion services, scoped
search services, administration service surface, audit logging, and optional
agent/plugin adapters.

This project intentionally contains no Razor components, pages, JavaScript, or
static UI assets. Applications can build their own UI on top of the services in
this package.

## What This Project Provides

- SQL Server GraphRAG schema under the existing `grag` schema.
- Idempotent schema initialization and migrations.
- Scope registry and scope-enforced knowledge search.
- Document ingestion into entities, chunks, relationships, taxonomy, and source
  metadata.
- Audit logging into `grag.ActionAudit`.
- Administration service methods for dashboards, CRUD screens, graph
  visualization data, search, orphan taxonomy cleanup, and ingestion metrics.
- FabrCore plugin/agent adapters for agent and tool-call scenarios.

## Database Requirements

GraphRAG uses SQL Server graph tables and vector columns. The configured database
must support the DDL used by `GraphRagSchemaInitializer`, including `VECTOR(1536)`.

The schema name and table structure are preserved from the previous GraphRAG
project. Everything lives under the `grag` schema.

## Basic Setup

Reference the project or package from your application:

```xml
<ProjectReference Include="..\FabrCore.Services.GraphRag\FabrCore.Services.GraphRag.csproj" />
```

Add a connection string:

```json
{
  "ConnectionStrings": {
    "GraphRagDb": "Server=.;Database=GraphRag;Integrated Security=true;TrustServerCertificate=true;"
  }
}
```

Register the services:

```csharp
using FabrCore.Services.GraphRag;

builder.Services.AddGraphRagServices(
    connectionStringName: "GraphRagDb",
    extractionModelName: "graph-extraction");
```

`extractionModelName` is optional. When omitted, ingestion prefers a `graphrag`
model configuration and falls back to `default`. Set
`GraphRag:Ingestion:EnableExtraction=false` for chunk-only ingestion.

Extraction prompts are limited by both the configured input-token budget and
`GraphRag:Ingestion:MaxChunksPerExtractionBatch` (default `32`). Independent
batches run concurrently through `MaxConcurrentChatCalls` (default `4`) and are
merged in source order. Malformed or length-limited responses are split and
retried up to `MaxExtractionRetryDepth` (default `2`).

`AddGraphRagServices` registers:

- `IKnowledgeScopeService`
- `IKnowledgeSearchService`
- `IKnowledgeIngestionService`
- `IGraphRagAuditLog`
- hosted schema initialization for `grag.*`

## Administration Services

If your app is building admin screens, dashboards, graph visualizations, or
maintenance workflows, also register the administration surface:

```csharp
using FabrCore.Services.GraphRag;

builder.Services.AddGraphRagServices("GraphRagDb");
builder.Services.AddGraphRagAdministration();
```

Then inject `IGraphRagAdminService`:

```csharp
using FabrCore.Services.GraphRag.Administration;

public sealed class GraphRagDashboard
{
    private readonly IGraphRagAdminService _admin;

    public GraphRagDashboard(IGraphRagAdminService admin)
    {
        _admin = admin;
    }

    public Task<AdminDashboardStats> GetStatsAsync(CancellationToken ct)
        => _admin.GetDashboardStatsAsync(ct);
}
```

Administration DTOs live in:

```csharp
using FabrCore.Services.GraphRag.Administration.Models;
```

## Working With Scopes

Scopes are the GraphRAG access boundary. Every entity belongs to a single
`ScopeKey`, and every search request must provide the allowed scopes.

```csharp
using FabrCore.Services.GraphRag.Services;

public sealed class ScopeSetup
{
    private readonly IKnowledgeScopeService _scopes;

    public ScopeSetup(IKnowledgeScopeService scopes)
    {
        _scopes = scopes;
    }

    public async Task EnsureCustomerScopeAsync(CancellationToken ct)
    {
        if (!await _scopes.ScopeExistsAsync("customer-a", ct))
        {
            await _scopes.CreateScopeAsync(
                scopeKey: "customer-a",
                description: "Customer A knowledge",
                ct: ct);
        }
    }
}
```

## Ingesting Documents

Inject `IKnowledgeIngestionService` to ingest Markdown or email-like Markdown
documents:

```csharp
using FabrCore.Services.GraphRag.Services;

public sealed class DocumentIngestion
{
    private readonly IKnowledgeIngestionService _ingestion;

    public DocumentIngestion(IKnowledgeIngestionService ingestion)
    {
        _ingestion = ingestion;
    }

    public Task<SourceDocumentDto> IngestAsync(
        string fileName,
        string markdown,
        CancellationToken ct)
    {
        return _ingestion.IngestDocumentAsync(new KnowledgeIngestionRequest(
            FileName: fileName,
            ScopeKey: "customer-a",
            MarkdownContent: markdown,
            ExtractionInstructions: "Prioritize operational decisions and dependencies."), ct);
    }
}
```

The ingestion service also supports listing, counting, fetching, deleting, and
inspecting document contributions.

## Searching Knowledge

Inject `IKnowledgeSearchService` for scope-enforced search:

```csharp
using FabrCore.Services.GraphRag.Services;

public sealed class KnowledgeLookup
{
    private readonly IKnowledgeSearchService _search;

    public KnowledgeLookup(IKnowledgeSearchService search)
    {
        _search = search;
    }

    public Task<string> SearchAsync(string query, CancellationToken ct)
    {
        var request = new ScopedSearchRequest(
            Query: query,
            Scopes: ["customer-a"],
            Limit: 10);

        return _search.SearchEntitiesAsync(request, ct);
    }
}
```

Search methods return JSON strings so results can be passed directly to agents,
plugins, APIs, or UI components.

### Canonical identity and scoped evidence

The same real-world entity may have different scope-owned views. Each
`KnowledgeEntity` carries a `CanonicalEntityId` that groups those views without
sharing their descriptions, content, embeddings, chunks, relationships, or
taxonomy assignments. Hybrid and deep search return a `canonicalEntities`
projection while preserving every scoped view and its provenance.

Domains and categories are shared taxonomy, not authorization. Entity taxonomy
assignments carry the entity scope; category-to-domain edges are global. Put
content intentionally shared by multiple audiences in an explicit shared scope
and include that scope in the agent's trusted `AllowedScopes`.

Available search methods:

- `SearchEntitiesAsync`
- `SearchChunksAsync`
- `SearchRelationshipsAsync`
- `HybridSearchAsync`
- `DeepSearchAsync`

## Using From Agents And Plugins

This package includes GraphRAG plugin and agent adapters in the
`FabrCore.Services.GraphRag` namespace:

- `GraphRagSearchPlugin`
- `GraphRagIngestPlugin`
- `GraphRagQueryPlugin`
- `GraphRagDomainPlugin`
- `GraphRagScopePlugin`
- `GraphRagSearchAgent`
- `GraphRagIngestionAgent`

Host applications should register `AddGraphRagServices` first so the adapters can
resolve `IKnowledgeSearchService`, `IKnowledgeIngestionService`, and
`IKnowledgeScopeService` from DI.

Plugin configuration still uses a `ConnectionStringName` setting:

```json
{
  "ConnectionStringName": "GraphRagDb",
  "AllowedScopes": "customer-a,customer-b"
}
```

`AllowedScopes` is required for search tool calls. The service layer enforces
scope filtering, so tools and agents cannot broaden access by changing prompts.

### File ingestion agent

Configure `graph-rag-ingestion-agent` with a trusted scope allow-list:

```json
{
  "AgentType": "graph-rag-ingestion-agent",
  "Args": {
    "AllowedScopes": "customer-a,shared-reference"
  }
}
```

Send FabrCore temporary file IDs in `AgentMessage.Files` and set either
`Args["Scope"]` or comma-separated `Args["Scopes"]`. Every requested scope must
be registered and authorized by `AllowedScopes`; otherwise no file is read.
The message text is optional extraction guidance. Each file is converted once
through the registered `IMarkdownConversionService` and ingested into every
requested scope. The response includes a readable summary and namespaced result
args.

OSS registers `PassThroughMarkdownConversionService`, which is appropriate for
Markdown and text inputs. Register another implementation for PDF, Office, audio,
or image conversion. The commercial
`FabrCore.Services.GraphRag.Vulcan365` package supplies the hosted Vulcan365
converter without coupling the OSS service to that endpoint.

## Embeddings And Host API Fallback

Search and ingestion use `IEmbeddings` when it is available from DI, usually from
`AddFabrCoreServer` and an embeddings model in `fabrcore.json`.

When `IEmbeddings` is not available, the services can fall back to the FabrCore
Host API embeddings endpoint if these are configured:

```json
{
  "FabrCoreHostUrl": "https://your-fabrcore-host"
}
```

## Schema Initialization

`AddGraphRagServices` registers a hosted service that runs
`GraphRagSchemaInitializer.EnsureSchemaAsync` at startup. You can also initialize
the schema manually:

```csharp
using FabrCore.Services.GraphRag;

await GraphRagSchemaInitializer.EnsureSchemaAsync(connectionString, logger);
```

Manual initialization is useful for tests, migrations, or one-off provisioning
tools.

## Project Boundary

Use this project for GraphRAG services and contracts. Build app-specific UI,
controllers, pages, and workflows in the consuming application.

The previous `FabrCore.Agents.GraphRagAgent` project remains available in the
repository for compatibility and history, but new service-first integrations
should target `FabrCore.Services.GraphRag`.
## Remote administration

`AddGraphRagAdministration()` registers both the in-process `IGraphRagAdminService` and the
versioned `/fabrcoreapi/graphrag/admin/v1` controller application part. A FabrCore service host
only needs its normal `UseFabrCoreServer()` call to map the endpoints:

```csharp
builder.AddFabrCoreServer();
builder.Services.AddGraphRagServices("GraphRagDb");
builder.Services.AddGraphRagAdministration();

var app = builder.Build();
app.UseFabrCoreServer();
```

Remote callers use the transport-neutral `FabrCore.Services.Contracts` package. The API
requires `graphrag.read.allow` for queries and `graphrag.manage.allow` for mutations, validates
requested scope keys against the server registry, accepts bounded multipart document uploads, and
records mutation identity in `grag.ActionAudit`. Connection strings, migrations, SQL exceptions,
and conversion infrastructure never cross the API boundary.
