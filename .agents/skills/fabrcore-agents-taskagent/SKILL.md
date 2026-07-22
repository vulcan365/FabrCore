---
name: fabrcore-agents-taskagent
description: >
  FabrCore TaskAgent — brain-like long-running task agent. Compresses Swarm-style
  multi-agent orchestration into a single agent that classifies every user message
  via fast-model intent triage, manages a persistent task plan, and delegates each
  task to a configured client agent. Auto-replans and merges new asks into a running
  plan, consults SMEs on roadblocks, learns rules from teaching messages, and stays
  responsive mid-delegation via an OnMessageBusy queue + drain pattern. Three model
  tiers per definition (FastModel for triage, WorkerModel for synthesis, PlannerModel
  for planning/replanning). Themed definitions live in fabrcore-taskagent.json.
  Covers TaskAgent, SubconsciousState, UserIntent, IntentTriage, ReplanAttempt,
  TaskPlan, TaskItem, TaskItemStatus, PlanReplanner, ReplanTrigger, PlanFormatter,
  TaskAgentDefinition, TaskAgentDefinitionFile, ITaskAgentDefinitionProvider,
  FileTaskAgentDefinitionProvider, TaskAgentOptions, TaskAgentServiceExtensions,
  AgentCapability, AgentCapabilityRegistry, AgentCapabilityProjection,
  SmeConsultationService, DelegationService, LessonExtractor, StructuredEnvelope,
  EnvelopeParser, TaskAgentMessageTypes, AddTaskAgentServices, ClientAgentHandles,
  SubjectMatterExperts, PersonaPrompt, ClientAgentOverlay, FastModelName,
  WorkerModelName, PlannerModelName, MemoryAllowedTypes, IsStuck, IsPaused,
  LearningModeEnabled, ActiveRuleRefs, AbsorbedAsks, OnMessageBusy,
  fabrcore-taskagent.json. Triggers on:
  "task agent", "TaskAgent", "FabrCore.Agents.TaskAgent", "task-agent alias",
  "long-running agent", "brain agent", "subconscious", "SubconsciousState",
  "intent triage", "IntentTriage", "UserIntent", "TriageResult",
  "task plan", "TaskPlan", "TaskItem", "PlanReplanner", "ReplanTrigger",
  "auto-replan", "auto-merge", "merge new asks", "AbsorbedAsks",
  "TaskAgentDefinition", "fabrcore-taskagent.json", "ITaskAgentDefinitionProvider",
  "AddTaskAgentServices", "ClientAgentHandles", "SubjectMatterExperts",
  "FastModelName", "WorkerModelName", "PlannerModelName", "three model tiers",
  "fast model triage", "planner model", "worker model",
  "OnMessageBusy queue", "ActiveMessage", "_pendingUserInputs",
  "drain user inputs", "BusyRouted", "queue mid-delegation",
  "DelegationService", "client agent delegation",
  "AgentCapability", "AgentCapabilityRegistry", "AgentCapabilityProjection",
  "SmeConsultationService" (TaskAgent variant), "LessonExtractor",
  "StructuredEnvelope", "EnvelopeParser", "fabrcore-envelope",
  "JSON envelope", "structured response tail",
  "Teaching intent", "LearningModeEnabled", "ActiveRuleRefs",
  "Pause", "Resume", "Stop", "Status", "GeneralQuestion",
  "ModifyPlan", "NewTask intent",
  "replan loop", "IsStuck", "StuckReason", "RecentReplans",
  "MemoryAllowedTypes", "memory recall on replan", "completion observation",
  "PlanFormatter", "FormatPlanStatus", "GetCompletedSummary".
  Do NOT use for: multi-agent orchestration with explicit waves, blackboard,
  HITL approval gates — use fabrcore-swarm. Do NOT use for: agent lifecycle
  basics — use fabrcore-agent. Do NOT use for: plugin/tool authoring — use
  fabrcore-plugins-tools. Do NOT use for: messaging primitives — use
  fabrcore-messaging. Do NOT use for: memory plumbing internals — use
  fabrcore-experimental-memory.
allowed-tools: "Bash(dotnet:*) Bash(mkdir:*) Bash(ls:*) Bash(pwsh:*) Bash(powershell:*) Bash(git:*) Bash(dir:*)"
metadata:
  author: FabrCore
  version: 1.0.0
