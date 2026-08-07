# Harness Configuration Reference

Every harness setting is reachable from blueprint `Args`, so an agent class written once can be
re-tuned without a rebuild. Code can still override anything through the `configure` callback.

## Precedence

Settings are resolved in this order, each layer overriding the one before:

1. **Defaults** baked into `CreateFabrCoreHarnessAgent`.
2. **`_Harness*` args** from `AgentConfiguration.Args`.
3. **The `configure` callback** — runs after args are read, so code always wins over config.
4. **Loop-mode fallback** — applies only when neither args nor code named a loop mode.

Step 4 is the one surprise worth internalizing. If `_HarnessLoop` is absent *and* `configure` left
`LoopMode` at `None`, the harness fills in `Todo`, plus `Background` when delegates exist. To get a
genuinely single-shot agent you must say so — `"_HarnessLoop": "none"` or
`o => o.LoopMode = HarnessLoopMode.None` — rather than simply omitting the setting.

## Argument keys

Constants live on `FabrCore.Sdk.HarnessArgs` (`src/FabrCore.Sdk/Harness/HarnessArgs.cs`).

| Key | Type | Default | Effect |
|-----|------|---------|--------|
| `_HarnessMode` | bool | `true` | Registers `mode_get` / `mode_set` and plan/execute instructions |
| `_HarnessDefaultMode` | string | `plan` | Initial mode for a fresh session and non-`AgentMessage` run paths |
| `_HarnessTodo` | bool | `true` | Registers the `todos_*` tools |
| `_HarnessLoop` | csv | `todo` (+ `background` when delegates exist) | Loop evaluators — see below |
| `_HarnessLoopMaxIterations` | int | `10` | Iteration cap. Clamped to at least 1 |
| `_HarnessLoopMarker` | string | — | Completion marker. Required by loop mode `marker` |
| `_HarnessLoopJudgeModel` | string | the agent's own model | Chat client config for loop mode `judge` |
| `_HarnessLoopJudgePrompt` | string | framework default | Judge instructions for loop mode `judge` |
| `_HarnessBackgroundAgents` | csv of handles | — | Agents the model may delegate to |
| `_HarnessBackgroundTimeoutSeconds` | int | `120` | Bound on one delegation. Values below 1 are ignored |
| `_HarnessSkills` | csv of `name@version` | — | Principal-scoped immutable skills loaded from Host typed Storage |
| `_HarnessMaxIterationsPerRequest` | int | `40` | Function-invocation iterations within one model request |
| `_HarnessInstructions` | string | built-in preamble | Replaces the preamble. **Empty string drops it entirely** |
| `_HarnessSessionPersistence` | bool | `true` | Persists the session across turns and deactivations |

`_HarnessLoop` accepts `todo` (or `todos`), `background` (or `delegation`), `marker` (or
`completion`), `judge`, and `none` (or `off`), comma-separated and case-insensitive.

## Parsing rules

These follow the established `_Context*` / `_Compaction*` / `_Projection*` convention:

- **Unparseable values fall back silently.** `"_HarnessTodo": "maybe"` leaves todos enabled; no
  warning, no throw. Deliberate — a typo in config must not take an agent down.
- **Unrecognized loop tokens log a warning and are skipped.** `"todo,teleport"` yields `Todo` and a
  warning naming the bad token. This is the one case that gets a log line, because a silently
  ignored loop mode is hard to diagnose from behavior alone.
- **Keys are case-sensitive.** `_HarnessLoop` works; `_harnessloop` is ignored.
- **Skill references are strict.** Every `_HarnessSkills` entry must include an exact version. A malformed, missing, or corrupt package fails `OnInitialize` with all bad references listed.
- **`_HarnessInstructions` treats empty as meaningful.** Every other string key blank-guards; this
  one does not, because an empty value is how you say "no preamble".

Genuine misconfiguration still throws, at `OnInitialize`:

- `_HarnessLoop: "todo"` with `_HarnessTodo: "false"` — nothing can drive the loop.
- `_HarnessDefaultMode` names a mode that is not configured.
- `_HarnessLoop: "background"` with no reachable delegates — the loop could never observe progress.
- `_HarnessLoop: "marker"` without `_HarnessLoopMarker`.

These fail loudly on purpose: each is a request the harness cannot honor, and papering over it
would produce an agent that looks configured and silently is not.

## Blueprint example

Bare handles are recommended — the principal comes from `x-user-handle` at apply time, so the same
blueprint provisions `eric:assistant` or `dana:assistant` unchanged.

```json
{
  "name": "ops-desk",
  "version": "1.0.0",
  "agents": [
    {
      "handle": "assistant",
      "agentType": "harness-researcher",
      "models": "default",
      "systemPrompt": "You are the operations assistant. Prefer primary sources.",
      "description": "Researches operational questions end to end.",
      "tools": ["sendEmail"],
      "args": {
        "_HarnessMode": "true",
        "_HarnessDefaultMode": "plan",
        "_HarnessLoop": "todo,background",
        "_HarnessLoopMaxIterations": "8",
        "_HarnessSkills": "policy-review@1.2.0,invoice-rules@2026-08-01",
        "_HarnessBackgroundAgents": "crm,policy-desk",
        "_HarnessBackgroundTimeoutSeconds": "180"
      }
    }
  ]
}
```

`_HarnessBackgroundAgents` entries are resolved with `AgentRosterBuilder`, which accepts bare or
principal-qualified handles. Bare entries are probed as written, so qualify them when the target
lives under a different principal and the caller has a grant for it.

