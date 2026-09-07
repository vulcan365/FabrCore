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
The sample configuration includes a dedicated `graphrag` alias using
`gpt-5.4-mini` with reasoning effort `none`, the measured ingestion baseline.

Ingestion defaults to `GraphRag:Ingestion:UseDocumentExtractionPlan=true`.
Extraction uses lossless source sections independent of the overlapping vector
retrieval chunks. `ExtractionSectionSizeChars` defaults to `2000` (range `256`–`16000`);
`MaxSectionsPerExtractionBatch` defaults to `8` (range `1`–`256`). The input-token
budget also limits batching. Independent calls share `MaxConcurrentChatCalls`
(default `4`) and graph results merge in source order. Malformed or length-limited
multi-section responses split and retry up to `MaxExtractionRetryDepth` (default `2`).

A document fitting one batch uses one combined graph/taxonomy call. Larger documents
use graph-only batches plus one concurrent classification call, which samples up to
32 sections across the document (up to 8000 source characters plus section labels).
All source text goes to graph extraction; only classification is sampled. This removes
repeated taxonomy prompts/output, but adds one call for multi-batch documents.
`ChatCallCount` includes classification; `ExtractionBatchCount` counts graph attempts.
Validate classification quality on heterogeneous long documents before broad rollout.

Opt in to structured responses with `GraphRag:Ingestion:UseExtractionJsonSchema=true`
(default `false`). This supplies separate combined, graph, and taxonomy JSON schemas
through the local `IFabrCoreChatClientService`. The Azure/OpenAI adapter sends
`json_schema` with `strict: true`. The schema requires the expected fields and JSON
types and prohibits extra properties; names, type vocabulary, confidence semantics,
and factual accuracy still need validation. Unsupported providers surface errors;
there is no automatic fallback to prompt-only JSON. The remote Host API currently
cannot carry a response schema and rejects this opt-in setting.

`ExtractionDescriptionTargetChars` (default `0`, range `0`–`2000`) optionally adds
compact-description guidance to the schema. It requires `UseExtractionJsonSchema`.
This is a target, not a hard length limit: the current SDK strips `maxLength` during
schema conversion. Keep this experiment disabled unless factual checks establish
acceptable coverage. Schema enforcement alone does not make graph facts correct.

`UseExtractionRelationGuidance` (default `false`) prepends versioned relationship
conventions to graph and combined extraction calls. It defines edge direction,
direct technical subjects, conditional facts, and consistent endpoint names, and
adds `PUBLISHED_BY` (work to publisher) and `ALIAS_OF` (alternate to canonical name).
It leaves standalone taxonomy classification unchanged and reserves input-budget
space for the added instructions. SQL relationship types are stored as strings;
this option introduces the new types only for newly extracted results. It does
not rewrite existing graphs. Evaluate it with fresh scopes or forced reingestion.

`UseExtractionEvidence` (default `false`) is an experimental fail-closed validation
path requiring both `UseExtractionJsonSchema` and `UseDocumentExtractionPlan`.
Each relationship must include an `evidence` passage present in the actual batch
text, allowing whitespace normalization. Endpoints must exist in that batch's
entity list. Opposing directions of selected asymmetric predicates are rejected
within and across batches. Reciprocal citations, uses, and dependencies are not
automatically rejected because they can be valid. Validation failures stop graph
ingestion without automatic repair calls or silently removing bad relationships.
Malformed/truncated JSON retains the existing retry behavior.

Quote presence is not semantic entailment: a quote can be real while the predicate
is wrong or ignores negation. Evidence is currently retained in eval response
snapshots, not persisted as a new SQL graph property. This feature remains opt-in
pending quality and latency evaluation; it does not validate completeness.

`UseExtractionSourceSpans` (default `false`) is an alternative to generated quotes.
It requires schema responses and the document extraction plan, and cannot be
combined with `UseExtractionEvidence`. Source sections receive content-derived
IDs; relationships must return a nonempty `sourceIds` list drawn from their actual
batch. Source schema v2 constrains the item values to that batch's IDs with a JSON
Schema enum; the service still rejects empty or invalid references. Identical
section text shares an ID. These are section-level provenance
references, not exact sentence evidence or proof of semantic entailment.

`RepairExtractionEndpoints` (default `false`, requires source spans) permits at
most one repair call per document after merging all extraction batches. The call
requests only unresolved endpoint names, their proposed relationships, and the
referenced source sections. It may add entity features but cannot modify edges or
existing nodes. More than 20 missing names or a repair prompt exceeding the input
budget fails without a call. Repair schema v2 requires an `unresolvedNames` array:
unsupported requested names belong there, not in placeholder entity descriptions.
Any unresolved name fails validation. Unrequested repair entities, unresolved endpoints,
invalid IDs, or asymmetric direction conflicts prevent graph writes. Invalid-ID
failures do not trigger repair. Repair usage is included in chat totals and LLM
phase time, separately from malformed-output retry counts.

Source-ID references and span locations are currently retained in eval snapshots,
not new SQL graph columns. Keep both options off until completion and quality
measurements justify adoption; the entity-cap behavior for email remains separate.

Incomplete extraction/classification returns `Status="Failed"` before graph writes.
Missing model configuration also fails when extraction is enabled. Explicitly set
`EnableExtraction=false` for chunk-only ingestion. To compare with the prior path,
set `UseDocumentExtractionPlan=false`; that path uses `MaxChunksPerExtractionBatch`
(default `32`) and retains its prior best-effort completion semantics.

