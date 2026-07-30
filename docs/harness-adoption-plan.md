# FabrCore × Microsoft Agent Framework Harness — Analysis & Adoption Plan

> Status: **Proposal** (2026-07-28; commercial-layer analysis added 2026-07-29). Analysis verified against `C:\repos\Microsoft\agent-framework` (Microsoft.Agents.AI.Harness **1.15.0**, stable), this repo (the **open-source** runtime — FabrCore.Sdk on Microsoft.Agents.AI **1.15.0**), and `C:\repos\FabrCore-V365` (the **commercial** offerings — `FabrCore.Services.Memory` v0.5.0 and the `FabrCore.Surface` squads/SwarmV2 orchestration, on FabrCore.Sdk 1.4.1 / Microsoft.Agents.AI 1.15.0 transitive). Every claim below cites a file path so it can be re-verified as the framework evolves.
>
> **Decisions to date:** (1) compaction is **hybrid** — framework-style in-run windowing + FabrCore storage summarization (§3.2); (2) **no file surfaces** — file memory and file access are rejected; `fabrcore.host` storage / MCP cover genuine file needs, and durable memory comes from the Services.Memory socket (§4.1, §5.3); (3) FabrCore ships a **native harness** (`FabrCoreHarnessAgent`) composed directly from the framework's providers, with Microsoft's `AsHarnessAgent` kept as a supported escape hatch (§5.1); (4) **open-core boundary** — memory abstractions, tool-result compression, and the capability roster move OSS (§8); **superseded in part on 2026-07-29**: the full Services.Memory and Services.GraphRag engines now also move OSS as optional SQL-backed packages — see [memory-graphrag-oss-plan.md](memory-graphrag-oss-plan.md).

