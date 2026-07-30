# FabrCore.Services.Memory.Tests

This project protects the memory library at three levels:

- Fast deterministic tests for taxonomy, scope resolution, provider caching, hot-index bounds,
  retrieval planning/selection, CRUD orchestration, extraction parsing, synthetic imagining,
  plugin validation, and tool-result compression.
- SQL Server 2025 integration tests for schema creation, VECTOR search, SQL Graph relationships,
  scope isolation/registry, auditing, CRUD, and concurrent hot-index writes.
- Live-model evaluations for durable-memory extraction and end-to-end retrieval quality.

## Local configuration

`fabrcore.json` is intentionally ignored because it contains API keys. Copy
`fabrcore.example.json` to `fabrcore.json` and configure a `default` chat model plus an
`embeddings` model before running evaluations.

Database tests accept either a full connection string:

```powershell
$env:FABRCORE_MEMORY_TEST_CONNECTION_STRING = "Server=...;Database=...;..."
```

or these individual settings:

```powershell
$env:FABRCORE_MEMORY_TEST_SERVER = "localhost"       # optional default
$env:FABRCORE_MEMORY_TEST_DATABASE = "fabrcore-testing" # optional default
$env:FABRCORE_MEMORY_TEST_USER = "fabrcore365"      # optional default
$env:FABRCORE_MEMORY_TEST_PASSWORD = "..."           # required
```

Every database test creates a unique `tests:*` memory scope and deletes only that scope's
entities, chunks, relationships, summaries, registry row, and audit entries during cleanup.

## Running tests

Run from the `src` directory so `global.json` selects Microsoft.Testing.Platform:

```powershell
# Fast deterministic suite
dotnet test --project FabrCore.Services.Memory.Tests/FabrCore.Services.Memory.Tests.csproj -- --filter "TestCategory!=Integration&TestCategory!=Evaluation"

# SQL Server integration suite
dotnet test --project FabrCore.Services.Memory.Tests/FabrCore.Services.Memory.Tests.csproj -- --filter "TestCategory=Integration"

# Live chat + embedding evaluations
dotnet test --project FabrCore.Services.Memory.Tests/FabrCore.Services.Memory.Tests.csproj -- --filter "TestCategory=Evaluation"
```

The retrieval eval reports and enforces:

- LLM warm-memory selection: Recall@2 = 100%, MRR >= 0.75.
- VECTOR archive retrieval: Recall@3 = 100%, MRR >= 0.65.
- Extraction: both durable test facts retained and no transient dashboard/on-call values stored.
