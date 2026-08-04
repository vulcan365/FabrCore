---
name: fabrcore-harness
description: >
  Build long-running FabrCore agents that plan their own work and delegate it — the FabrCore agent
  harness composes a model-managed todo list, an iteration loop that keeps the agent working until
  the plan is done, and background delegation onto other FabrCore agents, with the whole session
  persisted across grain deactivation.
  Triggers on: "harness", "agent harness", "harness agent", "FabrCoreHarnessAgent",
  "AsFabrCoreHarnessAgent", "CreateFabrCoreHarnessAgent", "FabrCoreHarnessOptions",
  "FabrCoreHarnessResult", "HarnessLoopMode", "HarnessArgs", "HarnessSessionSnapshot",
  "IHarnessSessionStore", "FabrCoreBackgroundAgent", "AgentRosterBuilder", "AgentRoster",
  "TodoProvider", "todos_add", "todos_complete", "todo list", "LoopAgent", "LoopEvaluator",
  "TodoCompletionLoopEvaluator", "BackgroundTaskCompletionLoopEvaluator",
  "CompletionMarkerLoopEvaluator", "AIJudgeLoopEvaluator", "BackgroundAgentsProvider",
  "background_agents_start_task", "background agents", "delegate to another agent",
  "agent fan-out", "iteration loop", "keep working until done", "_Harness", "_HarnessLoop",
  "_HarnessBackgroundAgents", "AsHarnessAgent", "Microsoft.Agents.AI.Harness", "HarnessAgent".
  Do NOT use for: the FabrCore in-memory unit-test harness (FabrCoreTestHarness, TestFabrCoreAgentHost) — use fabrcore-testing.
  Do NOT use for: ordinary single-turn agents built with CreateChatClientAgent — use fabrcore-agent.
  Do NOT use for: AIAgent, AgentSession, or ChatClientAgent internals — use fabrcore-agentframework.
  Do NOT use for: host-driven multi-agent squads and their blueprint extension — use fabrcore-surface.
allowed-tools: "Bash(dotnet:*) Bash(mkdir:*) Bash(ls:*) Bash(pwsh:*) Bash(powershell:*) Bash(git:*) Bash(dir:*)"
---

# FabrCore Agent Harness

Give an agent a work list, a loop, and colleagues. The harness composes a model-managed todo list, an iteration loop that re-invokes the agent until the plan is finished, and delegation onto other FabrCore agents — then persists the whole thing so it survives grain deactivation.

Everything lives in `src/FabrCore.Sdk/Harness/`. It is purely additive: `CreateChatClientAgent` and every existing agent behave exactly as before.

## Purpose

A plain `ChatClientAgent` answers one message. Its tool loop can run many tool calls, but when the model stops emitting tool calls the turn is over — whether or not the work is actually done. That is the right shape for a chat agent and the wrong shape for "audit these 40 records", "research this and write it up", or "coordinate three specialists".

The harness closes that gap with three cooperating pieces:

| Piece | What the model gets | What it changes |
|-------|--------------------|-----------------|
| **Todos** | `todos_add`, `todos_complete`, `todos_remove`, `todos_get_remaining`, `todos_get_all` | The plan becomes typed state the host can read, not prose buried in the transcript |
| **Loop** | nothing — it is invisible to the model | When the model stops with work outstanding, the agent is re-invoked instead of returning |
| **Background agents** | `background_agents_start_task`, `background_agents_wait_for_first_completion`, `background_agents_get_task_results`, `background_agents_get_all_tasks`, `background_agents_continue_task`, `background_agents_clear_completed_task` | The model fans work out to real, ACL-governed, monitored FabrCore agents and collects results |

Tool names are Microsoft's, unchanged, so prompts and community examples stay portable.

## When to Use This

**Use the harness when:**

- The work has steps and the agent should keep going until they are done, not stop at the first plausible answer.
- You want the plan readable by the host — a status heartbeat, a dashboard, a supervisor — not just visible in the chat log.
- The agent should hand work to other FabrCore agents and act on what comes back.
- Progress must survive a grain deactivation between user turns.

**Do not use the harness when:**

