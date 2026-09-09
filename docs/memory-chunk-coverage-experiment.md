# Several required chunks in one memory

The matched-chunk path retrieves each individual fact correctly but cannot return complete
evidence when a question needs several chunks from the same entity. Increasing selection
previews does not address that body-loading limit.

Completed report: `artifacts/memory-chunk-coverage/20260907T170215-a940004252c24992a0c477b34404e738/report.json`.
The adjacent `fixture.json` records the expected entity and chunk identities.

| Three repetitions | No preview | 256-character preview |
| --- | ---: | ---: |
| Full checks | 18/24 | 16/24 |
| Single-fact body checks | 15/15 | 15/15 |
| Two- and five-fact body checks | 0/6 | 0/6 |
| Unknown-answer abstention | 3/3 | 1/3 |
| Exact entity selection | 24/24 | 22/24 |
| Returned chunk provenance | 24/24 | 24/24 |
| Selection calls / query embeddings | 24 / 24 | 24 / 24 |
| Gross selection input tokens | 27,564 | 37,416 |
| Output tokens | 444 | 430 |
| Cached input tokens | 0 | 16,640 |

Every combined question selects the correct entity but returns only one chunk: chunk 1
for region plus owner, and chunk 5 for all five configuration values. The returned chunk
is authentic, untruncated evidence, yet incomplete. Provenance success alone cannot establish
answer coverage. With previews, two unknown-cost questions incorrectly select the configuration
entity and return its region chunk. This is a recall false positive; no answer-generation
model is evaluated here.

Previews increase gross selection input by 35.7% without improving body coverage. Omitting
them uses 26.3% less gross input in this fixture. Cache usage differs substantially, so these
figures do not establish billed-cost savings or a universal preview policy.

## Fixture and reproduction

```powershell
dotnet run --project src/FabrCore.Services.Memory.EvalConsole -- chunk-coverage --iterations 3
```

The fixture stores five short Atlas configuration facts as chunks 1 through 5 under one
entity, behind a generic chunk-0 overview. It then adds 240 newer distractor entities across
four memory types. The target is outside the recent-200 window. Its deliberately informative
description includes all five facts to isolate body loading from entity discovery. Real SQL,
embeddings and the configured gpt-5.4-mini selection model are used. Both variants enable
matched evidence, compact selection IDs, minimal selection, hybrid 8+8 candidates, and no
graph expansion. Preview lengths are fixed at 0 and 256; variant order alternates.

Eight questions cover five individual facts, two combined requests and one unknown. Success
requires exact entity selection, all requested fact bodies, source metadata, and a valid
seeded chunk ID/index without truncation. Each report case retains expected chunk IDs and
loaded bodies. The report marks the run complete and retains failing gates; diagnostic
candidate rediscovery costs are recorded separately from recall. The fixture comprises
241 entities and 245 evidence chunks, plus 241 generic overview chunks. It is a synthetic
regression fixture, not an independently curated or end-to-end agent benchmark.

## Decision

Retain this failing control for bounded multi-chunk retrieval. The next candidate should
preserve provenance per chunk and enforce a shared evidence budget, then compare combined
coverage, single-fact over-retrieval, unknown-answer abstention and context cost. Loading
every chunk by default would not establish a token-efficient solution. Existing production
behavior, defaults and baseline registrations are unchanged in this experiment.

Validation: the eval console built without warnings or errors, all 116 Memory tests passed
with none skipped, and whitespace checks passed. Candidate diagnostics found no missing
expected entities before selection on any failing check.
