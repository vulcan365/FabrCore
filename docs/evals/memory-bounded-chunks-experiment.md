# Bounded multi-chunk evidence

An opt-in five-chunk limit recovers all required source bodies in the retained grouped-memory
fixture. It also returns unnecessary facts for simple questions, so the default remains one.

Report: `artifacts/memory-chunk-coverage/20260907T170927-c2c13cb0963c46f2872ade01a4ea386c/report.json`.

| Three repetitions | One chunk | Three chunks | Five chunks |
| --- | ---: | ---: | ---: |
| Complete checks | 17/24 | 20/24 | **24/24** |
| Single-fact body coverage | 15/15 | 15/15 | 15/15 |
| Two-fact body coverage | 0/3 | 3/3 | 3/3 |
| Five-fact body coverage | 0/3 | 0/3 | 3/3 |
| Unknown-answer abstention | 2/3 | 2/3 | 3/3 |
| Valid returned provenance | 24/24 | 24/24 | 24/24 |
| Gross selection input tokens | 27,564 | 27,564 | 27,564 |
| Selection output tokens | 440 | 434 | 432 |
| Cached input tokens | 0 | 0 | 0 |
| Selection calls / query embeddings | 24 / 24 | 24 / 24 | 24 / 24 |
| Returned body characters, all questions | 1,409 | 4,301 | 6,951 |
| Formatted context characters, all questions | 19,053 | 25,553 | 30,681 |
| Returned chunks, 15 single-fact questions | 15 | 45 | 75 |
| Body characters, single-fact questions | 969 | 2,967 | 4,965 |
| Context characters, single-fact questions | 12,999 | 17,457 | 21,915 |

The combined-question gains follow directly from additional source chunks. The unknown-answer
differences are selector variation: changing the body limit does not change the selection
prompt, and a body-loading policy cannot fix an upstream false positive. All three variants
use the same fixture, prompts, model and no previews, with rotated execution order.

Five chunks return 60 unnecessary chunks across 15 single-fact requests. Their formatted
context grows 68.6%, and their body text grows 5.12 times. These are character measurements,
not downstream token usage: no answer-generation call consumes this context in the fixture.
The informative entity description already contains all five facts to isolate source-body
loading. This is a source-evidence coverage test, not proof that a generated answer previously
failed or now succeeds. The selection token counts exclude downstream consumption and the
separately recorded diagnostic embedding calls.

## Implementation

`Retrieval.MatchedChunksPerMemory` accepts 1 through 8, defaults to 1, and requires matched
evidence for values above 1. The existing matched-evidence option requires semantic candidates.
SQL returns at most the requested number of embedded chunks per selected, non-cold entity
in the exact scope. It orders by cosine distance, chunk index and chunk ID. One query embedding
is reused, and previews continue to use only the nearest chunk.

The existing per-entity body budget is `min(4096, 12000 / selected entity count)` characters.
Multi-chunk loading divides it evenly among the requested chunk slots, reserving two-character
separators, and enforces count and length again for custom stores. Fewer available chunks do
not reclaim unused slots. This preserves a bound but can truncate a long relevant chunk more
aggressively. Graph expansion retains its separate existing behavior; it is disabled here.
The body budget does not cap descriptions, provenance formatting or graph context.

`MemoryEntry.RecalledChunks` holds each returned body, source chunk ID/index and truncation
flag. `Content` joins those bounded bodies; singular source fields are null on this path.
Formatting emits each chunk with its own provenance. The evidence DTO now lives with the
shared Memory contracts. Direct retrieval/update and the single-chunk path retain their
semantics. No SQL schema change is required. Custom stores must implement the optional
`IMemoryCandidateStore.GetMatchedChunksAsync` capability before opting in.

An untruncated returned chunk does not imply complete entity coverage. SQL reads are not a
shared snapshot with selection, and source IDs are not immutable revision IDs. Fixed nearest-K
can miss a required lower-ranked chunk, especially with distractors inside the same entity.
It is not a relevance or completeness guarantee.

## Reproduction and decision

```powershell
dotnet run --project src/FabrCore.Services.Memory.EvalConsole -- chunk-coverage --iterations 3 --chunks-per-memory 5
```

The `chunk-coverage-v2` report retains contemporary one- and three-chunk controls and gates
on the five-chunk candidate. It extends the same synthetic 241-entity, 245-fact fixture as
the [prior experiment](memory-chunk-coverage-experiment.md); it is not an independent benchmark.
Earlier reports and baseline registrations are preserved.

Keep the option available for explicit coverage needs without promoting it. The next experiment
should add distractor chunks within the target entity and test selective chunk loading against
this fixed-K control. It must retain complete multi-fact evidence while reducing excess context
on single-fact questions, and separately score unknown-answer abstention.

All 118 Memory tests pass with none skipped. New checks cover SQL ranking and tie-breaking,
scope/cold filtering, per-chunk bounds, service enforcement of the shared budget against an
over-returning custom store, formatted provenance, embedding reuse and cancellation.

The contemporary two-iteration harness comparison passes 34/34 in both modes with no
comparison regressions. One versus five chunks used 31 versus 30 chat calls, 34,334 versus
32,534 input tokens, 650 versus 471 output tokens, and 18,432 versus 17,280 cached input
tokens. The harness corpus has single-chunk source memories; this checks compatibility, not
multi-chunk answer quality or repeatable savings. One candidate provider response took 18.3
seconds, so this pair also does not establish a latency advantage. Reports:

- `artifacts/memory-bounded-chunks-harness-1/20260907T171202-92addbf79bcb41e6a2836a9c3a9ebc6c/report.json`
- `artifacts/memory-bounded-chunks-harness-5/20260907T171329-b9d789bdfb9a4e9a8f2ba119be3da569/report.json`

Final whitespace checks passed. No defaults or baseline registrations were promoted.