- The agent answers one question per turn. `CreateChatClientAgent` is simpler and cheaper — see **fabrcore-agent**.
- The *host* owns the plan and dispatch, not the model. That is a squad — see **fabrcore-surface**.
- You want Microsoft's stock composition including file memory, filesystem skills, and hosted web search. Those are deliberately absent here; call `chatClient.AsHarnessAgent(...)` directly if you truly want them, and read "What This Deliberately Does Not Do" first.

## Two Entry Points

```csharp
// 1. Pure assembler — mirrors Microsoft's IChatClient.AsHarnessAgent. No FabrCore context needed.
public static FabrCoreHarnessAgent AsFabrCoreHarnessAgent(
    this IChatClient chatClient,
    FabrCoreHarnessOptions? options = null,
    ILoggerFactory? loggerFactory = null,
    IServiceProvider? services = null);

// 2. Ergonomic path on FabrCoreAgentProxy — reads _Harness* args, wires FabrCore infrastructure,
//    restores the persisted session, then calls (1).
protected Task<FabrCoreHarnessResult> CreateFabrCoreHarnessAgent(
    string chatClientConfigName,
    string threadId,
    IList<AITool>? tools = null,
    Action<FabrCoreHarnessOptions>? configure = null);
```

**Use `CreateFabrCoreHarnessAgent` inside an agent.** It is the drop-in sibling of `CreateChatClientAgent` and supplies everything the pure assembler cannot know about: the token-tracked chat client, the Orleans-backed history provider, compaction and projection registration, config-driven settings, and durable session snapshots.

Reach for `AsFabrCoreHarnessAgent` only outside a `FabrCoreAgentProxy` — a console tool, a test, a service that already holds its own `IChatClient`. Sessions are not persisted on that path unless you wire `IHarnessSessionStore` yourself.

## Minimal Agent

Nine lines of your code; everything else is platform.

```csharp
[AgentAlias("researcher")]
[Description("Researches a question end to end and reports back.")]
[FabrCoreCapabilities("Breaks a research goal into tracked steps, delegates lookups to specialist agents, and reports a consolidated answer.")]
public class ResearcherAgent : FabrCoreAgentProxy
{
    private FabrCoreHarnessResult harness = null!;

    public ResearcherAgent(AgentConfiguration config, IServiceProvider serviceProvider, IFabrCoreAgentHost fabrcoreAgentHost)
        : base(config, serviceProvider, fabrcoreAgentHost) { }

    public override async Task OnInitialize()
    {
        var tools = await ResolveConfiguredToolsAsync();
        harness = await CreateFabrCoreHarnessAgent(config.Models ?? "default", "main", tools);
    }

    public override async Task<AgentMessage> OnMessage(AgentMessage message)
    {
        var run = await harness.RunAsync(message.Message ?? string.Empty);

        var response = message.Response();
        response.Message = run.Text;
        return response;
    }
}
```

**Always run through `FabrCoreHarnessResult.RunAsync`, not `harness.Agent.RunAsync`.** The wrapper snapshots the session on the way out — including when the run throws — and that snapshot is what carries todos across turns. Calling the inner agent directly silently disables durability.

## Reporting Honestly

The loop is bounded, so a run can end with work outstanding. Say so rather than implying success:

```csharp
public override async Task<AgentMessage> OnMessage(AgentMessage message)
{
    SetStatusMessage("Planning...");

    var run = await harness.RunAsync(message.Message ?? string.Empty);
    var text = run.Text;

    // Delegations stranded by a restart — see references/durability.md.
    if (harness.DescribeLostDelegations() is { } lost)
    {
        text += $"{Environment.NewLine}{Environment.NewLine}{lost}";
    }

    var remaining = await harness.GetRemainingTodosAsync();
    if (remaining.Count > 0)
    {
        text += $"{Environment.NewLine}{Environment.NewLine}Not completed within the iteration budget:{Environment.NewLine}"
            + string.Join(Environment.NewLine, remaining.Select(item => $"- {item.Title}"));
    }

    SetStatusMessage(string.Empty);

    var response = message.Response();
    response.Message = text;
    return response;
}
```

## What It Composes

