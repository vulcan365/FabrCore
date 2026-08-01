# Memory Architecture

## Design Principles

This library is inspired by production AI coding agent memory systems with these core principles:

1. **Three-temperature retrieval** — Not everything needs to be in context. Hot memories are always loaded, warm memories are selectively retrieved, cold memories are searched only when needed.

2. **Scoped memory pools** — Every memory belongs to a `ScopeKey`. The default scope is the agent's own handle (isolated memory); configuration can bind many agents to one named shared scope (e.g. `"bank-recon"`) so they learn as a group. Scopes are registered in `mem.MemoryScope` and every memory-changing action is audited in `mem.MemoryAuditLog`.

3. **Taxonomy enforcement** — Memory stores drift when agents save everything. Hard enforcement (throw on invalid type) prevents ephemeral state from polluting the memory store.

4. **Bounded budgets** — The hot layer has fixed caps (20 entries and 3,000 tokens). Consolidation keeps the warm layer from growing unbounded. This prevents context window bloat.

5. **Freshness tracking** — Memories older than the configured threshold get staleness warnings injected alongside their content, prompting the agent to verify before relying on them.

6. **Archive, don't delete** — Consolidation demotes stale/contradicted memories to Cold temperature instead of deleting them. They remain searchable but won't appear in normal retrieval.

7. **Graceful degradation** — LLM-dependent features (relevance selection, imagining, consolidation) fall back to heuristics or return empty results when the LLM is unavailable. The agent never crashes from a memory subsystem failure. (Startup, by contrast, fails fast on missing connection string, schema DDL failure, or missing `IEmbeddings` — see `AllowStartupWithoutEmbeddings`.)

## SQL Storage Layer

All data lives in the self-contained `mem` schema (created automatically on startup). Content and embeddings live on chunks; entities are concept nodes.

```
mem.MemoryEntity (NODE table)
+-- EntityId (GUID)
+-- ScopeKey (NVARCHAR(200)) -- partition key (agent handle or shared scope)
+-- Name (NVARCHAR(500)) -- maps to MemoryEntry.Title
+-- EntityType (NVARCHAR(100)) -- maps to MemoryType enum
+-- Description (NVARCHAR(MAX))
+-- Content (NVARCHAR(MAX)) -- only used by the __MEMORY_INDEX__ sentinel row
+-- Visibility (NVARCHAR(20)) -- maps to MemoryTemperature enum
+-- IsPointInTime (BIT)
+-- Metadata (NVARCHAR(MAX)) -- JSON dictionary
+-- CreatedAt (DATETIME2)
+-- UpdatedAt (DATETIME2)

mem.MemoryChunk -- primary content + embedding store (1+ chunks per entity)
+-- ChunkId (GUID, PK)
+-- ScopeKey (NVARCHAR(200))
+-- EntityId (GUID)
+-- Content (NVARCHAR(MAX))
+-- Embedding VECTOR(EmbeddingDimensions) -- cosine similarity search
+-- ChunkIndex (INT)
+-- Metadata (NVARCHAR(MAX))
+-- CreatedAt / UpdatedAt (DATETIME2)

mem.MemoryRelationship (EDGE table)
+-- ScopeKey (NVARCHAR(200))
+-- RelationshipType (NVARCHAR(200))
+-- Description (NVARCHAR(MAX))
+-- Weight (FLOAT)
+-- Metadata (NVARCHAR(MAX))

mem.MemorySummaryNode -- hierarchical topic rollups (built during consolidation)
+-- NodeId (GUID, PK)
+-- ScopeKey (NVARCHAR(200))
+-- ParentNodeId (GUID, NULL)
+-- Depth (INT)
+-- Topic (NVARCHAR(500))
+-- Summary (NVARCHAR(MAX))
+-- Embedding VECTOR(EmbeddingDimensions)
+-- MemberCount (INT)

mem.MemoryScope -- scope registry
+-- ScopeKey (NVARCHAR(200), PK)
+-- Description (NVARCHAR(MAX), NULL)
+-- IsShared (BIT) -- true = explicitly created shared pool
+-- CreatedAt (DATETIME2)
+-- CreatedBy (NVARCHAR(200), NULL)

mem.MemoryAuditLog -- best-effort who/what/when trail
+-- AuditId (BIGINT IDENTITY, PK)
+-- OccurredAt (DATETIME2(3))
+-- ActionType (NVARCHAR(80))
+-- ScopeKey (NVARCHAR(200))
+-- MemoryId (GUID, NULL)
+-- ActorId / ActorName (NVARCHAR(200), NULL)
+-- Summary (NVARCHAR(500), NULL)
+-- Payload (NVARCHAR(MAX), NULL)
+-- DurationMs (BIGINT, NULL)
```