---

# FabrCore TaskAgent — Brain-Like Long-Running Agent

The TaskAgent compresses the multi-agent Swarm pattern down to a **single agent** that:

- Classifies every user message via **fast-model intent triage** before doing anything.
- Manages a **persistent task plan** that survives silo restarts.
- **Delegates** each task to a client agent — owns no domain tools itself.
- **Auto-replans and merges** new user asks into the running plan with no prompt-back.
- **Consults SMEs** on roadblocks before failing.
- **Learns rules** from Teaching-mode messages via `FabrCore.Experimental.Memory`.
- Stays responsive **mid-delegation** by overriding `OnMessageBusy` to queue messages
  and drain them at safe checkpoints — no more "Agent is busy, try again later".

Use this skill when wiring TaskAgent into a host, authoring `fabrcore-taskagent.json`,
building client agents that participate, or extending behavior.

## When To Use This Skill

- Wiring `services.AddTaskAgentServices(...)` into a FabrCore host.
- Authoring `fabrcore-taskagent.json` themed definitions.
- Building client agents that the TaskAgent delegates work to.
- Building SME agents that answer `swarm-sme-consultation` messages.
- Implementing the **structured envelope** contract on agent responses.
- Diagnosing replan loops, stuck flags, or busy-routed messages.
- Choosing model tiers (FastModel / WorkerModel / PlannerModel) and configuring memory.

## When NOT To Use This Skill

- Multi-agent orchestration with explicit waves, HITL approval gates, blackboard
  coordination → use `fabrcore-swarm`.
- Basic FabrCore agent lifecycle (`OnInitialize`, `OnMessage`, `OnEvent`) → use `fabrcore-agent`.
- Building plugins or standalone tools → use `fabrcore-plugins-tools`.
- Inter-agent messaging primitives → use `fabrcore-messaging`.
- Memory storage internals → use `fabrcore-experimental-memory`.

## Quick Reference

| Component | Type | Purpose |
|---|---|---|
| `TaskAgent` | Agent (`[AgentAlias("task-agent")]`) | The orchestrator. Owns triage, plan, self-loop, busy queue. |
| `SubconsciousState` | Persisted state object | Per-message flags + long-running data (pause, stuck, learning mode, rule refs). |
| `UserIntent` | Enum | NewTask, ModifyPlan, Pause, Resume, Status, Teaching, GeneralQuestion, Stop, Unknown. |
| `IntentTriage` | Stateless helper | One FastModel call → `TriageResult` with intent + confidence + lesson. |
| `TaskPlan` / `TaskItem` | Persisted models | Flat plan; each task → one client-agent delegation. |
| `PlanReplanner` | Service | Single replan entry point. `BuildInitialPlanAsync` for new plans, `ReplanAsync` for every other trigger (TaskCompleted, TaskFailed, UserMessage, SmeAnswer, GoalCheck). |
| `DelegationService` | Service | Delegates one task via `SendAndReceiveMessage`, parses the envelope, applies a timeout. |
| `AgentCapabilityRegistry` | Service | Discovers each client agent's capabilities at runtime from `IFabrCoreRegistry` + `GetAgentHealth`. |
| `SmeConsultationService` | Service | Queries SME agents in parallel; first answer wins (or `ConsultAllAsync`). 30s per-SME timeout. |
| `LessonExtractor` | Service | Teaching intent → `IAgentMemoryService.SaveMemoryAsync(Rule|Instruction|Observation)`. |
| `StructuredEnvelope` / `EnvelopeParser` | Convention + helper | Fenced `fabrcore-envelope` JSON tail at end of LLM/inter-agent responses; parser is defensive (never throws, returns null on missing/malformed). |
| `TaskAgentDefinition` | JSON-loaded config | Themed scope: `ClientAgentHandles`, `SubjectMatterExperts`, three model names, persona/overlay prompts. |
| `ITaskAgentDefinitionProvider` / `FileTaskAgentDefinitionProvider` | Provider | Loads definitions from `fabrcore-taskagent.json` (override path via `TaskAgentOptions.DefinitionFilePath`). |
| `TaskAgentOptions` | Options | Defaults for model tier names, replan-loop window, delegation timeout, clarify confidence threshold. |
| `TaskAgentServiceExtensions.AddTaskAgentServices` | DI | Registers options + provider. Memory is wired separately via `AddAgentMemoryServices`. |
| `TaskAgentMessageTypes.SmeConsultation` | Constant | `"swarm-sme-consultation"` — wire-compatible with Swarm SME agents. |
| `TaskAgentMessageTypes.TaskDelegation` | Constant | `"task-delegation"` — sent to client agents. |

