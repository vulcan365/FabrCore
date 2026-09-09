# Memory evaluation checkpoint — September 7, 2026

The first accepted **benchmark version 2** matrix passed **208/208 checks** across two
iterations per mode, using Azure `gpt-5.4-mini` and `text-embedding-ada-002` (1536 dimensions).
These are FabrCore regression checks, not a published industry benchmark score.

## Accepted run records

All paths below are relative to `artifacts/memory-evals/`, with `report.json` inside the run
directory. Reports and uniquely scoped SQL data are retained locally; artifacts are ignored
by Git. The tracked baseline registry is
`src/FabrCore.Services.Memory.EvalConsole/baselines.json`.

| Mode | Run ID | Checks | Chat calls | Input/output tokens |
| --- | --- | ---: | ---: | ---: |
| Code calls | `20260907T140055-55938e044c1149ac93397f70d358bb7d` | 34/34 | 14 | 12,200 / 516 |
| Plugin AIFunction calls | `20260907T140117-132fa43da08e43a19cdce5bf4626d268` | 34/34 | 14 | 12,410 / 506 |
| Extraction | `20260907T140138-25abcc34b71746568e9ecf3b4f1be740` | 34/34 | 30 | 29,474 / 2,231 |
| Compaction | `20260907T140222-e4114e6e052f4ccc9ccf31c541530aab` | 36/36 | 18 | 24,006 / 2,388 |
| FabrCore harness answers | `20260907T140257-b55e83fa4be0472da575226ccb92cca4` | 34/34 | 31 | 36,655 / 848 |
| Business holdout, code calls | `20260907T140337-20b6bf1ccd6b485ea6d3f7b502213202` | 36/36 | 16 | 16,424 / 643 |

The 208 checks comprise 86 question-level checks, two compaction checks, and 120 ingestion/
lifecycle checks. Harness questions score final answers; other modes score retrieved content.
The modes do different work, so their token/call totals are not competing efficiency scores.
Provider-reported tokens exclude embedding tokens. Embedding calls/input sizes and timing are
stored separately in each report. These data do not justify dollar-cost claims.

Compaction reduced a generated 41-message history to four messages in both passes:
**3,622 estimated tokens → 725 and 777**, while preserving the `RUN-482` checkpoint from
the discarded prefix. Estimates use FabrCore's estimator, not provider billing counts.
The transport was an in-process history stand-in; SQL memory and model calls were real.

## What the experiments changed

1. The initial development run `20260907T133909-715502eaface464e97d4de33a0a84243`
   passed 16/17 checks. The procedure failure came from a mistyped model-returned GUID.
   Relevance responses now use a schema restricted to supplied IDs.
2. The next development run `20260907T134121-0ad63bfc9f36485ca9eadb93499d93ca`
   passed 33/34. A historical question missed its snapshot. Previously the header carried
   only the title when no description was supplied. Bounded content previews and explicit
   historical-selection guidance expose dates and retain relevant snapshots.
3. Empty model selections previously fell back to recent memories; small pools skipped
   selection. Deterministic tests now require genuine empty results for irrelevant questions.
4. The first compaction smoke check was too weak: a summary and a retained tail checkpoint
   did not prove useful compaction. Version 2 requires actual history reduction and retention
   of a checkpoint from the discarded prefix. Production compaction also refuses replacements
   that would expand the history.

These observations motivated narrow fixes. They are not statistically powered A/B results.
Do not compare version-1 compaction scores with version 2. The interrupted extraction run
`20260907T134442-e2576ebed5b9462ab137c6e392ba108e` is incomplete and excluded; its
checkpoint may still say `running` because the process was stopped.

## Validation

- **81/81 Memory tests passed**, including 72 deterministic tests, seven real SQL integration
  tests and two live-model quality evaluations. None were skipped.
- **56 SDK harness/internal-agent/compaction tests passed; one live orchestration test skipped.**
  Do not count that skipped test as live background-agent end-to-end validation.
- Eval console builds with zero warnings/errors. The actual comparison command accepted
  the code/tool reports with no regressions. Comparison unit tests reject incomplete,
  incompatible, empty and missing/duplicate check sets.
- Scoped write-policy tests require both a fixed memory tool binding and explicit
  `ConcurrentWithMemory`; existing read-only policies and external mutation rejection remain.
