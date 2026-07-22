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
- Configure `FabrCoreHostUrl` and `IHttpClientFactory` for host API fallback.

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

- `extractionModelName` was omitted.
- `IFabrCoreChatClientService` is not registered.
- The named model is missing from `fabrcore.json`.
- Source content is too small or not meaningful.
- Extraction failed and status/error fields contain details.

Fix:

- Pass `extractionModelName` to `AddGraphRagServices`.
- Confirm the model exists and can be resolved.
- Check `SourceDocumentDto.Status` and `ErrorMessage`.
- Check ingestion metrics and action audit logs.

## Duplicate Or Reused Documents

GraphRAG tracks source identity using:

- `ScopeKey`
- `SourceKind`
- `SourceKey`

It also tracks content hash and version. If content is unchanged, ingestion may
return `Reused = true`.

This is expected and avoids rewriting graph data unnecessarily.

## Microsoft.Testing.Platform Test Invocation

If `dotnet test` reports old VSTest target errors, run tests from the directory
that contains `global.json` and use `--project`:

```powershell
dotnet test --project FabrCore.Tests\FabrCore.Tests.csproj --filter "FullyQualifiedName~GraphRag"
```