The VECTOR column dimension comes from `AgentMemoryOptions.EmbeddingDimensions` (default 1536) and is **fixed at schema creation** — changing it afterwards requires dropping the `mem` schema.

### Scope Registry

Shared scopes are created explicitly (`IMemoryScopeService.CreateScopeAsync` / `IMemoryAdminService.CreateSharedScopeAsync`, `IsShared = true`). Agent-handle scopes are auto-registered on first save via `EnsureScopeAsync` (MERGE, `IsShared = false`) so admin tooling can enumerate them. Scope keys that predate registration still appear in admin listings with `IsRegistered = false`.

### Hot Layer Index Storage

The hot layer index is stored as a single well-known entity row per scope:

- `Name = "__MEMORY_INDEX__"`
- `EntityType = "MemoryIndex"`
- `Visibility = "Hot"`
- `Content` = JSON-serialized `MemoryIndex` object

This row is excluded from header scans and vector searches by filtering on `Name != '__MEMORY_INDEX__'`.

Because multiple agents can share a scope, index writes go through `IMemoryStore.ModifyIndexContentAsync` — an atomic read-modify-write inside a transaction holding a scope-keyed exclusive applock (`sp_getapplock` on resource `mem-index-{scopeKey}`, 15-second timeout). Concurrent writers serialize instead of losing entries.

### Version Tagging

New entities created by this library include `"__memoryVersion": "2"` in their JSON metadata. This distinguishes them from entities created by the original `GraphRagAgentMemoryPlugin`. The library reads both versions transparently:

- `Visibility = "Private"` (v1, from GraphRagAgentMemoryPlugin) -> treated as `Warm`
- `Visibility = "Warm"` / `"Cold"` / `"Hot"` (v2, from this library) -> used directly

## Service Dependency Graph

```
AddAgentMemoryServices("MemoryDb")
  +-- AgentMemoryOptions (singleton; ConnectionStringName set from parameter)
  +-- IMemoryAuditLog -> MemoryAuditLog (singleton, best-effort writes)
  +-- IMemoryScopeService -> MemoryScopeService (singleton)
  +-- MemorySchemaHostedService (IHostedService)
  |     Ensures the mem schema + 6 tables on startup; fails fast on missing
  |     connection string / DDL failure / missing IEmbeddings unless
  |     AllowStartupWithoutEmbeddings
  +-- IMemoryStore -> SqlMemoryStore (singleton)
  |     Dependencies: AgentMemoryOptions, IConfiguration, ILoggerFactory, IEmbeddings?
  +-- IMemoryIndexManager -> MemoryIndexManager (singleton)
  +-- IMemoryRetriever -> MemoryRetriever (singleton)
  |     Lazy-resolves: IFabrCoreChatClientService -> IChatClient
  +-- IMemoryCompactor -> MemoryCompactor (singleton)
  |     Lazy-resolves: IFabrCoreChatClientService -> IChatClient
  +-- IRetrievalPlanner -> RetrievalPlanner (singleton)
  +-- IMemorySummaryTree -> MemorySummaryTreeBuilder (singleton)
  +-- IAgentMemoryProvider -> AgentMemoryProvider (singleton)
  |     Dependencies: store, index manager, retriever, compactor, planner,
  |     summary tree, scope service, audit log, options
  |     Creates: AgentMemoryService instances (cached per ScopeKey in a
  |     ConcurrentDictionary; EvictMemoryService removes one)
  +-- MemoryAwareCompactionService (singleton)
  +-- ISyntheticImaginingService -> SyntheticImaginingService (singleton)
        Lazy-resolves: IFabrCoreChatClientService -> IChatClient (ImaginingModelName)

AddMemoryAdministration()   [optional, requires AddAgentMemoryServices]
  +-- IMemoryAdminService -> MemoryAdminService (singleton)
        Reads mem schema directly; mutations route through the scope-bound
        IAgentMemoryService / IMemoryScopeService
```