## Architectural Model

```
USER MESSAGE
    │
    ▼
┌──────────────────────────────────────────────────────────┐
│  TaskAgent.OnMessage                                     │
│  ┌────────────────────────────────────────────────────┐  │
│  │ 1. Intent triage (FastModel)                       │  │
│  │    → SubconsciousState.CurrentIntent + flags       │  │
│  └────────────────────────────────────────────────────┘  │
│             │                                            │
│             ▼                                            │
│  ┌────────────────────────────────────────────────────┐  │
│  │ 2. Dispatch                                        │  │
│  │    NewTask  → BuildInitialPlanAsync (PlannerModel) │  │
│  │    ModifyPlan / NewTask-during-plan                │  │
│  │             → ReplanAsync (PlannerModel)           │  │
│  │    Teaching → LessonExtractor → memory             │  │
│  │    Pause / Resume / Stop / Status → flag mutation  │  │
│  │    GeneralQuestion → WorkerModel response          │  │
│  └────────────────────────────────────────────────────┘  │
│             │                                            │
│             ▼                                            │
│  ┌────────────────────────────────────────────────────┐  │
│  │ 3. Self-loop tick: ProcessNextTask                 │  │
│  │    a. Drain _pendingUserInputs (busy-routed msgs)  │  │
│  │    b. Pick next pending TaskItem                   │  │
│  │    c. DelegationService.DelegateAsync(client)      │  │
│  │    d. On failure: SmeConsultationService → retry   │  │
│  │    e. After completion: ReplanAsync (TaskCompleted)│  │
│  │    f. SendSelfMessage to continue                  │  │
│  └────────────────────────────────────────────────────┘  │
└──────────────────────────────────────────────────────────┘
       │                    │                    │
       ▼                    ▼                    ▼
  client agents          SMEs              Memory service
  (do real work)      (consult)        (rules / lessons)
```

The TaskAgent itself owns no domain tools. Every step is delegated to a client
agent listed in the definition's `ClientAgentHandles`. The agent's three LLM tier
sessions are tool-free — they only do triage, planning, replanning, and synthesis.

## The Three Model Tiers

Each `TaskAgentDefinition` declares three model config names that exist in
`fabrcore.json` `ModelConfigurations`:

| Tier | Model name field | Used by | Recommended size |
|---|---|---|---|
| **Fast** | `FastModelName` | `IntentTriage`, learning extraction, `LessonExtractor` description summary | small, cheap (gpt-4o-mini, haiku) |
| **Worker** | `WorkerModelName` | `GeneralQuestion` answers, `CompletePlan` final synthesis | mid-size (gpt-4o, sonnet) |
| **Planner** | `PlannerModelName` | `BuildInitialPlanAsync`, `ReplanAsync`, `GoalCheck` | reasoning model (o-mini, opus, sonnet) |

If a definition leaves a tier null, `TaskAgentOptions.Default*ModelName` is used
(`"default"` out of the box). The tier names map 1:1 to keys you've defined in
`fabrcore.json` — any name is allowed; the three property names above are just
where the TaskAgent looks up the resolved name.

---

## The Client Contract

Five things to wire to use the TaskAgent.

### 1. Reference + register the assembly

```csharp
using FabrCore.Agents.TaskAgent;
using FabrCore.Agents.TaskAgent.Configuration;
using FabrCore.Experimental.Memory.Configuration;

builder.AddFabrCoreServer(new FabrCoreServerOptions
{
    AdditionalAssemblies =
    [
        typeof(TaskAgent).Assembly,         // the TaskAgent itself
        typeof(MyDomainAgent).Assembly      // your client agent(s)
    ]
});
```

### 2. Register TaskAgent + memory services

