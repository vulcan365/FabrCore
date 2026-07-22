---
name: fabrcore-services-memory
description: >
  FabrCore agent memory service — three-temperature memory management (hot/warm/cold),
  scoped shared memory pools, taxonomy-enforced storage, LLM-based retrieval, freshness
  tracking, compaction, audit logging, administration surface, and synthetic imagining
  (conversation-aware multi-query recall).
  Self-contained SQL schema (mem) with SQL Server 2025 VECTOR + SQL Graph tables, auto-created on startup.
  Triggers on: "agent memory service", "IAgentMemoryService", "IAgentMemoryProvider",
  "AddAgentMemoryServices", "AgentMemoryPlugin", "agent-memory plugin", "memory index",
  "hot layer", "warm layer", "cold layer", "memory temperature", "MemoryTemperature",
  "MemoryType", "memory taxonomy", "memory compaction", "ConsolidateAsync",
  "RecallAsync", "SaveMemoryAsync", "ForgetMemoryAsync", "SearchArchiveAsync",
  "GetMemoryIndexAsync", "MemoryIndex", "MemoryIndexEntry", "MemoryHeader",
  "MemorySearchResult", "MemoryRecallResult", "MemoryConsolidationResult",
  "IMemoryStore", "IMemoryIndexManager", "IMemoryRetriever", "IMemoryCompactor",
  "SqlMemoryStore", "memory freshness", "memory deduplication", "memory pruning",
  "three temperature memory", "memory recall", "save memory", "forget memory",
  "FabrCore.Services.Memory", "structured memory", "agent long-term memory",
  "ISyntheticImaginingService", "SyntheticImaginingResult", "synthetic imagining",
  "ImagineAsync", "memory imagining", "conversation-aware recall",
  "IMemoryScopeService", "MemoryScope", "shared memory scope", "MemoryScopeResolver",
  "IMemoryAuditLog", "IMemoryAdminService", "AddMemoryAdministration", "mem schema",
  "EvictMemoryService", "EmbeddingDimensions", "FormatRecallContext", "memory-context markers".
  Do NOT use for: raw GraphRAG knowledge graph CRUD — use fabrcore-v365-graphknowledge.
  Do NOT use for: general agent lifecycle — use fabrcore-agent.
  Do NOT use for: general plugin patterns — use fabrcore-plugins-tools.
allowed-tools: "Bash(dotnet:*) Bash(mkdir:*) Bash(ls:*) Bash(pwsh:*) Bash(powershell:*) Bash(git:*) Bash(dir:*)"
---

# FabrCore Agent Memory Service

A structured memory management library for FabrCore agents. Provides three-temperature memory (hot/warm/cold), scoped memory pools (isolated per agent by default, or shared across agents), taxonomy-enforced storage, LLM-based retrieval, freshness tracking, automatic compaction, audit logging, an administration surface, and synthetic imagining (conversation-aware multi-query recall) — with a self-contained `mem` SQL schema (no dependency on GraphRagAgent). The project is `FabrCore.Services.Memory` (NuGet dependencies: `FabrCore.Host`, `Microsoft.Data.SqlClient`, target `net10.0`).

## Architecture Overview

```
+---------------------------------------------------------+
|                  Agent (FabrCoreAgentProxy)              |
|  OnInitialize: provider.GetMemoryService(scopeKey)      |
|  OnMessage:    memory.RecallAsync(query)                |
|            or: imagining.ImagineAsync(...)               |
+--------------------------+------------------------------+
                           |
                +----------v----------+
                | IAgentMemoryProvider |  Factory (singleton)
                |  -> per-scope cache  |  agents sharing a scope
                |  EvictMemoryService  |  share ONE instance
                +----------+----------+
                           |
                +----------v----------+
                | IAgentMemoryService  |  Bound to one ScopeKey
                |  SaveMemoryAsync     |  (agent handle by default,
                |  RecallAsync         |   or a named shared scope
                |  FormatRecallContext  |   like "bank-recon")
                |  SearchArchiveAsync  |
                |  ConsolidateAsync    |
                |  ForgetMemoryAsync   |
                |  UpdateMemoryAsync   |
                |  ExtractMemoriesAsync|
                +----------+----------+
                           |
         +-----------------+-----------------+
         |                 |                 |
  +------v-------+ +------v-------+ +-------v-------+
  | MemoryIndex  | |  Memory      | |  Memory       |
  | Manager      | |  Retriever   | |  Compactor    |
  | (hot layer)  | | (3-stage)    | | (consolidate) |
  +------+-------+ +------+-------+ +-------+-------+
         |                 |                 |
         +-----------------+-----------------+
                           |
                +----------v----------+
                | SqlMemoryStore       |
                | mem.MemoryEntity     |
                | VECTOR(dims) + SQL   |
                | Graph + Scope + Audit|
                +---------------------+

         +------------------------------+     +---------------------------+
         | ISyntheticImaginingService    |     | IMemoryScopeService        |
         | ImagineAsync(history, query) |     |  scope registry            |
         | -> LLM generates N queries   |     | IMemoryAuditLog            |
         | -> parallel RecallAsync      |     |  best-effort audit trail   |
         | -> parallel SearchArchive    |     | IMemoryAdminService        |
         | -> deduplicate & aggregate   |     |  (AddMemoryAdministration) |
         +------------------------------+     +---------------------------+
```

## Memory Scopes (Isolated vs. Shared)

Every memory belongs to a **ScopeKey** — the partition key for all reads and writes. By default an agent's scope is its own handle, so its memory is isolated (the majority case). Configuration can instead point an agent at a **named shared scope** so many agents read and write one memory pool.

Example: point every bank-reconciliation agent at the shared scope `"bank-recon"`. When one agent is taught "Habitat line items are business meal expenses", every agent in the scope knows it immediately — the memory pool, hot index, and consolidation are all shared.

```json
"Plugins": [{ "PluginAlias": "agent-memory", "Settings": { "MemoryScope": "bank-recon" } }]
```

Scope resolution (`MemoryScopeResolver.Resolve(config, explicitScope)`) follows this precedence:

1. Explicit code value (`explicitScope` parameter / `AgentMemoryPlugin.MemoryScope` property)
2. Plugin setting `"MemoryScope"` on plugin alias `"agent-memory"`
3. `Args["MemoryScope"]`
4. `Args["AgentHandle"]` (legacy)
5. `config.Handle` — the isolated default

