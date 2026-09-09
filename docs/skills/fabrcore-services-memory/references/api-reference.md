# Memory API reference

Use `FabrCore.Services.Memory.Abstractions` for provider/service interfaces,
`FabrCore.Services.Memory.Models` for memory models, and
`FabrCore.Services.Memory.Configuration` for options and integration extensions.
Some public contracts live in the Services.Contracts assembly; use their declared namespaces.

## Scope binding

`IAgentMemoryProvider.GetMemoryService(string scopeKey)` returns a cached, scope-bound
`IAgentMemoryService`. Scopes are trimmed, nonempty, and at most 200 characters.
`EvictMemoryService` evicts the process cache, not SQL data. Model tool arguments cannot
supply a different scope. See the skill entry point for configuration precedence.

## Service facade

All asynchronous operations below accept an optional final `CancellationToken ct`.

| Member | Arguments before cancellation | Result |
| --- | --- | --- |
| SaveMemoryAsync | string title, MemoryType type, string content, string? description = null, Dictionary<string,string>? metadata = null, bool isPointInTime = false | MemoryEntry |
| RecallAsync | string query, IReadOnlySet<Guid>? alreadySurfacedIds = null | MemoryRecallResult |
| GetMemoryIndexAsync | none | MemoryIndex |
| SearchArchiveAsync | string query, int limit = 10, MemoryType? typeFilter = null | IReadOnlyList<MemorySearchResult> |
| ConsolidateAsync | none | MemoryConsolidationResult |
| ForgetMemoryAsync | Guid memoryId | bool |
| UpdateMemoryAsync | Guid memoryId, string? title = null, MemoryType? type = null, string? content = null, string? description = null, MemoryTemperature? temperature = null | MemoryEntry |
| ExtractMemoriesAsync | IList<ChatMessage> messages | IReadOnlyList<MemoryEntry> |

`ScopeKey` is a read-only property.
`string FormatRecallContext(MemoryRecallResult recall)` formats reference context; it does
not perform retrieval or inject messages by itself.

## Saves, corrections, and deletion

Saves start Warm and receive a bounded hot-index pointer. Ordinary same-type active saves
can merge based on similarity. Similarity is not a stable identity: retain the returned ID
and use UpdateMemoryAsync for a known correction. Metadata-bearing saves and point-in-time
snapshots bypass automatic merge-on-save. Save provenance from trusted application code.

Set temperature Cold to archive, Warm or Hot to restore active retrieval eligibility.
Hot does not guarantee permanent index residency. Forget removes stored entity/chunks/edges;
built-in SQL mutations also invalidate derived summary nodes for the scope. Chat history,
audit, backups, and exported copies have separate retention.

## Recall and evidence

Frozen ordinary recall scans at most 200 headers and selects up to five memories before
graph expansion, loading primary chunks. It is not an exhaustive search of every chunk.
The header selector can fall back to recent candidates on failure. Semantic candidate
selection is opt-in and its selector failure does not silently become ordinary recency recall.

Matched-chunk evidence is also opt-in. Selected bodies are bounded to 4,096 characters per
entity and 12,000 total; graph expansion is separate. RecalledChunkId, RecalledChunkIndex,
and IsContentTruncated describe singular evidence. RecalledChunks carries per-chunk source
IDs, indices, content, and truncation when multiple chunks are returned; singular source
fields are null for multi-chunk evidence. These bounded recall bodies do not replace stored
content. Selection preview options affect selector input, not stored data.

SearchArchiveAsync searches all retained embedded memories, including Cold. It does not
promote results. alreadySurfacedIds suppresses returned warm/archive content, not index
pointers; only pass IDs whose evidence remains in current context.

FormatRecallContext includes the memory-context marker, index pointers, selected content,
and freshness warnings. The extraction path filters marked injected context. The marker
does not authenticate content, and a freshness warning does not verify truth.

## Extraction and compaction

Extraction examines eligible chat messages for durable knowledge. Empty eligible input or
a valid no-facts response can return an empty list. Missing model configuration, malformed
extraction output, persistence errors, and cancellation can throw.

Built-in SQL extraction uses a source-history receipt and transaction so retrying the same
extraction can reuse the committed result. This is not a universal exactly-once guarantee
for arbitrary writes or changed histories. MemoryCompactionHandler extracts before replacing
history; failures propagate and preserve history. See [architecture](architecture.md).

## Tools and optional features

The agent-memory plugin exposes nine methods: SaveMemory, SaveProcedure, UpdateMemory,
RecallMemories, SearchArchive, ForgetMemory, GetMemoryIndex, QuerySummaries, ConsolidateMemories.
QuerySummaries is a plugin/summary-tree feature, not an IAgentMemoryService method.
Summary trees are disabled by default.

CreateMemoryTools provides eight scoped tools (the above except QuerySummaries), or only
RecallMemories, SearchArchive, GetMemoryIndex with readOnly: true. See
[harness integration](harness-and-internal-agents.md).

Use [registration](../assets/server-registration.cs) for model names and SQL configuration.
Treat [frozen defaults](../../../memory-release-defaults.md) as the release policy;
change opt-in retrieval and imagining settings only after representative evaluations.
