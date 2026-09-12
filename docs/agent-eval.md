# A practical evaluation guide for AI applications

Audience: engineers, product owners, and reviewers establishing repeatable evaluations for
an AI application. Prepared September 12, 2026 from the FabrCore agent-memory evaluation work.

This guide explains how to build an evaluation system, diagnose failures, compare changes,
and decide whether to release. The method applies to chat assistants, retrieval applications,
tool-using agents, and workflows; SQL, .NET, FabrCore, and memory are implementation examples,
not requirements.

**Evaluate the application outcome, its supporting evidence, and its operational behavior
separately. Accept efficiency improvements only after the intended quality gates pass.**

The sections labeled “Memory implementation” describe work actually performed. Recommendations
for another team are not claims that every capability already exists in our evaluator.
No new benchmark runs were performed to prepare this guide.

## 1. Define the application contract first

Write down what the app must accomplish and what counts as a failure before changing its
prompt, model, retrieval, or execution policy.

For each agent class—a group of agents with similar responsibilities and tool contracts—define:

- User outcomes: correct answers, completed tasks, accurate updates, useful continuity.
- Permitted inputs, data scopes, tools, and side effects.
- Required evidence: source passages, record IDs, tool receipts, or independently checked state.
- Recovery rules: which errors permit retries, which require reconciliation, and which stop work.
- Quality and operating limits: unacceptable errors, latency, calls, tokens, time, and budget.
- The environment in which the claim must hold: model, data volume, concurrency, and duration.

Distinguish two meanings of long-running:

1. **Session continuity:** a user returns over days or weeks and expects relevant prior context.
2. **Continuous execution:** an agent keeps taking actions for hours or days without a new user turn.

The first requires memory/history/retrieval tests. The second additionally requires bounded
execution, tool outcome validation, restart recovery, cancellation, and side-effect checks.
A short retrieval evaluation cannot certify a multi-day autonomous workflow.

Choose gates appropriate to the contract. A data-import tool might require schema-valid JSON
and a verified row count; a research tool may legitimately return plain text. Neither exit
code zero nor valid JSON establishes that the task succeeded. Test both recoverable failures
and mandatory stops, including whether a framework turns an exception into a model-visible
tool error and continues.

## 2. Build a small, repeatable evaluation runner

Use a command-line runner or equivalent unattended entry point that exercises production
components with explicit configuration. Keep the scorer outside the app under test.
Make dataset, mode, variant, repetition count, model, and output directory parameters.

Recommended responsibilities:

1. Validate configuration and input manifests.
2. Identify the exact application build, settings, dataset, and scorer.
3. Create isolated state for each run/repetition.
4. Seed inputs or replay the required user/tool trajectory.
5. Execute the application through the selected test boundary.
6. Capture outputs, intermediate evidence, calls, timing, and failures.
7. Score against labels held by the evaluator, not supplied to the answer-producing model.
8. Write a structured report and explicit completion/quality status.
9. Compare only compatible reports and return a meaningful process exit code.

Keep these components separable:

| Component | Responsibility |
| --- | --- |
| Dataset loader | Load immutable cases, expected evidence, and labels |
| App adapter | Invoke the actual service, tools, or agent |
| State fixture | Provision isolated scopes and controlled initial state |
| Instrumentation | Observe work without changing prompts or caching behavior |
| Scorer | Apply versioned correctness and behavior rules |
| Reporter | Preserve raw observations, metrics, and reproducibility metadata |
| Comparator | Enforce compatibility and detect case-level regressions |
| Matrix runner | Run modes/variants and collect a summary without hiding failures |

### Environment setup

Use a non-production evaluation database or sandbox. Give every run/repetition unique
namespaces, accounts, or record prefixes so a candidate cannot read its control's output.
Allow deliberate sharing only inside a test designed to exercise shared state.
Retain a manifest of created resources and define an owner and retention period for cleanup.
Never use an unbounded cleanup command against shared infrastructure.

Store credentials in environment variables or ignored local configuration. Reports should
include sanitized configuration and fingerprints, not credentials or connection strings.
Real transcripts and tool responses may contain sensitive data; de-identify datasets and
restrict access to artifacts before sharing them with another team.

