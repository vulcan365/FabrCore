# Pitfalls and Common Mistakes

## Critical: Memory-Context Markers

**Problem:** Injecting recalled memories into user messages without wrapping them in `FormatRecallContext` causes the extraction system to re-extract them during compaction, creating duplicates every compaction cycle.

**Wrong:**
```csharp
var recall = await _memory.RecallAsync(message.Message);
// BAD: raw text injection — extraction will re-save these as new memories
var context = string.Join("\n", recall.WarmMemories.Select(m => m.Content));
var chatMessage = new ChatMessage(ChatRole.User, message.Message + "\n" + context);
```

**Correct:**
```csharp
var recall = await _memory.RecallAsync(message.Message);
// GOOD: wrapped in <memory-context> markers — extraction skips these
var memoryContext = _memory.FormatRecallContext(recall);
var chatMessage = new ChatMessage(ChatRole.User, message.Message + memoryContext);
```

The same applies to `SyntheticImaginingResult.AggregatedRecall` — always pass it through `FormatRecallContext`.

---

## Critical: Startup Now Fails Fast

**Problem:** `AddAgentMemoryServices("MemoryDb")` registers a hosted service that throws at startup when:

- the named connection string is missing from configuration,
- the `mem` schema DDL fails (e.g. no VECTOR support, insufficient permissions), or
- `IEmbeddings` is not registered (no `"embeddings"` model entry in `fabrcore.json`).

This is deliberate — a host that silently runs without working memory is worse than one that refuses to start. But it surprises client-only hosts that register memory services purely to share types.

**Fix for client-only hosts:**
```csharp
builder.Services.AddAgentMemoryServices("MemoryDb", o =>
{
    // Downgrades missing connection string / missing IEmbeddings to LogError.
    // Saving or recalling memories will still fail at call time.
    o.AllowStartupWithoutEmbeddings = true;
});
```

A schema DDL failure still throws regardless of this flag — if the database is reachable, the schema must be creatable.

---

## Critical: EmbeddingDimensions / Embedding Model Changes Break Inserts

**Problem:** The `VECTOR(n)` column dimension on `mem.MemoryChunk` and `mem.MemorySummaryNode` is fixed when the schema is first created from `AgentMemoryOptions.EmbeddingDimensions` (default 1536). If you later change `EmbeddingDimensions` — or switch to an embeddings model that produces a different vector size — inserts and searches fail with dimension-mismatch errors. The schema initializer only creates missing tables; it never alters existing VECTOR columns.

**Fix:** Drop the `mem` schema (all memories are lost) and let startup recreate it with the new dimension:

```sql
-- destructive: removes all agent memories, scopes, and audit history
DROP TABLE IF EXISTS mem.MemoryAuditLog, mem.MemoryScope, mem.MemorySummaryNode,
    mem.MemoryChunk, mem.MemoryRelationship, mem.MemoryEntity;
DROP SCHEMA IF EXISTS mem;
```

Pick the dimension to match your embeddings model **before** first startup (e.g. 1536 for text-embedding-3-small).

---

## Shared Scopes Concentrate Rows — Consolidation Gets Expensive

**Problem:** A shared scope (e.g. `"bank-recon"` used by ten agents) accumulates memories from every agent bound to it. Deduplication during consolidation runs an O(n²) CROSS JOIN over the scope's chunks (`VECTOR_DISTANCE` on every pair), so cost grows quadratically with the number of rows in the scope — ten agents writing one pool hits the cap ten times faster than isolated agents.

**Mitigations:**
- Tune `Consolidation.MemoryFileCap` (default 200) deliberately for shared scopes — it caps entities per **scope**, not per agent.
- Consolidate shared scopes on a regular cadence (scheduled maintenance calling `ConsolidateAsync` or `IMemoryAdminService.ConsolidateScopeAsync`) rather than letting rows pile up until auto-consolidation fires mid-conversation.
- Keep `Consolidation.EnableAutoConsolidation = false` for user-facing shared-scope agents — an unlucky save triggers the full O(n²) pass plus LLM merges.

