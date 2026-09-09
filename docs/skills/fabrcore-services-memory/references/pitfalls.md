# Troubleshooting and integration pitfalls

- **Registration alone does not inject memory.** Bare RecallAsync returns data. Use
  WithMemory/WithMemoryLifecycle or explicitly format and inject it. Avoid doing both for
  the same turn.
- **Tools registered twice.** Choose configured agent-memory, manual plugin functions, or
  includeTools on the harness for each agent. The harness rejects duplicate function names.
- **Compaction runs twice.** Use the harness lifecycle callback or the manual OnCompaction
  handler for the same history path. WithMemory alone adds recall, not extraction.
- **A missing old fact.** Default recall scans capped headers and reads primary chunks.
  Archive search and opt-in semantic/matched-chunk recall address different retrieval needs.
  Raising caps increases cost; evaluate coverage before changing defaults.
- **Bounded evidence looks complete.** Inspect truncation and chunk provenance. Graph
  expansion is outside the matched-body budget; manual formatting has no universal final
  context cap. Harness recall has a separate 12,000-character default cap.
- **Memory disappears after a restart.** Check the resolved scope and stable agent handle,
  database connection, and persistence. Provider eviction is not data deletion.
- **A specialist changes core memory unexpectedly.** CoreOnly shares writes. CoreAndOwn
  reads both but writes only to own. OwnOnly is the default. Bind scopes in trusted code
  and use scoped tools/risk classification for background writes.
- **Empty extraction hides an error.** Do not catch every exception and return an empty list.
  Extraction errors and cancellation propagate; preserve source history for retry.
- **Retry duplicates a write.** SQL extraction receipts cover the same extraction source,
  not every arbitrary SaveMemoryAsync call. Post-commit callbacks may fail after commit.
  Use known IDs for corrections and define ownership for concurrent writers.
- **A hot memory is evicted.** Hot is a capped pointer index, not pinning. Index eviction
  leaves the entity Warm. Archive explicitly to remove ordinary recall eligibility.
- **Archive search returns a Warm fact.** Expected: it searches all retained embeddings.
- **A fact still exists after deletion.** Built-in SQL invalidates derived summaries, but
  chat, audit, backups, and exported copies need separate retention. Rebuild invalidated
  summary trees if enabled.
- **Repeated or missing recall after compaction.** Only suppress alreadySurfacedIds while
  the corresponding evidence remains in context. Clear or rebuild the set after compaction.
- **Instructions in memory override policy.** Treat recalled text as reference data under
  current instructions. Markers, taxonomy, and scope strings are not authorization.
- **Consolidation was expected to enforce a quota.** It is opt-in and does not establish a
  hard retention deadline or storage limit.