- The repeat runner was verified with two code-mode iterations on the final build: **34/34
  checks passed**, and baseline comparison returned success with no regressions. Its matrix is
  `artifacts/memory-matrices/20260907T140747-2a0a2f6cddcb47f0b17b375a071bc458/matrix.json`.

Reports fingerprint the binaries actually used. Later scope-length validation and placement
of memory before context compaction were covered by the final source test run; the initial
accepted reports remain immutable and are not relabeled as a different binary snapshot.

## Foundation hardening follow-up

The next implementation pass completed before rerunning the matrix:

- `WithMemoryLifecycle` configures recall, optional tools and a history-specific compaction
  callback on proxy-created harnesses. Manual `OnCompaction` overrides are no longer required.
  Duplicate tool names fail configuration rather than reaching the model.
- SQL facade save/update/forget and extraction now use atomic entity/chunk/index transactions
  and shared-scope application locks. Independent store instances, rollback after partial work,
  and cancellation are covered against real SQL. Explicit corrections serialize in lock order.
- Extraction receipts survive fresh service instances, skip completed transcript retries and
  prevent a replay from recreating a forgotten entry. Failed batches roll back and can retry.
- Derived summary trees are invalidated transactionally after knowledge changes. Consolidation
  and admin scope deletion share the scope lock. The internal storage-version metadata no longer
  prevents normal entity matching and deduplication.
- Actual FabrCore background dispatch now exercises bounded specialists invoking memory save
  and recall tools, including storage errors and cooperative timeout cancellation. These use
  scripted chat responses and a substituted memory service; they prove runtime wiring, not live
  model task quality or Orleans process recovery.

Validation for this pass: **92/92 Memory tests passed**, including real SQL and two live-model
quality tests; **56 SDK harness/internal-agent/compaction tests passed, one existing orchestration
test skipped**. No skips in the Memory suite. See the design review for the precise transaction,
receipt and maintenance limits; low-level custom stores do not inherit SQL facade guarantees.

The subsequent two-iteration matrix passed **208/208 checks**, with successful comparisons
for all six modes against the original accepted baselines. Its retained result is
`artifacts/memory-matrices/20260907T144142-d1fc1d909b0c4dbc81f8282a0e036759/matrix.json`.
Per-mode scores and chat-call counts match the original matrix. This is regression evidence,
not a statistically established latency or accuracy improvement. Baselines were not promoted
or overwritten; each candidate report preserves its own binary fingerprints.

## Decisions and next benchmark work

The [selection-verifier follow-up](memory-selection-verification-experiment.md) added a conditional
second pass over selected headers and expanded the stress fixture with multi-memory questions.
All 54 stress checks and 312 paired SQL/harness checks passed. One trace shows removal of an
actual extra memory while all six genuine multi-memory selections in that variant were preserved.
Verified compact selection reduced retrieval input tokens by 23–26% and full-harness input by
4.9%, with additional calls. All 103 Memory tests passed. The profile remains opt-in because
the contemporary unverified controls also passed and these data do not establish general superiority.

The subsequent [compact selection experiment](memory-selection-experiment.md) passed all
156 checks per variant on the small SQL/harness matrix and reduced retrieval input tokens by
31–34%. A stricter 200-header selector test exposed extra-memory selections (GUID 11/12,
compact 10/12). A separate minimal-set prompt scored 12/12 and 11/12 respectively. Both
settings remain experimental opt-ins; neither was promoted to a production default. Failed
stress results and all original baselines are preserved.

Adopt both APIs, bounded harness recall, explicit core/own composition, opt-in scoped memory
writes for background specialists, safer extraction/compaction failure behavior and structured
relevance selection. Retain existing SQL storage and taxonomy. See
[the architectural review](memory-harness-design.md) for the industry/source analysis.

Continue using `scripts/Run-MemoryEvals.ps1`; it runs modes sequentially, compares available
baselines and retains a matrix result without automatically promoting candidates. No unattended
recurring job has been installed. The next sessions should prioritize:

1. Sustained contention and crash tests for the new shared-scope transaction/receipt behavior.
   Serialization prevents interleaved writes but does not decide which competing fact is true.
2. Much larger, independently labeled histories with old facts beyond the header cap, growing
   distractors, genuine supersession and provenance. The business holdout is a small additional
   domain fixture, not an independently curated external benchmark.
3. Extend the passing scripted background execution tests to live-model, real-SQL workloads
   with independent answer labels, failure and cancellation during longer trajectories.