```csharp
builder.Services.AddTaskAgentServices(opt =>
{
    opt.DefinitionFilePath = "fabrcore-taskagent.json";
    opt.DefaultFastModelName = "default";
    opt.DefaultWorkerModelName = "default";
    opt.DefaultPlannerModelName = "default";
    opt.MaxRecentReplans = 5;
    opt.ReplanLoopWindowSeconds = 60;
    opt.DefaultDelegationTimeout = TimeSpan.FromMinutes(4);
    opt.ClarifyConfidenceThreshold = 0.55;
});

// Memory is required for Teaching/learning to work end-to-end.
// Without it, Teaching messages still get acknowledged but nothing persists.
builder.Services.AddAgentMemoryServices(memOpt =>
{
    memOpt.ConnectionStringName = "expmem";   // expmem schema in your SQL Server 2025/Azure SQL
});
```

### 3. Author `fabrcore-taskagent.json`

See [assets/fabrcore-taskagent-example.json](assets/fabrcore-taskagent-example.json) for a fully-commented example. Minimum shape:

```json
{
  "agents": [
    {
      "name": "ops-agent",
      "description": "Operations supervisor",
      "clientAgentHandles": ["job-execution-agent", "data-fetcher"],
      "subjectMatterExperts": ["job-policy-sme"],
      "fastModelName": "gpt-4o-mini",
      "workerModelName": "gpt-4o",
      "plannerModelName": "o3-mini",
      "personaPrompt": "You are an operations supervisor.",
      "clientAgentOverlay": "Execute the task using your domain tools.",
      "memoryAllowedTypes": ["Rule", "Instruction", "Observation"],
      "replanLoopThreshold": 3
    }
  ]
}
```

### 4. Bring client agents + SMEs online before app.Run()

```csharp
var app = builder.Build();
app.UseFabrCoreServer();

var agentService = app.Services.GetRequiredService<IFabrCoreAgentService>();

// One ConfigureAgentAsync per alias listed in clientAgentHandles + subjectMatterExperts.
await agentService.ConfigureAgentAsync("system", new AgentConfiguration
{
    Handle = "job-execution-agent",
    AgentType = "job-execution-agent",
    Models = "default",
    Plugins = ["job", "scheduling"]
});

await agentService.ConfigureAgentAsync("system", new AgentConfiguration
{
    Handle = "job-policy-sme",
    AgentType = "job-policy-sme",
    Models = "default",
    Plugins = ["policy-knowledge-base"]
});

app.Run();
```

The TaskAgent dispatches to `{owner}:{alias}`. Bare aliases auto-prefix; cross-owner
targets like `"shared:my-agent"` pass verbatim.

### 5. Configure a TaskAgent grain per scope

```csharp
await agentService.ConfigureAgentAsync("system", new AgentConfiguration
{
    Handle = "ops-supervisor",            // the user-facing handle
    AgentType = "task-agent",             // matches [AgentAlias("task-agent")]
    Models = "default",                   // unused (the three tier names override)
    SystemPrompt = "Operations supervisor for job ops.",
    Args = new()
    {
        ["TaskAgentDefinition"] = "ops-agent"   // pick the named definition above
    }
});
```

You can configure as many TaskAgent grains as you want, each pointing at a
different definition — that's the "themed task agent" pattern. One grain per
definition per scope (per user, per team, per workspace).

See [assets/taskagent-registration-template.cs](assets/taskagent-registration-template.cs) for the full Program.cs template.

---

## How Client Agents Should Behave

A client agent receives a `MessageType = "task-delegation"` message containing:

- **`message.Message`** — a structured prompt with three sections:
  - `Upstream context` — results from prior tasks, plus rule references
  - `Task` — the imperative description from the plan
  - A trailing instruction asking for a `fabrcore-envelope` JSON tail
- **`message.State`** — same context as a `Dictionary<string,string>` (e.g. `original_goal`, `plan_summary`, `prior_results`, `sme_note`).
- **`message.TraceId`** — the originating plan ID, for end-to-end tracing.

The TaskAgent uses `SendAndReceiveMessage`, so the response goes back to it
synchronously. **Always include a fabrcore-envelope tail** so the TaskAgent can
read the outcome deterministically:

````
<your prose deliverable for the user>