Unchanged completed documents still reuse their stored results. To rebuild after
changing models or settings through the service API, use
`request with { ForceReingestion = true }`. This keeps document identity and increments
its version. Force is an explicit service-request property; existing upload/plugin
adapters do not expose it automatically.

Embedding batches run concurrently through `GraphRag:Ingestion:MaxEmbeddingConcurrency`
(default `4`, range `1`–`16`). This limit is shared by batch and single-item fallback
requests across ingestion operations on the same service instance. Set it to `1`
for a provider that requires serial requests. `EmbeddingBatchSize` defaults to `128`
(range `1`–`512`). Exact duplicate input strings are embedded once within each
embedding operation, with results restored to every original chunk/entity position.
Partial remote responses retry only missing vectors. No cross-scope cache is used.
The local `IEmbeddings` interface has no cancellation parameter; calls already in
flight must finish, while queued work and remote requests support cancellation.

See [the ingestion architecture review](../../docs/graphrag-ingestion-review.md)
for extraction tradeoffs, remaining bottlenecks, and a measurement plan.

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
  "FabrCore": {
    "HostUrl": "https://your-fabrcore-host"
  }
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

## Exact graph extraction result cache

`GraphRag:Ingestion:UseExtractionResultCache` defaults to `false`. Enable it for repeated
or partially changed documents with the in-process chat client. The ingestion singleton
keeps up to 256 validated responses in memory for 30 minutes (graph-only by default). Each response
is limited to 262,144 characters. Register a singleton `ExtractionResultCache` to share
it between ingestion service instances or customize these bounds. No cache data is
written to disk, and remote Host API extraction is excluded.

Keys hash the database connection identity, document ID (which isolates scopes), resolved
model configuration, exact prompt and extraction settings/guidance. Responses are parsed
and checked again on reuse. Failed, malformed, truncated or evidence-invalid responses
are not stored. Taxonomy and short-document combined responses remain live unless
`GraphRag:Ingestion:CacheTaxonomyResponses` is also enabled (default false). This
opt-in caches classifier and combined responses using the complete supplied taxonomy
context, including descriptions and category-to-domain mappings. Classifier keys also
include all source sections, even text outside the classification sample. New taxonomy
entries or changed descriptions/mappings invalidate affected responses; normal taxonomy
persistence and confidence/reuse rules still run on cached results. Concurrent
cold requests are not coalesced. Restarting the process clears the cache; model deployment
changes behind an unchanged configuration can remain cached until expiry.

An unchanged document normally skips ingestion entirely. `ForceReingestion` bypasses that
skip but still permits this opt-in cache; disable caching to force every extraction call.
Chunk/entity embedding stages and SQL writes still run on forced ingestion; an
explicit embedding cache can reuse the vectors. Actual chat usage
excludes cache hits. This accelerates repeat work without improving factual completeness.
See `docs/graphrag-result-cache-experiment-2026-09-05.md` for the measured tradeoffs.

## Exact embedding reuse (opt-in)

`CachedEmbeddings` decorates `IEmbeddings` and reuses exact vectors across calls.
It accepts an `EmbeddingResultCache`, a scope namespace including database/tenant
identity, the complete embedding model/configuration identity, and expected dimensions.
Share the cache, but construct the decorator for the actual scope and model. Do not
reuse a decorator for different scopes or providers. Existing default registration
is unchanged; pass the decorator as the ingestion service's `embeddings` dependency
when using a service dedicated to that scope. Dynamic multi-scope services need a
scope-aware factory before adopting this decorator.

The default cache holds up to 8,192 vectors for 30 minutes (48 MiB of vector data at
1,536 dimensions, plus keys/metadata). It stores hashes and vectors, not source text.
Eviction is constant-time FIFO; reads do not extend expiration. Cache hits return
independent arrays. Missing inputs retain their original order and are sent as a
batch; invalid count, dimension, nonfinite or all-zero vectors are rejected before
any results from that response are stored. The decorator supports dimensions 1�4096.
No persistence, cross-scope reuse, provider request coalescing or cancellation support
beyond the underlying `IEmbeddings` interface is added. Update the identity/recreate
the decorator when model deployment, revision, dimensions or preprocessing changes.

The eval console wires the decorator to each run's actual database/scope and serialized
resolved embedding configuration. `--embedding-cache on` enables it; the default is off.
The ingestion `EmbeddingBatchCount` still counts pipeline batch attempts. Use the eval's
separate `EmbeddingCalls` and `EmbeddingCacheHits` for actual SDK calls and cache reuse.

## Explicit endpoint initialisms

`GraphRag:Ingestion:ResolveExtractionEndpointAliases` defaults to false. When enabled,
merged relationship endpoints can resolve to an existing entity's unique, explicit
parenthesized initialism before relationship deduplication and persistence. For example,
IANA resolves to Internet Assigned Numbers Authority (IANA) if no exact IANA entity
exists. The expansion's capital letters must match the initialism. Both acronym-first
and expansion-first parenthesized names are supported; ambiguous aliases remain unchanged.
No entities, descriptions, predicates or directions are invented, and no LLM call is added.

This deliberately excludes inferred concept mappings such as JSON text to JSON and
implicit plural/singular conversion. It does not repair omitted facts or incorrect
predicates. Existing evidence validation still applies; a response rejected earlier by
batch evidence validation is not rescued by this later step. Keep it opt-in until the
naming conventions and collision behavior have been evaluated on the intended corpus.