`IAgentMemoryProvider` caches service instances per scope key, so agents configured with the same shared scope get the same `IAgentMemoryService` instance; `EvictMemoryService(scopeKey)` removes a cached instance (used after a scope is deleted). Scopes are registered in `mem.MemoryScope`: shared scopes explicitly (via `IMemoryScopeService.CreateScopeAsync` or `IMemoryAdminService.CreateSharedScopeAsync`), agent-handle scopes auto-registered on first save.

## Service Registration

```csharp
using FabrCore.Services.Memory.Configuration;

// In your FabrCore server startup (Program.cs or Startup.cs)
builder.Services.AddAgentMemoryServices("MemoryDb", options =>
{
    // optional configuration
});

// Optional: administration surface for admin UIs / maintenance tooling
builder.Services.AddMemoryAdministration();
```

The connection string name is a **required parameter**. The `mem` schema and tables are created automatically on startup — no manual migration needed.

**Startup fails fast** when the connection string is missing, schema DDL fails, or `IEmbeddings` is not registered. Set `AgentMemoryOptions.AllowStartupWithoutEmbeddings = true` to downgrade the missing-connection-string / missing-embeddings failures to `LogError` for client-only hosts that register memory services but never save memories.

**Prerequisites:**
- `AddFabrCoreServer()` with correctly configured models in `fabrcore.json` (see below)
- SQL Server 2025 / Azure SQL with `VECTOR` support (dimension set by `AgentMemoryOptions.EmbeddingDimensions`, default 1536)

## Model Configuration for Memory

The memory system uses LLM calls for several operations. Each operation resolves a named model from `fabrcore.json` via `IFabrCoreChatClientService`. **If a model name doesn't match a `fabrcore.json` entry, the operation silently returns empty results** — no error is thrown, and no memories are extracted or retrieved.

Model names live under `AgentMemoryOptions.Models` (`MemoryModelOptions`).

### Required Models in `fabrcore.json`

| Model Name (`options.Models`) | Used By | Purpose |
|---|---|---|
| `CompactionModelName` (default: `"default"`) | `ExtractMemoriesAsync`, `MemoryAwareCompactionService` (Tier 2 + Tier 3) | Extracts durable memories from conversation during compaction, generates structured summaries |
| `RelevanceModelName` (default: `"default"`) | `MemoryRetriever.SelectRelevantMemoriesAsync` | LLM selects which warm memories are relevant to the current query |
| `ImaginingModelName` (default: `"default"`) | `SyntheticImaginingService.GenerateQueriesAsync` | Generates diverse memory search queries from conversation context |
| `PlannerModelName` (default: `""` → falls back to RelevanceModelName) | `RetrievalPlanner` | Classifies queries into retrieval plans when `Retrieval.PlannerEnabled` |
| `SmallModelName` / `LargeModelName` (default: `""`) | Tier-level overrides | Route all small-tier (classification) or large-tier (merge/rollup) calls to one model without setting each name (`ResolveModelForCall`) |
| `"embeddings"` (hardcoded) | `SqlMemoryStore`, `IEmbeddings` | Generates VECTOR embeddings for memory storage and similarity search |

### Minimum `fabrcore.json` for Memory

All LLM model names default to `"default"`, so at minimum you need a `"default"` chat model and an `"embeddings"` model:

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
  ],
  "ApiKeys": [
    { "Alias": "openai", "Value": "your-key-here" }
  ]
}
```

### Using a Separate Model for Memory Operations

To use a cheaper/faster model for memory LLM operations (recommended for cost control):

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
      "Name": "memory",
      "Provider": "OpenAI",
      "Model": "gpt-4o-mini",
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

Then configure the memory services to use it:

```csharp
builder.Services.AddAgentMemoryServices("MemoryDb", options =>
{
    options.Models.CompactionModelName = "memory";   // Tier 2 extraction + Tier 3 summary
    options.Models.RelevanceModelName = "memory";    // Warm memory selection
    options.Models.ImaginingModelName = "memory";    // Synthetic imagining query generation
});
```

### Troubleshooting: Memories Not Being Saved

If compaction triggers but no memories are extracted, check for these log messages:

- **`"Failed to resolve chat client 'default'"`** — The `Models.CompactionModelName` doesn't match any entry in `fabrcore.json`. Fix: add a matching model entry or set `CompactionModelName` to an existing model name.
- **`"Chat client 'X' not available, skipping LLM selection"`** — The `Models.RelevanceModelName` doesn't match. Recall falls back to recency-based selection (still works, but less accurate).
- **`"Tier 2: memory extraction failed, continuing with Tier 3"`** — Extraction threw an exception (API error, timeout, JSON parse failure). Check the inner exception in the log.
- **No log messages at all about compaction** — Compaction never triggered. Check that `CompactionEnabled = true` and `MaxContextTokens` / `Threshold` in the model configuration are set correctly. The token count must exceed `MaxContextTokens * Threshold` after an `OnMessage` for compaction to fire.

## Three-Temperature Memory Model

| Layer | SQL Storage | Retrieval | Budget |
|-------|-------------|-----------|--------|
| **Hot** | Single `mem.MemoryEntity` row per scope: `Name="__MEMORY_INDEX__"`, `EntityType="MemoryIndex"`, `Content`=JSON | Always loaded into agent context | Max 20 entries / 3,000 tokens |
| **Warm** | Normal `mem.MemoryEntity` rows, `Visibility="Warm"` | LLM relevance selection (up to 5 per query) | Up to 200 scanned per query |
| **Cold** | `mem.MemoryChunk` rows + demoted entities, `Visibility="Cold"` | Vector search only, never bulk-loaded | Unlimited |

**Important:** Hot memory is NOT chat history. Chat history is transient and lost on session end or compaction. Hot memory is a persistent, bounded index of one-line pointers to warm memories — it survives across sessions and is re-injected after compaction so the agent always knows what it remembers.

### Column Mapping

| MemoryEntry field | SQL column | Values |
|---|---|---|
| `ScopeKey` | `ScopeKey` | Partition key (agent handle or shared scope) |
| `Title` | `Name` | Short descriptive title |
| `Type` | `EntityType` | `"Fact"`, `"Rule"`, `"Instruction"`, `"Observation"`, `"Procedural"` |
| `Temperature` | `Visibility` | `"Hot"`, `"Warm"`, `"Cold"` (backward compat: `"Private"` -> `Warm`) |
| `Description` | `Description` | Brief description |
| `Content` | `mem.MemoryChunk.Content` | Full content lives on the primary chunk (ChunkIndex=0), not the entity |
| `Metadata` | `Metadata` | JSON dictionary |
| `Embedding` | `mem.MemoryChunk.Embedding` | `VECTOR(EmbeddingDimensions)` for cosine search (default 1536) |

## Memory Types (Taxonomy)

Five types classify what a memory IS, not where it came from. The service **throws** on types not in the allowed set:

| Type | Purpose | Graph value | Example |
|------|---------|-------------|---------|
| `Fact` | Verified truths, domain knowledge, established states | Stable nodes that other memories link to | "The staging environment shares a database with QA" |
| `Rule` | Business rules, constraints, policies, conventions | Edges that define relationships and govern decisions | "All API responses must use camelCase" |
| `Instruction` | User directives, preferences, standing orders | Nodes that influence agent behavior until revoked | "Always check order status before offering a refund" |
| `Observation` | Patterns noticed, inferences, situational context | Candidate nodes that may promote to facts or get pruned | "Customer volume increases significantly on Mondays" |
| `Procedural` | Learned workflow patterns — ordered steps, tool preferences | Executable structure recalled when a query implies action | "Onboard new customer: query table, validate fields, send welcome email" |

### Type Enforcement

`MemoryTaxonomyRules.Validate()` rejects memories with a type not in `AgentMemoryOptions.AllowedMemoryTypes`. Content validation is left to the consuming agent or the extraction prompt — this is a general-purpose library serving any domain.

## Core Interfaces

### IAgentMemoryProvider (Factory)

Singleton registered in DI. Caches instances per scope key — agents sharing a scope get one instance:

```csharp
public interface IAgentMemoryProvider
{
    IAgentMemoryService GetMemoryService(string scopeKey);
    bool EvictMemoryService(string scopeKey);
}
```

### IAgentMemoryService (Main Facade)

Bound to a single memory scope. The primary interface agents consume:

```csharp
public interface IAgentMemoryService
{
    string ScopeKey { get; }