```fabrcore-envelope
{
  "status": "completed",
  "summary": "Created job 4 with 14 pipes",
  "data": { "job_id": "4", "pipe_count": 14 },
  "confidence": 0.95,
  "warnings": []
}
```
````

Status values:

| Status | TaskAgent behavior |
|---|---|
| `completed` | Task marked Completed, result stored, replanner invoked. |
| `failed` | Task fails — TaskAgent consults SME, retries up to `MaxAttempts`, then triggers `ReplanTrigger.TaskFailed`. |
| `partial` | Treated as success for this attempt; replanner may extend the plan. |
| `info` | Treated as success. |

Missing or malformed envelopes degrade gracefully — the TaskAgent treats the
response as success (assuming `SendAndReceiveMessage` returned without exception).
Always emitting the envelope is recommended but not required.

See [assets/client-agent-template.cs](assets/client-agent-template.cs) for a
production-ready template.

---

## How SME Agents Should Behave

SME agents receive `MessageType = "swarm-sme-consultation"` (kept identical to
the Swarm convention so existing SMEs work unchanged). Reply with:

```csharp
public override Task<AgentMessage> OnMessage(AgentMessage message)
{
    if (message.MessageType != TaskAgentMessageTypes.SmeConsultation)
    {
        // Not for me — fall through to your normal handler.
    }

    var question = message.Message ?? "";
    var context = message.State?.GetValueOrDefault("context");

    var response = message.Response();
    response.State ??= new Dictionary<string, string>();

    if (CanAnswer(question))
    {
        response.Message = AnswerWithDomainKnowledge(question, context);
        response.State["sme-status"] = "answered";
    }
    else
    {
        response.Message = "";
        response.State["sme-status"] = "unknown";
    }
    return Task.FromResult(response);
}
```

The TaskAgent calls `SmeConsultationService.ConsultAsync` (parallel, first wins,
30s per-SME timeout) when a delegation fails, or `ConsultAllAsync` for richer
planning context. Adding a `confidence` field to a fabrcore-envelope tail is
optional but encouraged for downstream weighting.

See [assets/sme-agent-template.cs](assets/sme-agent-template.cs).

---

## User Intents and Dispatch

The `IntentTriage` step at the top of every user turn classifies the message
into one of nine intents. The dispatch table:

| Intent | What happens | Plan side-effect |
|---|---|---|
| `NewTask` (no plan running) | `PlanReplanner.BuildInitialPlanAsync` → new `TaskPlan` → self-loop kicks in | new plan |
| `NewTask` (plan running) | **Auto-merged** via `ReplanAsync(UserMessage)` — no prompt-back | plan revised |
| `ModifyPlan` | `ReplanAsync(UserMessage)` — replanner adds/removes tasks based on the new ask | plan revised |
| `Teaching` | `LessonExtractor.ExtractAndSaveAsync` → memory write; plan keeps running | none (lesson loaded into next replan via `ActiveRuleRefs`) |
| `Pause` | `_subconscious.IsPaused = true` | self-loop ticks no-op |
| `Resume` | `_subconscious.IsPaused = false`; `SendSelfMessage` | resumes |
| `Stop` | `_currentPlan.IsCancelled = true; IsRunning = false` | plan abandoned |
| `Status` | `PlanFormatter.FormatPlanStatus` — read-only snapshot | none |
| `GeneralQuestion` | WorkerModel answers with plan as context | none |
| `Unknown` | Falls through to `GeneralQuestion`, or asks user to clarify if confidence < threshold and no plan running | none |

**Low-confidence handling.** If `triage.Confidence < ClarifyConfidenceThreshold`
(default 0.55) AND no plan is running AND intent isn't `Status`/`Teaching`,
the agent asks the user to clarify rather than guess.

**Learning mode.** When `_subconscious.LearningModeEnabled` is true, every
`GeneralQuestion` and `NewTask` is forced into Teaching mode. Toggle it from a
plugin tool or set it manually for a knowledge-transfer session.

---

## Concurrency: `OnMessageBusy` Queue + Drain

The big behavioral difference from a normal FabrCore agent. FabrCore decorates
`OnMessage` with `[AlwaysInterleave]` — a second message arriving mid-await goes
to `OnMessageBusy`. The default returns *"Agent is busy, try again later"* —
exactly the experience the TaskAgent eliminates.

