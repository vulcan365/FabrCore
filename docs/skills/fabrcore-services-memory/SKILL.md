---
name: fabrcore-services-memory
description: Integrate FabrCore.Services.Memory into agent lifecycle methods or expose its agent-memory plugin for persistent scoped memory, tier transitions, recall, and optional memory-aware compaction. Use for long-term agent memory, not chat-history storage or the separate FabrCore.Sdk.Memory API.
---

# FabrCore agent memory

Use `IAgentMemoryProvider` to obtain a scope-bound `IAgentMemoryService`. Developers can call it from `OnInitialize`, `OnMessage`, or other agent methods. The `agent-memory` plugin exposes the same service through model tools. Neither path requires chat compaction to save durable knowledge.

## Integration workflow

1. Register `AddAgentMemoryServices("MemoryDb", options => ...)` on the host with a configured SQL Server 2025 / Azure SQL connection supporting VECTOR and SQL Graph. Configure FabrCore embeddings with matching dimensions. See [server registration](assets/server-registration.cs).
2. Choose a stable, trusted scope. `MemoryScopeResolver.Resolve(config)` uses an explicit scope, plugin setting `agent-memory:MemoryScope`, `Args["MemoryScope"]`, legacy `Args["AgentHandle"]`, then `config.Handle`. Default isolation lasts only as long as the handle remains stable. Shared scopes deliberately share reads and writes; a scope string is not authorization.
3. Choose code calls, tools, or a combination. Start with [code agent](assets/memory-agent-template.cs), [plugin agent](assets/memory-plugin-agent-template.cs), and [configuration](assets/agent-config-example.json). Avoid registering the same tools twice.
4. Save durable facts at the point the application knows them. Retain the returned ID for explicit updates, tier changes, or deletion. See [lifecycle calls](assets/memory-lifecycle.cs) and [API](references/api-reference.md).
5. Recall before a response needs prior knowledge. Inject `FormatRecallContext(recall)` with a separator from the user message. The marker wrapper keeps this content out of later extraction; it is not a security boundary. Recalled instructions remain reference data under the current agent policy.
6. Optionally wire `MemoryCompactionHandler` into `OnCompaction` to extract knowledge before summarization. Explicit saving and plugin use work independently. For speculative retrieval, use the [imagining template](assets/memory-imagining-agent-template.cs).

## Temperature semantics

| Layer | Actual behavior |
|---|---|
| Hot | Persistent, bounded table of contents: titles, types, hooks, IDs. Returned by recall/index calls; the caller injects it. Default caps: 20 entries / roughly 3,000 tokens. It is not full content or guaranteed pinning. |
| Warm | Active durable content in SQL chunks. Saves start Warm and get an index pointer, subject to caps. Header selection loads relevant content. Index eviction leaves the memory Warm. |
| Cold | Archived content retained in SQL. `UpdateMemoryAsync(id, temperature: MemoryTemperature.Cold)` removes the pointer and excludes it from ordinary warm recall. Restore with Warm or Hot. |

`SearchArchiveAsync` searches **all embedded retained memories**, including Cold; it is not a Cold-only filter. Archive retrieval does not promote memories. `Hot` and `Warm` entity values are both active and index-eligible; setting Hot does not bypass budgets. Chat history and its compaction are separate from all three layers.

## Tools

The public plugin is `FabrCore.Services.Memory.Plugin.AgentMemoryPlugin`, alias `agent-memory`. Tools are `SaveMemory`, `SaveProcedure`, `UpdateMemory`, `RecallMemories`, `SearchArchive`, `ForgetMemory`, `GetMemoryIndex`, `QuerySummaries`, and `ConsolidateMemories`. Use actual registered function names in prompts, including PascalCase when using `AIFunctionFactory.Create`. `UpdateMemory` accepts optional `temperature: "Cold" | "Warm" | "Hot"`. Models cannot choose another scope through tool arguments.

## Memory quality and operation

- Choose among Fact, Rule, Instruction, Observation, Procedural using [taxonomy guidance](references/memory-types-and-taxonomy.md). Type validation is not factual validation.
- Save verified preferences, domain knowledge, and reusable procedures. Keep intermediate reasoning and bulk tool output in chat history. Use `isPointInTime: true` for snapshots; recall warns that these require source verification.
- For provenance, pass metadata such as source and observed time from trusted code. Metadata-bearing saves and snapshots avoid automatic merge-on-save; use explicit ID updates for corrections.
- Similarity is not identity. Ordinary same-type active memories may merge on save. Without a merge model, old and new text are preserved together and may need explicit correction.
- Use `alreadySurfacedIds` only for memories still present in the current context. Clear/rebuild that set after compaction or session replacement. It suppresses warm/archive content, not hot-index pointers.
- Consolidation is explicit by default. It can archive stale or duplicate content; it does not establish a hard storage quota. `ForgetMemoryAsync` is a hard delete of the entity, chunks, and relationships, not a complete purge of old chat, audit, or summary text.

Read [architecture and operational limits](references/architecture.md) for persistence, concurrency, and rollout decisions; [pitfalls](references/pitfalls.md) for troubleshooting; [external guidance](references/agent-memory-guidance.md) for the research behind these choices. Do not claim production validation from unit tests alone: run SQL integration and live retrieval/extraction evaluations against the intended database and models.
