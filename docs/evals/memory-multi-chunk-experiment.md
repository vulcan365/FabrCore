# Relevant evidence outside the primary chunk

Follow-up: [query-matched chunk loading](memory-matched-chunks-experiment.md) passes this
regression with explicit source identity and bounded body content. The failures below remain
the original primary-chunk control.

This experiment isolates content loading after successful memory selection. Each of 245
entities has a generic overview in chunk 0 and its actual content in chunk 1. Both chunks
have embeddings generated from their content inputs. Five target entities contain the
configuration facts; 240 distractors push those targets outside the recent-200 header cap.
The fixture reads back both chunks before scoring.

Headers deliberately retain the informative title and fact-bearing description. This makes
it possible to separate selecting the correct entity from returning the correct body. It is
not an end-to-end answer benchmark: an answer model might exploit the descriptions, but
the memory API is still required to return the requested body evidence.

Hybrid 8+8 is evaluated with no preview and a 256-character primary-body preview. Both use
compact IDs and minimal selection, with verification and graph expansion off. Three
repetitions alternate order over the same seven questions. Reports separately record
`IdsPassed`, `ContentPassed`, combined success and the actual loaded bodies. Empty evidence
on an unknown question passes; known questions require exact IDs, body content and provenance.

## Reproduce

Completed report:
`artifacts/memory-multi-chunk/20260907T164244-ee33a928cf234f89a298e7bd5fbd2f60/report.json`.

| Three repetitions | No preview | 256-character preview |
| --- | ---: | ---: |
| Correct selected ID sets | 21/21 | 21/21 |
| Correct bodies on known-answer checks | **0/18** | **0/18** |
| Combined checks (including abstention) | 3/21 | 3/21 |
| Selection calls | 21 | 21 |
| Gross input tokens | 22,173 | 29,694 |
| Output tokens | 396 | 396 |
| Cached input tokens | 0 | 15,360 |

Every known-answer case returns the generic chunk-0 overview instead of its selected
entity's chunk-1 fact. The three unknown-answer cases correctly return no evidence. The
preview adds 33.9% gross input without improving body retrieval. The quality gate fails,
and both failing variants are retained. All **114 existing Memory tests pass**, so these
results expose a coverage gap in the existing tests rather than a successful implementation.
Build and whitespace checks also passed.

```powershell
dotnet run --project src/FabrCore.Services.Memory.EvalConsole -- multi-chunk --iterations 3
```

Reports use `multi-chunk-v1`, record `EvidenceChunkIndex = 1`, and retain the fixture and
failed-case candidate diagnostics in an isolated scope. The preview candidate must pass all
checks for the quality gate. Both control failures and actual returned content remain visible.

The relevant read path currently ranks an entity by its closest chunk but later calls
`GetPrimaryChunkAsync` in `AgentMemoryService.LoadMemoriesWithChunksAsync` to populate its
recalled body. `MemoryRetriever.RetrieveMemoryAsync` and selection previews also read only
chunk 0. Increasing a primary-body preview cannot expose evidence in chunk 1. The next change
must address chunk-aware loading and provenance with a bounded content budget, rather than
increase a prefix length or concatenate every chunk indiscriminately.

These are directly seeded synthetic multi-chunk entities. This run does not establish how
often production ingestion creates this situation, nor validate extraction or crash recovery.
No production retrieval code, default setting or registered baseline changes are made here.