    Task<MemoryEntry> SaveMemoryAsync(
        string title, MemoryType type, string content,
        string? description = null, Dictionary<string, string>? metadata = null,
        bool isPointInTime = false,
        CancellationToken ct = default);

    Task<MemoryRecallResult> RecallAsync(
        string query,
        IReadOnlySet<Guid>? alreadySurfacedIds = null,
        CancellationToken ct = default);

    Task<MemoryIndex> GetMemoryIndexAsync(CancellationToken ct = default);

    Task<IReadOnlyList<MemorySearchResult>> SearchArchiveAsync(
        string query, int limit = 10, MemoryType? typeFilter = null,
        CancellationToken ct = default);

    Task<MemoryConsolidationResult> ConsolidateAsync(CancellationToken ct = default);
    Task<bool> ForgetMemoryAsync(Guid memoryId, CancellationToken ct = default);

    Task<MemoryEntry> UpdateMemoryAsync(
        Guid memoryId,
        string? title = null,
        MemoryType? type = null,
        string? content = null,
        string? description = null,
        MemoryTemperature? temperature = null,
        CancellationToken ct = default);

    string FormatRecallContext(MemoryRecallResult recall);

    Task<IReadOnlyList<MemoryEntry>> ExtractMemoriesAsync(
        IList<ChatMessage> messages,
        CancellationToken ct = default);
}
```

`UpdateMemoryAsync` is a partial update — only the supplied (non-null) fields change, and the embedding is regenerated when `content` changes. Recall always operates within the bound scope; the old `MemorySearchScope` enum and `RecallAsync` scope parameter no longer exist — cross-agent sharing is done by binding agents to a shared scope instead.

### IMemoryScopeService (Scope Registry)

Registry over `mem.MemoryScope`. Shared scopes are created explicitly; agent-handle scopes are auto-registered on first save:

```csharp
public interface IMemoryScopeService
{
    Task<MemoryScope> CreateScopeAsync(
        string scopeKey, string? description, bool isShared = true,
        string? createdBy = null, CancellationToken ct = default);
    Task EnsureScopeAsync(string scopeKey, bool isShared = false, CancellationToken ct = default);
    Task<MemoryScope?> GetScopeAsync(string scopeKey, CancellationToken ct = default);
    Task<IReadOnlyList<MemoryScope>> ListScopesAsync(CancellationToken ct = default);
    Task<bool> ScopeExistsAsync(string scopeKey, CancellationToken ct = default);
    Task<int> CountMemoriesInScopeAsync(string scopeKey, CancellationToken ct = default);
}
```

### IMemoryAuditLog (Best-Effort Audit Trail)

Writes to `mem.MemoryAuditLog`. All writes are best-effort — audit failures are logged and never fail the underlying memory operation. Action types: `MemorySaved`, `MemoryMerged`, `MemoryUpdated`, `MemoryForgotten`, `MemoriesExtracted`, `ScopeConsolidated`, `ScopeCreated`, `ScopeDeleted`, `AdminCreated`, `AdminUpdated`, `AdminDeleted`.

### IMemoryAdminService (Administration Surface)

Registered by `AddMemoryAdministration()`. Intended for admin UIs (e.g. the FabrCore.Surface.Admin memory page) and maintenance tooling, not for agents. Provides dashboard stats, scope listing/creation/destructive deletion, paged + filtered memory listing, memory detail, admin create/update/delete of memories (the "teach" path — e.g. save the Rule "Habitat line items are business meal expenses" into scope `"bank-recon"`), per-scope consolidation, and audit review. See [references/api-reference.md](references/api-reference.md) for the full signatures.

### FormatRecallContext

Wraps recalled memories in `<memory-context>` markers that the extraction system recognizes and skips during compaction. **Always use this** when injecting recalled memories into user messages — it prevents re-extraction of already-stored knowledge:

```csharp
var recall = await _memory.RecallAsync(message.Message);
var memoryContext = _memory.FormatRecallContext(recall);
var chatMessage = new ChatMessage(ChatRole.User, message.Message + memoryContext);
```

### Supporting Interfaces

| Interface | Purpose | Key Methods |
|---|---|---|
| `IMemoryStore` | Low-level SQL CRUD + vector search (entities, chunks, edges) | `InsertEntityAsync`, `VectorSearchAsync`, `FindSimilarByContentAsync`, `FindDuplicatePairsAsync`, `ModifyIndexContentAsync`, `GenerateEmbeddingAsync` |
| `IMemoryIndexManager` | Hot layer bounded index | `GetIndexAsync`, `AddIndexEntryAsync`, `TruncateIndexAsync` |
| `IMemoryRetriever` | Three-stage retrieval pipeline + graph traversal | `ScanMemoryHeadersAsync`, `SelectRelevantMemoriesAsync`, `GetRelatedEntitiesAsync`, `GetFreshnessWarning` |
| `IMemoryCompactor` | Consolidation engine | `DeduplicateAsync`, `PruneStaleAsync`, `ResolveContradictionsAsync` |
| `IMemorySummaryTree` | Hierarchical semantic summary tree | `BuildAsync`, `QueryAsync`, `ClearAsync` |
| `IRetrievalPlanner` | Per-query retrieval plan selection (opt-in) | `CreatePlanAsync` |
| `ISyntheticImaginingService` | Conversation-aware multi-query recall | `ImagineAsync` |

## Two Consumption Modes

The library supports two distinct ways of consuming memory. Pick one per agent:

1. **Service-driven (recommended)** — the developer injects `IAgentMemoryProvider` / `IAgentMemoryService` and calls memory explicitly from lifecycle methods: hot-index injection into the system prompt in `OnInitialize`, `RecallAsync` before answering in `OnMessage`/`OnEvent`, and `ExtractMemoriesAsync` via `MemoryCompactionHandler` in `OnCompaction`. Memory management is deterministic; the LLM never has to decide.
2. **Plugin-driven** — register the `agent-memory` plugin so the LLM autonomously decides when to call memory tools mid-conversation.

## Recommended Pattern: Recall + Automatic Memory Extraction (Service-Driven)

This is the recommended approach for most agents. It combines:
- **OnMessage**: Recall relevant memories and inject them into the user message
- **OnCompaction**: Automatically extract and save durable knowledge when the context window fills up

No plugin tools are needed — the compaction cascade handles memory saving automatically. The agent code only needs to recall memories on each message and wire up the compaction handler.

```csharp
using FabrCore.Services.Memory.Abstractions;
using FabrCore.Services.Memory.Configuration;
using FabrCore.Services.Memory.Models;
using FabrCore.Services.Memory.Services;
using FabrCore.Sdk;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;

