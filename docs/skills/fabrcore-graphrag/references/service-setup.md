# Service Setup Reference

## Table Of Contents

- Package reference
- Configuration
- DI registration
- Embeddings and extraction
- Schema initialization
- Consumer app checklist
- Manual initialization
- Test setup

## Package Reference

For source-based consumers:

```xml
<ProjectReference Include="..\FabrCore.Services.GraphRag\FabrCore.Services.GraphRag.csproj" />
```

For packaged consumers, select the intended published version of:

```powershell
dotnet add package FabrCore.Services.GraphRag
```

## Configuration

Minimum configuration:

```json
{
  "ConnectionStrings": {
    "GraphRagDb": "Server=.;Database=GraphRag;Integrated Security=true;TrustServerCertificate=true;"
  }
}
```

Optional host API fallback:

```json
{
  "FabrCore": {
    "HostUrl": "https://your-fabrcore-host"
  }
}
```

Optional ingestion tuning values:

```json
{
  "GraphRag": {
    "Ingestion": {
      "MaxEmbeddingConcurrency": 4,
      "EmailExtractedEntityLimit": 60
    }
  }
}
```

## DI Registration

Basic:

```csharp
using FabrCore.Services.GraphRag;

builder.Services.AddGraphRagServices("GraphRagDb");
```

With LLM extraction:

```csharp
builder.Services.AddGraphRagServices(
    connectionStringName: "GraphRagDb",
    extractionModelName: "graph-extraction");
```

With admin service surface:

```csharp
builder.Services.AddGraphRagServices("GraphRagDb");
builder.Services.AddGraphRagAdministration();
```

## Registered Services

`AddGraphRagServices` registers:

- `GraphRagOptions` as singleton.
- `GraphRagSchemaHostedService` as hosted service.
- `IGraphRagAuditLog`.
- `IKnowledgeScopeService`.
- `IKnowledgeSearchService`.
- `IKnowledgeIngestionService`.

`AddGraphRagAdministration` registers:

- `IGraphRagAdminService`.

## Embeddings And Extraction

Search and ingestion use `IEmbeddings` when available from DI. In FabrCore server
hosts, this usually comes from `AddFabrCoreServer` and an embeddings model entry
in `fabrcore.json`.

If `IEmbeddings` is not available, services can use the FabrCore Host API
embeddings endpoint when:

- `IHttpClientFactory` is registered.
- `FabrCore:HostUrl` is configured.

LLM extraction is enabled by default, independently of embeddings. An explicit
`extractionModelName` selects that alias; when omitted, ingestion tries `graphrag`
then `default`. Disable it with `GraphRag:Ingestion:EnableExtraction=false`, not by
omitting the alias. Configure `IFabrCoreChatClientService` for local schema extraction.
The Host API extraction fallback does not transport response schemas, so
`UseExtractionJsonSchema=true` requires the local chat-client service.

Core ingestion/search/scope registrations are singletons. Preserve that ingestion
lifetime so concurrent documents share chat and embedding semaphores. Plain console
or Generic Host applications work with the same dependencies; no Blazor lifecycle is
required. A plain DI provider must initialize schema explicitly if hosted services
are not started. Register `AddHttpClient()` when using Host API fallback.

Read [ingestion and evaluations](ingestion-and-evals.md) for defaults, bounded
parallelism, caching, experimental settings, and the current measured shortlist.

## Schema Initialization

`AddGraphRagServices` registers a hosted service that calls:

```csharp
await GraphRagSchemaInitializer.EnsureSchemaAsync(connectionString, logger);
```

This initializes the `grag` schema and runs registered migrations. It is designed
to be idempotent.

## Manual Initialization

Use manual initialization for provisioning tools, tests, or startup flows that do
not use hosted services:

```csharp
using FabrCore.Services.GraphRag;

await GraphRagSchemaInitializer.EnsureSchemaAsync(connectionString, logger);
```

## Consumer App Checklist

- Confirm the app targets `net10.0`.
- Confirm SQL Server supports graph tables and `VECTOR(1536)`.
- Add the connection string to the app configuration.
- Register `AddGraphRagServices` before resolving GraphRAG services.
- Register `AddGraphRagAdministration` only for admin screens or APIs.
- Ensure app logging captures schema initialization errors at startup.
- Ensure authenticated user/tenant logic maps to allowed GraphRAG scope keys.
- Ensure search endpoints never accept scopes from an LLM as authoritative.

## Test Setup

Unit tests that only validate DI can use an in-memory configuration with a fake
connection string. Tests that start hosted services or call database operations
need a real SQL Server database.

For this repository's executable test project:

```powershell
dotnet run --project src/FabrCore.Services.GraphRag.Tests --no-restore -- --filter FullyQualifiedName~Unit
```

Restore first if dependencies are unavailable. Live tests need configured SQL/model
access; unit success does not imply live evaluation or all-fact coverage.
