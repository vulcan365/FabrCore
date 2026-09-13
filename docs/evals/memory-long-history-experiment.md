# Bounded recall beyond the recent-header cap

The September 7 experiment found a concrete coverage failure: with 1,200 stored memories,
the newest-200 header scan cannot retrieve facts outside that window. An opt-in semantic
candidate pool recovered every labeled answer in three repetitions while sending fewer
headers to the selection model. Defaults and existing baseline registrations remain unchanged.

## Experiment

Completed report:
`artifacts/memory-long-history/20260907T152431-f1983c18ef514e94a3bf1c19b84e903b/report.json`.
The adjacent `fixture.json` contains the facts, SQL entity IDs, provenance identifiers and
expected question sets. The report retains the exact recent-200 inventory, model, binary
hash, raw model responses, cache counts and embedding measurements.

The fixture contains five target memories and 1,195 later distractors, including other
projects' deployment records. Before scoring, every primary chunk is read back and all
five targets are checked to be outside the recent-200 inventory. Eight questions cover
current and historical region, procedure, combined requests, date comparison, preference,
resume checkpoint and unknown information. Scoring requires the exact entity set, unchanged
content and matching source metadata. Variant order alternates between repetitions.

Both variants use compact selection IDs and the minimal-selection prompt, with verification
and graph expansion disabled. The control scans 200 recent headers. The candidate combines
20 distinct semantic matches with 20 recent headers, deduplicates them and asks the same
selection model to choose at most five memories. This isolates candidate retrieval from
the earlier prompt experiments.

| Measurement, three repetitions | Recent 200 | Semantic 20 + recent 20 |
| --- | ---: | ---: |
| Exact checks passed | 3/24 | 24/24 |
| Selection calls | 24 | 24 |
| Gross selection input tokens | 255,270 | 57,888 |
| Selection output tokens | 410 | 438 |
| Cached input tokens | 241,408 | 30,720 |
| Query embedding calls | 0 | 24 |
| Query embedding input characters | 0 | 1,401 |

The control passes only abstention. The candidate reduces gross selection input by 77.3%.
This is **not a measured dollar-cost or latency improvement**: the repeated control prompt
was heavily cached, and the candidate introduces embeddings and SQL vector work. Uncached
input is 13,862 for control versus 27,168 for candidate. Seeding is recorded separately:
12 batch embedding calls, 1,200 inputs and 170,424 characters. Embedding token usage is not
provided by this wrapper.

## Implementation and limits

Contemporary end-to-end harness controls also passed: **34/34 checks in each variant**
over two iterations, with 31 chat calls each. The comparison command reported no regressions.
Gross input was 33,725 for control and 34,043 for semantic candidates; output was 664 versus
678. This small corpus shows no token advantage from semantic candidates. Both used compact
IDs and minimal selection, with only semantic retrieval enabled for the candidate.
Reports:

- `artifacts/memory-long-history-harness-control/20260907T152625-89867cdfba2d4e4b8e8bcef41c1c11b9/report.json`
- `artifacts/memory-long-history-harness-candidate/20260907T152840-b57d30ca4de24bbebad7c65753866f9a/report.json`

Validation: all **107 Memory tests passed**, including real-SQL scope/cold/exclusion filtering
before the candidate limit, multiple chunks per entity, old/recent candidate composition,
and unavailable/malformed selector behavior. The console build and whitespace checks passed.

`Retrieval.UseSemanticCandidates` defaults to false. `SemanticCandidateLimit` and
`RecentCandidateLimit` default to 20 each, and must fit within `HeaderScanLimit`.
The optional `IMemoryCandidateStore` capability avoids breaking existing custom stores;
opting in with an unsupported store throws explicitly. SQL filters by scope and excludes
cold and already-surfaced entities before applying the limit. It ranks each entity by its
closest chunk, so multiple chunks cannot crowd other entities out of the pool. Ranking
uses exact vector distance; bounded model context does not imply constant storage cost.

The usual relevance selector still decides what to return. With semantic candidates enabled,
missing or malformed selection returns no memories rather than treating vector neighbors
as relevant evidence. Cancellation propagates. This failure behavior was added after the
successful scale run and is covered by tests; the report's binary hash identifies the
measured build. Existing recent-header fallback behavior remains unchanged when opted out.
The option applies to the header-selection plan step, including normal harness recall;
explicit vector-only or hot-index-only plans retain their existing behavior.

This is a synthetic coverage experiment, not an independently labeled external benchmark.
It seeds entities/chunks directly with batch embeddings of title plus content; it does not measure extraction,
ingestion deduplication, actual correction writes, compaction, process restarts or a long
running agent. Targets are old by insertion order, not simulated elapsed days. There is no
populated hot index in this fixture. Only five target memories and eight questions are
labeled, despite the larger distractor population. Dense same-subject conflicts and facts
whose headers omit the answer still need separate evaluation. Semantic candidate limits
can miss relevant facts, particularly broad questions requiring many memories.

An earlier interrupted run at
`artifacts/memory-long-history/20260907T152258-c918519db15049488a166484cfa9b1bd/report.json`
is marked `invalid-fixture`: its seeder attached chunks using fixture IDs instead of the
SQL-generated IDs. It is retained for audit and excluded from all results above.

## Reproduce

```powershell
dotnet run --project src/FabrCore.Services.Memory.EvalConsole -- long-history --iterations 3
dotnet run --project src/FabrCore.Services.Memory.EvalConsole -- run --mode harness --iterations 2 --compact-ids true --minimal-selection true --semantic-candidates true
```

The first command retains both controls and candidates without baseline promotion. Exit 0
requires every candidate check to pass; control failures remain visible in the report.
The existing database and ignored local model configuration are used. Each run retains a
new isolated scope. No recurring job is installed.
