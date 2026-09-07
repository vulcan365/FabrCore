# Integration pitfalls

- **Two memory libraries:** `FabrCore.Sdk.Memory` is a separate API. These examples use `FabrCore.Services.Memory` and its provider, plugin, and SQL schema.
- **No automatic injection:** returning a hot index does not update an existing model prompt. Recall and format when needed; avoid a stale initialization-only copy.
- **Duplicate extraction:** use `FormatRecallContext` for injected recall. Its markers are a convention, not tamper-proof parsing. Plugin JSON results are not marker-wrapped; automatic extraction from tool history needs separate application consideration.
- **Cold is retained:** ordinary recall excludes Cold, but explicit archive search includes all temperatures. Restoring requires an update. Neither index eviction nor search deletes a memory.
- **Dimensions:** embedding dimensions are fixed in VECTOR columns. Changing models/dimensions requires a planned migration and re-embedding, with backups; do not drop production memory to change this setting.
- **Scope reuse:** ephemeral handles create separate memory pools. Bind a stable scope for cross-session continuity and authorize shared scopes in trusted code.
- **Tool discovery:** ensure the memory assembly is in plugin discovery, or explicitly initialize `AgentMemoryPlugin` and register its functions once. The supplied explicit template uses PascalCase function names.
- **Compaction:** optional `MemoryCompactionHandler` integration extracts before summarizing. Ordinary chat compaction alone does not populate this store. Direct saves work with neither hook.
- **Budgets:** hot index tokens are estimates. Graph expansion and individual content size can exceed the warm selection budget; apply a total context budget in your application.
- **Failure handling:** SQL writes can fail partially. Avoid unconditional retry of saves because similarity matching is not exactly-once persistence. Background auto-consolidation is optional and not a durable job queue.
- **Staleness:** freshness warnings are cues to verify source data, not a guarantee of correctness. Rebuild derived summaries after material changes when enabled.
