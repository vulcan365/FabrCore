# Agent Memory API Reference

## Namespaces

| Namespace | Contents |
|---|---|
| `FabrCore.Services.Memory.Abstractions` | All interfaces: `IAgentMemoryService`, `IAgentMemoryProvider`, `IMemoryScopeService`, `IMemoryStore`, `IMemoryIndexManager`, `IMemoryRetriever`, `IMemoryCompactor`, `IMemorySummaryTree`, `IRetrievalPlanner`, `ISyntheticImaginingService` |
| `FabrCore.Services.Memory.Models` | All models: `MemoryEntry`, `MemoryScope`, `MemoryIndex`, `MemoryIndexEntry`, `MemoryHeader`, `MemoryChunkEntry`, `MemoryRelationshipEntry`, `MemorySearchResult`, `MemoryRecallResult`, `MemoryConsolidationResult`, `MemorySummaryNode`, `SyntheticImaginingResult`, `ProceduralSteps`, `MemoryType`, `MemoryTemperature`, `MemoryTaxonomyRules` |
| `FabrCore.Services.Memory.Configuration` | `AgentMemoryOptions` (+ `HotIndexOptions`, `RetrievalOptions`, `ConsolidationOptions`, `SummaryTreeOptions`, `CompactionOptions`, `MemoryModelOptions`), `MemoryScopeResolver`, `MemoryServiceExtensions`, `LlmModelTier` |
| `FabrCore.Services.Memory.Services` | `MemoryCompactionHandler`, `MemoryAwareCompactionService`, `ToolResultCompressor` |
| `FabrCore.Services.Memory.Plugin` | `AgentMemoryPlugin` |
| `FabrCore.Services.Memory.Audit` | `IMemoryAuditLog`, `MemoryAuditEntry` |
| `FabrCore.Services.Memory.Administration` | `IMemoryAdminService` (+ `Administration.Models` DTOs) |

## Registration

```csharp
// Required parameter: connection string name for the SQL Server 2025 / Azure SQL
// database hosting the mem schema. Fails fast at startup when the connection
// string is missing, schema DDL fails, or IEmbeddings is not registered
// (relax with options.AllowStartupWithoutEmbeddings for client-only hosts).
IServiceCollection AddAgentMemoryServices(
    this IServiceCollection services,
    string connectionStringName,
    Action<AgentMemoryOptions>? configure = null);

// Optional: registers IMemoryAdminService for admin UIs / maintenance tooling.
// Requires AddAgentMemoryServices to be called first.
IServiceCollection AddMemoryAdministration(this IServiceCollection services);
```

## MemoryScopeResolver

Resolves the memory scope key for an agent from its configuration.

```csharp
public static class MemoryScopeResolver
{
    public const string PluginAlias = "agent-memory";
    public const string MemoryScopeKey = "MemoryScope";

    /// Precedence: explicit value -> plugin setting "MemoryScope" ->
    /// Args["MemoryScope"] -> Args["AgentHandle"] (legacy) -> config.Handle.
    /// Throws InvalidOperationException when no scope can be resolved.
    public static string Resolve(AgentConfiguration config, string? explicitScope = null);
}
```

## IAgentMemoryProvider

Factory for scope-bound memory service instances. Singleton in DI.

```csharp
public interface IAgentMemoryProvider
{
    /// Thread-safe. Same scope key always returns the same cached instance —
    /// agents configured with the same shared scope share one instance.
    IAgentMemoryService GetMemoryService(string scopeKey);

    /// Remove the cached instance for a scope (used after a scope is deleted).
    /// Returns true when an instance was cached.
    bool EvictMemoryService(string scopeKey);
}
```

## IAgentMemoryService

Main facade. Bound to a single memory scope — the agent's own handle by default, or a named shared scope.

```csharp
string ScopeKey { get; }
```

### SaveMemoryAsync

Validates taxonomy, generates embedding, inserts as Warm, adds to hot index. Similar existing entities (within `Consolidation.EntityMatchThreshold`) are merged instead of duplicated.

```csharp
Task<MemoryEntry> SaveMemoryAsync(
    string title,
    MemoryType type,
    string content,
    string? description = null,
    Dictionary<string, string>? metadata = null,
    bool isPointInTime = false,
    CancellationToken ct = default);
```

**Throws:** `InvalidOperationException` when taxonomy validation fails.

