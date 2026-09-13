# Taxonomy-context extraction cache experiment - 2026-09-05

Implemented and tested opt-in caching of combined graph/classification and standalone
classification responses. With graph-result and embedding caches already enabled,
forced repeat ingestion averaged **0.542 seconds versus 8.457 seconds (93.6% faster)**.
Repeated passes made zero LLM and zero embedding calls. Persisted graph snapshots,
taxonomy assignments, chunk-vector fingerprints and factual checks matched exactly.
This is a repeat-rebuild improvement, not a cold-ingestion or extraction-quality gain.

## Method

Same existing localhost/graphrag database, gpt-5.4-mini via graphrag, schema-enforced
JSON, current extraction instructions, concurrency four, and unchanged description
settings. Evidence and endpoint repair remain disabled. Embeddings use the existing
1536-dimensional text-embedding-ada-002 configuration. The v2 JSON/CSV/UUID RFC corpus
has 71,291 characters and 179 retrieval chunks. Initial taxonomy rows were seven in
every pass. No test directly mutated shared database taxonomy rows.

Both arms use graph-result and embedding caching. Only taxonomy/combined-response
caching differs. Each arm uses its own scope and four forced rebuilds within it:
cold original, repeat original, tail mutation, repeat mutation. The synthetic mutation
replaces the last source character with whitespace, preserving length; it is not a
realistic edit-distribution benchmark. Only one persisted retrieval chunk changes
because chunk preparation removes trailing whitespace in the other two documents.

## Results

| Pass | Taxonomy cache off seconds | On seconds | LLM calls off/on | Embedding calls off/on | Extraction cache hits off/on | Required facts off/on |
| --- | ---: | ---: | --- | --- | --- | --- |
| Cold original | 27.713 | 26.348 | 7 / 7 | 6 / 6 | 0 / 0 | 1/6 / 2/6 |
| Repeat original | 8.420 | 0.558 | 3 / 0 | 1 / 0 | 4 / 7 | 2/6 / 2/6 |
| Tail mutation | 22.753 | 25.375 | 5 / 5 | 4 / 4 | 2 / 2 | 3/6 / 4/6 |
| Repeat mutation | 8.494 | 0.526 | 3 / 0 | 1 / 0 | 4 / 7 | 2/6 / 4/6 |

Repeat means are 8.457 and 0.542 seconds. The cold and mutation differences reflect
LLM latency/output variation; they do not demonstrate a speedup. The mutation arm
still needs fresh classification/combined responses and changed graph batches.
Repeating the modified input then reuses all seven extraction responses.

All 24 ingestions completed. Every pass retained all 179 embedded retrieval chunks,
Recall@3=100%, MRR@3=1.0, and the selected negative factual checks passed. SQL
ChatCallCount matched actual measured calls on every document. Cache hits contributed
no fabricated chat input/output usage. Cached-repeat graph comparisons include entity
names, complete persisted edge snapshots (endpoints/type/description), domains and
categories. Chunk-vector text-serialization fingerprints and all factual check objects
matched between passes 1/2 and 3/4 for every document. Snapshots do not independently
compare every entity feature or raw SQL vector binary representation.

The cache preserves extraction omissions as well as correct facts. Required fact
coverage remains 2/6 in the cached original and 4/6 in the cached mutation. There is
no basis here to claim the overall extraction-quality problem is solved. These are
two sequential arms with two repeat observations each, not randomized statistical
trials; the reduction in provider calls is stronger evidence than precise percentages.

## Code and invalidation

`GraphRag:Ingestion:CacheTaxonomyResponses` defaults to false and requires the existing
result cache to be enabled. `--taxonomy-cache on` enables it in the console and
requires `--result-cache on`. Both modes retain the bounded, process-local cache:
256 responses, 30-minute TTL, at most 262,144 characters per response. No schema
migration or persistent cache was introduced. Default application flags are unchanged.

A shared key builder separates graph, combined and taxonomy response kinds. Keys hash
the database connection identity, scoped document ID, resolved model configuration,
exact prompt, applicable output/schema settings and guidance. Combined keys additionally
include every supplied domain/category name, description and category-domain mapping.
Classifier keys include this context plus all source sections and source metadata,
so even a change outside sampled classification evidence forces a fresh call. Available
local chat client resolution is required; remote fallback responses are excluded.

Only successfully parsed combined responses with both classification objects are stored.
Classifier responses require both objects and an acceptable finish reason; malformed,
filtered and length-limited responses are excluded. Hits are parsed again. Classification
responses cannot introduce graph facts. Normal taxonomy persistence and confidence/reuse
rules still run after reuse. A changed taxonomy may require a fresh response until the
context stabilizes. If an exact earlier context returns, its unexpired result is eligible.

The cache remains process-local, without cold-request coalescing. Changes to a deployment
behind identical configuration require a new identity/restart or expiration. ForceReingestion
bypasses the document-unchanged shortcut but permits these opt-in caches; disable them to
force new model/vector generation. Normal unchanged-document skipping already avoids
more work than the forced-rebuild scenario tested here.

Validation: **82 unit tests passed**. New checks cover combined and standalone classifier
reuse; zero usage on hits; domain addition/description changes; category description and
category-domain mapping changes; instruction changes; content changes; and incomplete
combined classification exclusion. A separate test edits source text while proving the
classifier's sampled prompt evidence is unchanged, then verifies a fresh classifier
call. Model/document/schema isolation and invalid-response behavior remain covered by
the prior tests. Scoped whitespace checks passed.

## Reproduction and artifacts

```powershell
dotnet run --project src/FabrCore.Services.GraphRag.EvalConsole -- run --model graphrag --manifest src/FabrCore.Services.GraphRag.EvalConsole/corpus-relations-v2.json --response schema --result-cache on --taxonomy-cache on --embedding-cache on --reingest edit --iterations 4
# Repeat with --taxonomy-cache off for the control.
```

Raw JSON and Markdown reports under artifacts/graphrag-evals:

- 20260906T004219-5a8bde1c8f984d3abd6d7b315a87ae61 - taxonomy cache on.
- 20260906T004357-c2e17a95ec934d5a8810cd8c99b71554 - taxonomy cache off.

The UTC run IDs cross midnight; the local experiment date is September 5.

Use this option for repeated/partially changed ingestion with stable taxonomy. The next
performance experiment should return to cold ingestion: compare extraction batch sizes
with mini, keeping typed factual-edge checks as an acceptance gate. More repeat-cache
work will not address the approximately 26-second cold ingestion time or missing facts.
