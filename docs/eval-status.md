# GraphRAG evaluation status

Last updated: **2026-09-07**. Status: **paused at the user's request**.

This is the resume point for ingestion performance and extraction-quality evaluations
of `FabrCore.Services.GraphRag`. No additional experiment should be started as part
of this pause. The latest completed work is the separate policy-obligation experiment.

## Objective and current decision

Ingest documents faster while retaining chunk embeddings, useful entity/node features,
typed factual edges, and domain/category assignments. Keep SQL VECTOR storage. Faster
output is not acceptable if required facts disappear. Retrieval-only success does not
establish graph correctness.

Use **mini (`graphrag`, currently `gpt-5.4-mini`)**, **two concurrent documents**, and
the existing **shared limit of four chat calls** for further evaluation. Keep eight
2,000-character extraction sections per batch. Use schema responses explicitly in
the eval command. Nano and `gpt-5.6-luna` did not justify replacing mini in prior tests.

These are evaluation recommendations, not a claim that all production defaults were
changed. In particular, the console defaults to one concurrent document and its
appsettings still set `UseExtractionJsonSchema=false`; pass the options below.
The policy vocabulary, structured taxonomy names, and separate obligation field
remain **opt-in**. Do not enable the separate obligation variant on the fast path.

The architecture is reasonable: separate retrieval chunks from extraction sections,
extract graph facts with the LLM, and persist scoped contributions and embeddings.
The main remaining problems are extraction fidelity, merging, retries, and scheduling,
not evidence that SQL VECTOR must be replaced. See the
[architecture review](graphrag-ingestion-review.md) for the comparison with other
GraphRAG approaches. Co-occurrence graphs or deferred extraction do not by themselves
satisfy the requirement for factual edges available after ingestion.

## Environment and artifacts

| Item | Location / setting |
| --- | --- |
| Workspace | `C:\repos\FabrCore` |
| Runner | `src/FabrCore.Services.GraphRag.EvalConsole` — console execution works; Blazor is not required |
| Database | Existing `localhost/graphrag`; user `graphrag365`, dbowner, cannot create databases |
| Local settings | `src/FabrCore.Services.GraphRag.EvalConsole/appsettings.local.json` contains the connection string and model-config path |
| Model configuration | `samples/FabrCore.SampleApp/fabrcore.json`; local configuration contains provider credentials, do not print or commit them |
| Embeddings | Existing `text-embedding-ada-002`, 1536 dimensions |
| Downloaded source cache | `artifacts/graphrag-corpus` |
| Run artifacts | `artifacts/graphrag-evals/<run-id>/report.json` and `report.md` |
| Tests | `src/FabrCore.Services.GraphRag.Tests`; latest unit run: **97 passed** |

Local settings and artifacts may be ignored by Git; preserve them for resuming on
this machine. There are unrelated workspace changes: do not reset the working tree.
Do not create/drop databases or globally clear taxonomy. Fresh passes create new
evaluation scopes; taxonomy remains shared across scopes.

## Corpora and scoring

- **RFC corpus:** JSON/CSV/UUID documents used for early experiments. Use
  `corpus-relations-v3.json` for corrected factual tracing; some historical runs used
  v2 labels with overly broad matching. Do not compare those scores as identical labels.
- **Policy corpus:** Project Open Data policy memo and governance document, plus CISA
  open-source policy, from pinned GitHub Markdown sources. Full text is **59,152
  characters and 163 retrieval chunks**. Always use `--max-chars 0` for full documents.
- **`corpus-policy-v1.json`:** nine positive typed-fact checks and three retrieval questions.
- **`corpus-policy-v2.json`:** identical documents, instructions, and original nine
  labels, plus two positive obligation checks and one negative check. Current eval corpus.

The added checks cover mandatory machine-readable/open formats, recommended
non-proprietary formats, and incorrectly upgrading the latter to a requirement.
Evidence strings were checked against the actual cached sources. Labels are never
included in extraction prompts. The documents are reproducible snapshots, not a
statement about current policy applicability.

**A green runner exit is a smoke gate, not an all-facts gate.** Read `FactChecks`,
`FactTraces`, raw responses, and persisted graph snapshots. A negative check can pass
because an edge is absent while the corresponding positive fact is missing. Regex
qualifier checks are not a full assessment of exceptions, scope, or obligation accuracy.

## Completed findings — do not repeat without a new hypothesis