## Blueprint lifecycle caveat

**Re-applying a blueprint does not pick up changed args on a live agent.** The blueprint path forces
`ForceReconfigure = false` (`src/FabrCore.Host/Services/FabrCoreAgentService.cs:163`), so
`PrincipalGrain.CreateAgent` sees the agent already tracked and only health-probes it.

To make a tuning change take effect:

- `POST /fabrcoreapi/Agent/create` with `"ForceReconfigure": true`, or
- reset the agent (which also clears custom state, and with it the harness session), or
- evict and re-provision.

Expect to hit this the first time you tune `_HarnessLoopMaxIterations` and see no change. See
**fabrcore-server → references/blueprints.md** for the full lifecycle rules.

## Code configuration

`FabrCoreHarnessOptions` (`src/FabrCore.Sdk/Harness/FabrCoreHarnessOptions.cs`) is the full surface.
Reach for it when a setting has no arg equivalent — custom evaluators, a bespoke judge client, extra
context providers, or delegates you construct yourself.

```csharp
harness = await CreateFabrCoreHarnessAgent(
    config.Models ?? "default",
    "main",
    tools,
    options =>
    {
        // Delegates built in code rather than from _HarnessBackgroundAgents.
        options.BackgroundAgents = squadMembers;

        // Explicit loop composition, suppressing the fallback in step 4 above.
        options.LoopMode = HarnessLoopMode.Todo | HarnessLoopMode.Background;
        options.LoopMaxIterations = 12;

        // An evaluator of your own, appended after the ones implied by LoopMode.
        options.AdditionalLoopEvaluators = [new DelegateLoopEvaluator(MyRule)];
    });
```

| Property | Notes |
|----------|-------|
| `Id`, `Name`, `Description` | `Name` defaults to the agent handle. Must be non-empty if this agent is itself delegated to |
| `ChatOptions` | Carries the agent's tools and its own instructions. Set by the proxy from `config.SystemPrompt` and the `tools` argument |
| `HarnessInstructions` | `null` uses `FabrCoreHarnessAgent.DefaultInstructions`; `""` drops the preamble |
| `ChatHistoryProvider` | Set by the proxy to `FabrCoreChatHistoryProvider`. Overriding it moves history out of Orleans — and into the session snapshot |
| `AIContextProviders` | Appended after the harness's own providers |
| `DisableAgentModeProvider`, `AgentModeProviderOptions` | Mode tools and instructions. Null options use FabrCore's todo-backed `plan` / `execute` modes |
| `PlanningModeName`, `ExecutionModeName` | Map `_plan-mode` to custom code-configured modes; both names must exist and be distinct |
| `DisableTodoProvider`, `TodoProviderOptions` | Todo tools and their instructions/list rendering |
| `BackgroundAgents`, `BackgroundAgentsProviderOptions` | Any `AIAgent` with a non-empty, case-insensitively unique `Name` |
| `AgentSkillsSource`, `AgentSkillsProviderOptions` | Skills are composed only for an explicit source. The callback may replace or null the `_HarnessSkills` source; no current-directory discovery occurs |
| `LoopMode`, `LoopMaxIterations`, `LoopAgentOptions` | Supplying `LoopAgentOptions` uses it verbatim and ignores `LoopMaxIterations` |
| `LoopCompletionMarker`, `LoopJudgeChatClient`, `LoopJudgeOptions` | Required by `Marker` and `Judge` respectively |
| `AdditionalLoopEvaluators` | Appended after the mode-implied evaluators |
| `MaximumIterationsPerRequest` | Function-invocation cap within one model request |
| `DisableOpenTelemetry`, `EnableSensitiveTelemetryData`, `OpenTelemetrySourceName` | Sensitive data defaults to on, matching `CreateChatClientAgent` |

## Per-message mode selection

The blueprint default initializes a fresh session, but the normal FabrCore path is intentionally per-message. Call `harness.RunAsync(message)` with the complete `AgentMessage`; the wrapper reads `Args["_plan-mode"]` before the run. Missing, invalid, or `true` selects `PlanningModeName`; `false` selects `ExecutionModeName`. This selection occurs after session restoration and therefore wins for every inbound message. The model may subsequently call `mode_set` during that run.

The string and `IEnumerable<ChatMessage>` overloads cannot see `AgentMessage.Args`; they retain the restored/current mode or initialize it from `_HarnessDefaultMode`. `_HarnessMode=false` removes the provider, ignores `_plan-mode`, and restores the previous mode-independent todo-loop behavior.

## Model configuration

The harness needs no harness-specific `fabrcore.json` keys. The compaction ladder reads
`ContextWindowTokens` and `MaxOutputTokens` as its anchor, plus `ContextCompactionEnabled`,
`PerTurnMaxInputTokens`, `MaxPromptInputTokens`, and the `_Context*` / `_Compaction*` /
`_Projection*` args — see **fabrcore-agent → Context Management: the compaction ladder**.

Set both `ContextWindowTokens` and `MaxOutputTokens`. Without them layer 1 cannot be composed and a
harness agent runs its whole tool loop with no in-run context bound; the startup log says
`context:unconfigured` when this happens.

One interaction worth knowing: loop iterations and delegations multiply LLM calls, so a harness
agent reaches a per-turn token budget sooner than a single-shot agent on the same model. If runs
stop early with `_error` and `_fabrcore_run_stop_reason`, that is `ChatRunSafetyScope` doing its job
— raise `PerTurnMaxInputTokens` or lower `_HarnessLoopMaxIterations`, and prefer the latter first.