### RecallAsync

Returns hot index + selectively retrieved warm memories with freshness warnings. Always operates within the bound scope (the old `MemorySearchScope` parameter no longer exists — share memories by binding agents to a shared scope).

```csharp
Task<MemoryRecallResult> RecallAsync(
    string query,
    IReadOnlySet<Guid>? alreadySurfacedIds = null,
    CancellationToken ct = default);
```

**Parameters:**
- `query` — the current user query or context
- `alreadySurfacedIds` — IDs of memories already shown in the current conversation (excluded from warm retrieval)

**Pipeline:**
1. Load hot index
2. Scan up to `Retrieval.HeaderScanLimit` headers (metadata only)
3. LLM selects up to `Retrieval.WarmRetrievalLimit` relevant memories
4. Load full content for selected; graph traversal up to `Retrieval.RecallGraphHops` runs in parallel
5. Compute freshness warnings for memories older than `Retrieval.FreshnessDaysThreshold`

When `Retrieval.PlannerEnabled = true`, the call is routed through `IRetrievalPlanner`, which picks a per-query plan (hot-index-only, standard, or deep) instead of always running the full pipeline.

### GetMemoryIndexAsync

Returns the hot layer bounded index (always-loaded table of contents).

```csharp
Task<MemoryIndex> GetMemoryIndexAsync(CancellationToken ct = default);
```

### SearchArchiveAsync

Vector search across the cold layer (chunks + entities).

```csharp
Task<IReadOnlyList<MemorySearchResult>> SearchArchiveAsync(
    string query,
    int limit = 10,
    MemoryType? typeFilter = null,
    CancellationToken ct = default);
```

### ConsolidateAsync

Run full consolidation: dedup, prune stale, resolve contradictions, truncate index (and rebuild the summary tree when `SummaryTree.Enabled`).

```csharp
Task<MemoryConsolidationResult> ConsolidateAsync(CancellationToken ct = default);
```

### ForgetMemoryAsync

Delete a memory by ID. Removes from store (entity + chunks + relationships) and hot index.

```csharp
Task<bool> ForgetMemoryAsync(Guid memoryId, CancellationToken ct = default);
```

### UpdateMemoryAsync

Partial update — only the supplied (non-null) fields change. Re-generates the embedding when `content` changes and keeps the hot index entry in sync when the title, type, or description changes.

```csharp
Task<MemoryEntry> UpdateMemoryAsync(
    Guid memoryId,
    string? title = null,
    MemoryType? type = null,
    string? content = null,
    string? description = null,
    MemoryTemperature? temperature = null,
    CancellationToken ct = default);
```

**Throws:** `InvalidOperationException` when the memory does not exist.

### FormatRecallContext

Wraps a `MemoryRecallResult` in `<memory-context>` markers that the extraction system recognizes and skips during compaction. **Always use this when injecting recalled memories into user messages** to prevent re-extraction duplicates.

```csharp
string FormatRecallContext(MemoryRecallResult recall);
```

**Returns:** A formatted string with `<memory-context source="agent-memory-system">...</memory-context>` markers. Append this to the user's ChatMessage content.

**Critical:** If you skip this and inject raw memory text, the extraction system will treat it as new conversation content during compaction and create duplicate memories every cycle.

### ExtractMemoriesAsync

Extract durable memories from chat messages using an LLM. Typically called during OnCompaction.

```csharp
Task<IReadOnlyList<MemoryEntry>> ExtractMemoriesAsync(
    IList<ChatMessage> messages,
    CancellationToken ct = default);
```

**Behavior:**
- Sends the messages to an LLM with a prompt asking "what's worth remembering long-term?"
- The LLM returns structured JSON with title, type, content, and description for each memory
- Each extracted memory is saved via `SaveMemoryAsync` (warm + hot index)
- Checks existing hot index to avoid duplicating already-stored memories
- Returns empty list (never throws) if LLM is unavailable or nothing worth saving
- Uses `Models.CompactionModelName` from options for the LLM call
- Content inside `<memory-context>` markers is automatically stripped before extraction

## IMemoryScopeService

Registry of memory scopes (`mem.MemoryScope`). Shared scopes are created explicitly (by admins or host code); agent-handle scopes are auto-registered on first save so they show up in administration tooling.