[AgentAlias("my-memory-agent")]
public class MyMemoryAgent : FabrCoreAgentProxy
{
    private IAgentMemoryService? _memory;
    private MemoryCompactionHandler? _compactionHandler;
    private AIAgent? _agent;
    private AgentSession? _session;

    public MyMemoryAgent(
        AgentConfiguration config,
        IServiceProvider serviceProvider,
        IFabrCoreAgentHost fabrcoreAgentHost)
        : base(config, serviceProvider, fabrcoreAgentHost) { }

    public override async Task OnInitialize()
    {
        // 1. Set up memory service and compaction handler.
        //    MemoryScopeResolver honors a configured shared scope
        //    (plugin setting / Args "MemoryScope") and falls back to config.Handle.
        var provider = serviceProvider.GetRequiredService<IAgentMemoryProvider>();
        _memory = provider.GetMemoryService(MemoryScopeResolver.Resolve(config));

        var compactionService = serviceProvider.GetRequiredService<MemoryAwareCompactionService>();
        var memoryOptions = serviceProvider.GetRequiredService<AgentMemoryOptions>();
        _compactionHandler = new MemoryCompactionHandler(
            _memory, compactionService, memoryOptions,
            serviceProvider.GetRequiredService<ILoggerFactory>());

        // 2. Inject hot layer index into system prompt
        var index = await _memory.GetMemoryIndexAsync();
        if (index.Entries.Count > 0)
        {
            var memoryBlock = string.Join("\n", index.Entries.Select(e =>
                $"- [{e.Type}] {e.Title}: {e.DescriptionHook}"));
            config.SystemPrompt += $"\n\n## Agent Memory\n{memoryBlock}";
        }

        // 3. Set up tools and LLM agent
        var tools = await ResolveConfiguredToolsAsync();
        var result = await CreateChatClientAgent(
            chatClientConfigName: config.Models ?? "default",
            threadId: config.Handle ?? fabrcoreAgentHost.GetHandle(),
            tools: tools);
        _agent = result.Agent;
        _session = result.Session;
    }

    public override async Task<AgentMessage> OnMessage(AgentMessage message)
    {
        var response = message.Response();
        SetStatusMessage("Recalling memories...");

        // Recall relevant warm memories for this query
        var recall = await _memory!.RecallAsync(message.Message);

        // Format with memory-context markers (prevents re-extraction during compaction)
        var memoryContext = _memory.FormatRecallContext(recall);

        SetStatusMessage(null);
        var chatMessage = new ChatMessage(ChatRole.User, message.Message + memoryContext);
        await foreach (var update in _agent!.RunStreamingAsync(chatMessage, _session!))
        {
            response.Message += update.Text;
        }

        return response;
    }