## Retrieval Pipeline Detail

```
RecallAsync(query)
|
+- 0. (optional, Retrieval.PlannerEnabled) IRetrievalPlanner.CreatePlanAsync
|     -> Classifies the query into hot-index-only / standard / deep plan
|
+- 1. GetIndexAsync(scopeKey)
|     -> Read __MEMORY_INDEX__ row
|     -> Deserialize JSON -> MemoryIndex
|     -> Return hot index (always)
|
+- 2. ScanMemoryHeadersAsync(scopeKey, Retrieval.HeaderScanLimit=200)
|     -> SELECT TOP(200) EntityId, Name, EntityType, Description, UpdatedAt
|     -> WHERE ScopeKey = @scopeKey AND Name != '__MEMORY_INDEX__'
|     -> ORDER BY UpdatedAt DESC
|     -> Returns List<MemoryHeader> (no content, no embeddings)
|
+- 3. SelectRelevantMemoriesAsync(query, headers, Retrieval.WarmRetrievalLimit=5)
|     +- Filter out alreadySurfacedIds
|     +- If candidates <= maxToSelect -> return all
|     +- Try LLM selection:
|     |    -> Build manifest: "[type] id (date): title -- description"
|     |    -> Send to IChatClient with selection system prompt
|     |    -> Parse JSON response: { "selected_memories": ["id1", "id2"] }
|     |    -> Validate IDs against candidate set
|     +- Fallback: return first N candidates by recency
|
+- 4. RetrieveMemoryAsync(scopeKey, id) x N
|     -> Load entity + primary chunk content
|     -> Graph traversal (GetRelatedEntitiesAsync, Retrieval.RecallGraphHops=1)
|        runs in parallel with vector search — no added latency
|
+- 5. GetFreshnessWarning(header) x N
      -> If (UtcNow - UpdatedAt).TotalDays >= Retrieval.FreshnessDaysThreshold:
         "[Stale: last updated N days ago] This is a point-in-time
          observation. Verify against current state before relying on it."
```

## Synthetic Imagining Pipeline Detail

```
ImagineAsync(chatHistory, lastUserMessage, scopeKey, alreadySurfacedIds)
|
+- 1. BuildConversationText
|     -> Take last 20 messages from chat history
|     -> Strip <memory-context> markers (prevent feedback loops)
|     -> Format as "[Role]: text" per message (skip trivial <10 chars)
|
+- 2. GenerateQueriesAsync (LLM call using Models.ImaginingModelName)
|     -> System prompt: "You are a memory retrieval strategist"
|     -> Considers five dimensions:
|        1. Direct topic matches for what the user is asking about
|        2. Implicit context needs (domain knowledge, system behaviors)
|        3. Applicable user preferences or standing instructions
|        4. Relevant rules, constraints, or policies
|        5. Prior observations that provide useful situational context
|     -> LLM returns JSON: {"queries": ["query1", "query2", ...]}
|     -> Capped at Retrieval.MaxImaginingQueries (default: 5)
|     -> Returns [] if conversation is trivial or no search would help
|
+- 3. ExecuteQueriesAsync (all in parallel)
|     -> For each of N queries:
|        -> RecallAsync(query, alreadySurfacedIds) -- recall tasks
|        -> SearchArchiveAsync(query)              -- archive tasks
|     -> Task.WhenAll(Task.WhenAll(recallTasks), Task.WhenAll(archiveTasks))
|
+- 4. Deduplicate & Aggregate
      -> Hot index: from first recall result (identical across queries)
      -> Warm memories: deduplicate by ID across all recall results
      -> Archive results: deduplicate by ID, keep lowest distance
      -> Freshness warnings: collect unique across all recalls
      -> SyntheticImaginingResult with UniqueMemoryCount
```

## Consolidation Pipeline Detail