| Experiment | Decision / finding | Detail |
| --- | --- | --- |
| Models and provider caching | Keep mini; nano and Luna were slower in tested workloads. Prompt-cache hits do not avoid output generation. | [Model/cache](graphrag-model-cache-experiment-2026-09-05.md), [Luna](graphrag-luna-experiment-2026-09-05.md) |
| Enforced JSON | Supported and used in later evals; syntax enforcement does not ensure factual correctness. | [Structured JSON](graphrag-structured-json-experiment-2026-09-05.md) |
| Compact descriptions, relation guidance, evidence, source-span repair | No demonstrated overall speed/quality adoption win; keep experimental settings off by default. | [Relations](graphrag-relation-conventions-experiment-2026-09-05.md), [Evidence](graphrag-evidence-experiment-2026-09-05.md), [Span repair](graphrag-source-span-repair-experiment-2026-09-05.md) |
| Result + taxonomy + embedding caches | Forced repeat rebuild averaged 0.542s versus 8.457s with taxonomy cache off and other caches on; zero LLM/embedding calls on fully cached repeat. Not a cold-ingestion gain. | [Taxonomy cache](graphrag-taxonomy-cache-experiment-2026-09-05.md), [Embedding cache](graphrag-embedding-cache-experiment-2026-09-05.md) |
| Batch sizes 4/8/16 | Keep eight; sixteen reduced output/calls without a convincing latency and factual-retention win. | [Batch size](graphrag-batch-size-experiment-2026-09-05.md) |
| Endpoint tracing/aliases | Some raw facts disappear because exact endpoint entities are missing. Explicit unique acronym resolution helps selected cases, not overall extraction completeness. | [Fact tracing](graphrag-fact-trace-experiment-2026-09-05.md) |
| Document concurrency | Two documents averaged 16.9s versus 25.5s sequential, about 34% faster. Three added little. Peak chat concurrency stayed four; seven calls per pass. | [Concurrency](graphrag-document-concurrency-experiment-2026-09-05.md) |
| Structured taxonomy names | Separated names from domain metadata; rejects unknown names marked as reused. No speed/quality adoption win; old duplicate names remain. | [Taxonomy names](graphrag-taxonomy-names-experiment-2026-09-05.md) |
| Policy relation vocabulary | Original facts improved to 8/9 in both runs versus 6–7/9, but time increased to 20.0s versus 17.9s. Still missing obligations and confusing REQUIRES with USES. | [Policy relations](graphrag-policy-relations-experiment-2026-09-05.md) |
| Separate obligation field | 46.4s versus 28.8s for same-day policy controls; nine calls instead of seven. All 313 retained qualifiers round-tripped, but required facts remained missing. Do not adopt. | [Obligations](graphrag-policy-obligation-experiment-2026-09-07.md) |

Most comparisons had only two observations per setting. Absolute timings across days
are not directly comparable. Provider caching, routing, output volume, shared taxonomy,
and scheduling vary. Use contemporaneous controls and record cached-token telemetry.

## Exact latest checkpoint

The obligation experiment added `--relations policy-obligation`, backed by
`UsePolicyRelations=true` and `UsePolicyObligations=true`. A required enum separates
obligation strength from action type. PROHIBITS/prohibited must agree. Both new runs
returned contradictory CISA fields; rejecting them triggered existing split retries.

Qualifiers are stored experimentally in a tagged SQL description suffix, **not a new
column**. Eval read-back separates the tag from source description before fact scoring.
Other readers may display the suffix. Existing edge merge keys still collapse assertions
by endpoints/type, including potentially conflicting qualifiers across sources.

Completed runs:

| Run ID | Variant | Seconds | Calls | Positive facts |
| --- | --- | ---: | ---: | ---: |
| `20260907T132242-640b5a86552e4619a6ac7cc4e35eeae0` | policy control | 30.402 | 7 | 8/11 |
| `20260907T132316-60320b2e22424eb9838da7f8f222bbd9` | separate obligation | 50.536 | 9 | 7/11 |
| `20260907T132410-da2ce92d5144412aac02083b2b74712f` | separate obligation | 42.331 | 9 | 8/11 |
| `20260907T132722-b2c67f0f98d04413b6d426bd89fae78e` | replacement policy control | 27.215 | 7 | 5/11 |

`20260907T132455-cd99b8964ceb4099910d365c611b504c` finished ingestion but failed a
report write before retrieval. Exclude it from completed comparisons; its checkpoint
is partial. The replacement control above completed all checks. ReportWriter now
retries transient IO/access failures with bounded backoff; a Windows file-sharing
regression test passes. The exact external cause of the access error was not established.

Machine-readable summary:
`artifacts/graphrag-evals/policy-obligation-experiment-2026-09-07.json`.
No experiment process was left running at the pause.

## Remaining experiments, in priority order

These are planned work, **not completed results**. Keep each comparison bounded and
do not reopen every rejected setting at once.

### 1. Targeted repair versus whole-batch regeneration — next experiment

Use captured contradictory responses from the last two CISA runs to develop deterministic
tests first. Compare the existing split/retry behavior with one bounded repair of only
invalid assertions, retaining valid extraction work. Supply source evidence and exact
endpoint context; do not silently coerce the relation type, drop a required edge, or
infer an obligation from unsupported text. Keep a clear failure/fallback path.