    public override async Task<CompactionResult?> OnCompaction(
        FabrCoreChatHistoryProvider chatHistoryProvider,
        CompactionConfig compactionConfig,
        int estimatedTokens = 0)
    {
        // Three-tier cascade: tool compression -> memory extraction -> structured summary
        return await _compactionHandler!.CompactAsync(chatHistoryProvider, compactionConfig);
    }
}
```

### How It Works

1. **OnMessage** — `RecallAsync` retrieves relevant warm memories, `FormatRecallContext` wraps them in `<memory-context>` markers (so they aren't re-extracted during compaction), and the enriched message is sent to the LLM.
2. **OnCompaction** — When the framework detects token usage exceeding the threshold (after any `OnMessage`), it calls `OnCompaction`. The `MemoryCompactionHandler` runs a three-tier cascade:
   - **Tier 1**: Compress large tool results (free, no LLM call)
   - **Tier 2**: Extract durable memories from older messages and save them to the memory store (LLM call)
   - **Tier 3**: Create a structured handover summary of remaining older messages (LLM call)
3. **Post-compaction** — The hot memory index is re-injected as a system message so the agent knows what it remembers.

Memories extracted during compaction are saved permanently and will be available via `RecallAsync` in future messages and sessions — for shared scopes, to every agent in the scope.

## Optional: Plugin Pattern (LLM Tool Calling)

> **When to use this pattern:** Only use the plugin when you need the LLM to autonomously decide when to call memory tools mid-conversation — for example, a personal assistant that learns user preferences in real-time. For most agents (data query agents, task-oriented agents, domain specialists), the recommended pattern above is sufficient. Memories are extracted automatically during compaction without needing plugin tools.
>
> **Do NOT combine the plugin with the recommended pattern.** The plugin gives the LLM tools to save/recall memories manually. The compaction handler extracts memories automatically. Using both adds unnecessary tool bloat and the LLM may never call the save tools, giving a false sense that memories are being managed.

Register the `agent-memory` plugin so the LLM decides when to save and recall memories:

```csharp
using FabrCore.Services.Memory.Plugin;

[AgentAlias("self-learning-agent")]
public class SelfLearningAgent : FabrCoreAgentProxy
{
    private AIAgent? _agent;
    private AgentSession? _session;

    public SelfLearningAgent(
        AgentConfiguration config,
        IServiceProvider serviceProvider,
        IFabrCoreAgentHost fabrcoreAgentHost)
        : base(config, serviceProvider, fabrcoreAgentHost) { }

    public override async Task OnInitialize()
    {
        // Initialize the memory plugin. MemoryScope is optional — when unset, the scope
        // resolves from the plugin setting / Args "MemoryScope", then config.Handle.
        var memoryPlugin = new AgentMemoryPlugin();
        await memoryPlugin.InitializeAsync(config, serviceProvider);

        // Resolve configured tools + add memory tools
        var tools = await ResolveConfiguredToolsAsync();
        var pluginType = typeof(AgentMemoryPlugin);
        tools.Add(AIFunctionFactory.Create(pluginType.GetMethod(nameof(AgentMemoryPlugin.SaveMemory))!, memoryPlugin));
        tools.Add(AIFunctionFactory.Create(pluginType.GetMethod(nameof(AgentMemoryPlugin.SaveProcedure))!, memoryPlugin));
        tools.Add(AIFunctionFactory.Create(pluginType.GetMethod(nameof(AgentMemoryPlugin.RecallMemories))!, memoryPlugin));
        tools.Add(AIFunctionFactory.Create(pluginType.GetMethod(nameof(AgentMemoryPlugin.SearchArchive))!, memoryPlugin));
        tools.Add(AIFunctionFactory.Create(pluginType.GetMethod(nameof(AgentMemoryPlugin.ForgetMemory))!, memoryPlugin));
        tools.Add(AIFunctionFactory.Create(pluginType.GetMethod(nameof(AgentMemoryPlugin.GetMemoryIndex))!, memoryPlugin));
        tools.Add(AIFunctionFactory.Create(pluginType.GetMethod(nameof(AgentMemoryPlugin.QuerySummaries))!, memoryPlugin));
        tools.Add(AIFunctionFactory.Create(pluginType.GetMethod(nameof(AgentMemoryPlugin.ConsolidateMemories))!, memoryPlugin));

        // System prompt instructs the LLM to use memory tools
        if (string.IsNullOrWhiteSpace(config.SystemPrompt))
        {
            config.SystemPrompt = """
                You are an agent with persistent memory. Use your memory tools to:
                - recall_memories: Check memories before answering questions
                - save_memory: Store important user preferences, feedback, and project context
                - save_procedure: Store reusable multi-step workflows with trigger conditions
                - forget_memory: Remove outdated or incorrect memories
                - consolidate_memories: Clean up duplicates when memory quality degrades

                Memory types:
                - Fact: verified truths, domain knowledge, system behaviors
                - Rule: business rules, constraints, policies, conventions
                - Instruction: user directives, preferences, standing orders
                - Observation: patterns noticed, inferences, situational context
                - Procedural: reusable workflow patterns (prefer save_procedure)

                Save only durable knowledge that will still be true and useful in future conversations.
                Prefer fewer high-confidence memories over many speculative ones.
                """;
        }

        var result = await CreateChatClientAgent(
            chatClientConfigName: config.Models ?? "default",
            threadId: config.Handle ?? fabrcoreAgentHost.GetHandle(),
            tools: tools);
        _agent = result.Agent;
        _session = result.Session;
    }

    public override async Task<AgentMessage> OnMessage(AgentMessage message)
    {
        var response = message.Response();
        var chatMessage = new ChatMessage(ChatRole.User, message.Message);
        await foreach (var update in _agent!.RunStreamingAsync(chatMessage, _session!))
        {
            response.Message += update.Text;
        }
        return response;
    }
}
```

### Declarative Plugin Configuration

Or configure via agent configuration JSON (no code changes needed). Omit `MemoryScope` for isolated per-agent memory, or set it to bind the agent to a shared pool:

```json
{
  "handle": "user123:my-agent",
  "agentType": "my-agent",
  "Plugins": [
    {
      "PluginAlias": "agent-memory",
      "Settings": { "MemoryScope": "bank-recon" }
    }
  ]
}
```

The plugin's `AgentHandle` property still exists as an `[Obsolete]` alias for `MemoryScope` (one release only) — migrate configs to `MemoryScope`.

## Optional: Synthetic Imagining (Conversation-Aware Recall)

Synthetic imagining analyzes the full conversation context to generate multiple targeted memory search queries, runs them in parallel, and returns aggregated, deduplicated results. Use this when a single `RecallAsync` query would miss relevant memories because the conversation context implies needs the user hasn't explicitly stated.

```csharp
using FabrCore.Services.Memory.Abstractions;
using FabrCore.Services.Memory.Configuration;
using FabrCore.Services.Memory.Models;

[AgentAlias("imagining-agent")]
public class ImaginingAgent : FabrCoreAgentProxy
{
    private IAgentMemoryService? _memory;
    private ISyntheticImaginingService? _imagining;
    private AIAgent? _agent;
    private AgentSession? _session;
    private readonly HashSet<Guid> _surfacedMemoryIds = [];

