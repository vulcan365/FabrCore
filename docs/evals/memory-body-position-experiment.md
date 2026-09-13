# Evidence beyond the first preview window

This experiment tests whether the earlier 160-character preview success depends on facts
being near the beginning of short bodies. It uses the five-fact configuration fixture with
opaque, unique headers and 240 distractors.

Every body receives the same 145-character administrative introduction and a long trailing
commentary section. The original fact begins at zero-based offset 145. A 160-character
prefix includes only the first 15 characters of that fact; a 256-character prefix contains
the complete target fact while still excluding most of the body. Embeddings are generated
from the actual enlarged bodies plus their original topical titles, not reused from the
short-body fixture. These additions apply equally to targets and distractors.

All three variants use hybrid semantic 8+8, compact IDs and minimal selection. The variants
differ only in preview length: 0, 160 or 256. Every variant runs the same seven questions
over three repetitions, with rotating execution order. Exact-set scoring also verifies full
loaded body content and provenance. All five targets are outside the recent-200 header cap.
The service's 4,096-character aggregate preview cap remains active.

## Reproduce

Completed report:
`artifacts/memory-body-position/20260907T163751-7ef8e09c5a9a49c18caae4650ee36f35/report.json`.

| Three repetitions | No preview | 160 characters | 256 characters |
| --- | ---: | ---: | ---: |
| Exact checks | 7/21 | 11/21 | **21/21** |
| Selection calls | 21 | 21 | 21 |
| Gross input tokens | 22,368 | 32,238 | 38,412 |
| Output tokens | 430 | 486 | 402 |
| Cached input tokens | 0 | 17,920 | 17,920 |
| Unknown-query failures | 0/3 | 2/3 | 0/3 |

The 256-character candidate passes all single-fact, combined and abstention checks. It uses
19.2% more gross selection input than the inadequate 160-character preview, with the same
selection/embedding call counts and one bounded prefix SQL read per recall. Failure
diagnostics found no expected IDs missing from the candidate pools: those failures occur
after candidate retrieval. The shorter preview also returns irrelevant evidence for two
unknown-answer requests. More context is not automatically sufficient or reliably helpful.

Decision: preserve the opt-in setting rather than increase the default. A 256-character
prefix covers this deliberately positioned evidence, but says nothing about facts farther
down a body. The next retrieval validation should test query-relevant excerpts or chunks
with facts beyond any fixed prefix and with evidence in non-primary chunks. Baselines and
production retrieval code remain unchanged. This run does not claim dollar-cost savings;
cache behavior differs and the larger prefix increases measured input.

```powershell
dotnet run --project src/FabrCore.Services.Memory.EvalConsole -- body-position --iterations 3
```

The command retains all controls and candidate diagnostics in a new `memory-body-position`
scope. Reports use `body-position-v1` and record `BodyFactOffset = 145`. The quality gate
targets the 256-character candidate, keeping failures of shorter previews visible.

This is a synthetic positional stress test, not an external benchmark. It does not establish
a universally safe preview size: useful evidence can occur farther into a body, in another
chunk, or across many memories. Candidate-count growth also reduces each preview's share of
the aggregate budget. Body padding changes embedding inputs relative to earlier experiments,
so only the within-run preview comparisons isolate preview length. No production retrieval
code, default setting or baseline registration changes are part of this experiment.