Record the effective configuration, not just command-line overrides:

- Application revision, dirty-worktree indicator or patch digest, built binary hashes.
- Runtime and dependency versions; tool versions where relevant.
- Model provider, deployment/alias and resolved version when available.
- Embedding model and dimensions, if used.
- Prompt/template, tool-schema, dataset, scorer, and benchmark versions or hashes.
- Retrieval limits, context limits, retry/time budgets, and cache configuration.
- Start/end timestamps, run ID, case order, shuffle seeds, and concurrency.

A local alias such as “default” is insufficient to identify a model for a handoff. If a
provider does not expose an immutable model version, record that limitation. Fingerprints
make differences visible; they do not make model output deterministic or automatically
enforce an environment-change gate.

### Memory implementation

The console used production Memory services against an existing SQL VECTOR database and
configured chat/embedding models, without starting a web host. Each pass received a fresh
scope. It probed embedding dimensions before initializing the Memory schema and retained SQL
rows and reports. Later recall rebuilt the dependency-injection graph to avoid relying on
the original service instance.

Reports captured corpus SHA-256 plus a corpus copy, benchmark version, effective options,
model identities, dimensions, Memory/SDK binary hashes, scopes, checks, and call measurements.
This is a useful starting point; the broader environment manifest above is a recommendation.

## 3. Evaluate at several boundaries

Start with cheap deterministic checks, then use real infrastructure and models where they
are needed. Do not combine unlike checks into an “accuracy” percentage.

| Level | Example | What it establishes | What it does not establish |
| --- | --- | --- | --- |
| Deterministic unit | Candidate limits, parsing, scope rules | Local invariants | Live model quality |
| Real persistence | Save/update/rollback with actual database | Storage behavior under tested conditions | Recovery from every process crash |
| Scripted agent integration | Model stand-in invokes tools during a real harness run | Runtime wiring and error propagation | Autonomous tool choice |
| Live component | Real model selects evidence | Quality of that component on labeled cases | Final answer quality |
| Live end-to-end | User input through retrieval, context, model, and tools | Tested application outcome | General reliability outside the workload |
| Fault/soak | Cancellation, outages, contention, process restart | Recovery and sustained behavior | Unlimited execution guarantees |

### Memory implementation: six matrix entries

| Entry | Execution path | Question scoring boundary |
| --- | --- | --- |
| Code | Public save/recall APIs, SQL, then fresh services | Returned memory content |
| Tools | Actual plugin AIFunction save/recall invocation, driven by test code | Returned memory content |
| Extract | Live extraction from source sessions, persistence, fresh-service recall | Returned memory content |
| Compact | Production extraction/summarization over generated history | Returned content plus reduction/checkpoint gates |
| Harness | Real FabrCore harness, memory context provider, live answer model | Final answer |
| Holdout | Code mode on a separate business-domain fixture | Returned memory content |

Every ordinary mode also checked ingestion and lifecycle behavior: core/own isolation,
layered indexes, protection against deleting core memory through an own-write facade,
correction, archive, restoration, and forgetting.

Important boundaries:

- Tools mode used scripted tool calls; it did not measure the model's ability to choose tools.
- Harness mode created a fresh session for each question. It was not a days-long dialogue.
- Compaction used real SQL and model calls but an in-process history transport standing in
  for Orleans. It did not prove process-crash recovery.
- Background specialist tests ran real harness dispatch with scripted model responses and
  substituted memory. They exercised save/recall, failures, and cooperative cancellation,
  not live-model quality with real SQL throughout.

## 4. Design datasets that can expose the expected failure

Begin with a small, human-reviewable corpus for debugging. Expand by failure category and
real user workload, rather than adding many near-duplicate easy questions.

Use stable case IDs and independently specified expected outcomes. Each case should contain:

- Initial state or source history, with source IDs and relevant timestamps.
- User request and the application mode being tested.
- Required facts, relationships, ordered actions, or final state.
- Forbidden claims/actions and expected abstention or clarification behavior.
- Gold evidence IDs/chunks and provenance requirements where applicable.
- Category, difficulty, scope/tenant, and risk classification.
- Allowed recovery behavior and expected side effects for tool workflows.