### What `OnMessageBusy` does

- **Status query** (read-only) → answered inline with `PlanFormatter.FormatPlanStatus(_currentPlan)`, no mutation.
- **Anything else** → `_pendingUserInputs.Writer.TryWrite(message)` (thread-safe
  unbounded `Channel<AgentMessage>`) + a one-line ack:
  > *Got your message. I'm in the middle of running a task step — I'll process this at the next checkpoint.*
- The ack carries `Args["BusyRouted"] = "true"` so the agent monitor records it.

### What `ProcessNextTask` does

1. **Drain first.** Reads every queued message and processes it through the same
   triage + dispatch pipeline as primary `OnMessage`, sending the real response
   back unsolicited via `SendResultToCaller`.
2. **Bail early.** After each drained message, if `IsPaused`, `IsStuck`, or
   `IsRunning != true`, return — later messages stay queued until the user
   explicitly resumes or restarts.
3. **Then** pick the next `Pending` task and delegate.

This keeps shared state mutations (the plan, subconscious flags) on the primary
turn — `OnMessageBusy` only writes to the channel. The pattern is safe under
FabrCore's "no shared-state mutation in busy" rule.

### Stale-message protection

If primary `OnMessage` runs longer than 5 minutes (e.g. a hung delegation),
FabrCore stops busy-routing and accepts the next message as a fresh primary.
The TaskAgent's default `DefaultDelegationTimeout` is 4 minutes, intentionally
under that threshold.

---

## Replan Loop Safety Rail

`SubconsciousState.RecentReplans` is a sliding window (default 5 entries). After
every replan, `RecordReplanAndCheckLoop` adds an entry and trims. A loop fires
when the last `ReplanLoopThreshold` entries (default 3) within
`ReplanLoopWindowSeconds` (default 60) are either:

