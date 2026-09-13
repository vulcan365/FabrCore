# Policy corpus: bounded document concurrency, 2026-09-05

Two concurrent documents reduced mean corpus ingestion wall time from 25.523s to
16.938s (33.6% lower, 1.51x throughput). Three averaged 16.254s (36.3% lower),
only 0.684s better than two. Use two as the next controlled workload setting;
keep the eval default at one and the shared LLM limit at four. This experiment
changes the console runner, not production ingestion defaults.

## Method

Six fresh-scope passes in order 1, 2, 3, 3, 2, 1. Existing localhost/graphrag database;
no database creation or global cleanup. Same full policy-v1 corpus (59,152 characters,
163 retrieval chunks), graphrag mini alias, schema response, extraction instructions,
eight sections per batch, endpoint aliases off, result/taxonomy/embedding caches off.
One ingestion service per pass shares its existing chat and embedding semaphores.
Documents share a scope; graph verification occurs after all writers settle.
The nine positive labels remain outside the extraction prompts.

IngestionWallMs measures elapsed time for the whole ingestion batch, excluding
provider warmup, download, schema setup, graph checks and retrieval. Individual
document times include waiting for shared capacity and must not be summed to
report parallel throughput. Observed peak chat concurrency was four in all six
passes. Each pass made seven chat calls (five memo, one governance, one CISA);
per-document measured call counts agreed with persisted ingestion metrics.

| Concurrent documents | Wall seconds | Required facts | Persisted edge contributions | Output tokens | Cached input tokens |
| ---: | ---: | ---: | ---: | ---: | ---: |
| 1 | 26.057 | 6/9 | 132 | 10811 | 0 |
| 2 | 16.943 | 4/9 | 129 | 11605 | 13568 |
| 3 | 15.922 | 5/9 | 125 | 10883 | 11520 |
| 3 | 16.586 | 4/9 | 115 | 11135 | 15872 |
| 2 | 16.932 | 6/9 | 127 | 10766 | 13824 |
| 1 | 24.989 | 5/9 | 98 | 9816 | 17152 |

Every pass completed all documents, retained all 163 embedded chunks, and returned
the correct document at rank one for all three queries. No SQL or provider failures
were reported. Edge totals sum document contributions, not distinct scope edges.

## Interpretation and limits

The improvement comes from overlapping documents while keeping a fixed peak request
limit. It does not reduce LLM calls or demonstrate lower token cost. The long memo's
individual latency increased under contention (about 11s sequential versus 14-17s
concurrent); corpus completion improved because short documents no longer wait for it.
Provider prompt caching was not disabled: the reversed order gives a warm sequential
control (24.989s) and still shows a substantial gap from the concurrent passes.
Shared taxonomy had nine rows at the start of every pass, but scheduling may change
which taxonomy context each document sees. This is an end-to-end throughput test,
not an identical-prompt replay or an isolated model-quality comparison.

Facts were 6/9 and 5/9 sequential, 4/9 and 6/9 at two, and 5/9 and 4/9 at three.
These small samples cannot establish quality equivalence or attribute differences
to scheduling. Retrieval smoke success does not establish graph correctness. None
passed every required fact, so this is a throughput result, not quality acceptance.
Existing taxonomy naming and typed-edge fidelity problems remain unresolved.
Two observations per level on three documents are not a production load test; they
do not establish p95 latency, sustained rate-limit behavior or absence of SQL races.

## Code and validation

- Added --document-concurrency (1-16; default 1) with shared service limits.
- Added async-flow-owned chat/embedding samples, observed peak chat concurrency,
  and explicit batch wall timing. Historical reports retain summed-time fallback.
- Parallel workers return independent results; checkpoint writes and graph checks
  remain serialized. Finished results are checkpointed on cancellation/failure.
- Concurrent runs reject enabled result/embedding caches because current global
  cache counters cannot correctly attribute overlapping document hits.
- 90 unit tests passed, including overlapping chat attribution, embedding attribution,
  scope restoration, cancellation telemetry and invalid concurrency/cache options.
- Six live passes completed with all smoke gates passing. Final checkpoint-on-failure
  handling was added after the live matrix and compiled/unit-checked; the live matrix
  did not exercise cancellation or failure paths.

Reproduce each matrix entry by varying N over 1,2,3,3,2,1:

```powershell
dotnet run --project src/FabrCore.Services.GraphRag.EvalConsole -- run --model graphrag --manifest src/FabrCore.Services.GraphRag.EvalConsole/corpus-policy-v1.json --max-chars 0 --response schema --sections-per-batch 8 --document-concurrency N --endpoint-aliases off --result-cache off --taxonomy-cache off --embedding-cache off --reingest fresh --iterations 1
```

Next quality experiment: separate taxonomy names from domain metadata in the response
schema and lookup, preventing literal category names such as `Name (in Engineering)`.
Keep the concurrency setting, model and fact labels fixed when measuring that change.

## Artifacts

Summary: artifacts/graphrag-evals/document-concurrency-experiment-2026-09-05.json.
Full report.json and report.md under artifacts/graphrag-evals for each run:

- `20260906T014750-a38a6a7e391942548d3bb98087125950` (concurrency 1)
- `20260906T014818-6f2ce99bb79c41dcbb99c2f45bb17414` (concurrency 2)
- `20260906T014838-b03c4d7d2c4847b5b7457643de723c27` (concurrency 3)
- `20260906T014857-b0a36a93606b4f2392c151a0bc10ef3e` (concurrency 3)
- `20260906T014916-1ea21ed6fa9248c5b9635e21da32a227` (concurrency 2)
- `20260906T014935-3e555dca5b254bab85c3bd3d441cf8b0` (concurrency 1)