4. Process-crash fault injection against real Orleans persistence. Compare FabrCore's run-level
   session snapshots with the latest framework's explicit per-service-call persistence path.
5. Storage/receipt retention, durable scheduled consolidation and memory-context cost
   under sustained workloads. Memory is one part of long-running reliability, not an endless
   execution guarantee.

Preserve these baselines and collect contemporary controls before changing defaults further.

## Long-history candidate experiment, September 7

The new `long-history` command tests 1,200 stored memories with all five target facts outside
the recent-200 header cap. Across three repetitions, recent-only recall passed 3/24 exact
checks (abstention only); the opt-in semantic-20 plus recent-20 pool passed 24/24. Gross
selection input fell 77.3%, with 24 chat calls in either variant and 24 extra query embedding
calls for the candidate. Heavy control caching means this is not a measured dollar saving.
The fixture is synthetic and seeds storage directly; independent labels, actual correction
trajectories and crash/restart continuity remain open work.

Contemporary harness runs passed 34/34 in both variants with no comparison regressions;
there was no input-token saving on that small corpus. All 107 Memory tests passed.
`UseSemanticCandidates` remains false by default, and the baseline registry is unchanged.
See [the experiment record](memory-long-history-experiment.md) for retained reports,
the invalid initial fixture run, costs, implementation limits and reproduction commands.

## Correction and candidate-budget experiment

The `corrections` command applies public API updates to old facts among 240 same-subject
distractors. Over three repetitions, recent-only recall passes 9/24; both semantic 20+20
and 8+8 pass 21/24. Both correctly return updated content and preserve historical answers,
but omit the procedure on every combined region-and-procedure question. A diagnostic
repetition confirms the procedure is absent from both candidate pools before selection.
The smaller pool uses 50.2% less gross selection input, with different cache behavior;
neither semantic variant passes the full quality gate. No defaults or baselines were changed.
See [correction results](memory-correction-experiment.md). Next: bounded multi-part query
retrieval, evaluated against this retained failure rather than another selector-only tweak.

## Bounded candidate diversity

An opt-in `DiversifySemanticCandidates` now interleaves SQL candidates across memory types
within the existing semantic limit. The contemporary four-variant correction run retained
the known failures: recent-only 9/24, semantic 20+20 and 8+8 each 21/24. Diverse 8+8 passed
24/24, recovering the procedure in every combined-question repetition. Gross selection input
was 24,624 versus 50,199 for 20+20, with 24 chat and query-embedding calls each. Cache usage
differed, so no dollar-cost claim is made. All 109 Memory tests passed. Defaults and baseline
registrations remain unchanged. See [results and limits](memory-diverse-candidates-experiment.md).

Contemporary semantic-8+8 harness checks passed 34/34 both with and without diversity; the
comparison reported no regressions. Total harness calls increased 31 to 32 and input tokens
33,639 to 35,647, so selection savings do not establish end-to-end savings on the small corpus.

## Same-type evidence regression

A new `same-type` fixture asks for five Fact memories amid distractors from four types.
Across three repetitions, ordinary 8+8, ordinary 20+20 and diverse 20+20 each passed 21/21;
diverse 8+8 passed 18/21. It omitted three required facts before selection on every combined
request. It also used 5.0% more gross input than ordinary 8+8 on this fixture. This blocks
promotion of the previously successful diverse 8+8 configuration. No production retrieval
code or defaults changed. Next candidate work should test a hybrid allocation against both
same-type and mixed-type evidence. See [the retained results](memory-same-type-experiment.md).

## Hybrid candidate allocation

The opt-in hybrid strategy reserves five closest matches within an eight-slot semantic
budget and fills three remaining slots across types. Contemporary runs passed 24/24 on
the correction fixture and 21/21 on the same-type fixture (45/45 combined). Ordinary
semantic retrieval still misses the procedure; diversity-only still loses same-type facts.
All three controls scored 42/45 combined. Hybrid used 47,160 gross selection input tokens
versus 98,268 for ordinary 20+20 (52.0% lower), with 45 selection and query-embedding calls
each. Caching differed; no dollar-cost claim is made. All 112 Memory tests passed. Defaults
and registered baselines remain unchanged. See [hybrid results](memory-hybrid-candidates-experiment.md).

The contemporary hybrid harness comparison passed 34/34 in each variant with no regressions.
Gross input was 32,242 versus 31,096 and total chat calls 30 versus 29; the small paired run
does not establish repeatable end-to-end savings. The next validation should use independently
labeled histories and broader workloads before promoting a retrieval strategy.