Generic case example (adapt the schema to your runner; this is not the Memory manifest format):

```json
{
  "id": "project-current-region-01",
  "category": "correction",
  "sources": [
    {"id": "s1", "text": "On Monday, Project Cedar used region-a."},
    {"id": "s2", "text": "On Tuesday, Cedar moved to region-b; region-a is retired."}
  ],
  "query": "Which region should Cedar use now?",
  "expected": {
    "facts": [{"subject": "Cedar", "predicate": "current-region", "value": "region-b"}],
    "evidenceIds": ["s2"],
    "forbiddenClaims": ["region-a is the current region"],
    "abstain": false
  }
}
```

Do not forbid every mention of an obsolete value: “region-a was retired” may be correct.
Labels should distinguish false assertions from legitimate historical explanations.

### Minimum case families

| Family | Cases to include |
| --- | --- |
| Basic correctness | Single fact, preference, rule, procedure |
| Composition | Two sources required; several facts required from one source |
| Temporal reasoning | Current state, historical state, explicit supersession |
| Abstention | Missing fact, irrelevant query, insufficient/conflicting evidence |
| Scale | Relevant old facts beyond normal scan limits; increasing distractors |
| Ambiguity | Same names across projects, similar topics, near-matching values |
| Position | Evidence at beginning, middle, and end; across chunk boundaries |
| Continuity | Checkpoint before discarded history; fresh service/session |
| Scope | Shared reads, private writes, cross-tenant leakage canaries |
| Mutation | Correction, deletion, archive/restore, retries, duplicate submissions |
| Execution | Malformed output, exception, timeout, unknown write outcome, denied action |

### Development set, holdout, and external benchmark

Keep a development set for iteration and a held-out set for acceptance. Once a holdout
failure is used to tune the system, that case becomes a regression case; replenish the
holdout with independently labeled examples. Split by user/project/source or trajectory
where possible, not just by individual questions from the same conversation.

Synthetic fixtures are valuable for controlled tests, such as moving one fact beyond a
header cap. They are not a substitute for representative histories. Avoid unrealistically
helpful titles, identifiers, or wording that leaks the answer to the selector.

The Memory business holdout was a small additional domain fixture, not an independently
curated external benchmark. We did not report LongMemEval or LoCoMo scores. If adopting an
external benchmark, preserve its official task/scoring protocol and report modifications.

## 5. Measure where the pipeline loses information

For retrieval applications, retain enough intermediate information to distinguish:

1. Required evidence was never stored correctly.
2. It was stored but omitted from the candidate pool.
3. It reached the pool but the selector rejected it.
4. The right entity was selected but the wrong chunk/body was loaded.
5. A context cap or preview removed required evidence.
6. The answer model received the evidence but answered incorrectly.

Log candidate IDs, selected IDs, actual loaded chunks, provenance, truncation flags,
formatted context size, and final answers where the test boundary permits. Keep stable
case/source IDs alongside run-specific database IDs so controls can be compared.

Memory experiments added separate candidate-coverage diagnostics after combined questions
failed. That showed a required procedure was absent before selection, so another selector
prompt alone could not fix it. Multi-chunk tests later separated correct entity selection
from correct body loading. These diagnostics were added in specific experiments; they were
not all fields in every ordinary matrix report.

## 6. Define metrics and denominators explicitly

Report raw counts alongside percentages, broken down by case family and execution mode.
Define behavior for empty denominators; do not silently count an empty test set as perfect.

### Quality and evidence metrics

| Metric | Definition / use |
| --- | --- |
| Task success | Tasks meeting all required outcome conditions / attempted eligible tasks |
| Check pass rate | Passed checks / expected checks; separate question, lifecycle, and infrastructure checks |
| Candidate recall | Required evidence present in candidate pool / required evidence |
| Evidence recall | Required evidence delivered to the evaluated stage / required evidence |
| Evidence precision | Relevant delivered evidence items / all delivered evidence items |
| Exact selection | Cases with the exact expected evidence set / eligible cases |
| Complete evidence coverage | Questions receiving every required fact/chunk / known-answer questions |
| Excess evidence | Count of unnecessary returned entities/chunks; optionally per question |
| Answer correctness | Final answers satisfying factual and relationship labels / answer cases |
| Unsupported claims | Unsupported factual claims / evaluated factual claims, with a stated labeling protocol |
| Abstention accuracy | Correct abstentions / unanswerable cases; also report false abstentions on answerable cases |
| Correction accuracy | Correct current-state answers after a real update / correction cases |
| Historical accuracy | Correct answers for the requested prior time / historical cases |
| Provenance validity | Returned citations/chunk IDs that support the associated claim / checked citations |
| Scope violations | Unauthorized cross-scope disclosures or mutations; a separate critical gate |

