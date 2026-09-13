# FabrCore.Services.Memory readiness review

Reviewed 2026-09-05 against this checkout. The code-call facade and plugin support the intended agent workflow after the fixes below. Deterministic checks pass. SQL persistence and live-model quality have not been validated in this environment, so this is not production sign-off.

## Intended developer workflow

Register `AddAgentMemoryServices` on the FabrCore host, obtain `IAgentMemoryService` from `IAgentMemoryProvider` with a stable authorized scope, and call save/recall/update/archive/restore/forget from agent lifecycle or application methods. Optional memory-aware chat compaction extracts durable knowledge before summarization; it is not required for explicit memory calls. Expose `agent-memory` when the model should decide when to use these operations.

Hot is a bounded persistent index of pointers, not a third full-content store. Warm is active SQL content. Cold is retained archived content. Hot/Warm entity values are both active; neither guarantees pinning. Archive search includes all temperatures. This preserves FabrCore's architecture rather than introducing another memory backend or taxonomy.

## Defects addressed

| Finding | Change |
|---|---|
| Cold updates re-added their index pointer; headers included Cold | Cold updates remove the pointer; SQL headers and ordinary recall exclude Cold content. Warm/Hot updates restore index eligibility. |
| Plugin could save and forget but could not update/archive/restore | Added `UpdateMemory` with validated type/temperature/ID arguments and a real AIFunction invocation test. |
| Formatted recall discarded archive-only and summary-only results | Includes both channels and their archive freshness warnings; plugin recall also exposes them. |
| Graph expansion could reintroduce already-surfaced content | Applies the caller exclusion set to graph results as well as selected content. |
| Merge fallback replaced prior knowledge; failed merge writes could create duplicates | No-model fallback retains both texts; write failures propagate instead of starting another insert. Snapshots, Cold, and metadata-bearing entries avoid automatic merge-on-save. |
| Deduplication hard-deleted source entities despite archival documentation | Archives compatible duplicate sources and preserves their content/edges. Skips Cold, snapshots, different types, and metadata-bearing entries. |
| Age pruning could archive standing instructions without a model | Excludes Instruction from age-based candidate selection. |
| Embedding failures left a vector representing outdated content | Clears the stale vector in service and SQL updates. Explicit content updates also repair a missing primary chunk. |
| Service fallback catches swallowed cancellation | Service catches now let cancellation propagate. This does not claim cancellation guarantees for every optional collaborator. |
| Distributed examples used incorrect tool names, host registration, and unavailable history variable | Corrected examples; imagining uses the resolved shared scope and clears surfaced IDs after compaction. Removed stale initialization-only memory injection. |

## Validation

- **57 deterministic tests passed**, including new update-tool invocation, archive/restore behavior, archive/summary formatting, cancellation, failed merge, missing-chunk repair, cold exclusion, dedup retention, and Instruction preservation.
- **7 SQL integration tests skipped**: `FABRCORE_MEMORY_TEST_CONNECTION_STRING` / password is absent. Added coverage for Cold header exclusion, retained archive search, restoration, and stale-vector clearing; that SQL coverage remains unexecuted.
- **2 live-model evaluations skipped** for the same database prerequisite. No claim is made about retrieval metrics, extraction quality, or production embeddings.
- Three agent templates plus the lifecycle asset compiled in a temporary project: **0 errors, 0 warnings**. Host registration compiled separately against the current host: **0 errors, 0 warnings**.
- Skill validator passed; local Markdown links resolve. Test README now uses the executable runner that works in this checkout.

Reproduce commands and configure the database using [the test README](../src/FabrCore.Services.Memory.Tests/README.md). The distributed client entry point is [the memory skill](skills/fabrcore-services-memory/SKILL.md).

## Remaining limits and rollout gates

1. Run SQL integration and live-model evaluations on the intended SQL/VECTOR deployment and configured chat/embedding models. Verify restart persistence and representative recall quality before rollout.
2. Save/update/merge span separate entity, chunk, index, and audit operations. SQL locks protect hot-index mutations only; partial failures and conflicting writers can still need reconciliation. No exactly-once/idempotency or optimistic concurrency guarantee is present.
3. A scope key is a data partition, not an authorization boundary. Derive stable agent/user/tenant scopes in trusted code before exposing shared memory.
4. Graph expansion/content size can exceed the warm selection budget. Vector-only warm retrieval filters Cold after vector ranking, so a result set dominated by Cold may yield fewer active matches. Apply application context budgets and measure archive-heavy corpora.
5. Forget deletes the entity, chunks, relationships, and index pointer, but existing chat, audit, and derived summaries can retain text. Summary rebuilding and complete-erasure workflows need application coordination.
6. Consolidation is heuristic, not a truth oracle or storage quota. Archiving duplicates increases retained storage compared with prior hard deletion. Without a merge model, conflicting versions remain available for explicit correction. Auto-consolidation is optional and not a durable scheduled job.
7. Hot-index pointer caps do not pin Hot content. Applications that require explicit guaranteed full-content hot memory need a separately designed policy; this review does not silently change that public contract.

The client skill now documents these semantics, provides code and tool examples, and separates essential instructions from focused references. It incorporates scoped long-term memory and selective context assembly guidance from [LangChain](https://docs.langchain.com/oss/python/concepts/memory) and [Anthropic](https://www.anthropic.com/engineering/effective-context-engineering-for-ai-agents), while retaining FabrCore's five memory types and service/plugin model. These are engineering patterns, not a universal temperature standard.