Outermost to innermost:

| Layer | Present when |
|-------|-------------|
| `LoopAgent` | At least one loop evaluator is configured |
| `OpenTelemetryAgent` | Always, unless `DisableOpenTelemetry` — sensitive data on, matching `CreateChatClientAgent` |
| `ChatClientAgent` | Always, carrying `TodoProvider` and `BackgroundAgentsProvider` as context providers |
| `ChatClientAgent` default middleware | Always — approval binding, approval bypass, function invocation |
| `TokenTrackingChatClient` → `ModelDefaultsChatClient` → provider | Supplied by `GetChatClient`, so run-safety budgets and cost tracking see every call |

`FabrCoreHarnessAgent` is a `sealed DelegatingAIAgent`, so `GetService<T>()` and session serialization forward through the whole stack. That forwarding is load-bearing: the loop evaluators locate their providers with `context.Agent.GetService<TodoProvider>()`, and an evaluator that cannot find its provider throws. If you add a decorator of your own, it must forward `GetService`.

## The Loop

`HarnessLoopMode` is a `[Flags]` enum. Evaluators are consulted in flag order and **the first one asking to continue wins**; one declining is not a veto over the others.

| Mode | Continues while | Requires |
|------|----------------|----------|
| `Todo` | Incomplete todos remain | The todo provider (on by default) |
| `Background` | Delegations are still running | At least one background agent |
| `Marker` | The response lacks a completion marker | `LoopCompletionMarker` — matched ordinally, case-sensitively |
| `Judge` | A judge model rules the request unanswered | `LoopJudgeChatClient` — an `IChatClient`, not an agent. Costs an extra LLM call per evaluation |

Default when `_HarnessLoop` is unset: `Todo`, plus `Background` when background agents were configured. `HarnessLoopMode.None` gives a single-shot agent with todo tools but no re-invocation.

The iteration cap defaults to 10 (`_HarnessLoopMaxIterations`). **It is a budget, not a guarantee** — always read `GetRemainingTodosAsync()` after a run and report what is left.

## Background Agents

Delegates come from FabrCore handles, resolved through `AgentRosterBuilder`:

```json
"args": {
  "_HarnessBackgroundAgents": "eric:crm,eric:policy-desk",
  "_HarnessBackgroundTimeoutSeconds": "180"
}
```

The builder probes each handle with `GetAgentHealth` and produces the non-empty case-insensitively-unique names `BackgroundAgentsProvider` requires — `owner1:crm` becomes `crm`, a colliding `owner2:crm` becomes `crm-2`. Agents that fail their probe are excluded and carry a reason on the roster; nothing throws.

**The delegate's own `description` is what the delegating model reads**, so write it for that audience. Precedence is the target's `AgentConfiguration.Description`, then its `[Description]` from `IFabrCoreRegistry`, then a bare `Agent {name}` fallback; `[FabrCoreCapabilities]` is appended when present, and the whole thing is truncated at 500 characters. A delegate described as *"Advises on internal policy. Consult for guidance; do not assign execution work."* gets used differently than one described as *"Policy agent."*

Each delegation becomes a real `AgentMessage` sent with `SendAndReceiveMessage`, bounded by `.WaitAsync(timeout)`. A breach surfaces to the model as `BackgroundTaskStatus.Failed` with the reason readable via `background_agents_get_task_results`.

To supply delegates in code instead — squad members, agents with their own transport — set `options.BackgroundAgents` in the `configure` callback with any `AIAgent` whose `Name` is non-empty and unique.

**`A2AAgentProxy` cannot back this.** It leaves `Name` and `Description` null, which `BackgroundAgentsProvider` rejects outright, and it has no delegation timeout. Use `FabrCoreBackgroundAgent`.

## Session Durability

Harness state — todos, delegation records, loop position — lives in the `AgentSession` state bag, which FabrCore does not otherwise persist. `CreateFabrCoreHarnessAgent` restores it on activation and `RunAsync` snapshots it after every turn, under agent custom state key `_harness_session:{threadId}`.

