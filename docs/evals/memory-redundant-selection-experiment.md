# Avoiding redundant chunk selection

The opt-in shortcut skips the second selector only when it would inspect the same complete
body text already presented to the entity selector. It is intended to reduce duplicate work
on short, single-chunk memories without weakening the bounded multi-chunk selection path.

## Harness results

| Three repetitions | Always select | Conservative shortcut |
| --- | ---: | ---: |
| Harness checks | 51/51 | 51/51 |
| Total chat calls | 65 | 45 |
| Memory selection calls | 39 | 21 |
| Gross total chat input tokens | 59,929 | 49,629 |
| Memory selection input tokens | 21,391 | 13,869 |
| Output tokens | 1,405 | 853 |
| Cached input tokens | 27,648 | 28,160 |

The comparison reports no check regressions. The shortcut removes all 18 redundant second
selection calls in this corpus. Gross total input falls 17.2%. Of the 10,300-token reduction,
7,522 tokens come from memory selection; the remaining difference includes agent-generation
variation. Total chat calls fall by 20, of which 18 are eliminated selector calls and two
are other model-call variation. This is a measured small-corpus result, not a repeatable
17.2% savings guarantee or a pricing claim.

Reports:

- `artifacts/memory-redundant-selection-control/20260907T180852-7c737de972444cd299d42c132cf771bb/report.json`
- `artifacts/memory-redundant-selection-candidate/20260907T181034-024cc859f1fa43a6bde7f4dee49e713c/report.json`

## Conditions and limits

`Retrieval.SkipRedundantChunkSelection` requires `SelectMatchedChunks` and defaults to false.
Every returned entity must have exactly one nonempty, untruncated chunk. That chunk must
exactly equal its description in the original entity-selection manifest, and the title,
type, timestamp and snapshot flag must still match the loaded entity. Preview-augmented
descriptions generally fail this exact comparison. Any entity that does not qualify keeps
the entire second selection; this includes mixed single-/multi-chunk pools.

The comparison uses the original offered headers, not descriptions reloaded after selection.
This avoids treating newly edited content as previously reviewed. It is not a transaction
snapshot or immutable revision guarantee. Count, body and provenance bounds are unchanged.
Cancellation is checked before bypassing selection. No model, embedding or SQL call is added
to decide whether the shortcut applies.

Two model selections can disagree even when they see the same body. The shortcut trusts the
first selection and is not proof of equivalent model behavior. Content absent from embedded
chunks, content beyond a prefix cap and concurrent source edits retain their existing limits.
The shortcut qualifies on returned chunks, not an assertion that the entity has no other
unembedded content. It also cannot optimize multi-chunk memories, long bodies truncated by
the shared budget, or short bodies whose first-stage descriptions are incomplete.

## Validation design

The contemporary harness pair uses three iterations, matched semantic recall, hybrid 8+8
entity candidates, compact IDs, minimal selection, eight-chunk pools and chunk selection.
Only the shortcut differs. Control runs precede candidate runs, so caching and provider
variation can affect totals. Reports retain actual usage rather than estimates.

The internal-distractor guard uses the retained synthetic production/sandbox fixture. It
compares always-select and shortcut policies in alternating order over three repetitions.
Both select from eight chunks and require complete requested source evidence, valid provenance
and zero excess chunks. The shortcut must not bypass this multi-chunk selection. As previously
documented, the informative header itself contains production facts; this is source-evidence
validation rather than a generated-answer benchmark.

```powershell
dotnet run --project src/FabrCore.Services.Memory.EvalConsole -- run --mode harness --iterations 3 --semantic-candidates true --hybrid-candidates true --candidate-budget 8 --compact-ids true --minimal-selection true --matched-chunks true --chunks-per-memory 8 --select-chunks true --skip-redundant-selection true
dotnet run --project src/FabrCore.Services.Memory.EvalConsole -- selective-chunks --iterations 3 --skip-redundant-selection true
```

All 126 Memory tests pass with none skipped. Seven new cases verify the shortcut across two
selected memories and reject changed bodies, truncated bodies, mixed pools, changed timestamps,
changed titles and empty content. Existing tests retain the default selection, abstention,
cancellation, provenance and shared-budget coverage.

## Multi-chunk guard results and decision

Report: `artifacts/memory-redundant-selection-guard/20260907T181156-62bf99bef9e64566832c2a61e7c396db/report.json`.

Both policies pass 24/24 strict checks, returning no excess chunks, 2,340 body characters
and 20,454 formatted context characters each. All 42 combined known-answer checks still
make both selection calls. Thus the shortcut preserves multi-chunk filtering in this fixture.
Both variants make 24 query embeddings. Always-select versus shortcut totals are 47 versus
45 chat calls, 42,988 versus 41,652 input tokens, 876 versus 840 output tokens and zero cached
input. The two-call difference comes from unknown questions: two control entity selections
require a second selector to abstain, while the shortcut variant's first selector abstains
on all three repetitions. This is model variation, not shortcut savings on multi-chunk data.

Keep the shortcut opt-in and retain these controls. The short single-chunk harness shows a
measured reduction in duplicate work without check regressions; the internal-distractor guard
retains full source coverage and relevance. Neither small fixture establishes long-running
reliability or equivalence across models. Future cost work should target multi-chunk selection
without assuming this shortcut applies. No defaults or baseline registrations were promoted;
final whitespace checks passed.
