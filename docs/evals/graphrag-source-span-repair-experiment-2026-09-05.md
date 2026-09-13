# GraphRAG source IDs and endpoint repair — September 5, 2026

## Decision

Keep the current production defaults: mini with reasoning disabled, the established
ingestion optimizations, and experimental extraction modes off. Source IDs solve
much of the quote-format problem, and a bounded repair can recover missing endpoint
entities. However, the final implementation has not demonstrated an ingestion
speedup or adequate factual coverage.

The final validation pass completed all three documents in **30.817 seconds**, with
one repair call, no invalid IDs, and **3/6** consistent required facts. The two
schema controls averaged **27.560 seconds**, scoring 4/6 and 2/6. This is a small,
variable sample, not a statistical latency comparison. Do not accept fewer graph
items or successful chunk retrieval as evidence of equal extraction quality.

## Implementation

- `UseExtractionSourceSpans` labels lossless extraction sections with a stable
  content-derived ID. Relationships return `sourceIds` instead of generated quotes.
  IDs survive batching/retry changes; identical section text shares an ID.
- The final source schema (`spans_v2`) enumerates only IDs supplied in that batch.
  The server additionally rejects empty/invalid lists. IDs identify sections,
  generally up to 2,000 characters here, not precise supporting sentences.
- `RepairExtractionEndpoints` permits one call per document after merging all
  batches. The repair receives only missing names, affected proposed relationships,
  and referenced sections. It may add entity features but cannot rewrite edges or
  existing entities. More than 20 missing names or an over-budget prompt fails
  without a repair call.
- Repair schema v2 requires `entities` and `unresolvedNames`. Unsupported names
  must remain unresolved; any unresolved name prevents completion. Unrequested
  entities are rejected. Missing endpoints and selected asymmetric direction
  conflicts are checked again after repair. There is no repeated repair loop.
- Repair calls/tokens/time are included in ingestion telemetry. They are distinct
  from existing malformed-output split retries. Eval reports retain provider
  schema names and span locations as UTF-16 offsets in the hashed corpus text.

Both features default to false and require the document extraction plan and schema
responses. Generated-quote evidence and source-ID evidence are mutually exclusive.
Source IDs/evidence are retained in eval snapshots, not new SQL graph columns.
No SQL VECTOR change or schema migration was needed.

## Fixed settings and trial sequence

All trials used the dedicated `graphrag` alias / `gpt-5.4-mini`, reasoning `none`,
strict JSON, current relation conventions, no description target, unchanged
embedding/concurrency settings, and fresh scopes in `localhost/graphrag`.
The original corpus remained 71,291 characters and 179 chunks. Initial taxonomy
row count was seven in the initial matrix. Shared taxonomy and provider caches
were not frozen; no competing live model tests ran. Setup and warmup were excluded.

Initial order: control → IDs → IDs+repair → IDs+repair → IDs → control. GZIP
(25,037 characters / 62 chunks) was then tested control → IDs+repair. Review exposed
two defects, described below. Two repair-v2 passes and one final enum-constrained
source-v2 pass followed. Keep these versions separate when comparing results.

## Initial matrix: source v1 / repair v1

| Variant/order | Completed documents | Attempt seconds | Calls | Repair calls | Output tokens | Consistent required facts |
| --- | ---: | ---: | ---: | ---: | ---: | ---: |
| Control 1 | 3/3 | 28.962 | 7 | 0 | 9,174 | 4/6 |
| Source IDs 1 | 2/3 | 24.697 | 7 | 0 | 8,385 | 1/6 |
| IDs + repair 1 | 3/3 | 24.395 | 8 | 1 | 8,509 | 3/6 |
| IDs + repair 2 | 3/3* | 29.285 | 8 | 1 | 9,472 | 2/6 |
| Source IDs 2 | 2/3 | 25.942 | 7 | 0 | 9,411 | 2/6 |
| Control 2 | 3/3 | 26.157 | 7 | 0 | 7,975 | 2/6 |
| GZIP control | 1/1 | 8.558 | 3 | 0 | 2,837 | 2/2 |
| GZIP IDs + repair | 1/1 | 6.770 | 4 | 1 | 2,117 | 2/2 |

Failed documents count as zero matches against the six corpus requirements here;
their absent graph snapshots are unscored in the raw report's per-document checklist.
Attempt time for a failed pass is not a successful ingestion time.

The ID-only passes both failed CSV because endpoints were unresolved. All source
references in this initial matrix passed ID validation. The first repair pass
added CSV's missing entity and completed without discarding its relationships.

