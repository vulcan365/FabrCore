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
  "CreateInternalAgentAsync", "InternalAgentResult", "private specialist", "in-proxy multi-agent",
  "background_agents_start_task", "background agents", "delegate to another agent",
  "agent fan-out", "iteration loop", "keep working until done", "_Harness", "_HarnessLoop",
  "_HarnessBackgroundAgents", "_HarnessSkills", "AgentSkillsSource", "AgentSkillsProvider",
  "load_skill", "read_skill_resource", "AsHarnessAgent", "Microsoft.Agents.AI.Harness", "HarnessAgent".
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

The harness closes that gap with four cooperating pieces:

| Piece | What the model gets | What it changes |
|-------|--------------------|-----------------|
| **Todos** | `todos_add`, `todos_complete`, `todos_remove`, `todos_get_remaining`, `todos_get_all` | The plan becomes typed state the host can read, not prose buried in the transcript |
| **Modes** | `mode_get`, `mode_set` | `plan` builds an approval-ready todo list; `execute` performs it |
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
- You want Microsoft's stock composition including file memory, current-directory skill discovery, and hosted web search. Those are deliberately absent here; call `chatClient.AsHarnessAgent(...)` directly if you truly want them, and read "What This Deliberately Does Not Do" first.

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

**Use `CreateFabrCoreHarnessAgent` inside an agent.** It is the drop-in sibling of `CreateChatClientAgent` and supplies everything the pure assembler cannot know about: the token-tracked chat client, the Orleans-backed history provider, the full compaction ladder (layer 1 context compaction plus the history/fuse/stop rungs), config-driven settings, and durable session snapshots.

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
        var run = await harness.RunAsync(message);

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

    var run = await harness.RunAsync(message);
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
| `ChatClientAgent` | Always, carrying `TodoProvider`, `AgentModeProvider`, and `BackgroundAgentsProvider` as context providers |
| `AgentModeProvider` | On by default; `_HarnessMode=false` disables it |
| `AgentSkillsProvider` | Only when an explicit `AgentSkillsSource` is supplied; `_HarnessSkills` creates a principal-scoped Storage source |
| `ChatClientAgent` default middleware | Always — approval binding, approval bypass, function invocation |
| `TokenTrackingChatClient` → `ModelDefaultsChatClient` → provider | Supplied by `GetChatClient`, so run-safety budgets and cost tracking see every call |

`FabrCoreHarnessAgent` is a `sealed DelegatingAIAgent`, so `GetService<T>()` and session serialization forward through the whole stack. That forwarding is load-bearing: the loop evaluators locate their providers with `context.Agent.GetService<TodoProvider>()` and `GetService<AgentModeProvider>()`, and an evaluator that cannot find its provider throws. If you add a decorator of your own, it must forward `GetService`.

## Operating Modes

Modes are on by default. FabrCore supplies `plan` and `execute` instructions that keep the plan in the durable todo list—never a memory file. Plan mode may explore and clarify, but stops before executing. Execute mode works autonomously and lets incomplete todos drive the outer loop.

Use `harness.RunAsync(message)`, passing the complete `AgentMessage`. The wrapper reads `message.Args["_plan-mode"]` before every run:

| Value (with the default behavior) | Starting mode |
|-------|---------------|
| missing, invalid, or `true` | `plan` |
| `false` | `execute` |

The flag chooses the starting mode; the model may still call `mode_set` during the run. String and `ChatMessage` overloads cannot see `AgentMessage.Args` and preserve the session's current/default mode. Hosts can read or change it with `GetModeAsync`, `SetModeAsync`, and `SetPlanModeAsync`; external setters snapshot immediately.

Set `FabrCoreHarnessOptions.MissingPlanModeBehavior` when an omitted or invalid flag should behave
differently. `SelectPlanning` is the compatibility default; `PreserveCurrentMode` honors the current
session/default mode, and `SelectExecution` selects execution. Explicit valid `true`/`false` values
always win.

## The Loop

`HarnessLoopMode` is a `[Flags]` enum. Evaluators are consulted in flag order and **the first one asking to continue wins**; one declining is not a veto over the others.

| Mode | Continues while | Requires |
|------|----------------|----------|
| `Todo` | Incomplete todos remain while the agent is in `execute`; every mode when modes are disabled | The todo provider (on by default) |
| `Background` | Delegations are still running | At least one background agent |
| `Marker` | The response lacks a completion marker | `LoopCompletionMarker` — matched ordinally, case-sensitively |
| `Judge` | A judge model rules the request unanswered | `LoopJudgeChatClient` — an `IChatClient`, not an agent. Costs an extra LLM call per evaluation |

Default when `_HarnessLoop` is unset: `Todo`, plus `Background` when background agents were configured. `HarnessLoopMode.None` gives a single-shot agent with todo tools but no re-invocation.

The iteration cap defaults to 10 (`_HarnessLoopMaxIterations`). **It is a budget, not a guarantee** — always read `GetRemainingTodosAsync()` after a run and report what is left.

## Background Agents

Background delegates may be external FabrCore agents or private in-process specialists. These are
different topologies and should not be mixed up.

External delegates come from FabrCore handles, resolved through `AgentRosterBuilder`:

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

For private specialists owned by the same proxy, create them in `OnInitialize` with
`CreateInternalAgentAsync` and add `result.AsBackgroundAgent()`. FabrCore gives these agents separate
tracked clients, fail-closed risk-classified tool scopes, timeout/concurrency bounds, and child
attribution. They have no FabrCore handles and do not use `SendMessage`; this is not squads, the A2A
protocol (**fabrcore-a2a**), or FabrCore agent-to-agent delegation. Only `Read` and `Compute` tools are permitted under background
execution policies. See **fabrcore-agent → `references/internal-agent-composition.md`**.

