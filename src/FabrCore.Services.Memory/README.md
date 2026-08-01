# FabrCore.Services.Memory

Public Memory administration interfaces, DTOs, audit records, and transport client contracts are
provided by the shared `FabrCore.Services.Contracts` package. Their existing
`FabrCore.Services.Memory.*` namespaces are preserved, and this SQL-backed service package forwards
the moved public types for binary compatibility. This boundary allows remote Memory administration
to use the same contracts NuGet as GraphRAG without bringing SQL or schema services into a UI host.

Three-temperature agent memory management for FabrCore agents, backed by SQL Server knowledge graph tables with vector search.

Provides structured long-term memory with hot/warm/cold retrieval layers, taxonomy-enforced storage, LLM-based relevance selection, freshness tracking, and automatic compaction — designed as a service library that any `FabrCoreAgentProxy` implementation can consume via DI.

## Prerequisites

- **FabrCore.Host** 0.9.2+ with `AddFabrCoreServer()` configured
- **SQL Server 2025** or **Azure SQL** with `VECTOR` support (dimension configurable via `EmbeddingDimensions`, default 1536)
- **IEmbeddings** registered — provided by `AddFabrCoreServer()` with an `"embeddings"` model entry in `fabrcore.json`

The `mem` schema and memory tables (`MemoryEntity`, `MemoryChunk`, `MemoryRelationship`, `MemorySummaryNode`, `MemoryScope`, `MemoryAuditLog`) are created automatically on startup. Startup fails fast when the connection string is missing, schema creation fails, or `IEmbeddings` is not registered — set `AllowStartupWithoutEmbeddings` to relax this for client-only hosts.

## Memory scopes: isolated by default, shared when configured

Every memory belongs to a **scope**. An agent's default scope is its own handle, so its memory is isolated — the majority case. To give a fleet of agents one shared memory pool (e.g. every bank-reconciliation agent should know "line items with Habitat are business meal expenses" once one of them is taught), point them all at a named shared scope:

```json
"plugins": ["agent-memory"],
"args": { "agent-memory:MemoryScope": "bank-recon" }
```

Scope resolution precedence: explicit code value → plugin setting `MemoryScope` → `Args["MemoryScope"]` → `Args["AgentHandle"]` (legacy) → the agent handle. Shared scopes can be pre-created via `IMemoryScopeService` or from the FabrCore.Surface.Admin memory page (`/surface/admin/memory`); agent-handle scopes auto-register on first write.

## Installation

Add the NuGet package to your FabrCore server or agent project:

```xml
<PackageReference Include="FabrCore.Services.Memory" Version="*" />
```

## Quick Start

### 1. Register services

In your FabrCore server startup:

```csharp
using FabrCore.Services.Memory.Configuration;

services.AddAgentMemoryServices("MemoryDb");

// Optional: administration surface for admin UIs (e.g. /surface/admin/memory)
services.AddMemoryAdministration();
```

### 2. Use in an agent (programmatic)

