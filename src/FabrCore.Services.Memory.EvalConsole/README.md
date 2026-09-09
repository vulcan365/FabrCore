# Memory evaluation console

Runs the production Memory services against an existing SQL VECTOR database and configured
FabrCore models. No web host is needed. Each run/pass uses a new scope; reports and SQL rows
are retained. This never creates/drops a database or cleans unrelated scopes.

## Configuration

Create the ignored `appsettings.local.json` in this project:

```json
{
  "ConnectionStrings": { "MemoryEvalDb": "<existing evaluation database connection>" },
  "Eval": { "ModelConfigurationPath": "C:/path/to/fabrcore.json" }
}
```

Environment variables override local settings, for example `ConnectionStrings__MemoryEvalDb`.
`--models` overrides the model file; `--model` selects the chat alias (default `default`).
The file must also define `embeddings`. The console probes embedding dimensions before
initializing the `mem` schema. Existing VECTOR columns must have the same dimensions.
This checkout's ignored local settings reuse the existing GraphRag evaluation database;
Memory uses its separate `mem` schema. No GraphRag data is changed.

## Run and compare

From the repository root:

```powershell
dotnet run --project src/FabrCore.Services.Memory.EvalConsole -- run --mode code --iterations 2
dotnet run --project src/FabrCore.Services.Memory.EvalConsole -- run --mode tools --iterations 2
dotnet run --project src/FabrCore.Services.Memory.EvalConsole -- run --mode extract --iterations 2
dotnet run --project src/FabrCore.Services.Memory.EvalConsole -- run --mode compact --iterations 2
dotnet run --project src/FabrCore.Services.Memory.EvalConsole -- run --mode harness --iterations 2
dotnet run --project src/FabrCore.Services.Memory.EvalConsole -- compare --baseline artifacts/memory-evals/BASE/report.json --candidate artifacts/memory-evals/CANDIDATE/report.json
```

| Mode | What is exercised | Question score |
| --- | --- | --- |
| code | Save/recall through `IAgentMemoryService`, then rebuild the DI graph | Required evidence in retrieved content |
| tools | Actual `AIFunction` invocation of the plugin's save and recall tools | Same retrieval score; tool calls are scripted, not model-selected |
| extract | Live extraction of each source session, SQL persistence, fresh-service recall | Same retrieval score |
| compact | Production memory-aware compaction over an in-process history transport, then fresh-service recall | Same retrieval score plus history reduction and checkpoint retention |
| harness | Real `FabrCoreHarnessAgent` with the memory context provider and live model | Required evidence in the final answer; exact `UNKNOWN` for abstention |

Compaction mode stores its generated trajectory beside the report. Repeated neutral progress
messages create context pressure; a checkpoint in the discarded prefix must survive in the
summary. This exercises real SQL, embeddings, extraction and summarization, but the history
transport is a test stand-in for Orleans. It does not prove process-crash recovery.

Every mode also checks core/own isolation, layered indexes, protected core deletion,
correction, archive, restoration and forgetting. These lifecycle checks are deterministic
SQL checks, not additional model-quality questions. The seven bundled questions cover
preferences, current facts, procedures, multi-session evidence, continuation, history and
abstention. `corpus-holdout.json` provides a separate business-domain check:

```powershell
dotnet run --project src/FabrCore.Services.Memory.EvalConsole -- run --mode code --iterations 2 --manifest src/FabrCore.Services.Memory.EvalConsole/corpus-holdout.json
```

Reports include corpus SHA-256 and source copy, benchmark version, effective memory options,
chat/embedding model identities, embedding dimensions, library binary fingerprints, scopes,
per-check timing/content, chat calls/responses/provider tokens and embedding work. Unknown
token usage remains null. There is no invented dollar cost. Calls from failed operations
remain in the final report; abrupt process termination can leave a partial checkpoint with
`Status=running`, which must be excluded.

`compare` rejects incomplete runs, changed corpus/version/iteration counts, missing/duplicate
checks and mixing harness answer scores with retrieval scores. It reports each regression
and returns a failing gate if the candidate has any failed check. Changes to models/options
are experiments: inspect those fields before attributing results to code. Keep all but the
intended variable fixed. Compaction has an extra gate, so compare compact with compact.

Exit codes: 0 all checks pass, 1 configuration/infrastructure error, 2 quality gate fails,
130 cancellation. Some `dotnet run` wrappers normalize child nonzero codes; inspect the
report and run the built executable directly when exact exit codes matter.

## Continuing benchmarks