---

## Shared Scopes and Hot-Index Concurrency

Hot-index writes are serialized per scope via a SQL application lock: `IMemoryStore.ModifyIndexContentAsync` takes `sp_getapplock` on resource `mem-index-{scope}` (transaction-scoped, exclusive) so concurrent shared-scope agents don't lose each other's index entries. Two consequences:

- **Correctness is handled for you** — do not build your own read-modify-write over `GetIndexContentAsync`/`UpsertIndexContentAsync` for a shared scope; use the service-layer operations which route through the lock.
- **Long-held locks time out** — the applock waits up to 15 seconds. Under heavy concurrent saving into one scope (e.g. many agents compacting simultaneously), index writes can queue and eventually throw on timeout. Stagger consolidation/compaction of agents sharing a scope where possible.

---

## Critical: Using Plugin When Automatic Compaction Is Sufficient

**Problem:** Registering `AgentMemoryPlugin` tools (SaveMemory, RecallMemories, etc.) on agents that don't need the LLM to autonomously manage memories mid-conversation. The plugin adds 8 tools to the LLM's context, but the LLM may never call them — especially on task-focused agents (data query, domain specialist, etc.). This gives a false sense that memories are being managed when they aren't.

**When the plugin is wrong:**
```csharp
// BAD: Data query agent with plugin tools — LLM is focused on querying data,
// it will never spontaneously call save_memory
var memoryPlugin = new AgentMemoryPlugin();
tools.Add(AIFunctionFactory.Create(...SaveMemory...));   // Never called
tools.Add(AIFunctionFactory.Create(...RecallMemories...)); // Never called
// ... 6 more unused tools bloating the context
```

**Correct — use the recommended pattern instead:**
```csharp
// GOOD: Memory recall in OnMessage, extraction in OnCompaction
// No plugin tools needed — compaction handles saving automatically
public override async Task<AgentMessage> OnMessage(AgentMessage message)
{
    var recall = await _memory!.RecallAsync(message.Message);
    var memoryContext = _memory.FormatRecallContext(recall);
    // ... run LLM with enriched context
}

public override async Task<CompactionResult?> OnCompaction(...)
{
    return await _compactionHandler!.CompactAsync(chatHistoryProvider, compactionConfig);
}
```

**When the plugin IS appropriate:** Only use the plugin pattern for agents where the LLM must autonomously decide when to save memories mid-conversation — for example, a personal assistant that learns user preferences in real-time, or a coaching agent that tracks progress observations as they arise.

---

## Critical: Not Overriding OnCompaction

**Problem:** Without the `MemoryCompactionHandler`, the default compaction summarizes away conversation content without extracting durable knowledge first. Memories that should persist get lost in the generic summary.

**Wrong:**
```csharp
// No OnCompaction override — default compaction runs, memories lost
public class MyAgent : FabrCoreAgentProxy { /* ... */ }
```

**Correct:**
```csharp
public override async Task<CompactionResult?> OnCompaction(
    FabrCoreChatHistoryProvider chatHistoryProvider,
    CompactionConfig compactionConfig,
    int estimatedTokens = 0)
{
    return await _compactionHandler!.CompactAsync(chatHistoryProvider, compactionConfig);
}
```

If you use memory but don't override OnCompaction, your agent will lose knowledge every time compaction triggers.

---

## Data Query Agent Saving Stale Facts

**Problem:** Agents that query databases or live data sources have their conversation history extracted during compaction. The extraction LLM saves query results as `Fact` memories (e.g., "Job 1 has plates 11001 and 11002"), but these are stale the moment they're created because the database can change at any time.

**Fix:** Enable `PointInTimeMemories` for agents that work with live data:
```csharp
builder.Services.AddAgentMemoryServices("MyDb", options =>
{
    options.PointInTimeMemories = true;
});
```

This causes:
- The extraction prompt to discourage saving transient data as Facts
- All extracted memories to be marked as point-in-time snapshots
- Recalled memories to always show a `[Snapshot]` freshness warning
- Point-in-time memories to be pruned after 3 days instead of 30