```csharp
using FabrCore.Services.Memory.Abstractions;

[AgentAlias("my-agent")]
public class MyAgent : FabrCoreAgentProxy
{
    private IAgentMemoryService? _memory;

    public MyAgent(
        AgentConfiguration config,
        IServiceProvider serviceProvider,
        IFabrCoreAgentHost fabrcoreAgentHost)
        : base(config, serviceProvider, fabrcoreAgentHost) { }

    public override async Task OnInitialize()
    {
        // Get a memory service scoped to this agent
        var provider = serviceProvider.GetRequiredService<IAgentMemoryProvider>();
        _memory = provider.GetMemoryService(MemoryScopeResolver.Resolve(config));

        // Load the hot index into the system prompt
        var index = await _memory.GetMemoryIndexAsync();
        if (index.Entries.Count > 0)
        {
            var memoryContext = string.Join("\n", index.Entries.Select(e =>
                $"- [{e.Type}] {e.Title}: {e.DescriptionHook}"));
            config.SystemPrompt += $"\n\n## Agent Memory Index\n{memoryContext}";
        }

        var tools = await ResolveConfiguredToolsAsync();
        var result = await CreateChatClientAgent(
            chatClientConfigName: config.Models ?? "default",
            threadId: config.Handle ?? fabrcoreAgentHost.GetHandle(),
            tools: tools);
    }

    public override async Task<AgentMessage> OnMessage(AgentMessage message)
    {
        // Recall relevant memories for this query
        var recall = await _memory!.RecallAsync(message.Message);

        // Build context from warm memories
        var context = "";
        if (recall.WarmMemories.Count > 0)
        {
            context = "\n\n## Relevant Memories\n" + string.Join("\n\n",
                recall.WarmMemories.Select(m =>
                    $"### {m.Title} ({m.Type})\n{m.Content}"));
        }
        if (recall.FreshnessWarnings.Count > 0)
        {
            context += "\n\n## Freshness Warnings\n" + string.Join("\n",
                recall.FreshnessWarnings);
        }

        // Format with memory-context markers (prevents re-extraction during compaction)
        var memoryContext = _memory.FormatRecallContext(recall);

        // Pass context + message to the LLM
        var chatMessage = new ChatMessage(ChatRole.User, message.Message + memoryContext);
        // ... run agent with enriched context

        // Save important observations as memories
        await _memory.SaveMemoryAsync(
            title: "Customer prefers email communication",
            type: MemoryType.Fact,
            content: "Customer explicitly stated they prefer email over phone for all communications.");

        return response;
    }
}
```

### 3. Use via plugin (LLM tool calling)

Register the `agent-memory` plugin so the LLM can save and recall memories autonomously:

```csharp
using FabrCore.Services.Memory.Plugin;

public override async Task OnInitialize()
{
    // Initialize the memory plugin
    var memoryPlugin = new AgentMemoryPlugin(); // scope resolves from config (MemoryScope setting or agent handle)
    await memoryPlugin.InitializeAsync(config, serviceProvider);

    // Resolve all tools (including memory plugin tools)
    var tools = await ResolveConfiguredToolsAsync();

    // Or register the plugin tools manually
    tools.Add(AIFunctionFactory.Create(
        typeof(AgentMemoryPlugin).GetMethod(nameof(AgentMemoryPlugin.SaveMemory))!,
        memoryPlugin));
    tools.Add(AIFunctionFactory.Create(
        typeof(AgentMemoryPlugin).GetMethod(nameof(AgentMemoryPlugin.RecallMemories))!,
        memoryPlugin));
    // ... add other tools as needed

    var result = await CreateChatClientAgent(
        chatClientConfigName: config.Models ?? "default",
        threadId: config.Handle ?? fabrcoreAgentHost.GetHandle(),
        tools: tools);
}
```

Or configure the plugin declaratively in agent configuration:

```json
{
  "handle": "my-agent",
  "agentType": "my-agent",
  "plugins": ["agent-memory"],
  "args": {
    "ConnectionStringName": "MemoryDb"
  }
}
```

## Memory Types

The taxonomy enforces what should and should not be stored as durable memory. Five types are allowed:

| Type | Purpose | Examples |
|------|---------|---------|
| `Fact` | Verified truths, domain knowledge, system behaviors | "The staging environment shares a database with QA" |
| `Rule` | Business rules, constraints, policies, conventions | "Line items with Habitat are business meal expenses" |
| `Instruction` | User directives, preferences, standing orders | "Always check order status before offering a refund" |
| `Observation` | Patterns noticed, inferences, situational context | "Customer volume increases significantly on Mondays" |
| `Procedural` | Reusable workflows — ordered steps for a class of task | "To onboard a customer: validate, create record, send welcome email" |

### Type Enforcement

The service **rejects** (throws) memories with a type not in the configured `AllowedMemoryTypes` set. Content validation is left to the consuming agent — this is a general-purpose library serving any domain.

## Three-Temperature Architecture