```csharp
public interface IMemoryScopeService
{
    /// Create a new scope. Throws InvalidOperationException when the scope key
    /// already exists. Writes a ScopeCreated audit entry.
    Task<MemoryScope> CreateScopeAsync(
        string scopeKey, string? description, bool isShared = true,
        string? createdBy = null, CancellationToken ct = default);

    /// Idempotently register a scope (MERGE). Never overwrites an existing row.
    Task EnsureScopeAsync(string scopeKey, bool isShared = false, CancellationToken ct = default);

    /// Get a scope by key, or null when not registered.
    Task<MemoryScope?> GetScopeAsync(string scopeKey, CancellationToken ct = default);

    /// List all registered scopes.
    Task<IReadOnlyList<MemoryScope>> ListScopesAsync(CancellationToken ct = default);

    /// Whether a scope row exists.
    Task<bool> ScopeExistsAsync(string scopeKey, CancellationToken ct = default);

    /// Count memory entities in a scope (excludes the internal index sentinel).
    Task<int> CountMemoriesInScopeAsync(string scopeKey, CancellationToken ct = default);
}
```

## IMemoryAuditLog

Writes audit rows to `mem.MemoryAuditLog`. All methods are **best-effort** — implementations swallow database errors and only log internally, so audit failures never fail the underlying memory operation.

```csharp
public interface IMemoryAuditLog
{
    Task RecordAsync(MemoryAuditEntry entry, CancellationToken ct = default);

    Task RecordAsync(
        string actionType,
        string scopeKey,
        Guid? memoryId = null,
        string? summary = null,
        string? actorId = null,
        string? payload = null,
        long? durationMs = null,
        CancellationToken ct = default);
}
```

`MemoryAuditEntry` fields: `AuditId`, `OccurredAt`, `ActionType`, `ScopeKey`, `MemoryId?`, `ActorId?`, `ActorName?`, `Summary?`, `Payload?` (JSON), `DurationMs?`.

Action types: `MemorySaved`, `MemoryMerged`, `MemoryUpdated`, `MemoryForgotten`, `MemoriesExtracted`, `ScopeConsolidated`, `ScopeCreated`, `ScopeDeleted`, `AdminCreated`, `AdminUpdated`, `AdminDeleted`.

## IMemoryAdminService

Administration surface over agent memory — dashboards, scope management, memory CRUD, consolidation, and audit review. Registered by `AddMemoryAdministration()`. Intended for admin UIs (e.g. the FabrCore.Surface.Admin memory page) and maintenance tooling, not for agents. Reads query the `mem` schema directly; all mutations route through the scope-bound `IAgentMemoryService` / `IMemoryScopeService` so hot-index maintenance, embedding generation, taxonomy validation, and audit stay consistent.

```csharp
public interface IMemoryAdminService
{
    // Dashboard
    Task<AdminMemoryDashboardStats> GetDashboardStatsAsync(CancellationToken ct = default);

    // Scopes — registered mem.MemoryScope rows plus scope keys that only exist
    // implicitly through memory rows (IsRegistered = false)
    Task<IReadOnlyList<AdminMemoryScopeDto>> ListScopesAsync(CancellationToken ct = default);

    /// Create a shared scope (e.g. "bank-recon"). Throws when the key exists.
    Task<AdminMemoryScopeDto> CreateSharedScopeAsync(
        string scopeKey, string? description, string? actorId = null, CancellationToken ct = default);

    /// Destructive: delete a scope and every memory, chunk, relationship, and
    /// summary node in it. Returns the deleted counts. Writes a ScopeDeleted audit entry.
    Task<AdminScopeDeleteResult> DeleteScopeAsync(
        string scopeKey, string? actorId = null, CancellationToken ct = default);

    // Memories — paged, newest first; searchTerm matches title/description/content
    Task<IReadOnlyList<AdminMemoryDto>> ListMemoriesAsync(
        string scopeKey,
        MemoryType? typeFilter = null,
        MemoryTemperature? temperatureFilter = null,
        string? searchTerm = null,
        int page = 1, int pageSize = 25,
        CancellationToken ct = default);

    Task<int> CountMemoriesAsync(
        string scopeKey,
        MemoryType? typeFilter = null,
        MemoryTemperature? temperatureFilter = null,
        string? searchTerm = null,
        CancellationToken ct = default);

    /// Full detail for one memory (content, chunks, relationships), or null.
    Task<AdminMemoryDetailDto?> GetMemoryAsync(Guid memoryId, CancellationToken ct = default);

    /// Admin "teach" path — e.g. save the Rule "Habitat line items are business
    /// meal expenses" into scope "bank-recon". Runs the full save pipeline.
    Task<AdminMemoryDto> CreateMemoryAsync(
        string scopeKey, string title, MemoryType type, string content,
        string? description = null,
        MemoryTemperature temperature = MemoryTemperature.Warm,
        bool isPointInTime = false,
        Dictionary<string, string>? metadata = null,
        string? actorId = null,
        CancellationToken ct = default);

    Task<AdminMemoryDetailDto> UpdateMemoryAsync(
        Guid memoryId, string title, MemoryType type, string content,
        string? description, MemoryTemperature temperature,
        string? actorId = null,
        CancellationToken ct = default);

    /// Delete a memory (store + hot index). Returns false when not found.
    Task<bool> DeleteMemoryAsync(Guid memoryId, string? actorId = null, CancellationToken ct = default);

    // Maintenance
    Task<MemoryConsolidationResult> ConsolidateScopeAsync(
        string scopeKey, string? actorId = null, CancellationToken ct = default);

    // Audit — newest first, optionally filtered to one scope
    Task<IReadOnlyList<MemoryAuditEntry>> ListAuditEntriesAsync(
        string? scopeKey = null, int page = 1, int pageSize = 50, CancellationToken ct = default);
}
```