For an expected set G and returned set R:

```text
Recall    = |G intersect R| / |G|                 when |G| > 0
Precision = |G intersect R| / |R|                 when |R| > 0
Exact     = 1 when G = R, otherwise 0
Excess    = |R minus G|
```

When both sets are empty, score the explicit abstention case. Report no-selection cases
separately rather than letting an arbitrary precision convention inflate the average.
If several evidence sets are equally valid, label those alternatives; one exact set would
otherwise penalize a legitimate answer.

### Operational and efficiency metrics

| Metric | Collection and interpretation |
| --- | --- |
| End-to-end latency | Wall time for the user task, including retries and tool work |
| Stage latency | Ingestion, retrieval, selection, generation, persistence; state timer boundaries |
| Latency distribution | Median and p95 with sample counts; few observations do not support a stable tail estimate |
| Model calls | Calls per stage and task; include failed attempts where observable |
| Input/output tokens | Provider-reported totals by stage/model, plus per-task distribution |
| Cached input/reasoning tokens | Separate provider fields; avoid double-counting subsets |
| Embedding work | Calls, inputs, input characters, latency, failures; billing tokens only if actually available |
| Context size | Retrieved body characters, formatted context characters, and actual model input tokens separately |
| Compaction | Before/after messages and estimated tokens, plus facts/checkpoints retained |
| Retry/recovery | Attempts, recovered tasks, time to recovery, repeated failures |
| Tool result validity | Results satisfying the tool's contract / completed tool invocations |
| Side-effect integrity | Duplicate effects, incorrect state, unknown outcomes, and successful reconciliations |
| Storage growth | Records/chunks/receipts/bytes over a defined workload and period |
| Reliability | Canceled, interrupted, infrastructure-failed, and quality-failed runs separately |

Never substitute zero for unknown token usage. Report known totals plus the number of
calls with missing usage. Separate setup/ingestion cost from query cost: amortization depends
on how often the same data is reused.

Example calculations:

```text
Gross input-token reduction = (control input - candidate input) / control input
Compaction reduction        = 1 - after estimated tokens / before estimated tokens
Observed cost per success   = total measured workload cost / successful tasks
```

For dollar cost, use dated, applicable provider rates, model-specific cached/uncached and
output billing rules, embeddings, and other charged services. State whether retries,
judge calls, storage, and hosting are included. If those inputs are unavailable, report
tokens and calls without inventing a price. Provider pricing and token-category semantics
must be checked when doing that calculation.

### What Memory instrumentation actually captured

Chat instrumentation recorded model alias, elapsed milliseconds, provider input/output tokens,
response text or error type, and later cached-input/reasoning tokens and response-schema name.
The embedding wrapper sat inside the cache decorator to measure requests reaching the provider;
it counted inputs, characters, duration, and failures, not embedding billing tokens.

Per-check reports stored pass/fail, category, milliseconds, and retrieved content/final answer.
Some lifecycle checks used zero for their timing field; those are not measured zero-latency
operations. Advanced experiment reports added candidate/body/chunk/context diagnostics.

The instrumentation borrowed the GraphRag approach of passively observing model calls.
Adding measurement did not enable caching or modify prompts. Audit coverage for your own app:
streaming, delegated agents, background jobs, and provider-internal retries can bypass a wrapper
that only measures ordinary response calls. Sum of call durations is not wall time under concurrency.

## 7. Choose scorers that match the claim

Use deterministic assertions for exact state, IDs, schemas, counts, permissions, and known
evidence sets. They are cheap, explainable, and useful for regression gates.