Then run a small live comparison with identical corpus/model/concurrency and fixed
labels. Record repair requests, tokens, rejected assertions, total time, retained facts,
and final qualifier consistency. A retry optimization must save work without concealing
missing facts. Keep obligation repair experimental even if its retry path improves.

### 2. Representative edits and cache/concurrency interaction

Test meaningful insertions, deletions, and edits near the beginning/middle/end, not only
a one-character tail change. Compare normal unchanged-document skipping, forced cached
rebuild, and partially changed ingestion. Check graph/taxonomy freshness, unchanged
vector reuse, changed-vector regeneration, cache isolation and model/prompt invalidation.

The runner currently rejects caches with document concurrency above one because cache
hit counters are global. Fix attribution before testing the combined settings. Exercise
simultaneous identical work, eviction, restart/cold-cache behavior, and any proposed
request coalescing. Do not extrapolate the 0.542s repeat result to new documents.

### 3. Missing facts, conflicting assertions, and taxonomy isolation

Trace the missing non-proprietary-format recommendation from its source section through
raw output, merging, and persistence. Add a bounded labeled fixture for multiple
obligations or exceptions sharing endpoints/type. Determine whether the extraction or
the merge loses the fact before changing prompts again.

Separately test taxonomy with clean, known candidate sets and expected subject labels.
Existing Engineering-only bias and decorated duplicates confound the current corpus.
Use isolated fixtures/snapshots; do not delete shared database taxonomy. Test exact reuse,
new-name creation, concurrent creation, and provenance-preserving duplicate handling.
Do not adopt the tagged obligation storage as a production solution without deciding
how per-source assertions and conflicts should be represented.

### 4. Larger held-out corpus and quality assessment

Add business/policy documents and retain a technical holdout. Label representative
actors, actions, domains, obligations, exceptions and forbidden/reversed edges before
running. Include required-fact recall and a manual precision sample; entity/edge counts
and retrieval recall alone are inadequate. Fix acceptable quality criteria before
choosing settings and preserve every source/version/hash.

This should validate a small shortlist, not another broad model sweep. Revisit model
choice only if a measured remaining quality/latency gap warrants it.

### 5. Sustained load and end-to-end ingestion validation

Validate the shortlisted configuration on mixed short/long documents with enough
repetitions to assess median and tail latency. Measure documents/second, queue wait,
peak shared requests, tokens, retries/rate limits, embedding work, and SQL write time.
Include same-scope entity overlap, duplicate submissions, cancellation and failure
recovery. Check SQL races/deadlocks and partial publication; three-document trials do
not establish production load safety. A document loop must reuse the shared ingestion
service rather than multiply its request limits through separate instances.

### 6. Final regression, defaults, and closeout

Select the smallest justified set of changes. Run both technical and policy holdouts,
verify persisted graph contributions and SQL vector retrieval, and compare against a
same-session baseline. Explicitly accept or reject each experimental flag. Document
unresolved quality limitations, cold versus repeat timings, and realistic operational
settings. Keep unsupported changes off and consolidate experimental code only after
the final decision. Do not call the evals finished merely because smoke gates pass.

### Deferred architectural alternatives

Searchable chunks before asynchronous graph enrichment could improve time to first
search, but it does not reduce time to a fully enriched graph. Noun-phrase/co-occurrence
or lazy graph approaches similarly change the quality/readiness contract. Revisit these
only if that contract changes; they are not required to complete the current shortlist.

## Resume commands

Run from the repository root. First inspect current changes/configuration and read the
latest experiment report; credentials and deployment mappings may have changed.

```powershell
dotnet run --project src/FabrCore.Services.GraphRag.Tests --no-restore -- --filter FullyQualifiedName~Unit
```

Current general policy-corpus control (no experimental policy vocabulary):

```powershell
dotnet run --project src/FabrCore.Services.GraphRag.EvalConsole -- run --model graphrag --manifest src/FabrCore.Services.GraphRag.EvalConsole/corpus-policy-v2.json --max-chars 0 --response schema --sections-per-batch 8 --document-concurrency 2 --taxonomy-names current --relations current --endpoint-aliases off --result-cache off --taxonomy-cache off --embedding-cache off --reingest fresh --iterations 1
```

For the next retry experiment, the relevant existing comparison arms use `--relations
policy` and `--relations policy-obligation`; targeted repair does not yet exist as an
option. Keep all other settings fixed. Build before using `--no-build`; avoid building
while a live eval holds DLLs open on Windows. Run live comparison arms sequentially,
alternating their order, rather than competing for provider capacity.

Use `IngestionWallMs` for parallel batch time, not the sum of document durations.
Preserve failed reports, distinguish infrastructure failures from fact failures, and
inspect final artifacts after their writers finish. Update this document with each
completed experiment and its decision when work resumes.