    public ImaginingAgent(
        AgentConfiguration config,
        IServiceProvider serviceProvider,
        IFabrCoreAgentHost fabrcoreAgentHost)
        : base(config, serviceProvider, fabrcoreAgentHost) { }

    public override async Task OnInitialize()
    {
        var provider = serviceProvider.GetRequiredService<IAgentMemoryProvider>();
        _memory = provider.GetMemoryService(MemoryScopeResolver.Resolve(config));
        _imagining = serviceProvider.GetRequiredService<ISyntheticImaginingService>();

        // Inject hot index into system prompt
        var index = await _memory.GetMemoryIndexAsync();
        if (index.Entries.Count > 0)
        {
            var memoryBlock = string.Join("\n", index.Entries.Select(e =>
                $"- [{e.Type}] {e.Title}: {e.DescriptionHook}"));
            config.SystemPrompt += $"\n\n## Agent Memory\n{memoryBlock}";
        }

        var tools = await ResolveConfiguredToolsAsync();
        var result = await CreateChatClientAgent(
            chatClientConfigName: config.Models ?? "default",
            threadId: config.Handle ?? fabrcoreAgentHost.GetHandle(),
            tools: tools);
        _agent = result.Agent;
        _session = result.Session;
    }

    public override async Task<AgentMessage> OnMessage(AgentMessage message)
    {
        var response = message.Response();
        SetStatusMessage("Imagining relevant memories...");

        // Synthetic imagining: LLM generates N diverse queries from conversation context
        var imaginingResult = await _imagining!.ImagineAsync(
            chatHistoryProvider,         // current conversation context
            message.Message,             // latest user message
            _memory!.ScopeKey,           // memory scope to search
            _surfacedMemoryIds);         // skip memories already shown

        // Track surfaced IDs to avoid repeating memories in future turns
        if (imaginingResult.Success)
        {
            foreach (var warm in imaginingResult.AggregatedRecall.WarmMemories)
                _surfacedMemoryIds.Add(warm.Id);
        }

        // Format the aggregated recall context
        var memoryContext = _memory!.FormatRecallContext(imaginingResult.AggregatedRecall);

        SetStatusMessage(null);
        var chatMessage = new ChatMessage(ChatRole.User, message.Message + memoryContext);
        await foreach (var update in _agent!.RunStreamingAsync(chatMessage, _session!))
        {
            response.Message += update.Text;
        }

        return response;
    }
}
```

**When to use imagining vs. plain RecallAsync:**
- Use `RecallAsync` when the user's message is self-contained and directly describes what they need.
- Use `ImagineAsync` when conversation context matters — the LLM reads recent messages to infer implicit needs (applicable preferences, standing instructions, domain facts) that the user hasn't explicitly asked about.
- Imagining costs an extra LLM call (query generation) but runs all downstream searches in parallel.

## Standalone Memory Extraction

For extraction without the full compaction cascade:

```csharp
var memories = await _compactionHandler.ExtractMemoriesAsync(chatHistoryProvider);
// or
var memories = await _memory.ExtractMemoriesAsync(chatMessages);
```

## Plugin Tools Reference

The `AgentMemoryPlugin` (`[PluginAlias("agent-memory")]`) exposes eight LLM-callable tools:

| Tool | Parameters | Returns |
|------|-----------|---------|
| `SaveMemory` | `title`, `type` (Fact/Rule/Instruction/Observation/Procedural), `content`, `description?`, `isPointInTime?` | `{ memoryId, title, type, message }` |
| `SaveProcedure` | `title`, `triggerCondition`, `stepsJson`, `preferredToolsJson?`, `description?` | `{ memoryId, title, type, stepCount, message }` |
| `RecallMemories` | `query` | `{ hotIndex: { entries }, warmMemories: [...], freshnessWarnings }` |
| `SearchArchive` | `query`, `limit?`, `typeFilter?` | `{ results: [{ memoryId, title, content, distance }], count }` |
| `ForgetMemory` | `memoryId` (GUID string) | `{ memoryId, message }` |
| `GetMemoryIndex` | *(none)* | `{ entryCount, estimatedTokens, entries }` |
| `QuerySummaries` | `query`, `limit?` | `{ summaries: [{ nodeId, topic, summary, depth, memberCount }], count }` |
| `ConsolidateMemories` | *(none)* | `{ duplicatesMerged, staleMemoriesPruned, contradictionsResolved, indexEntriesEvicted }` |

## Retrieval Pipeline

The three-stage retrieval pipeline used by `RecallAsync`:

```
Stage 1: Header Scan
  -> IMemoryStore.GetHeadersAsync(scopeKey, limit=200)
  -> Returns lightweight headers (ID, title, type, description, updatedAt)
  -> No content or embeddings loaded -- cheap metadata scan

Stage 2: LLM Relevance Selection
  -> Builds text manifest: "[type] id (date): title -- description" per header
  -> Sends to IChatClient with selection system prompt
  -> LLM picks up to 5 most relevant memory IDs
  -> Falls back to recency-based selection if LLM unavailable

Stage 3: Full Content Load
  -> IMemoryStore.GetEntityByIdAsync + primary chunk for each selected ID
  -> Graph traversal (Retrieval.RecallGraphHops, default 1) runs in parallel
  -> Computes freshness warning for memories older than threshold
  -> Warning format: "[Stale: last updated N days ago] This is a point-in-time
    observation. Verify against current state before relying on it."
```

Set `Retrieval.PlannerEnabled = true` to route recall through `IRetrievalPlanner`, which picks a plan per query (hot-index-only, standard, or deep) instead of always running the full pipeline.

## Synthetic Imagining Pipeline

The `ISyntheticImaginingService` provides conversation-aware multi-query recall:

```
ImagineAsync(chatHistory, lastUserMessage, scopeKey, alreadySurfacedIds)
|
+- 1. BuildConversationText
|     -> Take last 20 messages from chat history
|     -> Strip memory-context markers (prevent feedback loops)
|     -> Format as "[Role]: text" per message
|
+- 2. GenerateQueriesAsync (LLM call)
|     -> System prompt: "You are a memory retrieval strategist"
|     -> Considers: direct topic matches, implicit context needs,
|        applicable preferences, relevant rules, prior observations
|     -> LLM returns JSON: {"queries": ["query1", "query2", ...]}
|     -> Capped at Retrieval.MaxImaginingQueries (default: 5)
|     -> Returns [] if conversation is trivial
|
+- 3. ExecuteQueriesAsync (parallel)
|     -> For each query:
|        -> RecallAsync(query, alreadySurfacedIds) in parallel
|        -> SearchArchiveAsync(query) in parallel
|     -> All tasks run via Task.WhenAll
|
+- 4. Deduplicate & Aggregate
      -> Hot index: taken from first result (identical across queries)
      -> Warm memories: deduplicated by ID
      -> Archive results: deduplicated by ID, keep lowest distance
      -> Freshness warnings: collect unique
      -> Returns SyntheticImaginingResult
