# Harness efficiency and Agent Framework 1.20

The harness keeps its explicit, tenant-safe provider composition. Agent Framework 1.20 is the
dependency baseline. Filesystem discovery, file memory, shell tools and hosted search are not
enabled by adopting the upstream harness wholesale.

## Implemented

- Move configured `CompactionProvider` instances inside function invocation. Compaction sees new
  tool results before each model call; memory recall, skills, modes and todos retain their
  per-agent-invocation scope. Keep token tracking below compaction.
- Add optional `ModelConfiguration.ContextWorkingSetTokens` and the agent override
  `_ContextWorkingSetTokens`. Null retains existing behavior. The positive working set is capped
  at window minus output reserve. The 0.5 and 0.8 thresholds now produce bounded tool excerpts
  while preserving task-bearing messages. See [compaction correctness](compaction-correctness.md)
  for the durable validation, budgeting, session and safe-stop behavior.
- Include request instructions, function schemas and serialized tool results in budget estimates.
  Keep existing public overloads and telemetry field names for compatibility. Estimates are not
  provider usage; the pre-call diagnostic now explicitly labels the count as an estimate.
- Release background sessions on clear and proxy disposal. Standalone results implement
  `IAsyncDisposable`. Disposal releases runtime resources without rewriting durable state after
  the host's final flush. Background records still running in the last snapshot become lost on
  restore as before. Cross-grain cancellation still abandons the wait, not remote execution.
- Expose `_HarnessBackgroundWaitTimeoutSeconds` (default 300) for the 1.20 wait-tool timeout.
  Timeout leaves work running; do not shorten it simply to reduce cost, because that can cause
  extra model turns.
- Reduce routine narration and unnecessary delegation in the default instructions.
- Restore the todo/delegation test with an explicit background wait. Its previous scripted model
  completed the todo without waiting for the background task, so the background evaluator could
  legitimately re-invoke it. No change to the evaluator's completion contract was required.

## Validation and tuning

Deterministic regression coverage checks streaming and non-streaming per-call compaction, recall
frequency, request guardrails, background cancellation, history replacement/session restoration,
loop usage aggregation and lower repeated input in a synthetic 12-tool-call workload. This is
evidence that the pipeline reduces repeated payloads, not a prediction of production savings.

Configure both `ContextWindowTokens` and `MaxOutputTokens`. Inspect the startup compaction ladder;
`context:unconfigured` means the in-loop layer is disabled. The sample GraphRAG model now reserves
8192 output tokens. The sample default GPT-4o model also reserves 8192 output tokens with its
[documented 128000-token window](https://developers.openai.com/api/docs/models/gpt-4o).
Models with unknown window sizes still require an explicit configuration.

Before selecting a smaller working set, compare representative tasks at the current setting and
at 32000 input tokens. Record task success, factual/tool-result fidelity, model-call count, provider
input/output/cached/reasoning tokens, latency and compaction calls. Retain the smaller setting only
if quality meets the application's existing acceptance criteria. Do not count framework aggregate
loop usage again on top of FabrCore per-call usage. Delegates have their own budgets and costs.

## Deliberately separate follow-ups

Per-service-call history middleware alone does not create durable checkpoints: FabrCore buffers
history until the host flushes. Keep the current turn-level write cadence until checkpoint cost,
side-effect replay and recovery semantics are designed together. Model routing and globally tighter
loop/token limits remain workload-specific options; they are not enabled globally without quality
measurements. Remote cancellation requires a separate protocol spanning the sender and target grain.