Run `./scripts/Run-MemoryEvals.ps1` for the sequential two-pass matrix plus comparisons
against `baselines.json`. Use `-Modes code,tools` for a shorter run. Baseline files are retained
local artifacts; a fresh clone warns when they are unavailable and still runs its own gates.
The script writes a separate `matrix.json`, never promotes a candidate automatically, and
returns failure for either failed runs or failed comparisons. Comparisons require matching
iteration counts, so provide corresponding baselines when changing `-Iterations`.

Preserve the first accepted version-2 report per mode as a baseline. Run code and tools
after service/tool changes, compact after lifecycle changes, and harness after context or
framework changes. Run the holdout before accepting a retrieval change. Use sequential,
alternating A/B order for timing comparisons; simultaneous provider work confounds latency.
Never replace a baseline with an interrupted or failed run. Keep historical reports even
when a corpus or scorer is improved; do not compare different benchmark versions.

This is a small FabrCore regression suite, not a LongMemEval/LoCoMo score. Substring checks
measure selected evidence and can miss wrong relationships, negation and reasoning errors.
Two repetitions do not establish statistical superiority. Next quality work should add
independently labeled longer histories, distractor growth, meaningful corrections, concurrent
writers, restart fault injection and a separate semantic answer judge with manual auditing.

## Selection token experiment

`scripts/Run-MemorySelectionExperiment.ps1` runs paired GUID/compact-label variants on the
main fixture, holdout and harness. `--compact-ids true|false` exposes the same setting for
individual console runs; the default is false. The `selection-scale` command tests exact
selection over 200 synthetic headers in shuffled orders, without SQL or embeddings. It is
a selector stress fixture, not an independent long-memory benchmark. `--minimal-selection true`
tests a separate smallest-sufficient-set prompt; it also defaults to false. Both switches are
experimental: the large-header test still showed extra-memory selections. See
[the experiment record](../../docs/memory-selection-experiment.md) for measured savings and limits.

`--verify-selection true` adds a conditional second selection pass. In `selection-scale` it
adds a third, verified compact variant; the version-2 fixture also includes multi-part and
date-comparison questions with exact expected sets. In ordinary `run` mode it enables the
verifier on that run. The paired script's `-VerifySelection` switch enables it on the compact
candidate only. See [verification results](../../docs/memory-selection-verification-experiment.md).

## Existing tests

`multi-chunk --iterations 3 --header-mode rich|opaque --matched-chunks true` compares primary
and query-matched preview/body loading and checks chunk provenance. See [matched chunk results](../../docs/memory-matched-chunks-experiment.md).

`multi-chunk --iterations 3` stores facts in chunk 1 behind a generic primary chunk and
scores selected IDs separately from loaded body evidence. See [multi-chunk results](../../docs/memory-multi-chunk-experiment.md).

`body-position --iterations 3` compares 0-, 160- and 256-character previews with the fact
beginning at body offset 145 and a long trailing section. See [positional results](../../docs/memory-body-position-experiment.md).

`sparse-headers --header-mode opaque --selection-preview 160 --iterations 3` compares hybrid
8+8 without previews to the same retrieval with bounded primary-body prefixes. Use `topic`
to measure overhead where headers already suffice. See [preview results](../../docs/memory-selection-preview-experiment.md).

`sparse-headers --iterations 3 --header-mode rich|topic|opaque` compares ordinary and hybrid
8+8 while changing only stored header informativeness across isolated runs. Full memory
bodies and embedding inputs are retained. See [sparse-header experiment](../../docs/memory-sparse-headers-experiment.md).

`corrections` and `same-type` accept `--hybrid-candidates true` to compare a hybrid 8+8 pool
against ordinary 20+20, ordinary 8+8 and diverse 8+8. In ordinary runs, combine it with
`--semantic-candidates true`; it is mutually exclusive with `--diverse-candidates true`.
See [hybrid experiment](../../docs/memory-hybrid-candidates-experiment.md).

`same-type --iterations 3` tests five required Fact memories among distractors of four types,
comparing ordinary and diverse 8+8 and 20+20 candidate pools. It retains failures and separate
candidate-coverage diagnostics. See [same-type experiment](../../docs/memory-same-type-experiment.md).

`corrections --iterations 3 --diverse-candidates true` adds an 8+8 candidate pool interleaved
across memory types, alongside all three original controls. In ordinary `run` mode, combine
`--semantic-candidates true --diverse-candidates true`; `--candidate-budget 8` sets both limits
to eight. See [candidate-diversity results](../../docs/memory-diverse-candidates-experiment.md).