```

## Compaction

Memory consolidation keeps the store bounded and high-quality:

```csharp
var result = await memory.ConsolidateAsync();
// result.DuplicatesMerged = 3
// result.StaleMemoriesPruned = 2
// result.ContradictionsResolved = 1
// result.IndexEntriesEvicted = 5
```

Four operations run in sequence:

1. **Deduplication** — `VECTOR_DISTANCE` over a chunk CROSS JOIN finds pairs with cosine distance < `Consolidation.DuplicateDistanceThreshold` (0.05) and same `MemoryType`. LLM merges content, older entry deleted.
2. **Staleness pruning** — Memories older than 30 days not in hot index. LLM confirms staleness. Demoted to `Cold` (archived, not deleted).
3. **Contradiction resolution** — Recent 20 memories sent to LLM. Conflicting facts identified. Stale side demoted to `Cold`.
4. **Index truncation** — Hot layer capped at `HotIndex.MaxEntries` / `HotIndex.MaxTokens`. Oldest evicted (remain as warm).

When `SummaryTree.Enabled = true`, consolidation also rebuilds the hierarchical semantic summary tree (`mem.MemorySummaryNode`).

**Auto-consolidation:** Set `options.Consolidation.EnableAutoConsolidation = true` to trigger consolidation automatically when memory count exceeds `Consolidation.MemoryFileCap` (200).

## Configuration Reference

Options are grouped: top-level settings plus `HotIndex`, `Retrieval`, `Consolidation`, `SummaryTree`, `Compaction`, and `Models` sub-groups. Old flat names (`HotLayerMaxEntries`, `RetrievalPlannerEnabled`, `SummaryTreeEnabled`, etc.) no longer exist.

```csharp
builder.Services.AddAgentMemoryServices("MemoryDb", options =>
{
    // Top-level
    options.EmbeddingDimensions = 1536;             // VECTOR dimension; fixed at schema creation (default: 1536)
    options.AllowStartupWithoutEmbeddings = false;  // true = LogError instead of fail-fast (client-only hosts)
    options.PointInTimeMemories = false;            // Mark all extracted memories as snapshots (default: false)
    options.AllowedMemoryTypes = new()
    {
        MemoryType.Fact, MemoryType.Rule, MemoryType.Instruction,
        MemoryType.Observation, MemoryType.Procedural
    };

    // Hot layer caps
    options.HotIndex.MaxEntries = 20;               // Default: 20
    options.HotIndex.MaxTokens = 3000;              // Default: 3000
    options.HotIndex.ReInjectAfterCompaction = true; // Default: true

    // Retrieval
    options.Retrieval.WarmRetrievalLimit = 5;       // Default: 5
    options.Retrieval.HeaderScanLimit = 200;        // Default: 200
    options.Retrieval.FreshnessDaysThreshold = 1;   // Default: 1
    options.Retrieval.RecallGraphHops = 1;          // 0 = vector only (default: 1)
    options.Retrieval.PlannerEnabled = false;       // Opt-in retrieval planner (default: false)
    options.Retrieval.MaxImaginingQueries = 5;      // Default: 5

    // Consolidation
    options.Consolidation.MemoryFileCap = 200;              // Default: 200
    options.Consolidation.EnableAutoConsolidation = false;  // Default: false
    options.Consolidation.DuplicateDistanceThreshold = 0.05; // Default: 0.05
    options.Consolidation.EntityMatchThreshold = 0.25;      // Merge-on-save threshold (default: 0.25)
    options.Consolidation.EnableRelationshipExtraction = true; // Default: true

    // Summary tree (opt-in)
    options.SummaryTree.Enabled = false;            // Default: false
    options.SummaryTree.MaxDepth = 2;               // Default: 2
    options.SummaryTree.Fanout = 7;                 // Default: 7

    // Compaction: Tier 1 (tool result compression, free)
    options.Compaction.ToolResultCompressionThreshold = 2000; // Default: 2000 chars
    options.Compaction.ToolResultKeepHeadChars = 200;         // Default: 200
    options.Compaction.ToolResultKeepTailChars = 200;         // Default: 200

    // Compaction: Tier 3 (structured summarization)
    options.Compaction.SummaryMaxTokens = 3000;               // Default: 3000

    // LLM models (must match fabrcore.json model entries)
    options.Models.RelevanceModelName = "default";   // Default: "default"
    options.Models.CompactionModelName = "default";  // Default: "default"
    options.Models.ImaginingModelName = "default";   // Default: "default"
    options.Models.PlannerModelName = "";            // "" = falls back to RelevanceModelName
    options.Models.SmallModelName = "";              // Tier-level override for classification calls
    options.Models.LargeModelName = "";              // Tier-level override for merge/rollup calls
});
```

`ConnectionStringName` is set internally from the required `AddAgentMemoryServices` parameter and cannot be assigned in the callback. **Changing `EmbeddingDimensions` after the schema exists requires dropping the `mem` schema** — VECTOR column dimensions are fixed at creation.

## Point-in-Time Memories (Snapshot Mode)

For agents that work with live data sources (database query agents, monitoring agents), extracted memories often contain transient data that is stale the moment it's saved. The `PointInTimeMemories` flag addresses this.

### Agent-Level (All Extracted Memories Are Snapshots)

```csharp
builder.Services.AddAgentMemoryServices("MyDb", options =>
{
    options.PointInTimeMemories = true;
});
```

When enabled:
- The extraction prompt discourages saving transient query results as Facts
- All extracted memories are stamped with `IsPointInTime = true`
- Recalled point-in-time memories always show: `[Snapshot: captured earlier today] This was a point-in-time snapshot. Query the source for current values.`
- Point-in-time memories are pruned after 3 days (vs 30 days for durable memories)
- Hot index entries and warm memories display `[snapshot]` annotations
- The LLM relevance selector deprioritizes `[snapshot]` memories

### Per-Memory (Mixed Agent)

For agents where some memories are durable and some are snapshots:

```csharp
// Save a point-in-time observation
await memoryService.SaveMemoryAsync(
    title: "Current plate inventory count",
    type: MemoryType.Observation,
    content: "There are 847 plates in inventory as of this query",
    isPointInTime: true);