Admin DTOs (`FabrCore.Services.Memory.Administration.Models`):

| DTO | Contents |
|---|---|
| `AdminMemoryDashboardStats` | TotalScopes, TotalMemories, TotalChunks, TotalRelationships, TotalSummaryNodes, MemoriesByType, MemoriesByTemperature, RecentActivity |
| `AdminMemoryScopeDto` | ScopeKey, Description, IsShared, IsRegistered, CreatedAt, CreatedBy, MemoryCount, LastUpdatedAt |
| `AdminScopeDeleteResult` | ScopeKey, MemoriesDeleted, ChunksDeleted, RelationshipsDeleted, SummaryNodesDeleted |
| `AdminMemoryDto` | List-row projection (no content/embeddings) |
| `AdminMemoryDetailDto` | Full detail: Content, Metadata JSON, Chunks, Relationships |

## ISyntheticImaginingService

Conversation-aware multi-query memory recall. Analyzes conversation context via LLM to generate targeted search queries, runs them in parallel, and returns aggregated, deduplicated results.

```csharp
public interface ISyntheticImaginingService
{
    /// Analyze conversation from chat history provider.
    /// Forks history internally -- original is never modified.
    Task<SyntheticImaginingResult> ImagineAsync(
        FabrCoreChatHistoryProvider chatHistoryProvider,
        string lastUserMessage,
        string scopeKey,
        IReadOnlySet<Guid>? alreadySurfacedIds = null,
        CancellationToken ct = default);

    /// Overload accepting an explicit message list (no fork needed).
    Task<SyntheticImaginingResult> ImagineAsync(
        IList<ChatMessage> messages,
        string lastUserMessage,
        string scopeKey,
        IReadOnlySet<Guid>? alreadySurfacedIds = null,
        CancellationToken ct = default);
}
```

**Parameters:**
- `chatHistoryProvider` / `messages` — conversation context (last 20 messages used)
- `lastUserMessage` — the most recent user message (primary search anchor)
- `scopeKey` — memory scope to search (agent handle or shared scope)
- `alreadySurfacedIds` — memories already shown (excluded from results)

**Behavior:**
- Returns `Success=true` with empty queries if `lastUserMessage` is blank
- Generates 1-N queries (capped at `Retrieval.MaxImaginingQueries`, default 5)
- Runs all `RecallAsync` + `SearchArchiveAsync` calls in parallel via `Task.WhenAll`
- Deduplicates warm memories by ID, archive results by ID (keeping lowest distance)
- Hot index taken from first query result (identical across queries for same scope)
- Never throws — returns `Success=false` with `ErrorMessage` on failure
- Uses the `Models.ImaginingModelName` LLM from options for query generation
- Strips `<memory-context>` markers from conversation text before sending to LLM

## MemoryCompactionHandler

Unified entry point for memory-aware compaction and standalone extraction.

**Namespace:** `FabrCore.Services.Memory.Services`

