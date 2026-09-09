# Architecture and operational limits

## Persistence and retrieval

The service stores scoped entities, chunks, relationships, a bounded index, extraction
receipts, and optional derived summaries in SQL Server 2025 / Azure SQL with the required
VECTOR and SQL Graph support. Embedding dimensions must match the configured database/model.
GraphRag is not a prerequisite.

IAgentMemoryProvider caches services per trimmed scope in process. SQL persists knowledge
across service instances; a stable scope preserves access across agent activations. Host
code must authorize scope bindings. A scope key alone is not a tenant security policy.

Hot is a bounded pointer index, Warm is active durable content, and Cold is archived
retained content. Ordinary recall selects active headers and primary chunks. Archive search
can retrieve all retained embeddings. Graph expansion can add related memories beyond the
initial selection count. Semantic candidates, matched evidence, LLM planning, and summary
trees are optional, not implied by registration.

The harness uses a Microsoft Agent Framework context provider for bounded recall before
model runs. Persisted-history compaction is wired separately by WithMemoryCompaction, or
together through WithMemoryLifecycle. Context reduction, persisted chat history, and durable
memory serve different purposes; none should silently substitute for the others.

## Mutations and retries

The built-in SQL facade performs related mutations in a per-scope transaction with an
application lock. Entity/chunk/index changes and derived-summary invalidation participate in
that mutation. Extraction receipts record the source-history hash and resulting entity IDs
in the transaction, allowing retries of the same committed extraction to reuse results.

These boundaries do not establish universal conflict-free memory. Concurrent writers can
still disagree semantically; there is no public optimistic-version parameter that reconciles
every correction automatically. Prefer explicit ID updates and application ownership rules.
Different extraction histories are different receipt inputs.

Callbacks run after commit, so a callback failure can be reported after data is committed.
Audit is best effort. Do not blindly assume every exception means nothing was persisted.
Low-level store operations and custom store implementations need their own guarantees.

Extraction and compaction failures propagate. Memory-aware compaction leaves original
history available when extraction fails. Evaluate cancellation, extraction retries, shared
corrections, and crash recovery against the deployed SQL and Orleans configuration.

## Retention and summaries

Knowledge-changing SQL mutations invalidate derived summary nodes in that scope; rebuild
them when needed if summary trees are enabled. They do not leave a deleted fact intentionally
available through the existing derived summary tree.

Consolidation is explicit by default and can archive candidates. MemoryFileCap is a
consolidation trigger/scan bound, not a hard storage quota or scheduled deletion policy.
Forget removes the entity, chunks, relationships and invalidates derived summaries.
Audit, chat histories, backups, telemetry, and external copies require separate erasure rules.

## Release and operations

Keep the [frozen defaults](../../../memory-release-defaults.md) until a measured change is
accepted. Run scripts/Run-MemoryEvals.ps1 from the repository for sequential mode comparisons;
retain contemporary controls and do not automatically promote a candidate baseline.

Unit coverage is not production certification. Release checks must include the intended
SQL/model stack, bounded background runs, failures/cancellation, concurrent scope mutations,
and persistence recovery. Monitor recall quality, stale facts, storage growth, latency, and
memory context/model cost. Long-running agents also require checkpoints, scheduling,
permissions, recovery, and execution budgets. Memory alone does not guarantee endless execution.
