# Compaction correctness

FabrCore's SDK and Host do not depend on `FabrCore.Services.Memory` or SQL. The fixes described
here use the existing host chat-history abstraction. Applications select their storage and may
opt into the Memory add-on at design time. A registered custom history-compaction callback still
replaces the SDK implementation for that provider; these SDK guarantees do not automatically
apply to an independently implemented callback.

## Two layers with different responsibilities

**In-run context compaction** uses Microsoft Agent Framework 1.20's provider and message-group
index with FabrCore's protected tool strategy. It runs inside function invocation, before every
model call, in both the FabrCore harness and the SDK's standard agent factory. Recall and other
invocation providers keep their original scope. Token tracking and the complete-request guard
run below compaction.

At 50% of the input working set, older tool results receive head-and-tail excerpts bounded to
2048 characters per result. At 80%, excerpts may tighten to 512 characters. The omission marker
explicitly identifies missing content. Structured results are serialized as JSON. Complete
call/result groups, call IDs, arguments, mixed-content prose, instructions, user messages,
assistant decisions, handovers, and the latest two groups are preserved. There is no automatic
deletion of old user requests or task-bearing prose. These excerpts are not semantic summaries.

The working set is a **soft target**. Protected content may exceed it. The physical input limit
is separately checked with reserved output, instructions and tool schemas included. If protected
content cannot fit, the request stops with a diagnostic. Long tool loops can therefore require a
larger window or an application-defined checkpoint; the SDK does not silently forget the task.

Original tool results remain in the transcript; excerpts do not rewrite persisted history.
Applications can expose original-result retrieval through their own tools using the existing
host/history interfaces. No SQL-backed retrieval service is assumed.

**Durable history compaction** runs between turns. By default, its trigger is now 70% of the
input working set when in-run compaction is configured, or the existing 75% fallback otherwise.
The input working set is capped at the physical window minus reserved output. Explicit history
threshold and budget overrides still apply. Preflight checks run for over-threshold history
whenever the legacy `StaleAfterMinutes` setting is positive; age is diagnostic, not a prerequisite.

The durable sequence is:

1. Flush pending history and snapshot the original transcript.
2. Identify complete interactions, matching parallel tool calls and results by ID. Reject malformed
   or unfinished tool interactions. Preserve authoritative instructions, the latest user request,
   and the latest complete interaction; keep additional recent groups when budget allows.
3. Summarize older groups into a historical handover covering intent, constraints/corrections,
   decisions, current state, open items and exact critical references. Mixed content is included.
4. Reject empty or incomplete generation, non-reducing or over-budget candidates, cancellation,
   and source-history changes. No destructive fallback truncation is applied to durable content.
5. Commit the validated replacement once through the existing host and refresh the cache.

Generated handovers use the assistant role and are explicitly contextual evidence. Original
system/developer instructions remain unchanged. Legacy messages authored `compaction` are also
read as assistant context, preventing old generated summaries from retaining system authority.
Prompts separate the summarization instruction from the untrusted transcript. This reduces
instruction confusion; it does not make model-generated summaries immune to errors or injection.

Thread writes must be serialized by the host, as in the Orleans host. The source check detects
changes during generation; it is not a distributed compare-and-swap guarantee for independent
external writers. Cancellation is checked immediately before commit. Once the host write starts,
its own completion/atomicity contract applies; cancellation cannot roll back an accepted write.

## Summarizer limits and configuration

The summarizer must have `ContextWindowTokens` configured. It reserves output and 1024 tokens
for instructions/framing, packs complete interaction groups, and bounds each chunk using a
conservative UTF-8 byte budget. Oversized individual groups are rejected intact: configure a
larger summarizer or reduce tool output at its source. Groups are never split at arbitrary
character positions. Reduction must make progress, with limits of eight passes and 64 calls.

The optional agent argument `_CompactionModelConfigName` selects a separately configured
summarizer. Direct SDK callers can set `CompactionConfig.SummaryModelConfigurationName`.
The default remains the agent's model. Validate continuation quality before selecting a cheaper
model. Unknown model limits cause a safe compaction failure, leaving history available.

Durable and request estimates share the same UTF-8-aware estimator. These are heuristics,
including framing allowance, not exact provider tokenization or multimodal token accounting.
Leave headroom and compare against reported provider usage. The final guard also applies when
there is no active turn-budget scope and respects a larger per-call output reservation.

## Session and read-side safety

The transient framework compaction index is rebuilt from authoritative history on each history
invocation and stripped from harness session snapshots. This handles same-length history rewrites
that retain the old final message, as well as activation/restore. Provider-managed remote
conversation histories remain subject to the upstream provider's remote-history limitations.

Read-side projection may excerpt older tool results, preserving complete interactions and task
content. If that is insufficient, it stops before sending the request. The former minimum token
floors and unconditional text clipping no longer bypass small configured budgets.

## Validation and operational follow-up

Deterministic tests cover failed and canceled generation, length termination, oversized summaries,
unknown/undersized summarizer windows, persistence failure, concurrent changes, mixed content,
parallel and malformed tool pairs, active-request preservation, repeated compaction, same-length
rewrites, session restore, output reservation, and streaming/non-streaming request guards.

These tests establish pipeline correctness and preservation of scripted handovers. They do not
prove semantic fidelity of a live summarizer. Before reducing working sets in production, replay
representative long tasks and measure exact-ID/correction recall, unfinished-task recall, tool
protocol validity, successful continuation, input/output/cached tokens, summarizer cost and latency.
Compare repeated-compaction runs against uncompacted baselines. Existing per-call usage telemetry
and `history.*` compaction events support this evaluation.

## Design references

- [Microsoft conversation compaction](https://learn.microsoft.com/en-us/agent-framework/concepts/agents/conversations/compaction)
  describes tool-loop placement and message-group preservation.
- [Microsoft 1.20 tool compaction source](https://github.com/microsoft/agent-framework/blob/dotnet-1.20.0/dotnet/src/Microsoft.Agents.AI/Compaction/ToolResultCompactionStrategy.cs)
  shows why a default formatter is not itself a bounded tool-result summary.
- [Anthropic context engineering](https://www.anthropic.com/engineering/effective-context-engineering-for-ai-agents)
  motivates preserving continuation-critical details and evaluating compression against recall.
- [Anthropic long-running harnesses](https://www.anthropic.com/engineering/effective-harnesses-for-long-running-agents)
  motivates explicit progress and unresolved-work handovers.

The protected strategy and safe-stop policy are FabrCore design choices informed by these sources,
not guarantees supplied by the upstream framework.
