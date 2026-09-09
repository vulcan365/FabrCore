# Shorter evidence previews for chunk selection

This experiment separates the text shown to the second selector from the bodies returned
to the caller. It tests whether shortening the selection input can reduce token usage while
preserving exact requested source evidence and excluding distractors.

Report: `artifacts/memory-chunk-preview/20260907T183949-a1e5ff045fab4ccfb48e00a19ae16732/report.json`.

| Three repetitions | Full bounded body | 128 characters | 256 characters |
| --- | ---: | ---: | ---: |
| Strict evidence checks | 24/24 | 15/24 | **24/24** |
| Known-answer full-body coverage | 21/21 | 14/21 | 21/21 |
| Valid returned provenance | 24/24 | 24/24 | 24/24 |
| Unrequested chunks | 0 | 20 | 0 |
| Total selection input tokens | 50,013 | 44,223 | 46,878 |
| Second-stage input, known-answer questions | 21,423 | 16,662 | 19,317 |
| Output tokens | 858 | 884 | 840 |
| Cached input tokens | 0 | 0 | 0 |
| Chat calls | 46 | 45 | 45 |
| Query embedding calls | 24 | 24 | 24 |
| Returned body characters | 16,230 | 20,429 | 16,230 |
| Formatted context characters | 34,281 | 38,581 | 34,281 |

The 256-character candidate preserves the complete returned bodies and context size while
reducing second-stage input on the same known-answer questions by **9.8%**. Total observed
selection input falls 6.3%; of the 3,135-token difference, 2,106 comes from shorter known-answer
selection input, while 1,029 comes from an extra second-stage call on an unknown question in
the control. That call-count difference is first-selector variation, not a preview saving.
All three conditions ultimately abstain on all unknown-answer repetitions.

The 128-character condition fails in two ways: it omits required late evidence and sometimes
selects extra chunks instead. In the first owner check it selects all eight chunks, including
the correct owner but seven unwanted chunks; in the recovery check it returns the retention
chunk; in the five-fact check it returns only the three early facts. It consequently returns
more total context despite using less selection input. Some combined queries still recover
late evidence; that does not establish that the preview exposes or reliably identifies it.
These observations reject the shorter prefix on this fixture.

## Implementation and fixture

`Retrieval.ChunkSelectionPreviewCharacters` defaults to zero, which uses full bounded bodies.
Positive values from 1 to 4096 require `SelectMatchedChunks`. When a body exceeds that length,
the selector sees its prefix and an explicit notice that remaining evidence was not shown.
The selected IDs still map to the original bounded bodies. Preview truncation does not set
source-body truncation flags or alter stored content, chunk IDs or returned provenance. No
additional model, embedding or storage call is introduced by the preview option.

```powershell
dotnet run --project src/FabrCore.Services.Memory.EvalConsole -- chunk-preview --iterations 3
```

The synthetic grouped-memory fixture retains five production facts, three sandbox distractor
facts and 240 other entities. Each of the eight target-entity fact chunks is padded to 450
characters with neutral administrative text. Facts alternate between offsets zero and 145;
the production owner and recovery objective are late, while region, retention and encryption
are early. There are 241 entities, 248 fact chunks and 241 overview chunks. The target header
retains its short description of all production facts, so first-stage selection does not
change across conditions. This isolates source-body selection, not generated-answer accuracy.

Conditions use full bounded bodies, 128-character prefixes or 256-character prefixes. Order
rotates over three repetitions of eight questions. All use hybrid 8+8 entity candidates,
eight-chunk pools, compact IDs, minimal selection and chunk selection. First-stage previews,
redundant-selection skipping, verification and graph expansion are disabled. Real SQL,
embeddings and the configured gpt-5.4-mini model are used. The 450-character bodies fit the
existing selected-body budget without source truncation.

Strict success requires correct entity IDs, every requested full padded body, valid source
metadata and chunk provenance, and no unrequested chunks. The fixture records fact offsets,
preview lengths, usage, body/context sizes and all failing cases. Only the 256-character
candidate gates the run. Earlier controls and baseline registrations are retained.

## Limits

A prefix cannot expose facts beyond its boundary. The truncation notice makes that limit
visible to the selector but does not recover hidden evidence. The fixture's 145-character
offset does not establish that 256 characters is generally sufficient; longer introductions,
late corrections and negation can require more context. This is a small synthetic regression
experiment, not an independent long-history benchmark. The option remains off by default.
The truncation notice itself consumes tokens and can outweigh a small prefix reduction.
Keep full bounded bodies as the default rather than treating 256 as a universal threshold.

All 127 Memory tests pass with none skipped. The extended selector test checks the exact
bounded preview and truncation notice while requiring the returned body and source truncation
flag to remain unchanged; it also retains abstention, unknown-ID filtering and cancellation
checks. The console built without warnings or errors.

The contemporary two-iteration harness comparison passes 34/34 in both modes with no
comparison regressions and 44 chat calls each. Control versus 256-character preview totals
are 41,241 versus 40,684 input tokens, 922 versus 822 output tokens and 18,944 versus 20,736
cached input tokens. Memory-selection input is 14,242 versus 14,186. This small compatibility
pair does not establish repeatable end-to-end savings. Reports:

- `artifacts/memory-chunk-preview-harness-control/20260907T184345-d610e3502ede4db4a864410121bb5e29/report.json`
- `artifacts/memory-chunk-preview-harness-candidate/20260907T184535-06862a198332421daf2900b37abdc78e/report.json`

Whitespace checks passed. Defaults and baseline registrations are unchanged. Retain both
the successful 256-character candidate and failing 128-character control before testing
longer fact offsets and late corrections.