*The second repair pass was a **semantic repair failure despite Completed status**:
the model supplied UTC and DBMS entities whose descriptions explicitly said they
were unsupported by the supplied source text. The v1 structural check accepted
the names. That implementation must not be promoted based on completion rate.
The original raw report is retained; no performance data or labels were rewritten.

GZIP's single faster repair pass preserved two checked facts but reduced graph
entities from 42 to 22 and edges from 34 to 20. That limited checklist cannot
establish safe preservation of all other facts.

## Hardening and final validation

Repair schema v2 makes unsupported names explicit in `unresolvedNames`, and the
service rejects a nonempty list. Tests cover that rejection. This is still model-
reported support, not a semantic proof; the model can incorrectly claim support.

| Source / repair version | Completed | Attempt seconds | Calls | Repairs | Invalid IDs | Consistent facts |
| --- | ---: | ---: | ---: | ---: | ---: | ---: |
| v1 / v2, pass 1 | 3/3 | 29.266 | 8 | 1 | 0 | 1/6 |
| v1 / v2, pass 2 | 2/3 | 25.643 | 8 | 1 | 1 | 1/6 |
| v2 / v2, final | 3/3 | 30.817 | 8 | 1 | 0 | 3/6 |

The second repair-v2 pass exposed a mistyped source ID in CSV. Source schema v2 now
uses an enum of actual batch IDs; an Azure transport test confirms this enum reaches
the request. The final live pass used that constraint and repaired CSV successfully.
It used 30,103 input tokens (4,608 cached) and 9,278 output tokens. SQL chat totals
matched observed calls and input usage, including repair.

All 179 chunks were embedded in the final run, and Recall@3=100% / MRR@3=1.000.
The selected negative check passed. Required JSON-to-UTF-8, ECMA-404 publisher,
and UUID-to-URN representations still did not pass the fact checklist. Existing
alternative-representation diagnostics remain available for manual inspection.
The final implementation received one live validation pass, not a repeated speed
benchmark; the earlier matrix used different schema versions.

## What this establishes

IDs are a better mechanical provenance representation than asking the model to
reproduce quotes. Constraining ID values in the API schema prevents the typo class
observed here. One bounded repair can recover missing entity features while keeping
the original relationships intact. Neither mechanism makes an unsupported or
misdirected relationship true, discovers omitted facts, or establishes that a
referenced section actually entails the relationship.

The next performance experiment should avoid reducing extraction coverage: cache
exact graph-extraction inputs/results with explicit model/deployment, prompt/schema,
source, instructions, and relevant scope/context identity. Measure repeated and
partially changed documents separately from first-time ingestion. Continue the
factual checklist so response reuse is not mistaken for improved graph correctness.

## Validation and reproduction

All **72 unit tests pass**, including source-ID validity, actual Azure enum/schema
serialization, one repair after merging multiple batches, refusal of unrequested
or unresolved entities, and prevention of repeated repairs. The initial eight-pass
matrix, two repair-v2 passes, and final source-v2 pass all finished as evaluations.
Some intentionally reported ingestion failure as documented above.

```powershell
# Schema control:
dotnet run --project src/FabrCore.Services.GraphRag.EvalConsole -- run --response schema --evidence off --repair off --manifest src/FabrCore.Services.GraphRag.EvalConsole/corpus-relations-v2.json
# Current source-v2 / repair-v2 experiment:
dotnet run --project src/FabrCore.Services.GraphRag.EvalConsole -- run --response schema --evidence spans --repair once --manifest src/FabrCore.Services.GraphRag.EvalConsole/corpus-relations-v2.json
```

Reports under `artifacts/graphrag-evals/`:

- Initial matrix, in order: `20260905T201249-851422ce43d94d5eacc316f9ffc81433`,
  `20260905T201321-d5dbb52894f6450ab2d3b33fdcab1841`,
  `20260905T201348-cca29afbf728437794f0eff51a023923`,
  `20260905T201414-8a2b650b47194da59d2330ec0676da2a`,
  `20260905T201446-78458100ff5b47128c404624c1100dbe`,
  `20260905T201514-80b8413f5fba48579c5db6275df186ad`.
- GZIP pair: `20260905T201543-009a3f5537e146728a116a96a65bd51c`,
  `20260905T201553-8883d501707142b0a52f37d9948879c3`.
- Repair-v2 pair: `20260905T201821-9d920e12562a41f8931281de629ede91`.
- Final source-v2 / repair-v2:
  `20260905T202120-5f9b7c7937dd423b8ee3255501f2b677`.