// Save a durable fact (default behavior)
await memoryService.SaveMemoryAsync(
    title: "Plate thickness unit is inches",
    type: MemoryType.Fact,
    content: "All plate thickness measurements in the system are in inches, not mm");
```

## Models Reference

| Model | Purpose |
|-------|---------|
| `MemoryEntry` | Core memory entity — Id, ScopeKey, Title, Type, Temperature, IsPointInTime, Description, Metadata, CreatedAt, UpdatedAt; Content/Embedding populated from the primary chunk; Chunks/Relationships loaded on request |
| `MemoryScope` | Scope registry row — ScopeKey, Description, IsShared, CreatedAt, CreatedBy |
| `MemoryIndex` | Hot layer — list of `MemoryIndexEntry` with `TotalEstimatedTokens` |
| `MemoryIndexEntry` | One-line pointer — MemoryId, Title, Type, DescriptionHook, IsPointInTime, UpdatedAt |
| `MemoryHeader` | Lightweight scan result — MemoryId, Title, Type, Description, UpdatedAt |
| `MemoryChunkEntry` | Content + embedding chunk belonging to an entity |
| `MemoryRelationshipEntry` | Typed, weighted, directed graph edge between entities |
| `MemorySearchResult` | Vector search result — Entry + Distance + FreshnessWarning |
| `MemoryRecallResult` | Full recall — HotIndex + WarmMemories + FreshnessWarnings |
| `MemoryConsolidationResult` | Compaction stats — DuplicatesMerged, StaleMemoriesPruned, ContradictionsResolved, IndexEntriesEvicted |
| `MemorySummaryNode` | Summary tree node — NodeId, Topic, Summary, Depth, MemberCount |
| `SyntheticImaginingResult` | Imagining output — GeneratedQueries, AggregatedRecall, ArchiveResults, UniqueMemoryCount, Success, ErrorMessage |
| `MemoryAuditEntry` | Audit row — AuditId, OccurredAt, ActionType, ScopeKey, MemoryId, ActorId, ActorName, Summary, Payload, DurationMs |
| `ProceduralSteps` | Structured procedure — TriggerCondition, ordered Steps, PreferredTools |
| `MemoryType` | Enum: `Fact`, `Rule`, `Instruction`, `Observation`, `Procedural` |
| `MemoryTemperature` | Enum: `Hot`, `Warm`, `Cold` |

## Pitfalls and Common Mistakes

See the [Pitfalls Reference](references/pitfalls.md) for a comprehensive list. Key ones:

1. **Forgetting `FormatRecallContext`** — If you inject recalled memories into messages without the `<memory-context>` wrapper, the extraction system will re-extract them during compaction, creating duplicates every compaction cycle.

2. **Not overriding OnCompaction** — Without the `MemoryCompactionHandler`, the default compaction summarizes away conversation content without extracting durable knowledge first. Memories that should persist get lost in the summary.

3. **Saving ephemeral state as memories** — Save only durable knowledge. Tool outputs, intermediate reasoning, and transient status should stay in chat history, not the memory store. The hot index has a small budget (20 entries / 3,000 tokens) — waste it on transient data and important memories get evicted.

4. **Shared scopes multiply consolidation cost** — many agents writing one scope concentrates rows; the O(n²) chunk CROSS JOIN dedup gets more expensive. Tune `Consolidation.MemoryFileCap` and consolidate regularly.

5. **Changing `EmbeddingDimensions` after schema creation** — VECTOR columns are fixed at creation; inserts break on dimension mismatch. Drop the `mem` schema to change.

6. **Not tracking `alreadySurfacedIds`** — Without tracking, the same warm memories resurface every turn, wasting the retrieval budget and cluttering context. Pass surfaced IDs to both `RecallAsync` and `ImagineAsync`.

7. **Hot index is not chat history** — Do not treat hot memory like a conversation log. It is a persistent index of durable knowledge. Chat history is transient; hot memory survives sessions, compaction, and restarts.

## Constraints

- **Self-contained schema** — creates `mem.MemoryEntity`, `MemoryChunk`, `MemoryRelationship`, `MemorySummaryNode`, `MemoryScope`, `MemoryAuditLog` on startup (no GraphRagAgent dependency)
- **Fail-fast startup** — missing connection string, failed schema DDL, or missing `IEmbeddings` stops the host unless `AllowStartupWithoutEmbeddings` is set
- **Taxonomy is enforced at the service layer** — `SaveMemoryAsync` throws on invalid types
- **LLM is optional** — retrieval falls back to recency when `IFabrCoreChatClientService` is unavailable
- **Consolidation archives, never hard-deletes** — stale/contradicted memories demoted to `Cold`
- **Backward compatible** — existing `Visibility="Private"` rows treated as `Warm` temperature
- **Thread-safe** — `AgentMemoryProvider` caches instances per scope key; hot-index writes are serialized per scope via a SQL applock (`sp_getapplock 'mem-index-{scope}'`, 15s timeout) so concurrent shared-scope writers cannot lose entries
- **Audit is best-effort** — `IMemoryAuditLog` swallows database errors; audit failures never fail the memory operation
- **Compaction is tiered** — free tool result compression first, then LLM extraction, then LLM summarization. Each tier exits early if tokens are under threshold.
- **Memory-context markers** — recalled memories are wrapped in `<memory-context>` tags that the extraction system strips, preventing re-extraction of already-stored knowledge during compaction
- **Summary is agent-optimized** — structured for continuation, not human readability. Includes a note about extracted memories so the agent knows to recall them.
- **Imagining is graceful** — `ImagineAsync` never throws; returns `Success=false` with `ErrorMessage` on failure, letting the agent fall back to plain `RecallAsync`.
