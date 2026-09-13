# Selective chunk evidence with internal distractors

This experiment tests whether a second relevance selection can remove unnecessary chunks
while preserving all requested source evidence. It compares fixed and selected pools of
five and eight nearest chunks. The option remains off by default.

Report: `artifacts/memory-selective-chunks/20260907T172952-96f461f1975247d6bbc3961f38c1fdc2/report.json`.

| Three repetitions | Fixed five | Selected five | Fixed eight | Selected eight |
| --- | ---: | ---: | ---: | ---: |
| Strict complete, relevant evidence | 2/24 | 21/24 | 2/24 | **24/24** |
| Known-answer body coverage | 18/21 | 18/21 | 21/21 | 21/21 |
| Unknown-answer abstention | 2/3 | 3/3 | 2/3 | 3/3 |
| Valid returned provenance | 24/24 | 24/24 | 24/24 | 24/24 |
| Unrequested chunks returned | 77 | 0 | 140 | 0 |
| Gross selection input tokens | 27,564 | 39,132 | 27,564 | 41,652 |
| Selection output tokens | 428 | 846 | 434 | 834 |
| Cached input tokens | 0 | 0 | 0 | 0 |
| Chat calls | 24 | 45 | 24 | 45 |
| Query embedding calls | 24 | 24 | 24 | 24 |
| Returned body characters | 7,912 | 2,148 | 13,530 | 2,340 |
| Formatted context characters | 32,816 | 20,016 | 43,846 | 20,454 |
| Sum of chat-call elapsed milliseconds | 32,424 | 52,183 | 27,428 | 53,592 |

At eight chunks, filtering reduces body characters by 82.7% and formatted context characters
by 53.4%, while raising selection input by 51.1% and adding 21 chat calls. Summed chat-call
time nearly doubles; it excludes other recall work and is not a stable latency estimate.
No provider errors occurred. Additional diagnostic embedding costs are recorded separately.
The fixed-eight control already contains every requested fact; its strict failures reflect
unrequested chunks, plus one unknown-answer false positive. Filtering does not create new
evidence. Unknown-answer differences do not establish a causal improvement: the selected
variants' first entity-selection call already abstained on all three unknown repetitions.

## Implementation and bounds

`Retrieval.SelectMatchedChunks` filters the already bounded multi-chunk pool through the
existing structured relevance selector. Its manifest identifies chunks, rather than entities,
and contains only their bounded bodies plus entity title/type/date. Returned IDs are intersected
with offered chunk IDs. Empty selection drops the entity; selected content and per-chunk
provenance are rebuilt from the offered evidence. No extra embedding or body fetch is needed.

The option requires matched semantic retrieval and a chunk limit of at least two. Each recall
with available bodies adds one selection call; optional verification can add more calls when
configured. The existing semantic selector returns no selection when its model fails, and
cancellation propagates. The body and candidate-count budgets remain unchanged. Custom
retrievers remain responsible for their own failure semantics. The option cannot recover a
required chunk that was not in the bounded pool or was cut off by its prefix limit.

Descriptions and other existing context remain in formatted recall. Filtering chunk bodies
does not make the whole context minimal, and the existing whole-context harness cap still
applies independently. This experiment does not change graph expansion or defaults.

## Fixture and scoring

```powershell
dotnet run --project src/FabrCore.Services.Memory.EvalConsole -- selective-chunks --iterations 3
```

The grouped entity has five production configuration chunks plus three sandbox configuration
chunks with a different region, owner and retention period. Its generic overview is chunk 0;
the eight fact chunks occupy indexes 1 through 8. Another 240 newer entities remain as
cross-entity distractors. There are 241 entities, 248 fact chunks and 241 overview chunks.
The target description deliberately contains all five production values to isolate source
body retrieval. As in the prior fixture, this evaluates source evidence rather than generated
answer accuracy; the header itself could already support an answer.

Both policies use hybrid 8+8 entity candidates, compact IDs, minimal selection, no previews,
no verification and no graph expansion. Real SQL, embeddings and gpt-5.4-mini are used.
Policy and chunk-limit order rotate across three repetitions of eight questions. Strict success
now requires exact entity selection, complete requested bodies, valid provenance and zero
unrequested chunks. This stricter score is not directly comparable with the prior coverage-only
scores. Reports retain coverage and excess counts separately. Only selected-eight gates the
run; all controls and separate diagnostic costs are retained.

For the five-fact question, the five-chunk pool ranks production encryption, retention, region
and recovery plus sandbox retention ahead of production owner. Filtering removes the sandbox
chunk but cannot restore the missing owner. The eight-chunk pool includes all five production
facts and all three sandbox distractors. This is the concrete candidate-coverage limit the
experiment tests. The fixture remains synthetic and small, not independent validation.

## Decision

Keep selective loading opt-in. Compare its extra selection tokens and latency with the cost
of carrying extra context downstream. A smaller returned body is not by itself an end-to-end
token saving. Wider pools, deeper evidence, genuine supersession, adversarial content and
long-running reuse remain separate validation needs.

Validation: the eval console builds without warnings or errors; all 119 Memory tests pass
with none skipped. The new service test verifies that unknown chunk IDs cannot introduce
content, only selected sources survive, empty selection drops evidence, embeddings are reused,
and cancellation propagates. Existing tests cover bounded SQL loading and semantic selector
failure behavior. Whitespace checks pass. Baseline registrations and defaults are unchanged.

The contemporary two-iteration harness comparison passes 34/34 in both modes with no
comparison regressions. Fixed-eight versus selected-eight used 31 versus 42 chat calls,
34,623 versus 37,888 input tokens, 545 versus 806 output tokens, and 18,944 versus 18,432
cached input tokens. On this single-chunk corpus, selection adds cost without a measured
quality gain. It verifies compatibility, not multi-chunk answer quality or sustained savings.
Reports:

- `artifacts/memory-selective-harness-control/20260907T173335-9c6a3729571b478cb9bee981f856d124/report.json`
- `artifacts/memory-selective-harness-candidate/20260907T173440-74410b1736c44cd1a7633df3522bfcf3/report.json`

The next cost experiment should test avoiding redundant second selection for simple evidence
or combining selection stages, while retaining this internal-distractor fixture and its strict
coverage/relevance checks as controls.
