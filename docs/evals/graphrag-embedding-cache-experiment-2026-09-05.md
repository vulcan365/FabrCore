# Exact embedding cache experiment — 2026-09-05

Implemented opt-in embedding reuse. It eliminates most repeat embedding work while
preserving vectors, but **did not improve full GraphRAG ingestion wall time in this
trial**. Keep it as an optional provider-work optimization; the remaining LLM calls
are the larger latency target.

## Fixed setup and method

Existing localhost/graphrag database, the v2 RFC corpus (JSON, CSV, UUID prefix),
179 retrieval chunks, gpt-5.4-mini extraction with schema-enforced JSON and the
existing 1536-dimensional text-embedding-ada-002 configuration. Graph result caching
was enabled in both full-pipeline arms. Other extraction settings were fixed, including
concurrency, prompts and schema; evidence and endpoint repair were disabled.

Each arm runs sequentially in its own scope: cold original, repeat original, a
same-length tail mutation, repeat mutation. All ingestions force rebuilding. Mutation
replaces the last source character with whitespace as in the previous experiment.
Only one persisted retrieval chunk actually changes: trailing whitespace differences
in the other two sources disappear during chunk preparation. This is a synthetic
cache-invalidation test, not evidence about arbitrary edits or insertion-heavy documents.

A second pair of arms uses vector-only ingestion, with graph extraction disabled,
to isolate the vector ingestion path. Warmup and search-query embeddings are excluded
from provider telemetry. Timing is IngestDocumentAsync wall time, excluding report
queries, fingerprinting and retrieval checks. SDK-internal HTTP retries are not
individually counted; per-call embedding times can overlap.

## Full GraphRAG results

| Pass | Cache off seconds | Cache on seconds | Embedding calls off/on | Embedded inputs off/on | Cache hits | Required facts off/on |
| --- | ---: | ---: | --- | --- | ---: | --- |
| Cold original | 29.235 | 34.349 | 6 / 6 | 315 / 330 | 0 | 1/6 / 2/6 |
| Repeat original | 10.677 | 12.356 | 6 / 1 | 310 / 21 | 315 | 3/6 / 3/6 |
| Tail mutation | 22.819 | 25.361 | 6 / 4 | 304 / 65 | 252 | 2/6 / 2/6 |
| Repeat mutation | 10.723 | 10.698 | 6 / 1 | 305 / 14 | 300 | 3/6 / 4/6 |

Repeat means: **11.527 seconds with caching versus 10.700 without**. Do not claim an
overall ingestion speedup from this trial. LLM generation and its output variation
dominate; CSV still regenerates its combined graph/taxonomy response. Each arm makes
7, 3, 5 and 3 actual extraction calls respectively. Factual completeness remains
limited and is not improved by the embedding cache.

Within the cache-enabled repeat passes, 615 of 650 unique batch inputs were reused
(**94.6%**). Only 35 inputs reached the provider across those two passes. Measured
input characters were 3,001 versus 191,571 in the cache-disabled repeats, although
those arms generated different entity descriptions. These are input-volume measures,
not billed token counts or a dollar-cost estimate. Embedding SDK calls fell from six
to one per repeat pass; new CSV descriptions account for the remaining requests.

## Vector-only results

| Pass | Cache off seconds | Cache on seconds | Embedding calls off/on | Embedded inputs off/on | Cache hits |
| --- | ---: | ---: | --- | --- | ---: |
| Cold original | 1.432 | 1.481 | 3 / 3 | 182 / 182 | 0 |
| Repeat original | 1.485 | 0.171 | 3 / 0 | 182 / 0 | 182 |
| Tail mutation | 1.193 | 0.332 | 3 / 1 | 182 / 1 | 181 |
| Repeat mutation | 1.297 | 0.139 | 3 / 0 | 182 / 0 | 182 |