Memory's initial question scorer used case-insensitive required/forbidden substrings.
Retrieval abstention required no warm memories; harness abstention required exactly UNKNOWN
after trimming, case-insensitively. Later stress tests used exact selected sets and stricter
body coverage, excess-chunk, and provenance checks.

Substring checks have important limits. They can pass an answer with the wrong relationship,
negation, mixed current/historical values, or an out-of-order procedure. “Migration, backup,
health” appearing somewhere does not prove a correct deployment sequence.

For richer applications, add structured fact/state assertions and a separately versioned
semantic rubric. If using a model judge:

- Give it the question, authoritative evidence, rubric, and candidate answer; do not ask it
  to invent ground truth.
- Hide variant identity and randomize presentation order for pairwise judging.
- Specify correctness, completeness, support, temporal meaning, and permitted abstention.
- Require structured judgments and supporting reasons.
- Calibrate against human-labeled answers; audit failures, disagreements, and sampled passes.
- Record judge model/prompt/version, latency, tokens, and malformed judgments.
- Treat judge errors as evaluation failures, not passes; do not mix judge cost into app cost.

A semantic judge with this calibration was recommended follow-up work, not the scoring
method behind the original Memory matrix.

Test the evaluator itself: deliberately supply missing/duplicate checks, interrupted reports,
incorrect IDs, malformed results, unsupported answers, and incompatible versions. Confirm
it rejects them. Fix a bad fixture or scorer transparently, version the benchmark, and rerun
both control and candidate; do not rewrite old scores as though the earlier test never existed.

## 8. Run controlled experiments

Use this sequence for each proposed improvement:

1. **State the hypothesis.** Example: smaller selection labels reduce selector input without
   losing required evidence or adding irrelevant selections.
2. **Declare the variable.** Change one option, prompt component, model, or algorithm at a time.
   If several changes are inseparable, name the bundle and avoid attributing gains to one part.
3. **Predeclare gates.** Specify critical failures, quality tolerance, efficiency target,
   relevant slices, and repetition plan before examining results.
4. **Preserve the control.** Retain its code/build, effective settings, dataset, and report.
5. **Run contemporary pairs.** Use the same cases and ordering within each pair. Alternate
   A/B and B/A sequentially across repetitions; rotate multiple variants. Record seeded shuffles.
6. **Retain all attempts.** Save failures, cancellations, and invalid-fixture runs with their
   status and exclusion reason. Do not retry until one passing sample becomes the reported result.
7. **Diagnose by stage.** Find the earliest divergence from expected evidence or state.
8. **Add a counterexample.** Test the nearby workload on which the proposed shortcut could fail.
9. **Rerun end-to-end controls.** A selector win must survive actual context and answer generation.
10. **Decide and record.** Accept, reject, or leave opt-in; do not automatically promote a passing run.

Keep provider load controlled for latency experiments. Parallel control/candidate calls can
compete for capacity and rate limits. Run a separate concurrency experiment when concurrency
is the intended variable. Record warm/cold cache conditions and do not flush shared caches
without an isolated test plan.

### Repetitions and uncertainty

Memory commonly used two matrix passes and three stress-test repetitions. Those were
practical regression probes, not statistically powered claims of superiority.

For a release decision, select sample size based on task diversity, observed variability,
severity of failure, and the smallest useful improvement. Repeated runs of one question
measure variability on that question; they are not independent new tasks.

Report paired candidate/control outcomes, per-case failure rates, and uncertainty intervals
when sample size supports them. Resample or analyze at the independent task/user/trajectory
level rather than treating every internal check as independent. Plan the analysis before
repeatedly testing candidates against a holdout.

## 9. Lessons from the Memory experiments

These examples explain the method; the figures are scoped regression observations, not
general claims about model performance.

| Observation | Evaluation lesson |
| --- | --- |
| A relevance model returned a mistyped GUID | Validate selection IDs against the supplied candidate set; preserve the raw failure |
| A small corpus passed while a 200-header exact-set fixture selected extras | Score precision and scale, not just required-word presence |
| Old facts lay beyond the recent-header cap in a 1,200-memory fixture | Put relevant evidence outside the easy retrieval path deliberately |
| A required procedure never entered the candidate pool | Instrument candidate coverage before tuning the selector |
| A selected entity's primary chunk lacked the answer | Score loaded body evidence separately from entity selection |
| A shorter prefix hid a late correction | Include supersession and evidence-position tests |
| Beginning/end previews recovered late corrections but missed middle facts | Pair a success fixture with a counterexample that challenges the same optimization |
| Smaller input sometimes came with more calls or different cache usage | Report total work and cache behavior, not a single token percentage |

