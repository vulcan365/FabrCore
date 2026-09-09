# Late corrections in chunk-selection previews

This experiment checks whether prefix selection changes relevance when a later sentence
rejects an earlier value. It adds a retained fixture without changing production retrieval,
defaults or baseline registrations.

Report: `artifacts/memory-late-correction/20260907T190646-6e3a157541eb415ea0257f65982cce02/report.json`.

| Three repetitions | Full bounded body | 256 characters | 384 characters |
| --- | ---: | ---: | ---: |
| Strict evidence checks | **24/24** | 21/24 | 22/24 |
| Known-answer current-body coverage | 21/21 | 21/21 | 20/21 |
| Rejected draft chunks returned | 0 | 3 | 2 |
| Valid returned provenance | 24/24 | 24/24 | 24/24 |
| Total selection input tokens | 50,856 | 46,965 | 49,821 |
| Second-stage input, known-answer questions | 22,236 | 19,401 | 22,257 |
| Output tokens | 852 | 852 | 836 |
| Cached input tokens | 0 | 0 | 0 |
| Chat calls | 46 | 45 | 45 |
| Query embedding calls | 24 | 24 | 24 |
| Returned body characters | 16,230 | 17,586 | 16,682 |
| Formatted context characters | 34,323 | 35,925 | 34,857 |

The 256-character condition retains current evidence but adds a rejected draft in three
checks: one retention question and two owner questions. The 384-character condition fails
two retention checks. In one it returns only the rejected draft, losing current evidence;
in the other it includes the draft alongside current evidence. All conditions ultimately
abstain on all three unknown-answer checks. All returned sources are authentic, illustrating
why provenance alone does not establish relevance or completeness.

On the same known-answer questions, 256 reduces second-stage input 12.8% but loses strict
quality, while 384 uses 21 more tokens than full bodies (about 0.1% more). Its truncation
notice consumes the small text saving. The full control performs an extra second-stage
call on one unknown question, costing 1,056 input tokens; that model-selection variation
explains why the 384 condition has a lower aggregate despite no comparable-work saving.

## Fixture and reproduction

```powershell
dotnet run --project src/FabrCore.Services.Memory.EvalConsole -- late-correction --iterations 3
```

The grouped entity contains five current production facts and three rejected draft records.
Each draft starts with a plausible production region, owner or retention value. At character
320, it states: "Correction: the preceding value is a rejected draft, not current production.
Do not use it as a production fact." All eight fact chunks are 450 characters long; neutral
administrative text fills the remaining space. These are static labeled source records, not
actual concurrent database corrections or extraction mutations.

The 256-character prefix hides the rejection. The 384-character prefix reveals its beginning,
including that the earlier value is a rejected draft, while full bounded loading exposes the
complete statement. The fixture retains 240 newer cross-entity distractors, for 241 entities,
248 fact chunks and 241 overview chunks. The target header describes the five valid production
values, isolating second-stage source selection. It is a synthetic evidence-selection test,
not a generated-answer benchmark or independently curated history.

All conditions use hybrid 8+8 entity candidates, eight-chunk pools, compact IDs, minimal
selection and chunk selection, with no first-stage previews, shortcut, verification or graph
expansion. Real SQL, embeddings and gpt-5.4-mini are used. Three repetitions rotate full,
256 and 384 conditions over eight questions. Strict success requires the full requested
current bodies, valid source metadata/provenance and no unwanted chunks. Only the 384-character
candidate gates the run. All failures, costs and returned bodies remain in the report.

The returned bodies retain their existing bounds and include the late rejection even when
the selector cannot see it. Surfacing a rejected draft therefore does not itself prove that
a downstream agent would answer with the rejected value. Missing current evidence and extra
draft evidence are scored separately from source authenticity.

## Decision and validation

Retain full bounded bodies as the default. The earlier 256-character success does not
generalize to late corrections, and simply increasing the preview to 384 does not restore
reliable selection or reduce like-for-like input in this fixture. Retain both failing prefix
conditions as guards for future cost changes. A future alternative must preserve corrective
context and should be assessed against complete current evidence, unwanted drafts, token
cost and latency; no new production policy is introduced here.

The run completed all 72 cases and retained a failing 384-candidate gate, rather than an
infrastructure error. The console built without warnings or errors. Existing harness results
were not rerun because this change adds only an eval fixture and documentation; production
retrieval is unchanged. Baseline registrations and default options remain unchanged.
All 127 Memory tests passed with none skipped, and final whitespace checks passed.