The upstream background provider stores live child tasks and sessions only in memory. After proxy
deactivation, an in-flight private task becomes `Lost`; it is not automatically restarted. The
FabrCore wrapper can enforce its timeout even though the upstream provider does not pass the parent
tool-call cancellation token into a started child.

**Do not confuse this with the A2A protocol.** Background delegation is in-process and FabrCore-internal, and `FabrCoreBackgroundAgent` is its delegate type; `FabrCore.Host`'s A2A endpoints publish agents to *external* clients over Agent2Agent. They compose — an A2A caller can reach an agent that then delegates in the background — but neither replaces the other. See **fabrcore-a2a**.

## Session Durability

Harness state — todos, operating mode, delegation records, and loop position — lives in the `AgentSession` state bag, which FabrCore does not otherwise persist. `CreateFabrCoreHarnessAgent` restores it on activation and `RunAsync` snapshots it after every turn, under agent custom state key `_harness_session:{threadId}`.

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
    "_HarnessSkills": "policy-review@1.2.0,invoice-rules@2026-08-01",
    "_HarnessBackgroundAgents": "eric:crm,eric:policy-desk"
  }
}
```

Full key table, parsing rules, and blueprint lifecycle caveats: `references/configuration.md`.

## What This Deliberately Does Not Do

Microsoft's `AsHarnessAgent` composes more. The following are **absent by design**, not missing:

| Upstream feature | Why not |
|-----------------|---------|
| File memory (default-on upstream) | Its default store is silo-local disk — shared across every tenant in the process. Durable notes belong in the memory service; compaction insurance is already the ladder's job |
| File access | Same reason. Agents that genuinely need files use host storage services or a scoped MCP server, both governed by the normal tool pipeline |
| Current-directory skill discovery | Upstream discovers from `Directory.GetCurrentDirectory()`, which on a silo is the shared process directory — wrong tenant boundary and a supply-chain risk. FabrCore only composes explicitly supplied, principal-scoped sources |
| Hosted web search (default-on upstream) | Fails outright on providers that do not support hosted tools |
| Tool approval | Not yet built. Approval belongs on FabrCore channels with a durable pending state, which is a larger piece of work |

Tool approval remains sequenced work tracked in `docs/harness-adoption-plan.md`; the other rows are deliberate server-safety decisions.

In-run `CompactionProvider` **is** composed — `CreateFabrCoreHarnessAgent` passes one as layer 1 of the
compaction ladder, and it matters more here than anywhere else because harness agents run long tool loops.
Its session state is stripped before the snapshot is persisted, so the group index never reaches durable
storage. See **fabrcore-agent → Context Management: the compaction ladder**.

## Reference Routing

Read only what the task needs:

- `references/configuration.md` — use for the `_Harness*` args table, parsing rules, blueprint examples, code-vs-config precedence, and why re-applying a blueprint may appear to do nothing.
- `references/skills.md` — use for publishing immutable skill ZIPs, Storage layout and durability, exact-version assignment, limits, security rules, and administration APIs. For how a loaded skill appears on an agent's A2A card, see **fabrcore-a2a**.
- `references/durability.md` — use for session snapshot format and lifecycle, size limits, corruption handling, lost delegations, and how reset, thread-clear, and eviction interact with harness state.

## Assets

- `assets/harness-agent-template.cs` — a complete harness agent, ready to copy, with honest reporting of unfinished work.
- `assets/harness-blueprint.json` — a blueprint provisioning a harness agent plus the two agents it delegates to.
- `assets/policy-review/` — a valid V1 text-only skill directory; ZIP this directory before publishing it as `policy-review@1.0.0`.
- `assets/harness-skills-blueprint.json` — a blueprint pinning that published sample skill.

## Important Constraints

- **Run through `FabrCoreHarnessResult.RunAsync`** — calling `harness.Agent.RunAsync` directly skips the snapshot and silently disables durability.
- **The iteration cap is a budget, not a guarantee** — always check `GetRemainingTodosAsync()` and report what is unfinished. Reporting success with open todos is the failure mode this whole subsystem exists to prevent.
- **Any decorator you add must forward `GetService`** — the loop evaluators resolve their providers through it, and a broken chain means the loop cannot observe completion.
- **In-flight delegations do not survive deactivation** — they are marked `Lost` and surfaced by `DelegationsLostOnRestore`. Report them; do not assume the work happened.
- **Every turn writes the whole grain state blob** — the snapshot flush is a `WriteStateAsync`. Acceptable for agents doing real work; set `_HarnessSessionPersistence=false` for a high-frequency agent that does not need state carried across turns.
- **Background agent names must be non-empty and case-insensitively unique** — `AgentRosterBuilder` guarantees this. If you build delegates by hand, so must you.
- **Arg keys are case-sensitive** — `_HarnessLoop` works, `_harnessloop` is silently ignored.
- **Skills are exact-version and activation-cached** — changing `_HarnessSkills` or deleting a package affects a new activation; force reconfigure or evict an active agent to reload it.
- **Skills are principal-scoped, and that principal is not always the one you published from** — an agent reached over A2A runs as the A2A principal (`a2a` by default), so publish there for its `_HarnessSkills` to resolve. That agent's card also advertises the skills it loads, so a published description is read by remote orchestrators as well as by the model. See **fabrcore-a2a**.
- **Harness types are `[Experimental]` upstream** — files in `src/FabrCore.Sdk/Harness/` open with `#pragma warning disable MAAI001`. Do the same in agent code that names `TodoProvider`, `AgentModeProvider`, `LoopAgent`, or `BackgroundAgentsProvider` directly.