```csharp
public class MemoryCompactionHandler
{
    public MemoryCompactionHandler(
        IAgentMemoryService memoryService,
        MemoryAwareCompactionService compactionService,
        AgentMemoryOptions memoryOptions,
        ILoggerFactory loggerFactory);

    // Full tier cascade (tool compression + extraction + summarization)
    Task<CompactionResult?> CompactAsync(
        FabrCoreChatHistoryProvider chatHistoryProvider,
        CompactionConfig compactionConfig,
        CancellationToken ct = default);

    // Standalone extraction from chat history provider
    Task<IReadOnlyList<MemoryEntry>> ExtractMemoriesAsync(
        FabrCoreChatHistoryProvider chatHistoryProvider,
        int keepLastN = 20,
        CancellationToken ct = default);

    // Standalone extraction from explicit message list
    Task<IReadOnlyList<MemoryEntry>> ExtractMemoriesAsync(
        IList<ChatMessage> allMessages,
        int keepLastN = 20,
        CancellationToken ct = default);
}
```

**CompactAsync** runs the full three-tier cascade via `MemoryAwareCompactionService`. Returns `CompactionResult` or null on failure. Never throws — logs and returns null so the agent can fall back to default compaction.

**ExtractMemoriesAsync** extracts durable memories from older messages without running the full compaction cascade. Never throws — returns empty list on failure.

## MemoryAwareCompactionService

Three-tier compaction cascade that integrates memory extraction into the compaction pipeline.

**Namespace:** `FabrCore.Services.Memory.Services`

```csharp
public class MemoryAwareCompactionService
{
    Task<CompactionResult> CompactAsync(
        FabrCoreChatHistoryProvider provider,
        CompactionConfig config,
        IAgentMemoryService memoryService,
        AgentMemoryOptions memoryOptions,
        string modelConfigName,
        CancellationToken ct = default);
}
```

**Tier cascade:**
1. **Tool result compression** (free): `ToolResultCompressor.CompressToolResults()` — compresses large tool results in older messages. If tokens drop below threshold, done.
2. **Memory extraction** (LLM): `memoryService.ExtractMemoriesAsync()` — extracts durable facts, rules, instructions, observations, procedures to the graph store.
3. **Structured summarization** (LLM): continuation-optimized handover summary with sections: Active Intent, Key Decisions, Current State, Open Items, Critical References.
4. **Post-compaction rebuild**: summary + hot memory index re-injection + kept recent messages.

## ToolResultCompressor

Static utility for Tier 1 compaction. No LLM call, pure string operations.

```csharp
internal static class ToolResultCompressor
{
    static (List<StoredChatMessage> Messages, int Compressed) CompressToolResults(
        List<StoredChatMessage> messages,
        int keepLastN,
        int thresholdChars,
        int keepHeadChars,
        int keepTailChars);
}
```

Skips messages in the keep window. For each older tool message with `ContentsJson` over `thresholdChars`: replaces with placeholder preserving head/tail of the output. Returns new list (input not mutated).

## IMemoryStore

Low-level storage over `mem.MemoryEntity` / `MemoryChunk` / `MemoryRelationship` tables. All methods take the scope key as the first parameter.

| Method | Purpose |
|---|---|
| `InsertEntityAsync(scopeKey, entry)` | Insert new entity node (content/embedding go in chunks) |
| `GetEntityByIdAsync(scopeKey, entityId)` | Get by ID (chunk content loaded separately) |
| `UpdateEntityAsync(scopeKey, entry)` | Update entity metadata (does not touch chunks) |
| `DeleteEntityAsync(scopeKey, entityId)` | Delete entity + chunks + relationships |
| `GetHeadersAsync(scopeKey, limit, typeFilter?)` | Lightweight metadata scan |
| `InsertChunkAsync(scopeKey, chunk)` / `UpdateChunkAsync(scopeKey, chunk)` | Chunk content + embedding writes |
| `GetPrimaryChunkAsync(scopeKey, entityId)` / `GetChunksAsync(scopeKey, entityId)` | Chunk reads |
| `InsertRelationshipAsync(scopeKey, fromId, toId, type, ...)` | Create directed, typed, weighted edge |
| `GetRelationshipsAsync(scopeKey, entityId)` | Edges in both directions with related entity info |
| `VectorSearchAsync(scopeKey, embedding, limit, typeFilter?)` | Cosine search on chunks JOINed to entities |
| `FindSimilarByContentAsync(scopeKey, embedding, limit, maxDistance)` | Entity matching on save (merge instead of duplicate) |
| `FindDuplicatePairsAsync(scopeKey, threshold, typeFilter?)` | Chunk CROSS JOIN near-duplicate pairs |
| `GetIndexContentAsync(scopeKey)` / `UpsertIndexContentAsync(scopeKey, json)` | Read/write raw JSON of index sentinel row |
| `ModifyIndexContentAsync(scopeKey, transform)` | Atomic read-modify-write of the index under a scope-keyed applock — concurrent shared-scope writers cannot lose updates |
| `GenerateEmbeddingAsync(text)` | Generate embedding via IEmbeddings |