## Sparse-header experiment

The new `sparse-headers` command compares rich, topic-only and opaque stored headers over
the same semantic content and embedding inputs. Across three repetitions, rich and topic-only
headers both pass 21/21 for ordinary and hybrid 8+8. Topic-only descriptions use about 19%
fewer selection input tokens without answer values in the headers. Generic headers fail:
ordinary 5/21, hybrid 6/21, with required evidence present before selection in diagnostic
checks. Ordinary recall also returns unrelated evidence for two unknown queries. An initial
opaque seeding failure was retained separately and excluded from quality results.
No production code or defaults changed; all 112 tests passed. Next: bounded body previews
or description repair for vague headers, with independently labeled validation before
generalizing the successful topical-header result. See [results](memory-sparse-headers-experiment.md).

## Bounded selection previews

An opt-in 160-character primary-body preview raises opaque-header checks from 5/21 to 21/21
across three repetitions. Topic-only headers remain 21/21 with or without previews. Gross
selection input increases 38.3% for opaque headers and 46.2% for topic headers; previews are
a quality/cost tradeoff rather than a blanket token optimization. The service shares a
4,096-character preview budget across candidates and leaves stored headers unchanged.
All 114 Memory tests passed. Defaults and registered baselines remain unchanged. See
[preview results](memory-selection-preview-experiment.md).

The contemporary harness comparison passed 34/34 both with and without previews, with
31 chat calls each. Gross input increased 6.9% (33,807 to 36,139), with no measured quality
gain on that corpus. Preview mode remains off by default.

## Evidence beyond the preview window

The `body-position` experiment moves each fact to offset 145 and adds long trailing content.
Across three repetitions, no preview passes 7/21, 160-character previews 11/21, and
256-character previews 21/21. The larger preview uses 19.2% more selection input than 160
characters; candidate diagnostics show required IDs were present even on failed checks.
The shorter preview also fails abstention twice. No preview-size default or baseline changed.
Next: query-relevant excerpts and non-primary chunk evidence rather than assuming a fixed
prefix is sufficient. See [results](memory-body-position-experiment.md).

## Non-primary chunk evidence

The new `multi-chunk` fixture stores the fact in chunk 1 behind a generic chunk-0 overview.
Both no-preview and 256-character-preview variants select correct IDs on 21/21 checks but
return correct body evidence on 0/18 known-answer checks. Only three abstention checks pass.
The facade's load helper fetches the primary chunk after selection, even though candidate
ranking searches across chunks. Larger primary previews add cost without fixing this.
All 114 existing tests pass, exposing an untested multi-chunk read-path gap. No production
code/defaults or baseline registrations changed. The next implementation should preserve
matched-chunk evidence and provenance within explicit content limits; the failing fixture
is retained as its control. See [results](memory-multi-chunk-experiment.md).

## Matched-chunk evidence

The opt-in matched-chunk read path uses the query's closest embedded chunk for previews and
selected body loading. It passes 42/42 combined checks under rich and opaque headers, versus
6/42 for primary-chunk controls. Checks require body content and the expected chunk ID/index;
all 36 known-answer bodies are recovered and all six unknown checks abstain. Selection input
rises about 0.6% with unchanged model/embedding call counts. Returned bodies have explicit
source and truncation fields, capped at 4096 characters each and 12000 total for selected
entities. All 116 tests passed. Primary retrieval/update semantics, defaults and baseline
registrations remain unchanged. See [results and limits](memory-matched-chunks-experiment.md).

The contemporary harness comparison passes 34/34 in both variants with no regressions.
Input was 37,679 versus 36,459 and calls 32 versus 31; this small run is not a repeatable
savings claim. Next chunk validation should cover several required chunks within one entity.

## Several chunks within one entity

The `chunk-coverage` fixture confirms that the current one-chunk return limit loses required
evidence: individual facts pass 30/30 across both variants, while combined requests pass
0/12. No-preview recall passes 18/24 overall; 256-character previews pass 16/24, including
two unknown-answer false positives. Previews add 35.7% gross selection input without fixing
coverage; cache differences preclude a billed-cost claim. The failing control is retained
for bounded multi-chunk retrieval with per-chunk provenance and a shared content budget.
Defaults and baseline registrations remain unchanged. See [results](memory-chunk-coverage-experiment.md).

## Bounded multi-chunk candidate