- All triggered by `user-message` (user is thrashing the goal), or
- All assessed as `needs_adjustment` (planner can't settle).

When fired:

1. `_subconscious.IsStuck = true`
2. `_subconscious.StuckReason` set to a human-readable explanation
3. Self-loop ticks become no-ops
4. The agent surfaces a clarifying question on its next user turn

The flag clears automatically when the user sends another message — the next
`OnMessage` resets per-turn flags, and `HandleResume` (or any successful
non-replan turn) flips `IsStuck` back to false.

---

## The Structured Envelope Convention

Every prompt the TaskAgent issues to an LLM (its own three tiers) and every
delegation/SME consultation **asks the responder to append a fenced JSON block
at the end of the response**. The contract:

````
<natural prose for humans / chat history>

```fabrcore-envelope
{
  "status": "completed|failed|partial|info",
  "summary": "one-line summary",
  "data": { "task-specific": "payload" },
  "confidence": 0.0,
  "follow_ups": ["optional"],
  "warnings": ["optional"]
}
```
````

`EnvelopeParser.TryExtract(text)` returns the parsed `StructuredEnvelope` or null.
`EnvelopeParser.StripEnvelope(text)` removes the fenced block so chat history
shows only prose.

Where the envelope is requested:

| Caller | Asked of | Why |
|---|---|---|
| `IntentTriage` | FastModel | Intent + lesson + confidence in `data`. |
| `PlanReplanner.BuildInitialPlanAsync` | PlannerModel | `data.tasks` array. |
| `PlanReplanner.ReplanAsync` | PlannerModel | `data.assessment` + `add_tasks` + `remove_task_ids`. |
| `DelegationService` | Client agent | Status / data / warnings. |
| `SmeConsultationService` | SME agent | Optional confidence. |

A missing envelope falls back to prose-only handling — it never breaks the agent.

See [references/envelope-contract.md](references/envelope-contract.md) for the
full schema, parser semantics, and adoption guidance.

---

## Memory Integration

Memory is the **persistence layer for what the user teaches** and **what the
agent learns from completing plans**. Wiring is `services.AddAgentMemoryServices(...)`
alongside `AddTaskAgentServices`. Per-agent isolation is automatic — the TaskAgent
calls `IAgentMemoryProvider.GetMemoryService(fabrcoreAgentHost.GetHandle())` in
`OnInitialize`, so each grain has its own partition keyed by full handle.

| Operation | Trigger | Memory type |
|---|---|---|
| `SaveMemoryAsync` | `Teaching` intent → `LessonExtractor` | `Rule` / `Instruction` / `Observation` (extracted by triage; defaults to `Observation` if unspecified) |
| `SaveMemoryAsync` | `CompletePlan` → "completion observation" | `Observation` |
| `RecallAsync` | Every `BuildInitialPlanAsync` and `ReplanAsync` | reads `WarmMemories` |

`TaskAgentDefinition.MemoryAllowedTypes` is an optional whitelist — a Teaching
that triage labeled `Rule` falls back to `Observation` if `Rule` isn't in the
whitelist.

Compaction (`MemoryAwareCompactionService`) is wired in via FabrCore's
standard `OnCompaction` hook automatically when `AddAgentMemoryServices` is
registered. The TaskAgent inherits that behavior without explicit code.

---

## Delegation Flow Detail

Per task, in `ProcessNextTask`:

1. `nextTask.Status = InProgress`; `_status` heartbeat sent to caller.
2. `DelegationService.DelegateAsync(alias, description, context, overlay, timeout, traceId)`.
   - Builds a structured prompt (overlay + context + task + envelope instruction).
   - Sends `MessageType = "task-delegation"` to the client agent.
   - Wraps `SendAndReceiveMessage` in a `Task.WhenAny(send, Task.Delay(timeout))` so a hung agent fails open at the timeout.
3. On `result.Success == false`:
   - **First attempt**: `SmeConsultationService.ConsultAsync(...)` for a domain hint.
   - If SME has guidance and `AttemptCount < MaxAttempts`: `RoadblockNote` is set, status reverts to `Pending`, next tick retries with the SME note in `context["sme_note"]`.
   - Otherwise: `Status = Failed`, `ReplanAsync(TaskFailed)` decides retry/replace/abort.
4. On `result.Success == true`:
   - `task.Result = result.ProseText` (envelope already stripped).
   - `Status = Completed`.
   - `ReplanAsync(TaskCompleted)` — the replanner sees the new completion + active rules + available agents, may add/remove tasks.
5. `SendSelfMessage` for the next iteration.

When no `Pending` tasks remain:

1. **Final goal check** via `ReplanAsync(GoalCheck)` — does the cumulative work cover the original request + absorbed asks?
2. If the replanner adds tasks, loop continues.
3. Otherwise: `CompletePlan` synthesizes the final deliverable with WorkerModel, sends it to `CallerHandle`, saves a completion observation to memory, and resets `_currentPlan = null`.

---

## Common Customizations

### Use a custom definition provider

Replace the file-backed default with a database, REST API, or feature-flag source:

```csharp
public class SqlTaskAgentDefinitionProvider : ITaskAgentDefinitionProvider
{
    public Task<TaskAgentDefinition?> GetByNameAsync(string name, CancellationToken ct = default) { ... }
    public Task<IReadOnlyList<TaskAgentDefinition>> GetAllAsync(CancellationToken ct = default) { ... }
}

builder.Services.AddTaskAgentServices();
builder.Services.Replace(ServiceDescriptor.Singleton<
    ITaskAgentDefinitionProvider, SqlTaskAgentDefinitionProvider>());
```

### Override busy ack text

Subclass `TaskAgent` and override `OnMessageBusy`. Keep the queue + drain pattern,
just customize the ack message — for example, include the running task name.

### Add tools to the planner / worker tier

The TaskAgent passes `tools: null` to all three `CreateChatClientAgent` calls.
If you need the WorkerModel's `GeneralQuestion` answers to call tools, subclass
and override `OnInitialize` to resolve and pass tools into the worker
`CreateChatClientAgent` only — leave Fast and Planner tool-free to keep
classification deterministic.

### Disable memory on a per-grain basis

Don't wire `AddAgentMemoryServices`. Teaching-intent messages still get a
graceful fallback message ("I noted that, but my long-term memory isn't wired
up so it won't survive past this session.").

### Tighten the replan loop window

```csharp
builder.Services.AddTaskAgentServices(opt =>
{
    opt.MaxRecentReplans = 3;
    opt.ReplanLoopWindowSeconds = 30;   // tighter window = fires sooner
});
```

Or per definition: set `replanLoopThreshold` to 2 in `fabrcore-taskagent.json`.

---

## Troubleshooting

| Symptom | Likely cause | Fix |
|---|---|---|
| User gets "Agent is busy" instead of the queue ack | Old TaskAgent.cs without `OnMessageBusy` override; or subclass forgot to call it | Confirm the override exists; deploy and pass `forceReconfigure: true` on next configure |
| Plan never resumes after silo restart | `IsPaused` or `IsStuck` was true at shutdown | These persist intentionally; user must send a message to resume / clarify |
| Triage always returns `GeneralQuestion` | FastModel ignoring the envelope contract or returning malformed JSON | Check FastModel choice — older models often skip fenced blocks; switch to a model that handles structured output |
| `IsStuck` never clears | Misuse of `_subconscious.IsStuck` — should clear on next user message | Check that `OnMessage` resets per-turn flags via `ResetTurn()` first |
| Delegations time out at exactly 4 min | Default timeout | Override per definition: `delegationTimeout` (TimeSpan) or globally via `TaskAgentOptions.DefaultDelegationTimeout` |
| Replan loop fires from honest user iteration | Threshold too tight | Raise `replanLoopThreshold` to 4-5 or widen `ReplanLoopWindowSeconds` to 120 |
| Memory recall returns nothing | `IAgentMemoryProvider` not registered, or schema not provisioned | Confirm `AddAgentMemoryServices` is in DI and `MemorySchemaInitializer` ran (check startup logs for `expmem` schema creation) |
| Client agent's response prose includes the JSON envelope | Client is returning `message.Message` verbatim from a wrapped LLM that includes the fence | TaskAgent strips it on receipt — but to keep client-side chat clean, call `EnvelopeParser.StripEnvelope` before passing to ChatDock or other UI |
| `task.AssignedClientAgentAlias` is null on first task | Planner LLM didn't include `assigned_agent` in the envelope | Ensure your `PlannerModel` is one that follows structured-output instructions reliably; raise the planner system prompt's emphasis on the field |

---

## Differences vs `fabrcore-swarm`

The TaskAgent is the **single-agent** path; the Swarm is the **multi-agent** path.
Choose:

| Capability | TaskAgent | Swarm |
|---|---|---|
| Single goal end-to-end | ✅ ideal | works but heavyweight |
| Long-running conversation, mid-flight asks | ✅ designed for it (auto-merge, busy queue) | possible but more wiring |
| Wave-parallel task execution | ❌ v1 is sequential (DependsOn reserved) | ✅ DependencyResolver waves |
| Per-plan blackboard for sibling coordination | ❌ | ✅ |
| Explicit user approval gate before execution | ❌ (trust the plan, replan freely) | ✅ |
| HITL escalation as first-class | partial — replan loop → ask user | ✅ ISwarmHumanInterface |
| Themed config | ✅ `fabrcore-taskagent.json` | ✅ `fabrcore-swarm.json` |
| SME consultation | ✅ same wire format | ✅ |
| Memory integration | ✅ first-class for teaching/learning | optional via guardrail store |
| Number of permanent grains | 1 per scope | 4-5 per scope (orchestrator + planner + factory + supervisor + N workers) |

A useful heuristic: if your users iterate conversationally on a goal, use TaskAgent.
If you have a fixed pipeline of well-defined task types and want explicit waves +
HITL gates, use Swarm. Both can run side-by-side in the same host.

---

## Files in this skill

- [SKILL.md](SKILL.md) — this file (API surface + quick reference)
- [assets/taskagent-registration-template.cs](assets/taskagent-registration-template.cs) — Program.cs / Startup template
- [assets/client-agent-template.cs](assets/client-agent-template.cs) — example client agent that participates
- [assets/sme-agent-template.cs](assets/sme-agent-template.cs) — example SME agent
- [assets/fabrcore-taskagent-example.json](assets/fabrcore-taskagent-example.json) — fully-commented definition file
- [references/architecture.md](references/architecture.md) — deeper architecture, state lifecycles, message flows
- [references/envelope-contract.md](references/envelope-contract.md) — the full envelope schema and parser semantics