```
ConsolidateAsync(scopeKey)
|
+- 1. DeduplicateAsync
|     -> CROSS JOIN mem.MemoryChunk with itself (per scope)
|     -> WHERE VECTOR_DISTANCE('cosine', c1.Embedding, c2.Embedding) < threshold
|     -> AND e1.EntityType = e2.EntityType AND c1.EntityId < c2.EntityId
|     -> For each pair: keep newer, LLM-merge content, delete older
|     -> O(n^2) in chunk count — shared scopes concentrate rows, so this step
|        grows fastest there (see Consolidation.MemoryFileCap)
|
+- 2. PruneStaleAsync
|     -> Get all headers, exclude hot index entries
|     -> Filter: UpdatedAt older than 30 days (3 days for point-in-time)
|     -> Send candidates to LLM for staleness confirmation
|     -> Demote confirmed stale to Temperature = Cold
|
+- 3. ResolveContradictionsAsync
|     -> Load 20 most recent memories with content
|     -> Send to LLM: "find contradicting facts, identify stale side"
|     -> Parse: { "contradictions": [{ "stale_id", "current_id", "reason" }] }
|     -> Demote stale side to Temperature = Cold
|
+- 4. TruncateIndexAsync
|     -> Sort entries by UpdatedAt DESC
|     -> Evict entries beyond HotIndex.MaxEntries (20)
|     -> Evict entries until TotalEstimatedTokens <= HotIndex.MaxTokens (3000)
|     -> Evicted entries remain Warm (just lose hot pointer)
|
+- 5. (optional, SummaryTree.Enabled) IMemorySummaryTree.BuildAsync
      -> Rebuild mem.MemorySummaryNode topic rollups from current warm memories
```

## Memory-Aware Compaction Pipeline (Three-Tier Cascade)

The `MemoryAwareCompactionService` replaces the default `CompactionService` for agents using memory. It treats memory extraction as a form of context compression — pulling durable knowledge into the graph IS compaction.

```
CompactAsync(provider, config)
|
+- Check threshold: if tokens <= threshold -> return (no compaction)
|
+- Tier 1: Tool Result Compression (free, no LLM)
|     -> ToolResultCompressor.CompressToolResults()
|     -> Skip messages in keep window (last N)
|     -> For each tool message outside keep window:
|        If ContentsJson > Compaction.ToolResultCompressionThreshold (2000 chars):
|          Replace with placeholder: head (200 chars) + "[omitted]" + tail (200 chars)
|     -> Re-estimate tokens
|     -> If now under threshold -> ReplaceAndResetCacheAsync, return
|
+- Tier 2: Memory Extraction (LLM call, durable output)
|     -> Split messages: older (to summarize) vs kept (last N)
|     -> Adjust split past orphaned tool messages
|     -> Convert older messages to ChatMessage
|     -> memoryService.ExtractMemoriesAsync(olderMessages)
|     -> Facts, Rules, Instructions, Observations, Procedures saved to graph
|     -> These survive compaction -- summary doesn't need to carry them
|
+- Tier 3: Structured Summarization (LLM call, last resort)
|     -> Structured prompt optimized for agent continuation:
|        1. Active Intent
|        2. Key Decisions
|        3. Current State
|        4. Open Items
|        5. Critical References
|     -> If memories were extracted: prompt notes "N memories saved,
|        agent can recall them -- focus on transient state"
|     -> MaxOutputTokens: Compaction.SummaryMaxTokens (default 3000)
|
+- Post-Compaction Rebuild
      -> Summary system message (author="compaction")
      -> Hot memory index system message (author="agent-memory")
         Re-injected so agent knows what it remembers
      -> Kept recent messages (last N)
      -> ReplaceAndResetCacheAsync with rebuilt message list
```

### Key Differences from Default CompactionService

| Aspect | Default CompactionService | MemoryAwareCompactionService |
|--------|---------------------------|------------------------------|
| Tiers | Single (summarize) | Three-tier cascade |
| Tool results | Summarized with everything | Compressed for free (Tier 1) |
| Durable knowledge | Lost in summary | Extracted to graph (Tier 2) |
| Summary format | Generic prose | Structured handover sections |
| Post-compaction | Summary + kept messages | Summary + memory index + kept messages |
| Summary prompt | "Preserve key decisions" | "Optimize for continuation, not readability" |