Blog references:
- [Microsoft Agent Framework at Build 2026](https://devblogs.microsoft.com/agent-framework/microsoft-agent-framework-at-build-2026-announce/)
- [The Microsoft Agent Framework Harness is now released](https://devblogs.microsoft.com/agent-framework/the-microsoft-agent-framework-harness-is-now-released/)
- [Build your own claw and agent harness with Microsoft Agent Framework](https://devblogs.microsoft.com/agent-framework/build-your-own-claw-and-agent-harness-with-microsoft-agent-framework/)

---

## 1. Executive summary

The Agent Framework's new **harness** (`IChatClient.AsHarnessAgent(HarnessAgentOptions)` → `HarnessAgent : DelegatingAIAgent`) packages the "build your own claw" stack: file memory, todo lists, plan/execute modes, skills, agentic loops, background agents, tool approval, in-loop context compaction, web search, mid-turn message injection, and OpenTelemetry — pre-wired around a `ChatClientAgent`.

FabrCore already references `Microsoft.Agents.AI` 1.15.0, the exact version the harness package depends on — **adoption is a package add, not a version upgrade**. But the harness alone is not a runtime: its defaults assume a single-process CLI on a developer's machine (session state in memory, file memory on local disk, skills discovered from the current working directory, no budget enforcement, no channels, no multi-tenancy).

**The FabrCore pitch:** *harness capabilities + FabrCore durability, channels, budgets, and governance = strictly better than any dev hand-wiring the harness themselves.* Concretely, FabrCore adds what the harness cannot do alone:

| Harness gives the model | FabrCore makes it production-grade |
| --- | --- |
| Todo lists, modes, approval rules in session state | Orleans-durable session snapshots that survive silo restarts and scale-out |
| Flat-file memory / file access on local disk (default-on) | **Rejected** — no file surfaces in the runtime; durable memory via the `FabrCore.Services.Memory` socket instead (§4.1, §5.3) |
| Tool approval requests surfaced in the response | Delivery over WebSocket / REST / Teams Adaptive Cards with the durable principal outbox |
| In-run loops (`LoopAgent`, max 10 iterations) | Durable recurrence via Orleans reminders + `ChatRunSafetyScope` spend kill-switch |
| Background agents as in-process `AIAgent`s | Fan-out onto real, ACL-governed, monitored FabrCore agents via `A2AAgentProxy` |
| Token-estimate compaction | Compaction informed by provider-actual token usage + storage-side map-reduce summarization |
| Code-only `HarnessAgentOptions` (30 properties, partly `[Experimental]`) | Zero-code blueprint JSON (`_Harness*` args) with the existing 3-tier config cascade |

**Architecture decision — native harness.** Microsoft's harness *package* is only a thin assembler (4 files); every capability lives in the core `Microsoft.Agents.AI` package FabrCore already references. FabrCore therefore ships its **own assembler** — `FabrCoreHarnessAgent` — composing those same providers (keeping Microsoft's tool names, so prompts stay portable) with FabrCore's pipeline, server-safe defaults, and the Services.Memory socket. The FabrCore glue (session snapshots, approvals, injection) is agent-agnostic, so devs who prefer Microsoft's stock `AsHarnessAgent` can still use it with full platform support — an escape hatch, not a second product (§5.1).

The analysis spans both halves of the product: the open-source runtime (this repo) and the commercial layer (`FabrCore-V365` — `FabrCore.Services.Memory` and the Surface squads/SwarmV2 orchestration). §4.1–4.2 cover how the commercial services compose with the harness rather than compete with it; §8 defines what moves across the open-core boundary.

The rest of this document is the capability reference, the compaction deep-dive, a feature-by-feature verdict matrix (plus two commercial-layer deep-dives), the integration architecture, ranked opportunities, risks, the open-core boundary, and a 4-phase roadmap.

---

## 2. Harness capability reference (.NET, 1.15.0)

> **Reading note:** this section documents *Microsoft's* harness as shipped — the upstream baseline FabrCore tracks and the behavior the escape hatch provides. Per the native-harness decision (§5.1), FabrCore composes the underlying **providers** (core `Microsoft.Agents.AI` package) directly and does not use the thin assembler package at runtime.

### 2.1 Packages and entry point

| | Location |
| --- | --- |
| Thin package | `dotnet\src\Microsoft.Agents.AI.Harness\` — `HarnessAgent.cs`, `HarnessAgentOptions.cs`, `ChatClientHarnessExtensions.cs` (NuGet **Microsoft.Agents.AI.Harness 1.15.0**, stable, depends only on `Microsoft.Agents.AI`) |
| Actual capabilities | Core package: `dotnet\src\Microsoft.Agents.AI\Harness\` (`AgentMode`, `BackgroundAgents`, `FileAccess`, `FileMemory`, `FileStore`, `Loop`, `Todo`, `ToolApproval`), `...\Compaction\`, `...\Skills\` |
| Samples | `dotnet\samples\02-agents\Harness\` — `Harness_Step01..05`, `BuildYourOwnClaw\Claw_Step01..03`, `Harness_Shared_Console` (reusable observers/formatters for todo/mode/file-memory tools) |
| Python parity | `python\packages\core\agent_framework\_harness\` (`create_harness_agent()` factory; same tool names — prompts/skills are cross-language portable) |

```csharp
// dotnet\src\Microsoft.Agents.AI.Harness\ChatClientHarnessExtensions.cs:34
public static HarnessAgent AsHarnessAgent(
    this IChatClient chatClient,
    HarnessAgentOptions? options = null,
    ILoggerFactory? loggerFactory = null,
    IServiceProvider? services = null)
```

It wraps an **`IChatClient`** (not an `AIAgent`) and internally builds a `ChatClientAgent` over a decorated pipeline:

- **Chat-client pipeline (outer → inner):** ApprovalResponseBinding → ApprovalNotRequiredFunctionBypassing → FunctionInvocation (`MaximumIterationsPerRequest`) → **MessageInjection (always on)** → **PerServiceCallChatHistoryPersistence (always on, required)** → CompactionProvider (only if configured) → OTel (`HarnessAgent.cs:216-247`).
- **Agent decorators (outer → inner):** `LoopAgent` (only when `LoopEvaluators` set) → `ToolApprovalAgent` → `OpenTelemetryAgent` → `ChatClientAgent` (`HarnessAgent.cs:133-162`).
- **Instructions:** `HarnessInstructions ?? DefaultInstructions` + `"\n\n"` + `ChatOptions.Instructions`. Set `HarnessInstructions = ""` to drop the preamble (`HarnessAgent.cs:200-209`).

### 2.2 Features, defaults, and tool names

| Feature | Default | Tools / key types | Notes |
| --- | --- | --- | --- |
| File memory | **ON** | `file_memory_write/read/delete/ls/grep/replace/replace_lines`; `FileMemoryProvider` | Flat namespace; `memories.md` index (max 50 entries) injected as a user message each turn; instructions tell the model to save large tool outputs so compaction can't lose them. Default store `FileSystemAgentFileStore({cwd}/agent-file-memory/{timestamp}_{guid})` — **silo-local disk** (`HarnessAgent.cs:303-312`) |
| File access | **OFF** (on when `FileAccessStore` set) | `file_access_*` (7 tools); `FileAccessProvider` | Approval-gated; read-only tier (`read/ls/grep`) has a canonical auto-approval rule (`FileAccessProvider.ReadOnlyToolsAutoApprovalRule`); supports directories |
| Todo | **ON** | `todos_add/complete/remove/get_remaining/get_all`; `TodoProvider` | State in session bag; host-readable via `GetAllTodosAsync`/`GetRemainingTodosAsync` (`Harness\Todo\TodoProvider.cs`) |
| Agent modes | **ON** | `mode_set/mode_get`; `AgentModeProvider` | Built-in `plan` (interactive; "write the plan to a memory file"; get approval) and `execute` (autonomous); configurable mode list |
| Skills | **ON** | `load_skill/read_skill_resource/run_skill_script`; `AgentSkillsProvider` | **Discovers `SKILL.md` from `Directory.GetCurrentDirectory()`** when no `AgentSkillsSource` given — wrong for a server. All three tools approval-required by default. Sources composable (Aggregating/Caching/Deduplicating/Filtering); MCP skills via `Microsoft.Agents.AI.Mcp` (`UseMcpSkills`) |
| Loops | **OFF** (on when `LoopEvaluators` set) | `LoopAgent` (`DefaultMaxIterations = 10`) | Evaluators: `CompletionMarkerLoopEvaluator` ("Ralph" loop), `TodoCompletionLoopEvaluator` (`Modes=["execute"]`), `AIJudgeLoopEvaluator`, `BackgroundTaskCompletionLoopEvaluator`, `DelegateLoopEvaluator`. `LoopAgentOptions.FreshContextPerIteration` clones the session per pass |
| Background agents | **OFF** (on when `BackgroundAgents` set) | `background_agents_start_task/wait_for_first_completion/get_task_results/get_all_tasks/continue_task/clear_completed_task` | Takes `IEnumerable<AIAgent>`; names must be non-empty and unique; task tracking is in-process/volatile |
| Tool approval | **ON** | `ToolApprovalAgent` (~950 lines) | Standing "don't ask again" rules + `AutoApprovalRules` heuristics + **approval-response binding** (anti-forgery: responses must match surfaced requests). Surfaces `ToolApprovalRequestContent` (with `CreateResponse` / `CreateAlwaysApproveToolResponse` helpers) and **ends the run** — no blocked thread waiting for a human |
| Compaction | **OFF unless configured** | see §3 | Needs `MaxContextWindowTokens` + `MaxOutputTokens` (or a custom `CompactionStrategy`) or the agent runs **uncapped** |
| Web search | **ON** | `HostedWebSearchTool` added to `ChatOptions.Tools` | Fails on providers that don't support hosted tools |
| Message injection | **Always on** | `MessageInjectingChatClient.EnqueueMessagesAsync(session, messages)` | External code can queue messages into a *running* turn; queue lives in the session bag |
| Shell | **Not in the harness package** | `Microsoft.Agents.AI.Tools.Shell` (preview): `LocalShellExecutor` / `DockerShellExecutor`, `ShellPolicy` denyList, `ShellEnvironmentProvider`, `AsAIFunction(requireApproval: true)` | Deliberately removed from the harness at graduation; manual wiring only (see `Claw_Step03_ScalingCapabilities`) |
| OTel | **ON** | `OpenTelemetryAgent` + chat-client decorator | Source `"Experimental.Microsoft.Agents.AI"` |
| History persistence | **Always on** | `PerServiceCallChatHistoryPersistingChatClient` | `RequirePerServiceCallChatHistoryPersistence = true` — history is handed to the `ChatHistoryProvider` after **every** model call for crash recovery |

### 2.3 State model — the critical architectural fact

There is **no harness-specific session type and no DI/hosting extension**. All harness provider state rides in the framework's `AgentSession` **StateBag** via `ProviderSessionState<TState>`:

- `TodoState`, `AgentModeState`, `FileMemoryState.WorkingFolder`, `BackgroundAgentState`, `ToolApprovalState` (standing rules + surfaced requests), `CompactionProvider.State.MessageGroups`, and the injected-message queue.
- Round-trip is `agent.SerializeSessionAsync(session)` → `JsonElement` → `agent.DeserializeSessionAsync(element)`.
- Chat history goes through the pluggable `HarnessAgentOptions.ChatHistoryProvider` (default `InMemoryChatHistoryProvider`, whose messages get serialized *into* the snapshot — unless a custom provider is supplied, which keeps snapshots small).

**Consequence for FabrCore:** whoever hosts a `HarnessAgent` must persist and restore session snapshots, or every harness feature is amnesiac. That is the #1 integration prerequisite (§5.2).

### 2.4 Corrections to the blog-level story (verified in source)

1. **Compaction is not on by default.** `HarnessAgent` only creates a `CompactionProvider` when a custom `CompactionStrategy` is set OR both `MaxContextWindowTokens` and `MaxOutputTokens` are provided (`HarnessAgent.cs:166-197`). No token params → no compaction at all.
2. **With a custom `ChatHistoryProvider`, harness compaction never touches storage.** The `compactionStrategy.AsChatReducer()` is only wired into the *default* `InMemoryChatHistoryProvider` (`HarnessAgent.cs:191-197`). Passing `FabrCoreChatHistoryProvider` means harness compaction is purely in-context — which is exactly the layering FabrCore wants (§3).
3. **The approval content type is `ToolApprovalRequestContent`** (not `FunctionApprovalRequestContent`), with `RequestId`, `ToolCall`, and response-builder helpers.
4. **There is a real-tokenizer hook, but the harness can't reach it.** `CompactionMessageIndex` accepts a `Microsoft.ML.Tokenizers.Tokenizer` (public ctor), falling back to `ByteCount / 4` when null — but `CompactionProvider` always calls `CompactionMessageIndex.Create(messages)` without one. A custom strategy *can* count real tokens; the stock path never does. This is a differentiation opening for FabrCore (§6, item 9).
5. **Many options are `[Experimental]`** (`MaxContextWindowTokens`, `CompactionStrategy`, `LoopEvaluators`, `BackgroundAgents`, `FileMemoryStore`, `FileAccessStore`, the whole Compaction namespace) — expect churn across 1.x even though the package itself is stable.

---

## 3. Compaction deep-dive: harness vs FabrCore

FabrCore's context management is its most mature subsystem — and the area where naive harness adoption would lose the most. The two systems compact at **different altitudes**, and correction #2 above makes the split natural.

### 3.1 Comparison

| Dimension | Harness (`Microsoft.Agents.AI\Compaction\`) | FabrCore (`src\FabrCore.Sdk\`) |
| --- | --- | --- |
| **Trigger fidelity** | `ByteCount/4` estimate per message group; composable triggers (`CompactionTriggers.TokensExceed/MessagesExceed/TurnsExceed/GroupsExceed/HasToolCalls/All/Any`). Real tokenizer hook unreachable through the harness | Storage: char/4 over `StoredChatMessage.ContentsJson` (`CompactionService.EstimateTokens`). Mid-turn: char/4 over the *actual outgoing request* (`ChatRunSafetyScope.PrepareCallAsync:137`). Cumulative turn budget: **provider-reported actual input tokens** (`TokenTrackingChatClient` → `RecordCompletedCall`) |
| **Timing** | In-loop: `CompactionProvider` runs **before every model call** inside the tool loop; plus as `IChatReducer` on the default in-memory history provider only | Three phases: preflight (stale thread > `StaleAfterMinutes=60`, before the turn — `TryPreflightCompactAsync`), mid-turn checkpoint (once per prompt over threshold), post-turn. Plus read-side projection (`ProjectForLlm`) on every history read |
| **Destructive?** | **Non-destructive**: groups marked `IsExcluded` with `ExcludeReason`; storage untouched; excluded groups recoverable; `GetIncludedMessages()` feeds the model | **Storage compaction is destructive**: `ReplaceThreadMessagesAsync` rewrites the persisted thread with a `[Compacted History]` system message + kept window. Projection is non-destructive |
| **Summarization** | **Opt-in** (`SummarizationCompactionStrategy`): single LLM call over all marked groups, `DefaultMinimumPreserved=8`, restores groups on failure, inserts `[Summary]` assistant message. Not in the default pipeline | **Built into the default path**: map-reduce — 200K-char input cap, 1536-token chunk summaries, recursive reduce to 2048-token final. Handles arbitrarily large backlogs; cost-attributed via `LlmCallContext` origin `"Compaction"`; bypasses run safety to avoid recursion |
| **Default strategy** | `ContextWindowCompactionStrategy(maxCtx, maxOut)`: input budget = ctx − out; pipeline of **tool-result eviction @50%** (old tool-call groups collapse to one-line summaries) then **truncation @80%** (oldest non-system groups dropped); last 2 groups always preserved. **Evict-then-truncate, not summarize** | Budget-aware keep window (newest under `threshold − 2500` reserve, `KeepLastN=20` floor), oversized-message truncation, then summarize the remainder; post-compaction validation pass if still over |
| **Tool-pair safety** | Yes — atomic `CompactionMessageGroup`s (assistant call + results never split) | Yes — split index advanced past orphaned tool-role messages; projection also orphan-safe |
| **Config surface** | Code-only, `[Experimental]`: `HarnessAgentOptions.{MaxContextWindowTokens, MaxOutputTokens, CompactionStrategy, DisableCompaction}` | 3-tier cascade: defaults → `fabrcore.json` `ModelConfiguration` (`ContextWindowTokens`, `CompactionEnabled/KeepLastN/Threshold/StaleAfterMinutes`, `PerTurnMaxInputTokens`, `MaxPromptInputTokens`, `MidTurnCompactionEnabled`, `RunawayBudgetBehavior` — `src\FabrCore.Core\ModelConfiguration.cs`) → per-agent Args (`_Compaction*`, `_Projection*`). Override hook: virtual `OnCompaction` |
| **State persistence** | Group index (exclusions + inserted summaries) in the session bag — **lost every activation until FabrCore persists session snapshots**; summarization work would be re-billed each turn | The compaction result *is* the persisted thread — durable by construction |
| **Failure handling** | Summarization failure → restore groups, log, proceed uncompacted. **No budget abort anywhere** — only `MaximumIterationsPerRequest` bounds the loop | Mid-turn failure → `RunStopReason.MidTurnCompactionFailed`; budget breaches → `FabrCoreRunStoppedException` → `_error` response with full token diagnostics; `RunawayBudgetBehavior` configurable |
| **Observability** | `CompactionTelemetry` ActivitySource spans + structured logs | `run-safety.*` monitor events with token diagnostics; compaction LLM calls cost-tracked via `ITokenCostCalculator` |

**Reading of the table:** the harness is strictly better at *"don't overflow the context window mid-tool-loop"* — cheap, mechanical, per-service-call, non-destructive, zero LLM cost on the hot path, and it degrades gracefully (one-line tool summaries) where FabrCore's projection just drops messages. FabrCore is strictly better at *"keep the durable Orleans blob small"* (whole-blob grain writes make this an operational requirement the harness doesn't even see) and *"never let a run bankrupt you"* (the harness has no spend guard at all). FabrCore's map-reduce summarizer is categorically more capable than the harness's single-shot opt-in summarizer on large backlogs.

### 3.2 Ownership decision (approved): hybrid with a hard boundary per layer

Under the native assembler (§5.1) this wiring is direct — FabrCore instantiates the `CompactionProvider`/strategy itself rather than steering Microsoft's assembler with off-switches. The §3.3 matrix still applies verbatim to escape-hatch agents. "Harness" in the table below means the framework's in-run compaction machinery, whichever assembler composed it.

| Layer | Owner | Detail |
| --- | --- | --- |
| In-run context windowing | **Harness** | `CompactionProvider` with `ContextWindowCompactionStrategy` fed from `ModelConfiguration.ContextWindowTokens` / `MaxOutputTokens`. FabrCore **validates both are configured** for harness agents — otherwise the agent silently runs uncapped (correction #1) |
| Storage hygiene + summarization | **FabrCore** | `CompactionService` runs **preflight + post-turn only**, against `MessageThreads`, with its threshold set *above* the harness's 0.8 truncation point (e.g. ~0.85–0.9 of the window) so the harness is always the in-run first responder and storage compaction is the between-turns consolidator that converts excluded mass into one high-quality `[Compacted History]` summary |
| Mid-turn destructive checkpoint | **Retired under harness mode** | `MidTurnCompactionEnabled` forced off. Rewriting the persisted thread mid-run while the harness `CompactionProvider` holds a group index over the old messages is a latent corruption bug (masked today only because the session bag isn't persisted). The harness's per-call compaction replaces this job with a better mechanism |
| Read-side projection | **Demoted to fuse** | If projection keeps clipping at 25K×0.75 it wins every race and harness compaction becomes decorative. Under harness mode: `_ProjectionMaxContextTokens` ≈ model window, `Threshold` ≈ 0.9 — pathological-case insurance below the provider hard limit. Legacy (non-harness) agents keep current defaults |
| Budget kill-switch | **FabrCore, always on** | `ChatRunSafetyScope` (`MaxPromptInputTokens`, `PerTurnMaxInputTokens`, `RunawayBudgetBehavior`, cost tracking) stays wrapped around every harness LLM call via `TokenTrackingChatClient` — it is the inner `IChatClient`, so nothing changes. Headline: *"the harness compacts; FabrCore guarantees the bill"* |
| Fidelity differentiator | **FabrCore value-add** | A `FabrCoreCompactionStrategy` / custom `CompactionTrigger` (it's just `Func<CompactionMessageIndex, bool>`) that reads `ChatRunSafetyScope.Current` (AsyncLocal — visible inside the provider call) and fires on `max(index estimate, last-request estimate, provider-actual input tokens)`; optionally counts real tokens with `Microsoft.ML.Tokenizers` inside the strategy. Nobody hand-wiring the harness gets this |

**Never do:** LLM summarization in the in-loop path (latency before every model call, and until session snapshots are battle-tested its output would be discarded and re-billed every turn). Summarization stays FabrCore's, post-turn.

### 3.3 Off-switch matrix (kills double-compaction)

| Knob | Harness compaction ACTIVE (harness agent) | Harness compaction OFF / legacy agent |
| --- | --- | --- |
| `MaxContextWindowTokens`/`MaxOutputTokens` | Set from `ModelConfiguration` (required; validated) | n/a |
| Storage compaction (`_CompactionEnabled`) | ON — preflight + post-turn only; threshold raised above harness truncation | ON — current behavior |
| `MidTurnCompactionEnabled` | **FORCED OFF** | available per config |
| `_ProjectionEnabled` | ON as fuse (window ≈ model ctx @ 0.9) | ON — current 25000 / 0.75 |
| Run-safety budgets / kill-switch | **UNCHANGED — always on** | unchanged |
| Harness `ChatHistoryProvider` | `FabrCoreChatHistoryProvider` (⇒ harness never reduces storage, by design) | n/a |
| `SummarizationCompactionStrategy` | never in-loop; summarization stays in `CompactionService` | n/a |
| Compaction session-state entry | cleared/rotated when storage compaction rewrites the thread (group index would be stale) | n/a |

---

## 4. Feature-by-feature verdicts

| Feature | Harness | FabrCore today | Verdict | Rationale |
| --- | --- | --- | --- | --- |
| **File memory** | ON; 7 tools; `memories.md` index; pluggable `AgentFileStore`; default = local disk | None (HTTP artifact `FileController` only) | **REJECT (decision 2026-07-29)** | No file surfaces in the runtime — a multi-tenant server doesn't want model-managed flat files, and `fabrcore.host` storage / MCP already cover genuine file needs. File memory's two jobs are covered better elsewhere: compaction insurance by FabrCore's storage-side summarization (§3.2), durable notes by the Services.Memory socket (§4.1, §5.3). One prompt fix required: override the plan-mode instruction that says "write the plan to a memory file" (§5.1) |
| **Memory service** | File memory only (session-scoped, lexical grep) | Commercial `FabrCore.Services.Memory` (FabrCore-V365): scoped SQL knowledge graph with an LLM-managed lifecycle — source-verified deep-dive in **§4.1**. OSS in-repo SQL-vector memory stays excluded from compilation (`FabrCore.Sdk.csproj:15`) | **ADOPT as *the* memory story (socket OSS, engine commercial)** | With file memory rejected, Services.Memory is FabrCore's only memory layer — a strength, not a gap: durable, scoped, graph-linked, and richer than anything the .NET framework offers (which has **no durable memory at all**; Python-only). Move the abstractions OSS so the native harness gets a first-class memory socket (§5.3, §8); bridge conversation → memory via `ExtractMemoriesAsync` (§4.1) |
| **Todo** | ON; `todos_*` tools; host-readable state | OSS: `TaskWorkingAgent` (plan/replan, dependency graphs) — not agent-callable, plan not persisted. Commercial: SwarmV2 `TaskLedger`/`ProgressLedger` — persisted, but **host-owned; nothing model-callable** (§4.2) | **HYBRID — different altitudes** | TodoProvider = in-run checklist for one agent; TaskWorkingAgent/squad ledgers = orchestration above it. Adopt TodoProvider for every harness agent — it also fills the squads' model-callable gap (§4.2). Let orchestrators *read* member checklists via `GetAllTodosAsync` surfaced through monitoring — fleet-level progress visibility no competitor has |
| **Agent modes** | ON; plan/execute; plan mode writes plan to memory + asks approval | None | **ADOPT (with instruction override)** | Cheap, high perceived value. Plan-mode approval routes naturally through FabrCore's multi-channel delivery. The built-in plan-mode instruction targets memory files (rejected) — the native assembler overrides it to target the **todo list**, which is durable via session snapshots (§5.2) and arguably the better home for a plan anyway |
| **Loops** | Opt-in `LoopAgent` + 5 evaluators; in-process only | No in-run loop (defers to `ChatClientAgent` tool loop, bounded by run safety); Orleans `RegisterTimer` (volatile) + `RegisterReminder` (durable, ≥ 1 min) | **HYBRID — combine** | Two different loops: `LoopAgent` = in-run iteration ("keep going until todos done"); reminders = durable recurrence surviving restarts. The composition is the differentiator: a reminder tick starts a harness run whose `TodoCompletionLoopEvaluator` drives it to completion, with `ChatRunSafetyScope` bounding the spend |
| **Background agents** | Opt-in; 6 tools over `IEnumerable<AIAgent>`; volatile tracking | OSS: `SendAndReceiveMessage`/streams/registry/ACL; `A2AAgentProxy : AIAgent` (`src\FabrCore.Sdk\A2AAgentProxy.cs:14`). Commercial: SwarmV2 squads — host-driven wave dispatch; the model's only delegation tools are *blocking* `ask_agent`/`consult_sme` (§4.2) | **ADOPT via A2AAgentProxy** | Zero-cost synergy: hand the provider `A2AAgentProxy` instances and the model gets `start_task` ergonomics while work runs in durable, ACL-governed, monitored FabrCore grains. Fix required: `A2AAgentProxy.Name` is null and the provider requires non-empty unique names. Volatile task tracking is acceptable v1; note as durability gap. Sharpest commercial synergy: give the SwarmV2 orchestrator/planner these tools (§4.2) |
| **File access** | Opt-in; approval-gated; read-only tier auto-approvable | None (MCP escape hatch) | **REJECT (decision 2026-07-29)** | Same decision as file memory: no file surfaces. Agents that genuinely need files use `fabrcore.host` storage services (`IFileStorageService`, `fabrcoreapi/File`) or a scoped MCP server — both already governed by FabrCore's tool pipeline and approvals |
| **Skills** | ON; **CWD `SKILL.md` discovery**; approval-gated tools; composable sources; MCP skills | None at runtime (`SystemPrompt` string only; the 34 skills in `.agents\skills\` are developer tooling, not runtime) | **ADOPT via custom `AgentSkillsSource`; never CWD** | CWD on a silo is the shared process directory — wrong tenant boundary and a supply-chain risk. Build a config/blueprint-backed source (per-agent skill sets, centrally versioned); compose MCP skills from existing `McpServerConfig`. Keep `run_skill_script` unwired until the shell posture is decided |
| **Tool approval** | ON; standing rules; auto-approval heuristics; anti-forgery response binding; run ends on pending approval | Approval semantics live in channels/host; `VerifiableExecutionAIFunction` signs tool executions | **ADOPT middleware; FabrCore owns delivery + durability** | The rule engine and binding are genuinely good. The "run ends" design fits the grain model perfectly (no held calls). FabrCore adds what it can't: delivery over WebSocket/Teams/outbox and durable pending-approval state |
| **Shell** | Manual, preview package; Local/Docker executors + `ShellPolicy` | None | **DEFAULT OFF; Docker-only if ever** | A shared server runtime must never run `LocalShellExecutor` in the silo process (shared filesystem/env/identity across agents and tenants). If offered: `DockerShellExecutor` only, non-root, network-none default, denyList, approval-required, per-agent opt-in, every command monitored |
| **Web search** | ON (`HostedWebSearchTool`) | None | **ADOPT, default OFF, gate by provider** | Hosted tools fail on providers that don't support them; key off model-capability config rather than inheriting the harness default |
| **Message injection** | Always on | `OnMessageBusy` valve (busy messages rejected/queued); 3s `_status` heartbeat | **ADOPT — direct synergy** | Turns the busy path from "try again later" into "your message reached the running task": `OnMessageBusy` → `EnqueueMessagesAsync`. Pair with the heartbeat for a see-progress + steer-mid-turn story |
| **CodeAct** | Separate providers (Hyperlight / LocalCodeAct); Build-2026 numbers: −52% latency, −64% tokens | None | **WATCH** | Track alongside the Docker/sandbox story; not worth integrating yet |
| **Per-service-call persistence** | Always on, required (`RequirePerServiceCallChatHistoryPersistence = true`) | `FabrCoreChatHistoryProvider` buffers writes; flush = whole-blob grain write | **ADOPT (free by construction) + storage watch-item** | The provider only *buffers* on store, so durable writes still happen at existing flush points — no write amplification by default. Optional `_HarnessFlushPerServiceCall` for crash-critical agents; long-term: delta-append thread storage |
| **Instructions** | `DefaultInstructions` preamble + agent instructions; `HarnessInstructions` override | `SystemPrompt` string | **ADOPT — clean mapping** | `SystemPrompt` → agent instructions; keep the harness preamble (it materially improves harness-tool uptake); blueprint-level `HarnessInstructions` override for full control |
| **OTel** | ON (agent wrapper + chat-client decorator) | `UseOpenTelemetry(EnableSensitiveData=true)` on the agent builder + `MonitoredMessage`/`MonitoredLlmCall` + cost | **HYBRID** | Set `DisableOpenTelemetry = true` on the harness and apply FabrCore's single standard wrap (parity with `CreateChatClientAgent`, including sensitive-data capture the harness knob can't provide). FabrCore monitoring keeps cost attribution |

### 4.1 Commercial deep-dive: FabrCore.Services.Memory vs harness file memory

Source: `C:\repos\FabrCore-V365\src\FabrCore.Services.Memory` (v0.5.0, ~7,900 LOC, references `FabrCore.Host` 1.4.1; `Microsoft.Agents.AI` 1.15.0 transitive). This is the real implementation behind the `fabrcore-services-memory` skill docs — everything below is verified in source.

**What it is:** a *scoped SQL knowledge graph with an LLM-managed lifecycle*, not files.

- **Storage** (`Services\MemorySchemaInitializer.cs`): schema `mem`, auto-created under `sp_getapplock`. `mem.MemoryEntity` (SQL Graph `AS NODE`), `mem.MemoryRelationship` (`AS EDGE`), `mem.MemoryChunk` (`Embedding VECTOR(1536)`, all vectors live here), `mem.MemorySummaryNode`, `mem.MemoryScope` registry, `mem.MemoryAuditLog` (11 action types with actor ids). Unique per-scope `(Name, EntityType)` index. Requires SQL Server 2025 (`VECTOR_DISTANCE`).
- **Scoping is first-class and multi-tenant-safe** — the sharpest contrast with the dead OSS SQL memory: `ScopeKey NVARCHAR(200) NOT NULL` partitions every table and is the first parameter of every store call. Isolated per agent handle by default; opt-in shared pools via the `agent-memory:MemoryScope` plugin setting (`Configuration\MemoryScopeResolver.cs`). Integration-tested scope isolation; full admin surface (`Administration\MemoryAdminService.cs`, remotable via `FabrCore.Services.Contracts` type forwarders).
- **Three temperatures** (`Hot | Warm | Cold`): saves land Warm + a pointer in the **hot index** — a bounded JSON blob (20 entries / 3,000 tokens) in a `__MEMORY_INDEX__` sentinel row, written under `sp_getapplock 'mem-index-{scope}'` so shared-scope writers can't lose entries. Cold is archive-only (`SearchArchiveAsync`); demotion never deletes ("archive, don't delete").
- **Lifecycle intelligence the harness has nothing like**: LLM **entity-merge on save** (cosine < 0.25 same-type ⇒ merge into the existing node instead of duplicating), consolidation = dedup (cosine < 0.05, LLM-merge) → LLM-confirmed staleness pruning (30 d, 3 d for point-in-time) → LLM contradiction resolution → hot-cap enforcement → optional summary tree. Plus **synthetic imagining** (read-side: small model generates up to 5 recall queries from recent conversation).
- **Recall is plan-driven hybrid** (`Services\RetrievalPlanner.cs`): heuristics or a small-tier LLM pick `hot_only | standard | deep`; standard = header scan (≤200) → LLM selects ≤5 → content load with parallel graph expansion; freshness warnings (`[Stale: last updated N days ago]…`) are injected inline — a concept file memory has no equivalent of.
- **How agents consume it:** `[PluginAlias("agent-memory")]` (`Plugin\AgentMemoryPlugin.cs`) exposes 8 tools — runtime names are the **PascalCase method names** (`SaveMemory`, `SaveProcedure`, `RecallMemories`, `SearchArchive`, `ForgetMemory`, `GetMemoryIndex`, `QuerySummaries`, `ConsolidateMemories`) because `FabrCoreToolRegistry` uses `AIFunctionFactory.Create(method, instance)`; the snake_case names in its README/SKILL are documentation drift. **No `AIContextProvider` integration exists** (zero hits repo-wide) — injection is manual: `FormatRecallContext` wraps recall in `<memory-context source="agent-memory-system">…</memory-context>` markers appended to the user message, and the same markers are stripped before extraction so recalled content is never re-learned. No tool-name collision with `file_memory_*`.

**Vs the harness and the Python `MemoryContextProvider`:**

| | Harness file memory (.NET, ON by default) | Python `MemoryContextProvider` (no .NET port) | FabrCore.Services.Memory (commercial) |
| --- | --- | --- | --- |
| Substrate | flat files over `AgentFileStore` | MEMORY.md + topics/ + transcripts/ files | SQL graph + vectors, scoped |
| Index | `memories.md` (50 entries), injected per turn | MEMORY.md (200 lines) | hot index (20 entries / 3K tokens), lock-protected, re-injected after compaction |
| Who writes | the model (tools) | LLM extraction per turn | model (tools) **and** LLM extraction on compaction pressure |
| Consolidation | none | 24 h clock | explicit call / opt-in post-save count check — **no scheduler or timer exists anywhere in the project** |
| Dedup/merge | none | none | entity-merge on save + vector dedup + contradiction resolution |
| Search | `grep` (lexical) | file reads | plan-driven hybrid (LLM select + vector + graph + summary tree) |
| Scope | session working folder | per store path | per-scope, shared pools, admin + audit |

**The .NET framework has no durable-memory provider at all** — the Python `MemoryContextProvider` was never ported. FabrCore.Services.Memory already *is* the .NET counterpart, and is richer on lifecycle (taxonomy, merge, contradiction, audit). What it lacks vs Python is the trigger model: extraction fires only on compaction pressure (`Services\MemoryAwareCompactionService.cs` — Tier 1 tool-result compression → Tier 2 `ExtractMemoriesAsync(olderMessages)` → Tier 3 structured handover summary), and consolidation has no schedule.

**Bridge design (the §6 opportunity, grounded):**
1. **Compaction boundary (exists today):** `MemoryCompactionHandler.CompactAsync` already implements "conversation → durable memory" inside `OnCompaction`. Under the compaction ownership model (§3.2) the same hook moves to FabrCore's post-turn storage compaction — unchanged mechanism, new position.
2. **Recall into harness turns (new):** inject `FormatRecallContext` output (or the hot index) as an `AIContextProvider` through the native harness's memory socket (§5.3) — reusing the existing `<memory-context>` marker protocol to keep extraction loops safe. This gives harness agents durable recall the stock harness cannot offer on .NET.
3. **Consolidation scheduling (gap):** neither the service nor the harness schedules consolidation; Orleans reminders are the natural supplier (a nightly `ConsolidateMemories` tick per scope).

(A fourth hop — promoting harness file-memory contents into the graph — was considered and dropped with the no-file-surfaces decision: the model's durable write path is `SaveMemory` directly, which is stronger than flat files anyway. The dead OSS `MemoryToolFactory` in `src\FabrCore.Sdk\Memory\` is deleted under §8 rather than revived.)

### 4.2 Commercial deep-dive: Squads / SwarmV2 vs harness background agents & loops

Source: `C:\repos\FabrCore-V365\src\FabrCore.Surface\Ai\` (`Swarm\`, `SwarmV2\`, `Orchestration\`, `Tasks\`) + `CommandCenter\`. A **squad** is a named set of FabrCore agent grains provisioned from one definition (`SurfaceSquadService`/`V2`, blueprints via `SurfaceBlueprintProvisioner`); there is no squad grain — the definition JSON is stamped into every member's `AgentConfiguration.Args`, and shells (`squad2-{slug}` orchestrator/planner/supervisor/verifier) are `FabrCoreAgentProxy` subclasses addressed by handle. Four squad types: `Swarm` (v1: one LLM turn + free-text plan + blocking `ask_agent` tool), `Orchestrator` (one-hop router), `Task` (sequential `SurfaceTaskRunnerAgent`), and **`SwarmV2` (current, added 2026-07-06)**.

**SwarmV2 in one pass:** orchestrator `TriageAsync` (structured output: `{mode: direct|plan, riskLevel, approvalRequired, maxConcurrency, verificationDepth, replanThreshold}`) → planner drafts a **`TaskLedger`** (dependency graph, acceptance criteria; SME consultations budgeted at 4/pass; one corrective retry) → `SurfaceSwarmV2PlanValidation` deterministically rejects invalid plans ("the planner LLM cannot smuggle invalid plans into the supervisor") → optional **HITL approval gate** (`swarm2.approval.request`, forced on when `riskLevel=high`) → the **supervisor — pure host code, no LLM** — drives timer-based wave execution over four persisted ledgers (Task/Progress/Artifact/Policy, Magentic-One-shaped, in grain custom state), with per-task **fail-closed LLM verification** (`SurfaceSwarmV2VerifierAgent`), retries with verifier feedback, replan-on-stall with SME escalation, and hard budgets (`SurfaceSwarmV2Budgets`: `MaxRounds=20`, `MaxWallClockMinutes=30`, `MaxReplans=2`, `MaxConsecutiveStalls=2`, `PerTaskTimeoutSeconds=180`, `MaxConcurrencyCeiling=3`) evaluated in a pure `SurfaceSwarmV2BudgetGuard`. Members are typed `Executor | SubjectMatterExpert | Helper`; SMEs are consult-only, enforced in validation, not prompts.

**The comparison is control inversion, not feature overlap:**

| Harness (model-driven, in-process) | SwarmV2 (host-driven, durable) |
| --- | --- |
| `background_agents_start_task` / `wait_for_first_completion` — the *model* fans out async work | Model's only delegation tools are **blocking** `ask_agent` / `consult_sme`; parallelism lives in the host (`DispatchWaveAsync` → `Task.WhenAll`, invisible to the model) |
| `LoopAgent` max 10 iterations | Supervisor drive loop bounded by rounds + wall clock + replans + stalls — more dimensions, host-owned |
| `AIJudgeLoopEvaluator` (whole-run verdict) | Per-task verifier with structured verdicts, `strict|basic` depth, **fail-closed** (contrast: v1 `SurfaceTaskRunnerAgent` goal-check fails *open*) |
| `TodoProvider` — model mutates the work list | Ledgers persisted but **nothing model-callable** — the model emits the plan once and never updates it |
| plan/execute `AgentModeProvider` | `TriageAsync` `direct|plan` + budget clamp — same concept, host branches on it |
| Volatile in-process task tracking | Ledgers in Orleans custom state; run resumes on reactivation (`OnInitialize` → re-arm tick when `PolicyLedger.IsRunning`). Caveat: the `swarm2-drive` tick is a **grain timer, not a reminder** — the timer dies with the grain; resume relies on the persisted flag |
| No HITL, no member role typing | Approval gate + deterministic SME/Executor role enforcement + transcript mirroring of all agent-to-agent traffic |

**Verdict: compose, don't compete.** Squads own cross-agent runs; the harness upgrades what happens *inside* them:

1. **Async fan-out for the orchestrator/planner** — wrap squad members as `A2AAgentProxy`-backed `BackgroundAgents` so the model gets real `start_task`/`wait_for_first_completion` instead of blocking one-at-a-time `ask_agent` and waiting on the host's 5-second drive tick. This is the sharpest gap the harness closes.
2. **Members as harness agents** — executors gain todos and modes; the supervisor reads member `GetAllTodosAsync` into its status mirror for live sub-task visibility.
3. **TodoProvider fills the model-callable work-list gap** — inside member turns immediately; potentially for supervisor-adjacent ledger updates later.
4. **Budget reconciliation required** — squads + run-safety + harness would stack **three** budget layers (`SurfaceSwarmV2Budgets` per run, `ChatRunSafetyScope` per turn, `LoopAgent`/`MaximumIterationsPerRequest` per harness run). Squad budgets own the run, run-safety owns the turn, harness loop caps stay at defaults unless the squad sets them.

**Maturity notes for planning:** SwarmV2 is one large, well-tested commit (1,104 test lines, 18 facts, zero TODOs) with **no design or skill documentation** — this section is currently its only prose spec. Swarm v1, the Orchestrator squad, and `SurfaceTaskRunnerAgent` are documented but have zero unit tests. `docs\swarm-plan.md` and the `fabrcore-swarm` skill describe a **deleted** system (`FabrCore.Experimental.Swarm` — blackboard, worker agents; stale `ProjectReference`s still dangle in `FabrCore.Tests.csproj`) — treat them as historical. Surface orchestration uses none of the SDK's `TaskWorkingAgent`/`TaskTracking`/`A2AAgentProxy` (parallel implementations).

---

## 5. Integration architecture

### 5.1 Dev-facing API — the native assembler

**FabrCore ships its own harness.** `FabrCoreHarnessAgent` is FabrCore's assembler over the framework's *providers* (all in the core `Microsoft.Agents.AI` package already referenced — the thin `Microsoft.Agents.AI.Harness` package is not used at runtime, so no new dependency). Why native rather than wrapping `AsHarnessAgent`:

- **Server-safe by construction** — nothing is composed unless FabrCore composes it; a future upstream default-on feature (the way file memory shipped default-on with a local-disk store) can never reach tenants.
- **Narrower `[Experimental]` contact surface** — dependency on the individual providers actually used, not the 30-property options bag.
- **FabrCore-authored instructions** — replaces the CLI-flavored upstream preamble; plan mode targets the **todo list** instead of memory files.
- **Full governance** — FabrCore owns function invocation, so provider tools (`todos_*`, `mode_*`) are wrapped in `VerifiableExecutionAIFunction` like every other tool; impossible under the upstream assembler.
- **Tool names stay Microsoft's** (`todos_add`, `mode_set`, …) so prompts, samples, and community knowledge remain portable.

The dev-facing entry point is unchanged — a drop-in sibling of `CreateChatClientAgent` (`src\FabrCore.Sdk\FabrCoreAgentProxy.cs:313` is the template). Make `FabrCoreAgentProxy` partial; add `src\FabrCore.Sdk\Harness\FabrCoreAgentProxy.Harness.cs`:

```csharp
protected Task<HarnessAgentResult> CreateHarnessAgent(
    string chatClientConfigName,
    string threadId,
    IList<AITool>? tools = null,
    Action<FabrCoreHarnessOptions>? configure = null);
```

Internally: build `FabrCoreHarnessOptions` from the 3-tier cascade → get the chat client via existing `GetChatClient` (so `TokenTrackingChatClient → ModelDefaults → ProviderSanitizing → provider` is the innermost `IChatClient` and run-safety sees every call) → assemble the FabrCore pipeline **mirroring Microsoft's tested ordering** (chat-client: approval-response binding → approval bypass → function invocation w/ verifiable-execution tool wrapping → message injection → per-service-call persistence into `FabrCoreChatHistoryProvider` → `CompactionProvider` per §3.2; agent decorators: `LoopAgent` (if configured) → `ToolApprovalAgent` → FabrCore OTel → `ChatClientAgent`) → restore session from snapshot or `CreateSessionAsync()` → register the history provider for storage compaction/projection as today. Conformance tests assert behavioral parity with upstream `HarnessAgent` on shared scenarios (§9 P1).

**Composition (all FabrCore-chosen; nothing else exists in the pipeline):** todos + modes ON (plan-mode instructions overridden per above), tool approval ON with binding, message injection ON, per-service-call persistence ON (buffered), compaction per §3.2 from `ModelConfiguration`, **no file memory and no file access — not even as options** (decision; `fabrcore.host` storage / MCP for genuine file needs), skills OFF (config/MCP source when enabled), web search OFF (capability-gated opt-in), memory socket inert until an `IAgentMemoryService` is registered (§5.3), `MaximumIterationsPerRequest = 40`.

**Escape hatch:** the FabrCore glue is **agent-agnostic** — `HarnessAgentResult`, session snapshots, approval extraction, and injection operate on plain `AIAgent` + `AgentSession`. A dev who wants Microsoft's stock harness calls `chatClient.AsHarnessAgent(...)` and passes the result through the same helpers, with §3.3's off-switches and §2's defaults forced by the platform. Documented as a supported escape hatch and used internally as the conformance baseline — not a second product surface.

The complete agent a dev writes:

```csharp
[AgentAlias("researcher")]
public class ResearcherAgent : FabrCoreAgentProxy
{
    private HarnessAgentResult harness = null!;

    public override async Task OnInitialize()
    {
        var tools = await ResolveConfiguredToolsAsync();
        harness = await CreateHarnessAgent("default", "main", tools);
    }

    public override async Task<AgentMessage> OnMessage(AgentMessage message)
    {
        var approvalReplies = await harness.TryBuildApprovalResponseMessagesAsync(message);
        var run = approvalReplies is not null
            ? await harness.RunAsync(approvalReplies)
            : await harness.RunAsync(message);
        return run.ToAgentMessage(message);
    }

    public override async Task<AgentMessage> OnMessageBusy(AgentMessage message)
    {
        await harness.InjectMessageAsync(message.Message ?? "");
        var ack = message.Response();
        ack.Message = "Got it — passing that to the task in progress.";
        return ack;
    }
}
```

Todos, modes, approvals, session durability, compaction safety, and heartbeat status all come from the platform.

**Zero-code path:** ship a registered `[AgentAlias("harness")]` agent type implementing exactly the class above plus Args-driven wiring, so a blueprint alone yields a full harness agent:

```json
{
  "Handle": "eric:assistant",
  "AgentType": "harness",
  "Models": "default",
  "SystemPrompt": "You are Eric's operations assistant...",
  "Tools": ["sendEmail", "createTask"],
  "McpServers": [{ "Name": "github", "TransportType": "Http", "Url": "https://..." }],
  "Args": {
    "_HarnessMemory": "true",
    "_HarnessLoop": "todo",
    "_HarnessLoopMaxIterations": "8",
    "_HarnessBackgroundAgents": "eric:researcher,eric:writer"
  }
}
```

`HarnessAgentResult` wraps the run loop: `RunAsync` (evidence recording, approval extraction, session snapshot + flush), `TryBuildApprovalResponseMessagesAsync`, `InjectMessageAsync` (via `Agent.GetService<MessageInjectingChatClient>()`), `SnapshotSessionAsync`.

### 5.2 Session-snapshot persistence (the #1 prerequisite)

Today `OnInitialize` runs on every grain activation and always calls `CreateSessionAsync()` — the `AgentSession` state bag is never persisted, so every harness feature would reset per activation.

Design: persist `agent.SerializeSessionAsync(session)` into grain `CustomState` under `"_harness_session:{threadId}"` (envelope: version, threadId, timestamp, harness package version, `JsonElement` payload) using the existing `SetState`/`FlushStateAsync` → `MergeCustomStateAsync` path.

- **When:** post-turn (inside `RunAsync`) + on-deactivate backstop. One grain change: flush pending custom state in `HandlePrimaryMessage`'s `finally`, next to the existing `FlushAllChatHistoryProvidersAsync()` (`src\FabrCore.Host\Grains\AgentGrain.cs:979`).
- **Restore:** in `CreateHarnessAgent` — `TryGetStateAsync` → `DeserializeSessionAsync`; on corruption, archive the bad payload under `"_harness_session_corrupt:{threadId}"`, log, fall back to a fresh session. Worst case: todos/mode/standing approvals reset; **conversation continuity survives** because history lives in `MessageThreads`, not the snapshot (that's why `FabrCoreChatHistoryProvider` is the pipeline's `ChatHistoryProvider` — snapshots stay KB-scale).
- **Scope note:** this requirement is independent of every other decision in this document — even the minimal native harness (todos/modes/approvals, no files, no memory) is broken without it.
- **Size guard:** warn > 256 KB, refuse + keep last good > 1 MB (whole-blob `WriteStateAsync` makes this matter).
- **Cleanup:** `RemoveState` on `OnReset`/`ClearThread`.

### 5.3 Memory socket (no file stores)

The file-store workstream (`IFileStoreGrain` / `GrainBackedAgentFileStore`) is **deleted** with the no-file-surfaces decision. In its place, the native harness exposes a **memory socket**:

- The abstraction is `IAgentMemoryService` (+ the models its signature needs), moved OSS from `FabrCore.Services.Contracts`-style splitting (§8).
- When an implementation is registered in DI (the commercial `FabrCore.Services.Memory`), `_HarnessMemory=true` adds the `agent-memory` plugin tools (`SaveMemory`, `RecallMemories`, …) to the agent and an `AIContextProvider` that injects `FormatRecallContext` / hot-index content using the existing `<memory-context>` marker protocol (§4.1).
- Without an implementation the socket is inert — the OSS harness runs with todos/modes/approvals only, and the blueprint key is a no-op with a startup log line.
- Agents that genuinely need file storage use the existing `fabrcore.host` storage services (`IFileStorageService`, `fabrcoreapi/File` endpoints) or a scoped MCP server — both governed by the standard tool pipeline, `VerifiableExecutionAIFunction`, and approvals.

### 5.4 Tool approval over FabrCore channels

The harness *ends the run* when approval is needed and surfaces `ToolApprovalRequestContent` — a perfect fit for the grain request/response model (nothing blocks).

1. **Surface:** `HarnessRunResult.ToAgentMessage` emits `MessageType = "_approval_request"` with a human-readable fallback text plus `Data = [{ requestId, toolName, argumentsJson, threadId }]`.
2. **Render:** WebSocket/REST clients get JSON; the M365 bridge maps to an Adaptive Card (Approve / Always allow tool / Always allow with these args / Deny). Offline users get it via the `PrincipalGrain` durable outbox — it's just an `AgentMessage`.
3. **Round-trip:** reply `_approval_response` with `Args["_approval_request_id"]` + decision → `TryBuildApprovalResponseMessagesAsync` rebuilds `request.CreateResponse(...)` / `CreateAlwaysApproveToolResponse(...)` as a user `ChatMessage` → next `RunAsync` resumes the tool call (the harness's response binding validates it against the surfaced request — restored from the session snapshot). Plain-text fallback: single pending approval + "approve"/"deny" text maps directly — critical for Teams DX.
4. **Durability:** binding + standing rules ride the session snapshot; additionally persist surfaced requests under `"_harness_pending_approvals:{threadId}"` so exact response contents can be rebuilt on a fresh activation.
5. **Modes:** `_HarnessApprovalMode` = `auto` (default: standing rules + heuristics + read-only auto-approve) | `always` (every approval-required tool asks) | `unattended` (all auto-approved — for reminder-driven autonomous agents; loudly logged).

### 5.5 Config surface (`_Harness*` Args, 3-tier cascade)

`ModelConfiguration` needs **no new keys** — `ContextWindowTokens` and `MaxOutputTokens` already exist and feed harness compaction. Per-agent Args (defaults in parentheses):

| Key | Effect |
| --- | --- |
| `_Harness` (false) | zero-code agent type / template switch |
| `_HarnessInstructions` (null) | `HarnessInstructions` override; `""` = no preamble |
| `_HarnessMemory` (false) / `_HarnessMemoryScope` (agent handle) | memory socket: registers `agent-memory` tools + recall context provider when an `IAgentMemoryService` implementation is installed (§5.3); no-op otherwise |
| `_HarnessTodo` (true) / `_HarnessMode` (true) | todo / mode providers |
| `_HarnessSkills` (false) / `_HarnessSkillsMcpServers` (null) | skills via FabrCore source; MCP skills from named `McpServers` |
| `_HarnessWebSearch` (false) | hosted web search |
| `_HarnessApprovalMode` (auto) | §5.4 |
| `_HarnessLoop` (none: todo\|marker\|judge\|background, csv) / `_HarnessLoopMaxIterations` (10) / `_HarnessLoopJudgePrompt` | loop evaluators |
| `_HarnessMaxIterationsPerRequest` (40) | function-invocation cap |
| `_HarnessBackgroundAgents` (null, csv of handles) | A2A-backed background agents |
| `_HarnessCompaction` (true for harness agents) | in-run compaction (framework `CompactionProvider`, wired by the native assembler) per §3.2; turning it off reverts full FabrCore ownership |
| `_HarnessFlushPerServiceCall` (false) | per-model-call history flush for crash-critical agents |

### 5.6 One full turn (with deactivation mid-approval)

1. **In:** WebSocket → `AgentGrain.OnMessage` → `HandlePrimaryMessage` (heartbeat, monitoring) → `InternalOnMessage` (LlmUsageScope + ChatRunSafetyScope begin; FabrCore preflight storage compaction may run) → dev `OnMessage` → `harness.RunAsync(message)`.
2. **Run:** LoopAgent → ToolApprovalAgent → FabrCore OTel → ChatClientAgent → FabrCore-assembled pipeline: approval binding/bypass → function invocation (provider tools wrapped in `VerifiableExecutionAIFunction`) → message injection (drains anything `OnMessageBusy` enqueued) → per-service-call persistence (buffers into `FabrCoreChatHistoryProvider`) → **CompactionProvider (in-run windowing)** → `TokenTrackingChatClient` (run-safety checkpoints, budget kill-switch) → provider. Provider tools (todos/modes, memory-socket tools if enabled) mutate session-bag state; status updates flow out on the 3s `_status` heartbeat.
3. **Approval needed:** run ends with `ToolApprovalRequestContent` → `RunAsync` records evidence, persists pending approvals + session snapshot (`SetState` → `FlushStateAsync`), grain flushes history → `_approval_request` goes out over WebSocket / Teams card / principal outbox.
4. **Idle:** grain deactivates. Nothing lost — history in `MessageThreads`, harness state + pending approvals in `CustomState`.
5. **Resume:** approval reply re-activates the grain → `OnInitialize` → `CreateHarnessAgent` restores the snapshot → `TryBuildApprovalResponseMessagesAsync` builds the response content → `RunAsync` → binding validates → tool executes (standing rule stored if "always") → post-turn FabrCore storage compaction if over threshold → snapshot + flush → answer out.

---

## 6. Net-new opportunities, ranked by dev-experience impact

1. **Batteries-included agents via blueprint** — one JSON flag turns a FabrCore agent into a full native-harness agent (todos + modes + approvals + memory socket pre-wired, server-safe by construction). Config-driven composition vs hand-assembling Microsoft's 30-property, partly-experimental options object is exactly the DX moat.
2. **Durable memory socket** — the native harness's memory slot backed by the commercial Services.Memory graph: durable, scoped recall and save tools no .NET harness can match (the framework's durable memory is Python-only). OSS ships the socket; V365 sells the brain (§5.3, §8).
3. **Multi-channel tool approvals with standing rules** — harness anti-forgery + "don't ask again", delivered over Teams/WebSocket/REST with the durable outbox. No competing runtime has the combination.
4. **Fleet-level observability** — `GetAllTodosAsync`, mode state, and loop iteration surfaced through monitoring APIs and the 3s heartbeat: a live dashboard of what every agent is planning and doing, plus per-agent cost from `TokenTrackingChatClient`.
5. **Durable autonomous loops** — Orleans reminders (survive restarts) triggering `LoopAgent` runs (todo/judge evaluators), bounded by run-safety budgets: "Ralph loops" with an SLA and a spending cap.
6. **Background-agent fan-out over A2A** — model-driven `start_task` delegation onto durable, ACL'd, monitored FabrCore agents rather than in-proc tasks. Commercial follow-on: the SwarmV2 orchestrator/planner today delegate through *blocking* `ask_agent` calls and wait on the host's 5-second drive tick — wrapping squad members with `BackgroundAgentsProvider` gives them true async `start_task`/`wait_for_first_completion` semantics (§4.2).
7. **Mid-turn steering** — message injection exposed via `OnMessageBusy` and as a channel verb, paired with heartbeat status: see progress, redirect the run.
8. **Skills as managed platform content** — config-server-versioned `AgentSkillsSource` with per-agent assignment and MCP skills; no CWD scanning, centrally auditable.
9. **Provider-actual-informed compaction** — the `ChatRunSafetyScope`-aware trigger + real tokenizer (§3.2): "compaction that knows your real token bill," unreachable through the stock harness.
10. **Conversation→durable memory bridge (commercial)** — compaction summaries and session content feeding `FabrCore.Services.Memory` through `ExtractMemoriesAsync` (§4.1): conversation overflow becomes taxonomy-typed, deduplicated, graph-linked durable memories at compaction boundaries, with the hot index injected into harness turns via the memory socket. The .NET framework has **no durable-memory provider at all** (the Python `MemoryContextProvider` was never ported) — a capability the stock harness cannot match on .NET, and it's already built.

---

## 7. Risks & watch-items

| Risk | Mitigation |
| --- | --- |
| **Session bag not persisted (prerequisite)** — until snapshots land, todos/mode/approval rules/compaction groups reset every activation; harness features *appear* amnesiac | Sequence §5.2 into Phase 1 before defaulting any harness feature on |
| **Double compaction** — harness `CompactionProvider` + FabrCore `CompactionService`/mid-turn checkpoint fighting over the same thread | Ownership boundary + off-switch matrix (§3.2–3.3); `MidTurnCompactionEnabled` forced off under harness mode |
| **Uncapped harness agents** — no token params ⇒ no compaction at all (silent) | `CreateHarnessAgent` validates `ContextWindowTokens`/`MaxOutputTokens` are configured (or an explicit strategy/disable) |
| **Per-service-call persistence write amplification** — whole-blob `WriteStateAsync` per flush | Non-issue by default (provider buffers; durable writes stay at existing flush points). `_HarnessFlushPerServiceCall` opt-in; long-term delta-append thread storage |
| **Server-hostile upstream defaults** — local-disk file store, CWD skills discovery, web search always on | Structurally eliminated by the native assembler: nothing is composed unless FabrCore composes it. Applies only to escape-hatch agents, where the platform forces the same off-switches |
| **`[Experimental]` API churn** across framework providers and the Compaction namespace | The native assembler narrows the contact surface to the individual providers actually composed; isolate behind `FabrCoreHarnessOptions` + blueprint schema; contain `#pragma warning disable` in the SDK `Harness\` folder (same pattern as existing `MEAI001` suppressions) |
| **Assembler drift** — owning the pipeline means Microsoft's tested wiring (decorator order, binding flags, persistence requirements) must be tracked release-over-release | Conformance tests asserting parity with upstream `HarnessAgent` on shared scenarios; diff `HarnessAgent.cs` each framework release — this is the "stay on top of the framework" activity, made concrete |
| **VerifiableExecution bypass (escape hatch only)** — under the *upstream* assembler, provider-built tools (`todos_*`, `mode_*`, skills) never pass through `VerifiableExecutionAIFunction` | Closed by the native assembler (FabrCore owns function invocation and wraps provider tools directly). For escape-hatch agents: post-run evidence recorder walking `FunctionCallContent`/`FunctionResultContent` from the response; document the boundary |
| **Shell security** | Default off; if ever: Docker-only, non-root, network-none, denyList, approval-required, fully monitored |
| **Summarizer prompt injection** — summaries persist as trusted history (harness `SummarizationCompactionStrategy` docs flag it; FabrCore's `[Compacted History]` is a *system* message — higher privilege) | Hardening pass on the summary role/labeling regardless of layer |
| **`A2AAgentProxy.Name` is null** — `BackgroundAgentsProvider` requires non-empty unique names | One-line override (`Name => handle`) + validation with a clear error in `CreateHarnessAgent` |
| **Triple budget stacking under squads** — SwarmV2 run budgets + per-turn `ChatRunSafetyScope` + harness loop caps would be three uncoordinated layers | Reconcile explicitly when harness agents join squads: squad budgets own the run, run-safety owns the turn, harness loop caps stay at defaults unless the squad sets them (§4.2) |
| **Squad resume rides grain timers, not reminders** — the `swarm2-drive` tick dies with the grain; resume depends on persisted `PolicyLedger.IsRunning` + reactivation re-arming | Fine while traffic keeps grains warm; for idle-resume guarantees move the drive tick to `RegisterReminder` (durable, ≥ 1 min) — V365 workstream item |
| **Naming collision** — FabrCore's *test* harness (`.agents\skills\fabrcore-testing\assets\test-harness.cs`) vs the framework feature | Disambiguate in docs/skills; update `.agents\skills\fabrcore-agentframework\SKILL.md` when code lands (roadmap item) |

---

## 8. Open-core boundary: what moves from FabrCore-V365 into OSS

**Principle: OSS the contracts, seams, and commodity utilities; keep the engines and operational surfaces commercial.** The open-source runtime is the adoption funnel being compared against "just hand-wire Microsoft's harness" — it needs the *sockets*. The commercial layer sells the *machinery that plugs into them*.

**Move to OSS (this repo):**

1. **Memory abstractions, not the engine** — `IAgentMemoryService` / `IAgentMemoryProvider` + the models the interface needs (`MemoryRecallResult`, `MemoryIndex`, `MemoryTemperature`, `MemoryType`) into the SDK (or a small `FabrCore.Memory.Abstractions`). Required by the native harness's memory socket (§5.3): the interface must live where the harness lives. Mechanically cheap — V365 already practices this split (`FabrCore.Services.Contracts` + type forwarders), and it's Microsoft's own pattern (capabilities in core, assembler thin). **Includes deleting the dead `src\FabrCore.Sdk\Memory\` folder** (excluded from compilation at `FabrCore.Sdk.csproj:15`, superseded).
2. **`ToolResultCompressor`** — static, no-LLM head/tail tool-result compression currently sitting in the commercial memory service as "Tier 1" but having nothing to do with memory. Moves into `CompactionService` as a pre-summarization tier (compress bulky tool results before spending LLM tokens on map-reduce). Commodity; zero commercial leakage; improves the free product at the exact point the harness comparison is fought.
3. **The capability roster** — generalize `SurfaceSquadAgentCapability` / `SurfaceSwarmV2CapabilityRegistry` into an SDK `AgentRosterBuilder`: `IFabrCoreRegistry` metadata + live `GetAgentHealth` → prompt-ready roster. Both data sources are already OSS — the commercial code only assembles them. Feeds harness `BackgroundAgents` (names/descriptions for `A2AAgentProxy` members), any community orchestrator, and lets SwarmV2 delete its near-duplicate of the V1 projection.

**Consider (strategic calls, not blockers):** generic multi-agent group provisioning (the squad *shape* — "N agents from one definition" — is runtime-level scaffolding; the SwarmV2 *engine* is the moat); A2A transcript mirroring as a host option (observability, already args-based).

**Stays commercial:** ~~the Services.Memory engine~~ — **superseded 2026-07-29 (twice; final state in [oss-platform-plan.md](oss-platform-plan.md))**: the Memory *and* GraphRag engines move OSS ([memory-graphrag-oss-plan.md](memory-graphrag-oss-plan.md)), and `FabrCore.Surface` — **including the SwarmV2 execution engine, renamed to plain "Swarm" on import (v1 retired)** — moves OSS as well. The commercial boundary is now: **Forge** (cloud config server + the hosted admin console — `FabrCore.Surface.Admin` migrates into the Forge product) plus the Vulcan365 markdown-conversion service. Boundary principle: develop/create/chat = OSS; operate/govern/fleet = Forge; admin APIs open, console commercial. The memory socket design (§5.3) is unchanged and binds to the OSS implementation.

**V365 hygiene (same workstream):** remove the dangling `ProjectReference`s in `FabrCore.Tests.csproj` to the deleted `FabrCore.Experimental.Swarm` / `FabrCore.Agents.TaskAgent` projects; mark `docs\swarm-plan.md` and the `fabrcore-swarm` skill as historical; write the missing SwarmV2 design/skill doc (§4.2 is currently its only prose spec); have Services.Memory consume the OSS abstractions via type forwarders.

---

## 9. Phased roadmap (each phase independently shippable)

| Phase | Contents | Size |
| --- | --- | --- |
| **P1 — Native harness core** | `FabrCoreHarnessAgent` assembler over core-package providers (no new package ref) + `FabrCoreHarnessOptions` + agent-agnostic `HarnessAgentResult` (run, approval extraction as raw content, injection, snapshot); **session snapshot persist/restore** (§5.2) incl. the grain post-turn custom-state flush; compaction wiring per §3.2 + off-switches (§3.3); plan-mode instruction override (todo list, not memory files); provider-tool `VerifiableExecutionAIFunction` wrapping; conformance tests vs upstream `HarnessAgent`; escape-hatch support documented | ~1 wk |
| **P2 — Open-core moves + memory socket** | Memory abstractions into OSS + delete dead `Sdk\Memory` (§8); `ToolResultCompressor` into `CompactionService`; `AgentRosterBuilder`; memory socket live in the native harness + commercial `Services.Memory` plug-in verified end-to-end (§5.3) | ~1 wk |
| **P3 — Channel-grade approvals + zero-code** | `_approval_request`/`_approval_response` message types; WebSocket JSON contract; Teams Adaptive Cards in the M365 bridge; pending-approval persistence + plain-text fallback; shipped `[AgentAlias("harness")]` agent type + full `_Harness*` Args surface | ~1 wk |
| **P4 — Skills, background agents, polish** | Config/MCP `AgentSkillsSource`; `A2AAgentProxy.Name` fix + background agents (roster-fed descriptions); loop evaluators from config; reminder-driven durable-loop template + docs; heartbeat status tracker (todo/mode → `SetStatusMessage`); the `ChatRunSafetyScope`-aware compaction trigger (§6.9) | ~1 wk |

**Back-compat guarantees:** `CreateChatClientAgent`, `ForkAsync`/`TaskWorkingAgent`, existing compaction/projection/run-safety paths untouched; the harness path is purely additive; blueprints without `_Harness*` args behave identically; `IFabrCoreAgentHost` unchanged (the file-store surface it would have grown is deleted with the no-file decision).

**Commercial-layer follow-ups (FabrCore-V365 workstream, sequenced after P2):** wire `BackgroundAgentsProvider` into the SwarmV2 orchestrator/planner via `A2AAgentProxy`-wrapped members (§4.2); run squad executors as native harness agents (todos + modes) and surface member `GetAllTodosAsync` into the supervisor's status mirror; land the `ExtractMemoriesAsync` bridge from compaction summaries into `FabrCore.Services.Memory` plus the recall context provider (§4.1); schedule memory consolidation via Orleans reminders; reconcile the three budget layers; consider moving the `swarm2-drive` tick to a durable reminder; execute the §8 hygiene list.

---

## Appendix: source anchors

**Agent framework** (`C:\repos\Microsoft\agent-framework`): `dotnet\src\Microsoft.Agents.AI.Harness\{HarnessAgent.cs, HarnessAgentOptions.cs, ChatClientHarnessExtensions.cs}`; capabilities under `dotnet\src\Microsoft.Agents.AI\Harness\` and `...\Compaction\`, `...\Skills\`; shell in `Microsoft.Agents.AI.Tools.Shell` (preview); samples `dotnet\samples\02-agents\Harness\`.

**FabrCore** (this repo, open source): `src\FabrCore.Sdk\FabrCoreAgentProxy.cs:313` (`CreateChatClientAgent` — the template), `src\FabrCore.Sdk\{CompactionService.cs, ChatRunSafetyScope.cs, TokenTrackingChatClient.cs, FabrCoreChatMessageStore.cs, A2AAgentProxy.cs, IFabrCoreAgentHost.cs}`, `src\FabrCore.Core\{ModelConfiguration.cs, AgentGrainState.cs}`, `src\FabrCore.Host\Grains\AgentGrain.cs`.

**FabrCore-V365** (commercial, `C:\repos\FabrCore-V365`): `src\FabrCore.Services.Memory\{Abstractions\IAgentMemoryService.cs, Services\AgentMemoryService.cs, Services\MemoryAwareCompactionService.cs, Services\MemorySchemaInitializer.cs, Services\ToolResultCompressor.cs, Plugin\AgentMemoryPlugin.cs, Configuration\MemoryScopeResolver.cs}`; `src\FabrCore.Surface\Ai\SwarmV2\{SurfaceSwarmV2OrchestratorAgent.cs, SurfaceSwarmV2SupervisorAgent.cs, SurfaceSwarmV2PlannerAgent.cs, SurfaceSwarmV2VerifierAgent.cs, SurfaceSwarmV2Ledgers.cs, SurfaceSwarmV2BudgetGuard.cs, SurfaceSwarmV2PlanValidation.cs, SurfaceSwarmV2CapabilityRegistry.cs}`, `src\FabrCore.Surface\Ai\{Swarm\, Orchestration\, Tasks\SurfaceTaskRunnerAgent.cs}`, `src\FabrCore.Surface\CommandCenter\SurfaceBlueprintProvisioner.cs`, tests `src\FabrCore.Surface.Tests\SurfaceSwarmV2Tests.cs`.