The opt-in `MatchedChunksPerMemory` extends matched recall to at most eight nearest chunks
per entity, sharing the existing body budget and exposing per-chunk provenance. On the
grouped fixture, five chunks pass 24/24 versus contemporary one-chunk 17/24 and three-chunk
20/24 controls. Selection input is identical, but single-fact context grows 68.6% with five
chunks, so one remains the default. All 118 Memory tests pass. Next evaluate selective chunk
loading with distractors inside the same entity; fixed-K success here is not a completeness
guarantee. See [results and limitations](memory-bounded-chunks-experiment.md).

The contemporary harness control and candidate both pass 34/34 with no comparison
regressions. That single-chunk corpus verifies compatibility, not multi-chunk completeness.

## Selective chunks with internal distractors

The new fixture adds three sandbox chunks inside the production configuration entity.
Selecting from eight bounded chunks passes 24/24 strict checks with complete requested
evidence and no excess chunks. Fixed-eight retains all requested bodies but returns 140
unrequested chunks across the run. Five-chunk selection passes 21/24: it cannot restore the
production owner excluded from the candidate pool on combined questions.

Selected-eight returns 53.4% less formatted context than fixed-eight but consumes 51.1%
more selection input tokens (45 versus 24 chat calls). This establishes a relevance/context
tradeoff, not end-to-end token savings. `SelectMatchedChunks` stays off by default; retained
baselines are unchanged. All 119 Memory tests pass. See [results](memory-selective-chunks-experiment.md).

The contemporary harness comparison passes 34/34 in both modes; chat calls increase from
31 to 42 and input from 34,623 to 37,888. Next reduce redundant selection work while retaining
the strict internal-distractor controls.

## Avoiding redundant chunk selection

The opt-in `SkipRedundantChunkSelection` bypasses the second selector only when every returned
entity has one complete, nonempty chunk exactly matching its original selection description
and unchanged header metadata. The contemporary three-iteration harness pair passes 51/51
in both modes. It removes 18 memory selector calls (39 to 21); total gross input falls from
59,929 to 49,629, or 17.2%, with some of that difference due to other model-call variation.

The multi-chunk guard passes 24/24 in both modes with identical returned context and zero
excess chunks; all known-answer requests retain both selectors. All 126 Memory tests pass.
The shortcut remains off by default and baselines are preserved. See [results and limits](memory-redundant-selection-experiment.md).

## Shorter second-stage evidence previews

The `chunk-preview` fixture pads target chunks to 450 characters and alternates facts between
offsets zero and 145. A 256-character selection prefix passes 24/24 strict checks, matching
the full-body control's returned evidence and context size. Second-stage input on known-answer
questions falls 9.8%. Observed total input falls 6.3%, partly because an unknown question
requires an additional selector call in the control. The 128-character prefix passes 15/24,
misses evidence and returns 20 unwanted chunks, increasing total returned context.

`ChunkSelectionPreviewCharacters` remains zero by default. Selected bodies retain their
existing size and provenance limits; selection prefixes do not redefine source truncation.
All 127 Memory tests pass. Preserve these controls before testing deeper facts and late
corrections. See [results and limitations](memory-chunk-preview-experiment.md).

The contemporary harness pair passes 34/34 in both modes, with 44 chat calls each and no
comparison regressions. Its small input difference is not a repeatable savings claim.

## Late corrections beyond a selection prefix

The `late-correction` fixture places a rejected draft value near the start of a chunk and
its rejection at character 320. Full-body selection passes 24/24; 256-character previews
pass 21/24 and surface three rejected drafts; 384-character previews pass 22/24, surface two
drafts and miss current evidence once. The latter uses slightly more second-stage input on
known-answer questions than full bodies. Authentic provenance does not prevent these relevance
failures. Keep full bodies as the default and retain both failing prefix guards. Production
code and baseline registrations are unchanged. See [results](memory-late-correction-experiment.md).

## Beginning/end selection windows

The opt-in split preview shows 144 characters from each end of the bounded body. It passes
24/24 late-correction checks with 7.4% less second-stage input than full bodies, but only
13/24 on the middle-fact guard. Full-body selection passes 48/48 across both fixtures; a
288-character prefix passes 40/48, and split windows 37/48. The window's smaller middle-case
context reflects missing required evidence. Keep full bodies as the default and preserve
both fixtures as guards. All 130 Memory tests pass; no baseline promotion occurred.
See [results and limits](memory-chunk-windows-experiment.md).