For mixed agents (some durable, some transient), use the per-memory flag:
```csharp
await memoryService.SaveMemoryAsync(title, type, content, isPointInTime: true);
```

---

## Hot Index Budget Is Small — Don't Waste It

The hot index defaults to **20 entries / 3,000 tokens** (`HotIndex.MaxEntries` / `HotIndex.MaxTokens`). Every memory saved via `SaveMemoryAsync` gets a hot index pointer. If you save ephemeral data (tool outputs, intermediate reasoning, transient status), important memories get evicted from the hot index as oldest entries are pushed out. In a shared scope, every agent's saves compete for the same 20 slots.

**Anti-pattern:** Saving every piece of information as a memory.

**Best practice:** Save only durable knowledge that will still be true and useful in future conversations. Prefer fewer high-confidence memories over many speculative ones. Use `Observation` type for uncertain knowledge — it gets pruned more aggressively during consolidation.

---

## Not Tracking alreadySurfacedIds

**Problem:** Without tracking surfaced memory IDs, the same warm memories resurface every turn, wasting the retrieval budget (5 per query) and cluttering context with repeated information.

**Wrong:**
```csharp
public override async Task<AgentMessage> OnMessage(AgentMessage message)
{
    // BAD: no surfaced tracking — same memories repeat every turn
    var recall = await _memory.RecallAsync(message.Message);
    // ...
}
```

**Correct:**
```csharp
private readonly HashSet<Guid> _surfacedMemoryIds = [];

public override async Task<AgentMessage> OnMessage(AgentMessage message)
{
    var recall = await _memory.RecallAsync(message.Message, _surfacedMemoryIds);
    foreach (var warm in recall.WarmMemories)
        _surfacedMemoryIds.Add(warm.Id);
    // ...
}
```

This applies to both `RecallAsync` and `ImagineAsync`.

---

## RecallAsync vs. ImagineAsync — Wrong Tool for the Job

**Problem:** Using `RecallAsync` for multi-turn conversations where the user's latest message doesn't capture the full context of what they need.

| Scenario | Use |
|----------|-----|
| User asks a self-contained question | `RecallAsync` |
| User says "yes, do that" (depends on conversation context) | `ImagineAsync` |
| First message in a conversation | `RecallAsync` |
| User builds on multiple previous turns | `ImagineAsync` |
| Performance-critical path, minimize LLM calls | `RecallAsync` |

**ImagineAsync** costs one extra LLM call (query generation) but runs all downstream searches in parallel. Use it when conversation context matters more than latency.

---

## Confusing Hot Memory with Chat History

Hot memory is **not** chat history. They serve fundamentally different purposes:

| | Hot Memory | Chat History |
|---|---|---|
| **Lifetime** | Persists across sessions | Lost on session end or compaction |
| **Content** | Classified entries (Fact/Rule/Instruction/Observation/Procedural) | Raw conversation messages |
| **Size** | Bounded (20 entries / 3,000 tokens), oldest evicted | Grows until compacted |
| **Purpose** | "What I permanently know" | "What was said this session" |

Do not attempt to "replay" chat history through the memory system. Do not save individual messages as memories. Memories should be synthesized knowledge, not raw conversation.

---

## Critical: Missing or Mismatched Model Configuration

**Problem:** The memory system uses LLM calls for extraction, relevance selection, and imagining. Each operation resolves a named model from `fabrcore.json` via `IFabrCoreChatClientService`. If the model name doesn't match a `fabrcore.json` entry, the operation **silently returns empty results** — no error is thrown, no memories are extracted.

This is the most common cause of "compaction runs but no memories are saved."

**What needs to match** (all under `options.Models`):

| Setting | Default value | What fails if missing |
|---|---|---|
| `Models.CompactionModelName` | `"default"` | Memory extraction during compaction (Tier 2) — zero memories saved |
| `Models.RelevanceModelName` | `"default"` | Warm memory selection — falls back to recency, less accurate |
| `Models.ImaginingModelName` | `"default"` | Synthetic imagining — returns empty, falls back to plain recall |
| `"embeddings"` (hardcoded) | N/A | Vector embeddings — save and search operations fail (and startup fails fast when `IEmbeddings` is missing entirely) |

