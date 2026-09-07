# Exact graph extraction caching — 2026-09-05

Implemented an opt-in, bounded in-memory cache of validated graph-only extraction
responses. On this corpus, repeat rebuilds averaged **12.431 seconds with caching
versus 27.730 seconds without it (55.2% faster)**. Actual LLM calls fell from seven
to three. A one-character edit reused two batches and reduced time by 23.0% against
the corresponding control. This does not accelerate cold ingestion or solve missing facts.

## Method

Used the existing localhost/graphrag evaluation database, gpt-5.4-mini through the
`graphrag` alias, schema-enforced JSON, current extraction instructions, concurrency
four, unchanged description settings, and no evidence/endpoint-repair experiments.
The v2 labeled corpus contains RFC 8259 (28,360 characters), RFC 4180 (12,931), and
the first 30,000 characters of RFC 9562: 179 retrieval chunks total. Taxonomy rows
at the beginning of every pass: seven. Schema use is held fixed for this trial;
this experiment does not establish schema mode as the production default.

Each arm uses its own isolated scope. Within an arm, all four passes use the same
document IDs with ForceReingestion enabled. Passes are: cold original content,
repeat original, one-character tail mutation, repeat mutated content. Mutation
replaces the last character with a space (or newline if already a space); it preserves
length and batch count. This is a synthetic invalidation test, not a realistic-edit
or semantic-equivalence benchmark. Per-document hashes record the actual inputs.
Both arms use exactly the same mutation. All embeddings and SQL writes still run.

## Results

| Pass | Cache off seconds | Cache on seconds | Actual calls off/on | Cache hits | Output tokens off/on | Consistent labeled facts off/on |
| --- | ---: | ---: | --- | ---: | --- | --- |
| Cold original | 25.343 | 29.491 | 7 / 7 | 0 | 8297 / 9001 | 1/6 / 2/6 |
| Repeat original | 25.097 | 13.330 | 7 / 3 | 4 | 7408 / 1535 | 2/6 / 3/6 |
| Tail mutation | 31.207 | 24.031 | 7 / 5 | 2 | 8954 / 4416 | 1/6 / 2/6 |
| Repeat mutation | 30.363 | 11.531 | 7 / 3 | 4 | 8392 / 1360 | 2/6 / 2/6 |

All 24 document ingestions completed. Every pass retained all 179 embedded chunks,
Recall@3 was 100%, MRR@3 was 1.0, and all selected negative checks passed. SQL
ChatCallCount matched measured actual calls for every document, including hits.
Cached responses do not add fabricated provider usage or provider prompt-cache tokens.

The JSON and UUID documents have separate taxonomy and graph calls. Their persisted
entity-name sets and complete edge snapshots (endpoints, type, description) were
identical between each cold/modified pass and its cached repeat. Cached JSON is
reparsed, retaining entity types/descriptions/confidence as well as names/edges.
The eval snapshot comparison does not independently compare every entity feature or
embedding value. CSV uses one combined graph/taxonomy call and remains uncached;
its extraction varies on every pass. Taxonomy also remains live for the other two.

These are two sequential arms with one cold and two repeat observations each, not
randomized repeated trials. Cold times differ from ordinary model latency and output
variation; the cache does not explain cold-run speed. Required-fact coverage remains
poor (1–3 of 6), so these timings are not evidence that extraction quality is solved.
The reliable result is reuse of exact graph batches with fewer provider requests.

## Code and operational behavior

- `GraphRag:Ingestion:UseExtractionResultCache` defaults to false.
- `ExtractionResultCache` defaults to 256 entries, 30-minute TTL, and at most 262,144
  characters per response. Optional singleton registration shares it across ingestion
  instances; otherwise the ingestion singleton owns its cache.
- Keys hash database connection identity, document ID, resolved model configuration,
  exact graph prompt, schema/evidence/description/token settings and guidance. The
  document ID is owned by a scope, preventing reuse between distinct scoped documents.
- Only graph-only batches using the in-process chat client are eligible. Mutable
  taxonomy, combined extraction, remote Host API calls and failed/invalid responses
  are excluded. Every hit is parsed and validated again, avoiding shared mutable results.
- Changed content only reuses batches whose entire prompt remains identical. Changes
  to section layout, section count, source context or instructions can invalidate more
  than one batch. Cross-document deduplication is intentionally absent.
- No persistence or concurrent-request coalescing. A process restart clears cached
  results. Changing a deployment behind identical model configuration can retain old
  results until TTL expiration. Disable the flag to force new model output.
- ForceReingestion bypasses the unchanged-document shortcut but permits this separate
  opt-in cache. Normal unchanged ingestion still skips all work before reaching it.

Validation: 75 unit tests passed, including exact reuse, actual-call accounting,
partial-content invalidation, shared-cache service reuse, document/model/schema
isolation, malformed-response exclusion, TTL, eviction, size bounds and key boundaries.
Scoped whitespace checks passed. No database schema changes were required.

## Reproduction and artifacts

```powershell
dotnet run --project src/FabrCore.Services.GraphRag.EvalConsole -- run --model graphrag --manifest src/FabrCore.Services.GraphRag.EvalConsole/corpus-relations-v2.json --response schema --result-cache on --reingest edit --iterations 4
# Repeat with --result-cache off for the control.
```

Raw JSON and Markdown reports remain in:

- `artifacts/graphrag-evals/20260905T203132-be2b5b4ab8e64e839eb9bdb4d5ee811f` — cache enabled.
- `artifacts/graphrag-evals/20260905T203332-245b19093e0141d2af5a35036a0d5e6e` — cache disabled.

Keep mini and enable this cache selectively for repeated/partially changed documents.
Next useful performance experiment: exact embedding reuse for unchanged chunks and
entity descriptions, with model/dimension isolation and retained vector-search checks.
For quality, continue labeled factual-edge work independently; caching faithfully
retains both correct extraction and omissions.