Repeat mean: **0.155 versus 1.391 seconds (88.9% faster)**. The 182 inputs comprise
179 chunks plus three document inputs. This isolates an approximately 1.24-second
saving in this workload; it is not a full GraphRAG speedup claim. There is no cold
reuse. SQL writes still execute on every forced rebuild.

These are sequential arms with two repeat observations each, not randomized statistical
trials. Provider work avoided and vector identity are stronger evidence than the
small-sample wall-time differences.

## Correctness and implementation

All 48 document ingestions completed and all 16 passes retained 179 embedded chunks,
Recall@3=100% and MRR@3=1.0. Full-pipeline selected negative checks passed. Vector-only
runs intentionally do not evaluate graph facts. Unchanged-chunk comparisons per
cache-enabled arm were 179, 178 and 179; no vector fingerprint changed. Fingerprints
hash the SQL vector text serialization, not raw SQL binary storage. The first full
cache arm recorded fingerprints before the automated comparison gate was added;
its comparisons were verified from raw JSON. The later vector-only arm ran the gate.
Entity-vector copy safety and input ordering are covered by unit tests; live database
fingerprints currently cover retrieval chunks only.

`CachedEmbeddings` wraps IEmbeddings. A shared EmbeddingResultCache holds up to 8,192
vectors with a 30-minute TTL and constant-time FIFO eviction. Vector payload at 1,536
dimensions is bounded to 48 MiB plus keys/metadata; dimensions up to 4,096 are supported.
Keys hash the exact text, explicit database/tenant/scope namespace, complete model
configuration identity and dimensions. Missing inputs retain their order and are
batched. Every output gets independent vector storage. Count mismatches, wrong
dimensions, NaN/infinity and zero vectors prevent the whole response from entering
cache. Cache storage contains hashes and vectors rather than source text.

There is no disk persistence or concurrent-request coalescing. A deployment changed
behind the same identity can remain cached until expiration; recreate the decorator
with a new revision/configuration identity when changing the model. Cancellation
semantics remain those of the existing IEmbeddings interface.

Default library registration is unchanged. The eval opts in with --embedding-cache on
and constructs a decorator for the actual run database/scope and resolved embedding
configuration. Production adoption needs an equivalent scope-aware factory, or an
explicit decorator on an ingestion service dedicated to that scope. Do not register
one scope's decorator on a singleton handling other scopes. The existing ingestion
EmbeddingBatchCount remains pipeline attempts; new EmbeddingCalls measures SDK calls
that actually reach the provider. Cache hits have separate counters.

Validation: **79 unit tests passed**, including exact reuse, ordering, duplicate and
cached-array mutation safety, scope/model/dimension/text isolation, whole-batch invalid
response rejection, TTL and eviction. Build and scoped whitespace checks passed.
No SQL schema changes were needed.

## Reproduction

```powershell
dotnet run --project src/FabrCore.Services.GraphRag.EvalConsole -- run --model graphrag --manifest src/FabrCore.Services.GraphRag.EvalConsole/corpus-relations-v2.json --response schema --result-cache on --embedding-cache on --reingest edit --iterations 4
# Repeat with --embedding-cache off for the full-pipeline control.
# For each vector-only arm, replace --model graphrag --response schema --result-cache on
# with --mode vector --result-cache off.
```

Reports under artifacts/graphrag-evals:

- 20260905T212455-422f27082cc34b6e9684437ac6fcbdc6 — full pipeline, cache on.
- 20260905T212647-dad7cd5108314a758690ec9a839bd26d — full pipeline, cache off.
- 20260905T212905-910b438308c6496d9eb8edbfe2d17862 — vector only, cache on.
- 20260905T212921-e0e23817c92d44fdbde8dcf9ed44978b — vector only, cache off.

Next latency experiment: exact reuse of the remaining combined extraction/classification
response, keyed to the complete taxonomy context. That call currently dominates repeated
CSV ingestion. Cold-ingestion performance and labeled fact coverage still need separate
work; neither cache experiment resolves those problems.
