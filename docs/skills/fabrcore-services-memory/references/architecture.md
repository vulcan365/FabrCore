# Architecture and operational limits

## Persistence and retrieval

`AddAgentMemoryServices` registers singleton infrastructure and a cached facade per trimmed scope key. SQL creates the `mem` schema with `MemoryEntity` (graph node), `MemoryChunk` (content and VECTOR embedding), `MemoryRelationship` (graph edge), `MemoryScope`, `MemoryAuditLog`, and `MemorySummaryNode`. No GraphRag service is required. The hot index is JSON in an internal `__MEMORY_INDEX__` sentinel entity.

Save creates Warm content plus a bounded index pointer. Same-type similarity matching can update an active entity; snapshots and metadata-bearing saves skip merging. Header scans exclude Cold. Default recall uses a planner and header selection; unavailable chat selection falls back to recency. Graph expansion may add additional memories beyond the selection limit. Archive vector search spans all temperatures. Missing embeddings make a memory unavailable to vector search, although active memories remain accessible through header selection.

Summary-tree building and LLM planning are opt-in. Summary nodes are derived text and can be stale after edits/deletions until rebuilt. `FormatRecallContext` includes hot pointers, selected content, archive matches, summary results, and freshness warnings. The returned text is not injected automatically.

## Concurrency and failure behavior

Hot-index read/modify/write uses a SQL application lock per scope. That protects index writers, not an entire save or merge transaction. Entity/chunk/index writes are separate operations; partial failures and concurrent updates can require reconciliation. Merge-on-save is not idempotent and has no optimistic concurrency token. Serialize conflicting application updates where needed; use returned IDs instead of retries that assume exactly-once behavior.

The provider cache is process-local and can be evicted. Persistence survives a process restart if the same database and scope are used. A caller-selected scope is a namespace, not a tenant access check: derive it in trusted application code and enforce access before selecting shared scopes.

Startup fails on missing connection configuration, unsupported schema DDL, or missing embeddings registration (unless explicitly relaxed). Runtime persistence errors can propagate. Audit is best-effort and can fail independently. Do not equate graceful LLM fallbacks with guaranteed availability.

## Consolidation and retention

Consolidation deduplicates compatible active memories, archives the source, prunes stale candidates to Cold, resolves contradictions, and enforces index caps. Snapshots, differing types, metadata-bearing entries, and already-Cold rows are excluded from deduplication. Instructions are excluded from age pruning. Without a merge model, the archived source still preserves its original content. Consolidation does not cap total SQL storage or provide a retention scheduler.

`ForgetMemoryAsync` deletes entity content and its edges, and removes its index pointer. It does not erase existing conversation copies, audit records, or summary text. Applications requiring complete erasure must coordinate those stores and derived artifacts.

## Deployment checks

Use the test executable runner from the repository root:

```powershell
dotnet run --project src/FabrCore.Services.Memory.Tests/FabrCore.Services.Memory.Tests.csproj -- --filter 'TestCategory!=Integration&TestCategory!=Evaluation'
dotnet run --project src/FabrCore.Services.Memory.Tests/FabrCore.Services.Memory.Tests.csproj -- --filter 'TestCategory=Integration'
dotnet run --project src/FabrCore.Services.Memory.Tests/FabrCore.Services.Memory.Tests.csproj -- --filter 'TestCategory=Evaluation'
```

SQL tests require `FABRCORE_MEMORY_TEST_CONNECTION_STRING` or documented test credentials. Evaluations additionally require configured chat/embedding models. Skipped tests are not evidence that SQL persistence or model quality passed. Before rollout, exercise restart persistence, scope isolation, save/update/archive/restore/delete, concurrent writers, and retrieval relevance with representative data. Confirm database backup/restore and account for model/embedding costs.