Conversation history is **not** in the snapshot — it stays in Orleans `MessageThreads` via `FabrCoreChatHistoryProvider` — so snapshots are kilobyte-scale and a lost snapshot never costs conversation continuity.

One thing genuinely cannot survive: delegations that were mid-flight. Read `references/durability.md` before relying on that behavior.

## Configuration

Everything is settable from blueprint `Args`, so an agent class written once can be re-tuned without a rebuild. Keys are constants on `HarnessArgs`; `configure` runs after args and wins.

```json
{
  "handle": "assistant",
  "agentType": "researcher",
  "models": "default",
  "systemPrompt": "You are Eric's operations assistant.",
  "args": {
    "_HarnessLoop": "todo,background",
    "_HarnessLoopMaxIterations": "8",
    "_HarnessBackgroundAgents": "eric:crm,eric:policy-desk"
  }
}
```

Full key table, parsing rules, and blueprint lifecycle caveats: `references/configuration.md`.

## What This Deliberately Does Not Do

Microsoft's `AsHarnessAgent` composes more. The following are **absent by design**, not missing:

| Upstream feature | Why not |
|-----------------|---------|
| File memory (default-on upstream) | Its default store is silo-local disk — shared across every tenant in the process. Durable notes belong in the memory service; compaction insurance is already FabrCore's job |
| File access | Same reason. Agents that genuinely need files use host storage services or a scoped MCP server, both governed by the normal tool pipeline |
| Skills (`SKILL.md` discovery) | Upstream discovers from `Directory.GetCurrentDirectory()`, which on a silo is the shared process directory — wrong tenant boundary and a supply-chain risk |
| Hosted web search (default-on upstream) | Fails outright on providers that do not support hosted tools |
| Agent modes (`plan` / `execute`) | Not yet built |
| Tool approval | Not yet built. Approval belongs on FabrCore channels with a durable pending state, which is a larger piece of work |
| In-run `CompactionProvider` | Compaction stays FabrCore-owned. The existing preflight, post-turn, mid-turn, and projection paths are unchanged — see **fabrcore-agent → Chat History Compaction** |

The last three are sequenced work, tracked in `docs/harness-adoption-plan.md`. The first four are decisions.

## Reference Routing

Read only what the task needs:

- `references/configuration.md` — use for the `_Harness*` args table, parsing rules, blueprint examples, code-vs-config precedence, and why re-applying a blueprint may appear to do nothing.
- `references/durability.md` — use for session snapshot format and lifecycle, size limits, corruption handling, lost delegations, and how reset, thread-clear, and eviction interact with harness state.

## Assets

- `assets/harness-agent-template.cs` — a complete harness agent, ready to copy, with honest reporting of unfinished work.
- `assets/harness-blueprint.json` — a blueprint provisioning a harness agent plus the two agents it delegates to.

## Important Constraints

- **Run through `FabrCoreHarnessResult.RunAsync`** — calling `harness.Agent.RunAsync` directly skips the snapshot and silently disables durability.
- **The iteration cap is a budget, not a guarantee** — always check `GetRemainingTodosAsync()` and report what is unfinished. Reporting success with open todos is the failure mode this whole subsystem exists to prevent.
- **Any decorator you add must forward `GetService`** — the loop evaluators resolve their providers through it, and a broken chain means the loop cannot observe completion.
- **In-flight delegations do not survive deactivation** — they are marked `Lost` and surfaced by `DelegationsLostOnRestore`. Report them; do not assume the work happened.
- **Every turn writes the whole grain state blob** — the snapshot flush is a `WriteStateAsync`. Acceptable for agents doing real work; set `_HarnessSessionPersistence=false` for a high-frequency agent that does not need state carried across turns.
- **Background agent names must be non-empty and case-insensitively unique** — `AgentRosterBuilder` guarantees this. If you build delegates by hand, so must you.
- **Arg keys are case-sensitive** — `_HarnessLoop` works, `_harnessloop` is silently ignored.
- **Harness types are `[Experimental]` upstream** — files in `src/FabrCore.Sdk/Harness/` open with `#pragma warning disable MAAI001`. Do the same in agent code that names `TodoProvider`, `LoopAgent`, or `BackgroundAgentsProvider` directly.