## IMemoryIndexManager

Hot layer bounded index management.

| Method | Purpose |
|---|---|
| `GetIndexAsync(scopeKey)` | Get index (empty if none exists) |
| `UpdateIndexAsync(scopeKey, index)` | Replace entire index |
| `AddIndexEntryAsync(scopeKey, entry)` | Add entry, enforce caps, evict oldest |
| `RemoveIndexEntryAsync(scopeKey, memoryId)` | Remove by memory ID |
| `TruncateIndexAsync(scopeKey)` | Enforce caps, return evicted entries |

## IMemoryRetriever

Three-stage retrieval pipeline plus graph traversal.

| Method | Purpose |
|---|---|
| `ScanMemoryHeadersAsync(scopeKey, limit, typeFilter?)` | Stage 1: cheap metadata scan |
| `SelectRelevantMemoriesAsync(query, manifest, maxToSelect, excludeIds?)` | Stage 2: LLM relevance selection |
| `RetrieveMemoryAsync(scopeKey, memoryId)` | Stage 3: load full content |
| `GetRelatedEntitiesAsync(scopeKey, seedEntityIds, maxHops)` | Graph-aware retrieval: entities within N hops of the seeds |
| `GetFreshnessWarning(header)` | Compute staleness text or null |

## IMemoryCompactor

Consolidation engine.

| Method | Purpose |
|---|---|
| `ConsolidateAsync(scopeKey)` | Full pass: dedup + prune + resolve + truncate |
| `DeduplicateAsync(scopeKey)` | Merge near-duplicate pairs |
| `PruneStaleAsync(scopeKey)` | Demote old memories to Cold |
| `ResolveContradictionsAsync(scopeKey)` | LLM identifies conflicting facts |

## IMemorySummaryTree

Hierarchical semantic summary tree over `mem.MemorySummaryNode` (opt-in via `SummaryTree.Enabled`).

| Method | Purpose |
|---|---|
| `BuildAsync(scopeKey)` | Rebuild the tree from current warm memories (final step of consolidation) |
| `QueryAsync(scopeKey, query, limit)` | Vector-search summary nodes for broad topic queries |
| `GetAllAsync(scopeKey)` | All nodes (browsing/debugging, not the hot path) |
| `ClearAsync(scopeKey)` | Delete all nodes for a scope |

## IRetrievalPlanner

Opt-in per-query retrieval planning (`Retrieval.PlannerEnabled`). Returns a `RetrievalPlan` (hot-index-only / standard / deep) that the memory service executes.

```csharp
Task<RetrievalPlan> CreatePlanAsync(string query, MemoryIndex hotIndex, CancellationToken ct = default);
```

## AgentMemoryOptions

Grouped options: top-level settings plus `HotIndex`, `Retrieval`, `Consolidation`, `SummaryTree`, `Compaction`, and `Models` sub-objects. Old flat names (`HotLayerMaxEntries`, `ReInjectHotIndexAfterCompaction`, etc.) no longer exist.

### Top-level

| Property | Type | Default | Description |
|---|---|---|---|
| `ConnectionStringName` | `string` | `""` | Set internally by `AddAgentMemoryServices(connectionStringName)` — not assignable in the callback |
| `EmbeddingDimensions` | `int` | `1536` | VECTOR column dimension; must match the embeddings model. Fixed at schema creation — changing later requires dropping the `mem` schema |
| `AllowStartupWithoutEmbeddings` | `bool` | `false` | true = log error instead of failing the host when the connection string or `IEmbeddings` is missing (client-only hosts) |
| `AllowedMemoryTypes` | `HashSet<MemoryType>` | All five | Restrict allowed types |
| `PointInTimeMemories` | `bool` | `false` | Mark all extracted memories as point-in-time snapshots |