**Fix:** Ensure your `fabrcore.json` has matching model entries. At minimum, you need `"default"` and `"embeddings"`:

```json
{
  "ModelConfigurations": [
    {
      "Name": "default",
      "Provider": "OpenAI",
      "Model": "gpt-4o",
      "ApiKeyAlias": "openai"
    },
    {
      "Name": "embeddings",
      "Provider": "OpenAI",
      "Model": "text-embedding-3-small",
      "ApiKeyAlias": "openai"
    }
  ]
}
```

**Debugging:** Look for these log messages:
- `"Failed to resolve chat client 'default'"` — Models.CompactionModelName doesn't match
- `"Chat client 'X' not available, skipping LLM selection"` — Models.RelevanceModelName doesn't match
- `"Tier 2: memory extraction failed"` — API error or timeout during extraction

---

## SQL Server Version Requirement

The `mem` schema uses the `VECTOR` data type, which requires **SQL Server 2025** or **Azure SQL Database** with vector support enabled. Running against SQL Server 2022 or earlier will fail on schema creation with a cryptic DDL error — and because startup now fails fast on DDL errors, the host will not start.

---

## Auto-Consolidation Gotcha

When `Consolidation.EnableAutoConsolidation = true`, consolidation runs during `SaveMemoryAsync` if the memory count exceeds `Consolidation.MemoryFileCap` (200). This means a save operation can trigger deduplication, staleness pruning, and contradiction resolution — all of which make LLM calls. A simple save can take 10-30 seconds instead of the expected sub-second. Shared scopes hit the cap sooner (many agents, one pool).

**Recommendation:** Leave `Consolidation.EnableAutoConsolidation = false` (default) for user-facing agents. Instead, call `ConsolidateAsync` explicitly during idle periods or scheduled maintenance (or `IMemoryAdminService.ConsolidateScopeAsync` from admin tooling), or let the compaction cascade handle it during OnCompaction.

---

## Forgetting to Re-Inject Hot Index After Session Restart

The hot index is injected into `config.SystemPrompt` during `OnInitialize`. If the agent restarts (silo rebalancing, deployment) but the system prompt was already built without the hot index, the agent loses awareness of its memories until the next OnInitialize.

**The library handles this for compaction** (`HotIndex.ReInjectAfterCompaction = true`), but session restarts are your responsibility in OnInitialize. Always call `GetMemoryIndexAsync` and append to the system prompt.

---

## Taxonomy Miscategorization

The extraction LLM sometimes miscategorizes memories — saving a user preference as a `Fact` instead of an `Instruction`, or a business rule as an `Observation`. This matters because:

- **Instructions are protected from staleness pruning** — if a fact is saved as an Instruction, it will never be auto-pruned even when outdated.
- **Observations are aggressively pruned** — if a critical rule is saved as an Observation, it may get removed during consolidation.

**Mitigation:** If you override the extraction prompt or use the plugin with custom system prompts, include clear examples of each type with domain-specific guidance.

---

## Synthetic Imagining with Trivial Conversations

`ImagineAsync` sends the last 20 messages to an LLM for query generation. If the conversation is only 1-2 messages deep (e.g., first turn), the LLM has almost no context to work with, and the extra LLM call adds latency without improving recall quality.

**Recommendation:** Use `RecallAsync` for the first 1-2 turns of a conversation. Switch to `ImagineAsync` once there's enough conversation context (3+ turns) for the LLM to identify implicit needs.

---

## Description Hook Truncation

Hot index entries store a `DescriptionHook` limited to approximately 120 characters. If the memory's description exceeds this, it's silently truncated. This means the hot index shows a partial description — the full content is only available when the warm memory is loaded.

This is by design (keeps the hot index within its token budget), but be aware that the LLM making retrieval decisions sees only the truncated hook, not the full description.
