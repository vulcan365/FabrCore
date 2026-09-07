# FabrCore.Services.GraphRag.Tests

This project protects GraphRAG at three levels:

- Fast deterministic tests for scoped-request security contracts, email normalization,
  migration ordering, graph/vector DDL, and provenance constraints.
- SQL Server 2025 integration tests for concurrent/idempotent schema startup, scope CRUD,
  audit logging, document reuse, VECTOR ranking, entity filters, and cross-scope isolation.
- Opt-in live evaluations for embedding retrieval quality and LLM entity/relationship extraction.

## Local configuration

Tests now also discover `../FabrCore.Services.GraphRag.EvalConsole/appsettings.local.json`
from the repository. Its `ConnectionStrings:GraphRagTestDb` and
`Eval:ModelConfigurationPath` settings provide the existing `localhost/graphrag`
database and live models. Explicit environment variables retain precedence.
See the [console evaluator](../FabrCore.Services.GraphRag.EvalConsole/README.md)
for the downloaded plain-text corpus and repeatable performance comparisons.

The M005 legacy-schema sandbox is opt-in through
`FABRCORE_GRAPHRAG_ALLOW_DATABASE_CREATION=true`; leave it unset for an account
without CREATE DATABASE permissions. All other integration tests use the existing
database and clean up their own scopes.

`fabrcore.json` is intentionally ignored because it contains API keys. Copy
`fabrcore.example.json` to `fabrcore.json`, then configure a `default` chat model and an
`embeddings` model. Alternatively set `FABRCORE_GRAPHRAG_TEST_CONFIG` to an existing config path.

SQL tests accept `FABRCORE_GRAPHRAG_TEST_CONNECTION_STRING`, or these individual settings:

```powershell
$env:FABRCORE_GRAPHRAG_TEST_SERVER = "localhost"          # optional default
$env:FABRCORE_GRAPHRAG_TEST_DATABASE = "graphrag"         # optional default
$env:FABRCORE_GRAPHRAG_TEST_USER = "graphrag365"          # optional default
$env:FABRCORE_GRAPHRAG_TEST_PASSWORD = "..."              # required
```

Every database test uses a unique `tests:grag:*` scope and removes only its own documents,
chunks, entities, edges, scope registry row, telemetry, and audit records.

## Running tests

Run from the `src` directory:

```powershell
# Fast deterministic suite
dotnet test --project FabrCore.Services.GraphRag.Tests/FabrCore.Services.GraphRag.Tests.csproj -- --filter "TestCategory!=Integration&TestCategory!=Evaluation"

# SQL Server integration suite
dotnet test --project FabrCore.Services.GraphRag.Tests/FabrCore.Services.GraphRag.Tests.csproj -- --filter "TestCategory=Integration"

# Live chat + embedding evaluations
dotnet test --project FabrCore.Services.GraphRag.Tests/FabrCore.Services.GraphRag.Tests.csproj -- --filter "TestCategory=Evaluation"
```

The live retrieval gate requires Recall@3 = 100% and MRR >= 0.70. The extraction gate
requires at least two expected durable entities, one explicit relationship, and searchable
answer evidence. Evaluation tests are skipped as inconclusive when model or database
credentials are not configured.
