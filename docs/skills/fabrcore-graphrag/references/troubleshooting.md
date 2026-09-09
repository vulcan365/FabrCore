# Troubleshooting Reference

## Table Of Contents

- Missing connection string
- Schema initialization failure
- VECTOR unsupported
- Missing IEmbeddings
- Search without scopes
- No search results
- Ingestion extracts no entities
- Duplicate/reused documents
- Microsoft.Testing.Platform test invocation

## Missing Connection String

Symptom:

```text
Connection string 'GraphRagDb' not found in configuration
```

Fix:

- Add `ConnectionStrings:GraphRagDb`.
- Confirm the name passed to `AddGraphRagServices` matches configuration.
- Confirm test configuration includes the same connection string.

## Schema Initialization Failure

Likely causes:

- SQL account cannot create schema/tables/indexes.
- Connection string points to the wrong database.
- SQL Server version does not support required features.
- Network or firewall blocks SQL access.

Fix:

- Run with a privileged migration/provisioning account.
- Call `GraphRagSchemaInitializer.EnsureSchemaAsync` in a provisioning step.
- Inspect startup logs from `GraphRagSchemaHostedService`.

## VECTOR Unsupported

Symptom:

SQL error near `VECTOR(1536)` or vector column creation.

Fix:

- Use SQL Server/Azure SQL with vector support.
- Do not remove vector columns unless the service implementation is changed.

## Missing IEmbeddings

Symptom:

```text
No IEmbeddings registered
```

Fix options:

- Register FabrCore server services with an embeddings model.
- Provide `IEmbeddings` in DI.
- Configure `FabrCore:HostUrl` and `IHttpClientFactory` for host API fallback.

## Search Without Scopes

Symptom:

```text
At least one scope is required
```

Fix:

- Resolve allowed scopes from user/tenant/agent configuration.
- Pass at least one non-empty scope to `ScopedSearchRequest` or
  `ScopedRelationshipRequest`.
- For plugins, set `AllowedScopes`.

## No Search Results

Check:

- Scope exists.
- Documents were ingested into the same scope.
- Embeddings were generated.
- Query limit is not too low.
- Entity type/domain filters are not over-restrictive.
- Domains/categories are taxonomy labels, not access scopes.

## Ingestion Extracts No Entities

Possible causes:

- `EnableExtraction=false`, or no explicit/conventional model can be resolved.
- `IFabrCoreChatClientService` is not registered.
- The named model is missing from `fabrcore.json`.
- Source content is too small or not meaningful.
- Extraction failed and status/error fields contain details.

Fix:

- Check `EnableExtraction`; verify the explicit alias or fallback `graphrag`/`default`.
- Confirm the model exists and can be resolved.
- Check `SourceDocumentDto.Status` and `ErrorMessage`.
- Check ingestion metrics and action audit logs.

## Duplicate Or Reused Documents

GraphRAG tracks source identity using:

- `ScopeKey`
- `SourceKind`
- `SourceKey`

It also tracks content hash, extraction-instruction hash, and version. If content and
instructions are unchanged, ingestion may
return `Reused = true`.

This is expected and avoids rewriting graph data unnecessarily.

## Microsoft.Testing.Platform Test Invocation

Use the GraphRAG executable test project in this repository:

```powershell
dotnet run --project src/FabrCore.Services.GraphRag.Tests --no-restore -- --filter FullyQualifiedName~Unit
```

## Slow Ingestion Or Missing Edges

Inspect model resolution, phase metrics, provider calls/output, retries and persisted
contributions. Cached chat-client instances are not cached responses. Provider prompt
caching still generates output. Parallel stage times are not additive wall time.
Reuse one ingestion service and bound document concurrency rather than giving every
document a new set of request limits.

Check `Status` and `ErrorMessage`: enabled document-plan extraction failures should
not be mistaken for successful complete graphs. A valid JSON response can still have
wrong relationships or missing endpoints; persistence requires resolvable endpoint
names. `ResolveExtractionEndpointAliases` is an opt-in explicit acronym resolver,
not fuzzy entity creation. Smoke retrieval success does not prove factual recall.

A policy-obligation mismatch can trigger full split retries despite valid JSON.
That option remains experimental. A suffix `[GraphRAG obligation v1: value]` in an
edge description comes from that experimental storage format, not a database column.
For remaining experiments and settings, read [ingestion and evaluations](ingestion-and-evals.md).

## Schema Or Cache Options Do Not Take Effect

Schema extraction requires a local `IFabrCoreChatClientService`; the Host API fallback
does not carry schemas. Check flag dependencies before enabling combinations.
`CacheTaxonomyResponses` only has effect with `UseExtractionResultCache`.
Embedding caching requires an explicit decorator; there is no automatic production
`UseEmbeddingCache` switch. Unchanged-document reuse may bypass the new extraction
configuration entirely; force rebuilding when testing a setting change.