```
Hot Layer (always loaded)
  Bounded index of one-line pointers to warm memories.
  Injected into every agent context window.
  Caps: 50 entries / 6,000 tokens (configurable).

Warm Layer (on-demand)
  Full memory entities with embeddings.
  Retrieved via three-stage pipeline:
    1. Cheap header scan (metadata only, up to 200)
    2. LLM relevance selection (picks up to 5)
    3. Full content load for selected memories
  Stale memories get freshness warnings.

Cold Layer (searchable archive)
  Chunks and demoted entities.
  Vector search only, never bulk-loaded.
  Used for deep historical lookups.
```

## Memory-Aware Compaction

The library provides a multi-tier compaction service that replaces the default `CompactionService` for agents using memory. It runs automatically from `OnCompaction`:

```csharp
using FabrCore.Services.Memory.Services;

// In OnInitialize:
var compactionService = serviceProvider.GetRequiredService<MemoryAwareCompactionService>();
_compactionHandler = new MemoryCompactionHandler(
    _memory, compactionService, memoryOptions,
    serviceProvider.GetRequiredService<ILoggerFactory>());

// In OnCompaction (one line):
public override async Task<CompactionResult?> OnCompaction(
    FabrCoreChatHistoryProvider chatHistoryProvider,
    CompactionConfig compactionConfig,
    int estimatedTokens = 0)
{
    return await _compactionHandler.CompactAsync(chatHistoryProvider, compactionConfig);
}
```

### Three-Tier Cascade

| Tier | Cost | What it does |
|------|------|-------------|
| **1. Tool Result Compression** | Free (no LLM) | Compresses large tool outputs in older messages, preserving head/tail |
| **2. Memory Extraction** | LLM call | Extracts durable facts, rules, instructions, observations into the graph |
| **3. Structured Summarization** | LLM call | Produces a continuation-optimized handover summary |

Each tier checks whether the token threshold is satisfied before proceeding. If Tier 1 alone brings tokens under the threshold, Tiers 2 and 3 are skipped entirely.

After compaction, the hot memory index is re-injected as a system message so the agent immediately knows what it remembers.

### Standalone Extraction

To extract memories without running the full compaction cascade:

```csharp
var extracted = await _compactionHandler.ExtractMemoriesAsync(chatHistoryProvider);
// or
var extracted = await _memory.ExtractMemoriesAsync(chatMessages);
```

## Configuration

```csharp
services.AddAgentMemoryServices("MemoryDb", options =>
{
    // Embedding vector dimension — must match the embeddings model; fixed at
    // schema creation (changing it later requires dropping the mem schema)
    options.EmbeddingDimensions = 1536;

    // Hot layer caps
    options.HotIndex.MaxEntries = 20;      // Max index entries
    options.HotIndex.MaxTokens = 3000;     // Max estimated tokens

    // Retrieval
    options.Retrieval.WarmRetrievalLimit = 5;     // Max warm memories per query
    options.Retrieval.HeaderScanLimit = 200;      // Max headers scanned
    options.Retrieval.FreshnessDaysThreshold = 1; // Days before staleness warning

    // Consolidation
    options.Consolidation.MemoryFileCap = 200;               // Max entities per scope
    options.Consolidation.EnableAutoConsolidation = false;   // Auto-compact on save
    options.Consolidation.DuplicateDistanceThreshold = 0.05; // Cosine distance for dedup

    // Compaction: Tier 1 (tool result compression)
    options.Compaction.ToolResultCompressionThreshold = 2000; // Chars, compress above this
    options.Compaction.ToolResultKeepHeadChars = 200;         // Keep first N chars
    options.Compaction.ToolResultKeepTailChars = 200;         // Keep last N chars

    // Compaction: Tier 3 (structured summarization)
    options.Compaction.SummaryMaxTokens = 3000;      // Max output tokens for summary
    options.HotIndex.ReInjectAfterCompaction = true; // Re-inject memory index after compaction

    // LLM models (must match fabrcore.json entries)
    options.Models.RelevanceModelName = "default";
    options.Models.CompactionModelName = "default";

    // Restrict allowed types (default: all five)
    options.AllowedMemoryTypes = new()
    {
        MemoryType.Fact,
        MemoryType.Rule,
        MemoryType.Instruction,
        MemoryType.Observation,
        MemoryType.Procedural
    };
});
```

## Plugin Tools

When using the `AgentMemoryPlugin`, the LLM gets access to these tools:

| Tool | Description |
|------|-------------|
| `save_memory` | Save a typed memory with taxonomy validation |
| `save_procedure` | Save a structured, reusable multi-step workflow |
| `recall_memories` | Recall hot index + relevant warm memories for a query |
| `search_archive` | Vector search the cold layer archive |
| `forget_memory` | Delete a memory by ID |
| `get_memory_index` | View the hot layer table of contents |
| `query_summaries` | Query the hierarchical summary tree for topic rollups |
| `consolidate_memories` | Run dedup, prune stale, resolve contradictions |

## Compaction

Memory compaction can be triggered manually or automatically:

```csharp
// Manual
var result = await memory.ConsolidateAsync();
// result.DuplicatesMerged, result.StaleMemoriesPruned, etc.

// Automatic (triggers when memory count exceeds MemoryFileCap)
options.EnableAutoConsolidation = true;
```

Consolidation performs four operations:

1. **Deduplication** — finds memory pairs with cosine distance below threshold (same type), merges content via LLM, deletes the older entry
2. **Staleness pruning** — identifies memories older than 30 days not in the hot index, confirms staleness via LLM, demotes to Cold (archive, not delete)
3. **Contradiction resolution** — sends recent memories to LLM to identify conflicting facts, demotes the stale side to Cold
4. **Index truncation** — enforces hot layer caps, evicts oldest entries

## Data Model

This library manages its own `mem` schema, created automatically on startup. Every row is partitioned by `ScopeKey`:

| Table | Type | Purpose |
|---|---|---|
| `mem.MemoryEntity` | SQL Graph NODE | Memory entities (metadata; content lives in chunks) |
| `mem.MemoryChunk` | Regular table | Content + configurable-dimension VECTOR embeddings |
| `mem.MemoryRelationship` | SQL Graph EDGE | Relationships between memory entities |
| `mem.MemorySummaryNode` | Regular table | Hierarchical topic rollups built during consolidation |
| `mem.MemoryScope` | Regular table | Scope registry (shared pools + auto-registered agent scopes) |
| `mem.MemoryAuditLog` | Regular table | Best-effort trail of memory-changing actions |

Column mapping:

| MemoryEntry field | SQL column | Notes |
|---|---|---|
| `ScopeKey` | `ScopeKey` | Partition key: agent handle or shared scope |
| `Title` | `Name` | Short descriptive title |
| `Type` | `EntityType` | "Fact", "Rule", "Instruction", "Observation", "Procedural" |
| `Temperature` | `Visibility` | "Hot", "Warm", "Cold" |
| `Description` | `Description` | Brief description |
| `Content` | `Content` | Full memory content (stored in `MemoryChunk`) |
| `Metadata` | `Metadata` | JSON dictionary |
| `Embedding` | `Embedding` | VECTOR for cosine search (dimension from options) |

The hot layer index is stored as a single entity row with `Name="__MEMORY_INDEX__"` and `EntityType="MemoryIndex"`.

## Interfaces and Services

For custom implementations or testing, all components are abstracted:

| Interface / Service | Purpose |
|---|---|
| `IAgentMemoryProvider` | Factory: scope key (agent handle or shared scope) to scoped service |
| `IAgentMemoryService` | Main facade (save, recall, extract, search, forget, update, consolidate) |
| `IMemoryScopeService` | Scope registry (create/list shared scopes, auto-registration) |
| `IMemoryAuditLog` | Best-effort audit writes to `mem.MemoryAuditLog` |
| `IMemoryAdminService` | Administration surface (dashboards, scope + memory CRUD, audit) via `AddMemoryAdministration()` |
| `IMemoryStore` | Low-level SQL CRUD + vector search |
| `IMemoryIndexManager` | Hot layer bounded index management (scope-locked writes) |
| `IMemoryRetriever` | Three-stage retrieval pipeline |
| `IMemoryCompactor` | Consolidation engine |
| `MemoryAwareCompactionService` | Three-tier compaction cascade (tool compression + extraction + summarization) |
| `MemoryCompactionHandler` | Unified entry point for compaction and standalone extraction |