### HotIndex

| Property | Default | Description |
|---|---|---|
| `MaxEntries` | `20` | Max entries in hot index |
| `MaxTokens` | `3000` | Max estimated tokens in hot index |
| `ReInjectAfterCompaction` | `true` | Re-inject the hot index as a system message after compaction |

### Retrieval

| Property | Default | Description |
|---|---|---|
| `WarmRetrievalLimit` | `5` | Max warm memories retrieved per query |
| `HeaderScanLimit` | `200` | Max headers scanned during retrieval |
| `FreshnessDaysThreshold` | `1` | Days before staleness warning |
| `RecallGraphHops` | `1` | Graph traversal hops during recall (0 = vector only) |
| `PlannerEnabled` | `false` | Route RecallAsync through IRetrievalPlanner |
| `MaxImaginingQueries` | `5` | Max search queries per ImagineAsync call |

### Consolidation

| Property | Default | Description |
|---|---|---|
| `MemoryFileCap` | `200` | Max memory entities per scope |
| `EnableAutoConsolidation` | `false` | Auto-consolidate when count exceeds MemoryFileCap |
| `DuplicateDistanceThreshold` | `0.05` | Cosine distance for dedup |
| `EntityMatchThreshold` | `0.25` | Merge-on-save distance (lower = stricter) |
| `EnableRelationshipExtraction` | `true` | Extract graph relationships during memory extraction |

### SummaryTree

| Property | Default | Description |
|---|---|---|
| `Enabled` | `false` | Build the hierarchical summary tree during consolidation |
| `MaxDepth` | `2` | Max tree depth (baseline builder materializes depth 0) |
| `Fanout` | `7` | Max child memories sampled per node summary |

### Compaction

| Property | Default | Description |
|---|---|---|
| `ToolResultCompressionThreshold` | `2000` | Tier 1: compress tool results above this (chars) |
| `ToolResultKeepHeadChars` | `200` | Tier 1: chars preserved from start |
| `ToolResultKeepTailChars` | `200` | Tier 1: chars preserved from end |
| `SummaryMaxTokens` | `3000` | Tier 3: max output tokens for summary |

### Models

| Property | Default | Description |
|---|---|---|
| `RelevanceModelName` | `"default"` | LLM for relevance selection |
| `CompactionModelName` | `"default"` | LLM for compaction/extraction |
| `ImaginingModelName` | `"default"` | LLM for imagining query generation |
| `PlannerModelName` | `""` | LLM for planner classification (falls back to RelevanceModelName) |
| `SmallModelName` | `""` | Tier-level model for all Small-tier calls (classification, dedup confirm) |
| `LargeModelName` | `""` | Tier-level model for all Large-tier calls (merges, rollups) |

`ResolveModelForCall(LlmModelTier tier, string explicitName)` resolves the model for a call: explicit non-"default" per-operation name wins → tier-level override → "default".

## SyntheticImaginingResult

Result of a synthetic imagining operation.

```csharp
public class SyntheticImaginingResult
{
    public List<string> GeneratedQueries { get; set; } = [];
    public MemoryRecallResult AggregatedRecall { get; set; } = new();
    public List<MemorySearchResult> ArchiveResults { get; set; } = [];
    public int UniqueMemoryCount { get; set; }
    public bool Success { get; set; }
    public string? ErrorMessage { get; set; }
}
```

| Property | Description |
|---|---|
| `GeneratedQueries` | The search queries the LLM generated from conversation analysis |
| `AggregatedRecall` | Hot index (fetched once) + deduplicated warm memories from all queries |
| `ArchiveResults` | Deduplicated archive search results, ordered by distance (best first) |
| `UniqueMemoryCount` | Total unique memories found across all queries |
| `Success` | Whether the operation completed successfully |
| `ErrorMessage` | Error message if failed, null on success |

## MemoryTaxonomyRules

Static validation class.

```csharp
public static (bool IsValid, string? RejectionReason) Validate(
    MemoryType type,
    string? content,
    IReadOnlySet<MemoryType> allowedTypes);
```

**Rejects:**
- Type not in `allowedTypes`

Content validation is not performed — content policy is the consuming agent's responsibility.
