# Hybrid candidate allocation

This experiment tests one bounded allocation against both retained failures: ordinary
semantic retrieval misses a procedure among dense facts, while diverse 8+8 retrieval loses
three facts when a question needs five memories of the same type.

`Retrieval.HybridSemanticCandidates` is opt-in and requires `UseSemanticCandidates`.
It reserves `min(limit, limit / 2 + 1)` globally closest entities, then fills remaining slots
using the existing per-type rank and similarity order. With eight semantic slots, five
are reserved for the closest matches and three are filled across types. Eight recent
headers are added in the experiment as before. All entities are deduplicated and the total
header cap remains enforced.

SQL performs this allocation in one candidate query using the existing embedding. Scope,
cold-memory and already-surfaced filters apply before ranking, and chunk aggregation prevents
one entity consuming multiple slots. No new model call, embedding call, taxonomy or schema
is introduced. Custom stores may implement the optional `FindHybridCandidateHeadersAsync`;
its default implementation rejects unsupported use. Enabling both hybrid and diversity
strategies fails explicitly before generating an embedding.

## Comparison design

Correction report:
`artifacts/memory-correction/20260907T161014-eb243a8a8d2d4504bf99ae6f55766fc6/report.json`.

| Correction fixture, three repetitions | Ordinary 20+20 | Ordinary 8+8 | Diverse 8+8 | Hybrid 8+8 |
| --- | ---: | ---: | ---: | ---: |
| Exact checks | 21/24 | 21/24 | 24/24 | **24/24** |
| Chat calls | 24 | 24 | 24 | 24 |
| Gross input tokens | 50,331 | 24,933 | 24,756 | 24,534 |
| Output tokens | 444 | 432 | 438 | 438 |
| Cached input tokens | 28,672 | 0 | 0 | 0 |

Hybrid recovered the procedure on all three combined requests. Gross input is 51.3% lower
than ordinary 20+20 and 1.6% lower than ordinary 8+8. This is a selection-token result,
not a priced or end-to-end saving; the larger control was heavily cached.

Same-type report:
`artifacts/memory-same-type/20260907T161224-d49beb22c5394687b2f2f123427d73a2/report.json`.

| Same-type fixture, three repetitions | Ordinary 20+20 | Ordinary 8+8 | Diverse 8+8 | Hybrid 8+8 |
| --- | ---: | ---: | ---: | ---: |
| Exact checks | 21/21 | 21/21 | 18/21 | **21/21** |
| Chat calls | 21 | 21 | 21 | 21 |
| Gross input tokens | 47,937 | 22,608 | 23,739 | 22,626 |
| Output tokens | 396 | 396 | 378 | 396 |
| Cached input tokens | 25,088 | 0 | 0 | 0 |

Hybrid preserves all five facts on every combined request while the diverse control again
loses three. Across both fixtures it passes **45/45**, versus 42/45 for each of the other
three configurations. Gross selection input totals 47,160 versus 98,268 for ordinary 20+20
(52.0% lower). It is nearly unchanged against ordinary 8+8 (47,541) while recovering the
missing procedure. Every variant uses 45 query embeddings and 45 selection calls, excluding
separately recorded failure-diagnostic embeddings. The report fixtures retain the labels,
entity IDs, provenance and any correction mutations.

Validation: all **112 Memory tests passed**. New tests cover preserving five close facts
plus three other types inside eight slots, scope-local exclusion/cold filtering, routing
the hybrid service path with one embedding, and rejecting conflicting strategy settings.

The contemporary two-iteration harness comparison passed **34/34 in both variants**, with
no comparison regressions. Both use semantic 8+8; only the candidate enables hybrid.
Total chat calls were 30 versus 29, gross input 32,242 versus 31,096, output 624 versus 478,
and cached input 18,432 versus 17,280. This is a 3.6% observed input reduction in one small
paired run, not an established repeatable end-to-end saving. Reports:

- `artifacts/memory-hybrid-harness-false/20260907T161449-fbca37055bb0451fafe358e02f9422fb/report.json`
- `artifacts/memory-hybrid-harness-true/20260907T161531-ba12d26ea5934e46b69180d033428239/report.json`

Both commands below compare ordinary 20+20, ordinary 8+8, diverse 8+8 and hybrid 8+8 on the
same stored fixture with rotating variant order over three repetitions. Compact IDs and
minimal selection are enabled for all variants; verification and graph expansion are off.
The quality gate targets the hybrid candidate and retains all control failures. The report
versions are `correction-v4` and `same-type-v2` with explicit variant settings and binary hash.

```powershell
dotnet run --project src/FabrCore.Services.Memory.EvalConsole -- corrections --iterations 3 --hybrid-candidates true
dotnet run --project src/FabrCore.Services.Memory.EvalConsole -- same-type --iterations 3 --hybrid-candidates true
dotnet run --project src/FabrCore.Services.Memory.EvalConsole -- run --mode harness --iterations 2 --compact-ids true --minimal-selection true --semantic-candidates true --candidate-budget 8 --hybrid-candidates true
```

No baseline registration or production default is promoted by these commands. These are
authored synthetic regression fixtures used to develop this strategy, not independent
validation. Five reserved slots do not guarantee coverage of larger same-type requests;
the remaining slots do not guarantee coverage across all types. SQL ranking costs and
provider caching must be measured before claiming end-to-end cost or latency savings.