For example, beginning/end previews passed 24/24 strict checks on the late-correction fixture
but only 13/24 on the middle-fact fixture. Full bounded bodies passed 48/48 across both;
split windows passed 37/48. The smaller returned context on missed questions was lost
coverage, not a useful efficiency gain. See the [experiment record](memory-chunk-windows-experiment.md).

The first accepted version-2 ordinary matrix passed 208/208 checks across two repetitions
per entry. That total combined 86 question checks, two compaction checks, and 120 ingestion/
lifecycle checks. It is not 208 independent correct answers. Harness entries scored final
answers while other entries scored retrieved content, so their token totals were not
competing efficiency scores. See [the checkpoint](memory-eval-status.md).

The compaction test was strengthened to require actual history reduction and retention of
a checkpoint from the discarded prefix. Retaining a fact already present in the untouched
tail would not have established preservation through compaction.

Experimental retrieval changes remained opt-in. The [release-default decision](memory-release-defaults.md)
froze compatibility defaults and kept accepted baselines intact. A freeze is not evidence
that every release, sustained-load, or recovery check has been completed.

## 10. Preserve reports and compare safely

Use one immutable directory per run, then a matrix/experiment file referencing those runs.
Checkpoint during execution and write final status even on ordinary exceptions/cancellation.
Prefer atomic file replacement for checkpoints in a new runner. Abrupt termination may still
leave partial evidence; retain it for diagnosis and exclude it from accepted comparisons.

Suggested generic layout:

```text
evals/
  datasets/development-v1.json
  datasets/holdout-v1.json
  rubrics/answer-v1.md
  baselines.json
artifacts/evals/<experiment-id>/
  experiment.json
  control/<run-id>/
    environment.json
    dataset.json
    report.json
    cases.jsonl
    calls.jsonl
    trajectories/
  candidate/<run-id>/
    ...
```

Suggested report fields (adaptation target, not the exact Memory JSON schema):

| Group | Fields |
| --- | --- |
| Identity | run/experiment ID, start/end, mode, variant, iterations, completion status |
| Reproducibility | dataset/scorer versions and hashes, build, model, sanitized effective config |
| Inventory | expected case/check IDs, executed IDs, missing/skipped IDs |
| Cases | labels, actual answer/state/evidence, score, category, timing, error classification |
| Calls | stage, parent task/span ID, model/tool, duration, tokens, outcome, retry linkage |
| Artifacts | fixture/trajectory references, state scope IDs, raw-output references |
| Decision | quality gate, comparison result, exclusions, reviewer rationale |

Separate **completed execution** from **passed quality**. A completed run may have failing
checks. An interrupted run with all completed checks passing is still incomplete.

The Memory comparator rejects incomplete reports, changed corpus/benchmark/iteration counts,
empty or duplicate check inventories, missing checks, and harness-answer versus retrieval
comparisons. It identifies previously passing checks that failed, and the comparison command
also fails if the candidate has any failed check. It permits some same-boundary comparisons,
such as code versus tools; callers still need to justify their equivalence.

A generic comparator should additionally enforce the declared compatible mode and experiment
manifest. Models and options can intentionally differ, so validate those differences against
the hypothesis instead of silently accepting or rejecting all configuration changes.

Do not let a missing baseline count as a successful comparison. Memory's matrix runner warns
and runs standalone quality gates if a local baseline is unavailable. For a release job,
require accessible compatible baselines explicitly. Its tracked baseline registry references
ignored local reports; give another team the sanitized reports through an approved artifact
store, or have them establish their own baseline.

## 11. Add failure, recovery, and sustained-work evaluations

Quality on a short happy path is insufficient for stateful or tool-using apps.
Create bounded fault scenarios and assert both the immediate response and subsequent behavior.

