# Cold-ingestion extraction batch size - 2026-09-05

Keep the default at **8 sections per batch**. Four sections were slower and emitted
more tokens without better required-fact coverage. Sixteen sections used fewer calls
and output tokens, but the small apparent latency advantage was within observed run
variation and only 3 of 6 required facts were retained. No production default changed.

## Method

Existing localhost/graphrag database, gpt-5.4-mini via graphrag, schema-enforced JSON,
current extraction instructions, 2,000-character extraction sections, concurrency four,
and the same v2 RFC corpus: JSON, CSV and the UUID prefix, 71,291 source characters
and 179 retrieval chunks. The existing 1536-dimensional embedding model is unchanged.
All application result, taxonomy and embedding caches were off. Each run used a fresh
scope with no previous document to skip. Existing taxonomy row count was seven throughout.

Limits were tested in order **8, 4, 16, 16, 4, 8**, with one corpus pass per run.
This counterbalances simple run-order effects but is only two observations per setting.
Provider-side prompt caching remained active and is recorded below; cold means new
application ingestion, not an empty provider prompt cache. No forced deployment reset
was performed. Timing excludes warmup, report reads and retrieval validation.

The existing routing rule combines graph extraction and classification if a document
fits one batch; otherwise it classifies separately. Thus 4 sections yields 10 graph
calls plus 3 classifiers; 8 yields 4 graph calls, 2 classifiers and 1 combined call;
16 yields 3 combined calls. This tests the real pipeline behavior under a changed
batch limit, not batch size with response routing independently fixed.

## Mean results (two runs each)

| Sections | Ingestion seconds | LLM calls | Input tokens | Output tokens | Entities | Persisted edges | Required facts by run |
| --- | ---: | ---: | ---: | ---: | ---: | ---: | --- |
| 4 | 25.380 | 13 | 29,857 | 13,522.5 | 189 | 166.5 | 2/6, 2/6 |
| 8 (current) | 23.621 | 7 | 25,784 | 8,481 | 115.5 | 113.5 | 3/6, 2/6 |
| 16 | 22.991 | 3 | 21,138 | 4,928.5 | 78.5 | 61 | 3/6, 3/6 |

Four sections were 7.4% slower and generated 59.4% more output tokens than eight.
Its higher entity/edge count did not translate to better labeled factual coverage.
Sixteen sections averaged 0.630 seconds (2.7%) faster, versus a 5.778-second spread
between the two baseline runs. That is insufficient evidence of a reliable latency
improvement. It emitted 41.9% fewer output tokens and 46.3% fewer persisted edges.
Those are measurable volume changes, but do not establish that the lost graph content
is expendable. Three consistent required facts out of six fails the acceptance gate.

Both sixteen-section runs retained JSON/UTF-8 and RFC4180/RFC2234. One retained
IANA/text-csv; the other retained UUID/GUID equivalence. Neither retained the required
ECMA-404 publisher or UUID/URN edge. The same required fact count therefore does not
mean the same facts survived. None of the six runs recovered all required facts.

## Individual runs

| Order | Sections | Seconds | Output tokens | Provider cached input tokens | Required facts |
| --- | ---: | ---: | ---: | ---: | --- |
| 1 | 8 | 20.732 | 7,718 | 21,504 | 3/6 |
| 2 | 4 | 25.758 | 12,646 | 4,608 | 2/6 |
| 3 | 16 | 22.169 | 4,781 | 3,840 | 3/6 |
| 4 | 16 | 23.813 | 5,076 | 20,224 | 3/6 |
| 5 | 4 | 25.002 | 14,399 | 9,984 | 2/6 |
| 6 | 8 | 26.510 | 9,244 | 23,808 | 2/6 |

All 18 document ingestions completed. Every run stored all 179 embedded chunks, with
Recall@3=100%, MRR@3=1.0 and the selected negative factual check passing. All application
cache hit counters were zero. No extraction retries occurred. SQL chat call counts
matched measured SDK calls. These smoke checks pass even when required facts are
missing; they do not establish complete or precise graph extraction.

## Code and reproduction

Added `--sections-per-batch N` to the eval console and stored its effective value in
JSON/Markdown reports. It accepts 1-256 for `run --mode document` and rejects invalid
values or incompatible modes. Default behavior uses the configured limit, normally 8.
The input-token budget can still split a batch before this limit. No ingestion-library
algorithm or production default was changed in this experiment.

```powershell
dotnet run --project src/FabrCore.Services.GraphRag.EvalConsole -- run --model graphrag --manifest src/FabrCore.Services.GraphRag.EvalConsole/corpus-relations-v2.json --response schema --result-cache off --taxonomy-cache off --embedding-cache off --reingest fresh --sections-per-batch 8
# Repeat with limits 4, 16, 16, 4, 8.
```

Build passed with no warnings; **82 unit tests passed**; scoped whitespace checks passed.
No database schema changes were needed. Application cache switches do not disable
provider prompt caching.

Raw reports under artifacts/graphrag-evals:

- 20260906T004917-4eda22ac8e7247a9b8c288830558f1d5 - 8 sections.
- 20260906T004941-0619be8ce13a486cbb4f14bea8c1574a - 4 sections.
- 20260906T005009-91455b298a5b4d7185898bbaaa4a92e5 - 16 sections.
- 20260906T005034-3cb053b6eb794fbfbde4b36df883ae74 - 16 sections.
- 20260906T005100-7bdf4de081864df88e60fed86e941e34 - 4 sections.
- 20260906T005128-de03b9d82f034fb2a21a8fd5574f23f5 - 8 sections.

A machine-readable comparison is retained at
`artifacts/graphrag-evals/batch-size-experiment-2026-09-05.json`.
UTC run IDs are September 6; the local experiment date is September 5.

The next priority is to diagnose missing factual relationships against the source and
captured extraction responses. We need to distinguish omitted entities, alternate or
reversed predicates, and relationships lost during persistence before making further
speed changes. The current retrieval smoke tests alone would accept all these variants
and miss the factual coverage problem.