`corrections --iterations 3` applies real corrections to old memories amid 240 same-subject
distractors and compares recent-only, semantic 20+20, and semantic 8+8 recall. Failed semantic
checks include separate candidate-coverage diagnostics. See [correction results](../../docs/memory-correction-experiment.md).

`long-history --iterations 3` compares the recent-200 scan with a bounded semantic candidate
pool over 1,200 SQL-backed synthetic memories. It records query embeddings separately from
batch ingestion, preserves failing controls, and requires all candidate checks to pass.
For ordinary corpus/harness runs, `--semantic-candidates true` enables the same opt-in path.
See [long-history results and limitations](../../docs/memory-long-history-experiment.md).

Memory tests discover this project's ignored local settings when environment overrides are
absent. `FABRCORE_MEMORY_TEST_CONNECTION_STRING`, `FABRCORE_MEMORY_TEST_PASSWORD`, and
`FABRCORE_MEMORY_TEST_MODELS` take precedence.

`chunk-coverage --iterations 3` stores five required facts in separate chunks of one entity
and compares matched recall with 0- and 256-character previews. It checks complete requested
body coverage and per-chunk provenance, retaining failing controls for combined questions.
See [chunk coverage results](../../docs/memory-chunk-coverage-experiment.md).

Add `--chunks-per-memory 5` to compare one-, three- and five-chunk body loading with no
previews, rotating order across repetitions. Reports retain body/context character counts,
returned chunk counts and per-chunk provenance; only the largest limit gates this comparison.
Ordinary `run` accepts the same option with `--semantic-candidates true --matched-chunks true`.
See [bounded chunk results](../../docs/memory-bounded-chunks-experiment.md).

`selective-chunks --iterations 3` adds three sandbox distractor chunks to the production
configuration entity and compares fixed versus selected pools of five and eight chunks.
Strict success requires complete requested evidence, provenance and zero excess chunks.
Reports also retain coverage and excess counts separately. Only selected-eight gates the run.
Ordinary `run` accepts `--select-chunks true` with matched semantic recall and a chunk limit
above one. See [selective chunk results](../../docs/memory-selective-chunks-experiment.md).

`run --select-chunks true --skip-redundant-selection true` enables the conservative duplicate
evidence shortcut with the existing matched semantic options. `selective-chunks --iterations 3
--skip-redundant-selection true` compares always-select and shortcut policies using eight-chunk
pools; both remain gated on strict evidence coverage and relevance. No shortcut is expected
for that multi-chunk fixture. See [shortcut results](../../docs/memory-redundant-selection-experiment.md).

`chunk-preview --iterations 3` compares full bounded bodies with 128- and 256-character
second-stage previews over 450-character chunks. Facts alternate between offsets zero and
145. All variants select from eight chunks with no shortcut or first-stage previews; only
256 gates this experiment. Ordinary `run` accepts `--chunk-selection-preview N` with chunk
selection enabled. See [chunk preview results](../../docs/memory-chunk-preview-experiment.md).

`late-correction --iterations 3` tests misleading draft values whose rejection appears at
character 320. It compares full bounded bodies with 256- and 384-character selection previews,
requiring current production evidence and excluding rejected drafts. Only 384 gates this
experiment; all controls are retained. See [late-correction results](../../docs/memory-late-correction-experiment.md).

`chunk-windows --iterations 3 --window-fixture late` compares full bodies, a 288-character
prefix and a 144+144-character beginning/end preview on the late-rejection fixture. Repeat
with `--window-fixture middle` to test facts in the omitted middle. Both commands retain all
controls and gate on the split-window candidate. Ordinary `run` accepts `--chunk-selection-tail
true` with chunk selection and a preview length of at least two. See [split-window results](../../docs/memory-chunk-windows-experiment.md).

```powershell
dotnet run --project src/FabrCore.Services.Memory.Tests -- --filter "TestCategory=Integration"
dotnet run --project src/FabrCore.Services.Memory.Tests -- --filter "TestCategory=Evaluation"
dotnet run --project src/FabrCore.Services.Memory.Tests -- --filter "TestCategory!=Integration&TestCategory!=Evaluation"
dotnet test src/FabrCore.Sdk.Tests --filter "FullyQualifiedName~InternalAgentHardeningTests|FullyQualifiedName~Harness|FullyQualifiedName~Compaction"
```

Integration tests delete only their own uniquely named scopes. Console runs retain theirs.
Detailed architectural decisions and examples: [memory harness design](../../docs/memory-harness-design.md).