| Injected condition | Assertions |
| --- | --- |
| Invalid tool output despite successful process exit | Contract failure is detected; mandatory gate prevents dependent dispatch |
| Recoverable read failure | Bounded retry/alternative succeeds or reports failure clearly |
| Permission denial | No retry path bypasses the denied capability |
| Write timeout with unknown outcome | Reconcile actual state before retry; no duplicate business effect |
| Extraction/persistence failure | Original history survives; partial mutations roll back where promised |
| Replayed extraction or submission | Idempotency behavior is verified across fresh service instances |
| Concurrent conflicting updates | No corrupt/interleaved state; documented conflict semantics |
| Cancellation | Work stops cooperatively; persistence and later recovery remain consistent |
| Process crash | Restart from actual durable state; inspect acknowledged/unacknowledged effects |
| Environment/configuration change | Apply the class's declared continue/revalidate/halt policy |
| Long workload | Measure storage, context, tokens, latency, error rates, and cleanup over time |

Use real persistence for recovery claims. Recreating a service in one process does not prove
recovery after a process dies between an external side effect and its checkpoint.
Likewise, signed evidence establishes integrity of a recorded event, not the truth of its
claimed outcome. Test outcome validation and enforcement separately.

Memory covered real-SQL transactions, rollback/cancellation, shared-scope locking, extraction
receipts, and derived-summary invalidation in targeted tests. Open follow-up work included
sustained contention, full process-crash injection, independently labeled longer histories,
and live-model/real-SQL background trajectories. Do not present those as completed based on
the short matrix.

## 12. Use explicit release gates and a stopping rule

Write a small acceptance table before running the candidate. Values below are policy
categories; the receiving team must choose appropriate thresholds for its app.

| Gate | Decision rule |
| --- | --- |
| Completeness | Every required case/mode completed; no unexplained missing/skipped checks |
| Critical invariants | No prohibited disclosure, permission bypass, destructive error, or duplicate effect |
| Quality | Required slices pass agreed thresholds; paired regressions stay within predeclared tolerance |
| Evidence | Claims have required support; provenance and coverage both pass |
| Efficiency | Meets the declared target at acceptable quality; include added calls/retries |
| Recovery | Applicable failure/cancellation/restart scenarios pass |
| Reproducibility | Reviewer can identify build/config/data and access sanitized artifacts |

For Memory's small regression suite, candidates had to pass all applicable quality checks.
Some focused experiments gated only a named candidate and deliberately retained failing
controls. An experiment exit code of zero therefore did not mean every displayed variant
passed. Always inspect per-variant results and the declared gate.

Freeze defaults when required outcomes are met and remaining experiments produce small or
inconsistent improvements, regressions on adjacent fixtures, or costs exceeding their likely
benefit. Shift effort to release validation and production feedback. Continue targeted
experiments for a concrete unresolved failure; avoid an indefinite sequence of prompt tweaks.

Promotion should be a reviewed action: record the accepted build/options, compatible report
IDs, rationale, limitations, and rollback baseline. Keep historical baselines after promotion.
If the scorer or dataset changes, establish a new benchmark version and rerun controls.

## 13. Operational cadence and ownership

A practical starting cadence:

- Every relevant change: deterministic tests and a small focused regression set.
- Before merging retrieval/prompt/model changes: paired live cases and relevant holdout.
- Before release: full compatible matrix plus applicable lifecycle/fault checks.
- Periodically: representative production replays, provider/model drift checks, and bounded soak tests.
- After an incident: add a de-identified regression trajectory and verify the fix against it.

This is a proposed cadence for the receiving team. The Memory runner was manually invoked;
no unattended recurring benchmark job was installed.

Assign an owner for dataset labels, scorer changes, run infrastructure, artifact retention,
and release acceptance. Set time/call/spend limits for jobs. Alert on meaningful regressions,
incomplete runs, or missing baselines rather than announcing unchanged results repeatedly.

## 14. Reproduce the Memory reference implementation

Use this section to inspect or reproduce the original approach. A different application
should replace the adapters and datasets while keeping the reporting and comparison discipline.

Prerequisites: the repository's .NET SDK, an existing compatible evaluation SQL database,
configured chat and embedding models, and permission to create isolated Memory scopes.
Create an ignored file at
`src/FabrCore.Services.Memory.EvalConsole/appsettings.local.json`:

