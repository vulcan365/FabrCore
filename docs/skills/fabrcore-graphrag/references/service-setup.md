# GraphRAG setup for FabrCore 2.0

## Package and startup

Reference `FabrCore.Host` 2.0.0 (`net10.0`); shared administration contracts live in Core.
The old GraphRAG package is retired. Namespaces remain `FabrCore.Services.GraphRag.*`.

```csharp
using FabrCore.Host;
var builder = WebApplication.CreateBuilder(args);
builder.AddFabrCoreServer();
var app = builder.Build();
app.UseFabrCoreServer();
app.Run();
```

Supply `ConnectionStrings:FabrCore` through secrets for an existing SQL Server 2025/Azure SQL
feature database with graph and VECTOR support. This enables ACL, Memory and GraphRAG together.
Standalone has none of these SQL-only services. Remove manual knowledge/admin registrations.
For existing split storage, configure `FabrCore:Database:GraphRagConnectionStringName` to name
another configured connection, while retaining the main feature connection.

## Models and tuning

Configure `default` chat and `embeddings` with 1536 dimensions in the active model store.
Set `FabrCore:GraphRag:ExtractionModelName` for an explicit extraction alias; otherwise resolution
tries `graphrag`, then `default`. Missing required aliases/credentials fail SQL startup.
Startup validation does not perform paid inference. The integrated feature set requires models
even when an individual request does not use extraction.

Ingestion options still bind from **top-level `GraphRag:Ingestion`**, including
`MaxEmbeddingConcurrency`, `EmailExtractedEntityLimit`, and `EnableExtraction`.
Do not relocate these legacy keys beneath FabrCore. See [ingestion and evaluations](ingestion-and-evals.md)
for current defaults and opt-in experiments. `UseExtractionJsonSchema=true` requires a local
chat-client service because the remote Host API fallback does not transport response schemas.

## Initialization and services

`AddFabrCoreServer` registers singleton scope, ingestion and search services, audit and
administration services in SQL mode. Preserve ingestion's singleton lifetime so concurrent
requests share its embedding/chat semaphores. GraphRAG does not require Blazor.

The integrated database schema hosted service initializes `grag` and applies migrations.
`FabrCore:Database:AutoInitialize=false` validates pre-provisioned schema/migration versions.
The database itself must exist. Invalid configuration or schema errors fail startup, with no
standalone fallback. Readiness checks feature database connectivity and ACL initialization.

Manual `GraphRagSchemaInitializer.EnsureSchemaAsync(connectionString, logger)` is appropriate
for a provisioning tool. Low-level service registration alone does not run the integrated Host
schema lifecycle; a standalone DI/test harness must arrange its own initialization.

## Consumer validation

Check startup/readiness, one scoped ingestion/search, and existing data after restart. Derive
allowed scopes from authenticated application context; never accept LLM/browser scope claims
as authorization. Tests starting SQL services require a real compatible database. Unit tests
with fake dependencies do not validate migrations or model quality.