```json
{
  "ConnectionStrings": {
    "MemoryEvalDb": "<existing evaluation database connection>"
  },
  "Eval": {
    "ModelConfigurationPath": "C:/path/to/fabrcore.json"
  }
}
```

The model configuration must define the selected chat alias and embeddings. Environment
variables override local settings, including `ConnectionStrings__MemoryEvalDb`.
Do not put real credentials into this document or source control.

From the repository root:

```powershell
# Build once and use the built runner when exact child exit codes matter.
dotnet build src/FabrCore.Services.Memory.EvalConsole --nologo -v quiet
$runner = 'src/FabrCore.Services.Memory.EvalConsole/bin/Debug/net10.0/FabrCore.Services.Memory.EvalConsole.dll'

# Focused component and end-to-end runs.
dotnet $runner run --mode code --iterations 2
dotnet $runner run --mode harness --iterations 2
dotnet $runner run --mode code --iterations 2 --manifest src/FabrCore.Services.Memory.EvalConsole/corpus-holdout.json

# Full sequential matrix and available baseline comparisons.
.\scripts\Run-MemoryEvals.ps1 -Iterations 2

# Explicit comparison: replace these placeholder report paths.
dotnet $runner compare --baseline artifacts/memory-evals/BASE/report.json --candidate artifacts/memory-evals/CANDIDATE/report.json

# Targeted stress examples; inspect the referenced experiment's gate and limits.
dotnet $runner selection-scale --iterations 3
dotnet $runner long-history --iterations 3
dotnet $runner corrections --iterations 3
dotnet $runner chunk-windows --iterations 3 --window-fixture late
dotnet $runner chunk-windows --iterations 3 --window-fixture middle
```

Ordinary run exit codes: 0 quality gates passed; 1 configuration/infrastructure error;
2 quality failure; 130 cancellation. Inspect the report as well as the exit code.
The matrix script returns failure for failed runs or failed available comparisons and writes
`matrix.json`; it never automatically promotes baselines.

Run commands retain reports and scoped SQL data. Artifact directories are ignored by Git.
Do not assume a fresh clone includes the baseline reports named in the registry.

Reference files:

- [Console setup and command reference](../src/FabrCore.Services.Memory.EvalConsole/README.md)
- [Runner, fixtures, and report types](../src/FabrCore.Services.Memory.EvalConsole/Program.cs)
- [Main dataset](../src/FabrCore.Services.Memory.EvalConsole/corpus.json) and [business fixture](../src/FabrCore.Services.Memory.EvalConsole/corpus-holdout.json)
- [Chat measurements](../src/FabrCore.Services.Memory.EvalConsole/Measurements.cs) and [embedding measurements](../src/FabrCore.Services.Memory.EvalConsole/MeasuredEmbeddings.cs)
- [Comparison rules](../src/FabrCore.Services.Memory.EvalConsole/ReportComparison.cs)
- [Matrix runner](../scripts/Run-MemoryEvals.ps1) and [baseline registry](../src/FabrCore.Services.Memory.EvalConsole/baselines.json)
- [Memory results and limitations](memory-eval-status.md)
- [Architecture review](memory-harness-design.md) and [frozen defaults](memory-release-defaults.md)

## 15. Handoff checklist

Before the receiving team starts an experiment, they should have:

- An application/agent-class contract and explicit success, recovery, and stop conditions.
- A runnable production adapter with isolated test state and sanitized configuration.
- Versioned development cases, a protected holdout, labels, and a tested scorer.
- Measurements covering the actual invocation paths, with unknown usage represented honestly.
- A baseline report plus the exact build/configuration needed to interpret it.
- A one-variable hypothesis, declared gates, repetition/order plan, and execution budget.
- An artifact location, retention policy, and named reviewer.

Each experiment decision should answer: What changed? Which tasks and boundaries were tested?
What passed or regressed? What did it cost? What remains unproven? Which report IDs support
the decision? Was the candidate accepted, rejected, or left optional?

The deliverable is a reproducible decision supported by preserved evidence—not just a
passing percentage or a model response that looks convincing.
