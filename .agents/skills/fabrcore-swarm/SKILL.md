---
name: fabrcore-swarm
description: >
  FabrCore Swarm — multi-agent orchestration where the swarm plans work and dispatches tasks
  to client-provided pre-existing agents. Two-phase planning/execution, task dependency graphs,
  per-plan blackboard for shared data, orchestrator reasoning turns on failure/stall, two-phase
  completion ack, drive-loop timer, full HITL wiring, Subject Matter Expert (SME) consultation
  at every decision point before human escalation. Long-lived worker agents (1:1 with client
  agents) have their own LLM and delegate domain work to paired client agents via DelegateToClientAgent.
  Permanent agent constellation: orchestrator + planner + factory + single supervisor + N workers.
  Covers SwarmOrchestratorAgent, SwarmPlannerAgent, SwarmSupervisorAgent, SwarmFactoryAgent,
  SwarmWorkerAgent (LLM-powered, long-lived), SwarmBlackboardAgent, SwarmAgentCapability, SwarmAgentDirectory,
  SwarmPlan, SwarmTask, SwarmRoadblock, SwarmDefinition, AgentHandles, SubjectMatterExperts,
  LiveAgentRegistry, AgentCapabilityProjection, ISwarmDefinitionProvider, FileSwarmDefinitionProvider,
  fabrcore-swarm.json, SwarmWorkerTools, SwarmPlannerTools, SwarmOrchestratorDecisionTools,
  SmeConsultationService, DependencyResolver, ProgressTracker, TaskDispatcher, SwarmPromptFoundation,
  ClientAgentGuidance, CoreDirectives, PlannerOverlay, OrchestratorOverlay,
  ISwarmHumanInterface, AddSwarmOrchestration, IFabrCoreRegistry, GetAgentHealth.
  Triggers on: "swarm", "swarm orchestration", "multi-agent plan", "subject matter expert",
  "SME", "SubjectMatterExperts", "SmeConsultationService", "ConsultSubjectMatterExpert",
  "QuerySubjectMatterExpert", "ConsultSME", "SwarmOrchestratorAgent",
  "SwarmPlannerAgent", "SwarmSupervisorAgent", "SwarmFactoryAgent", "SwarmWorkerAgent",
  "swarm-worker", "swarm-blackboard", "SwarmBlackboardAgent", "blackboard", "client agent",
  "adapter worker", "AssignedAgentAlias", "SwarmAgentCapability", "SwarmAgentDirectory",
  "AskAgent", "ISwarmWorker", "SwarmPlan", "SwarmTask", "AgentHandles", "LiveAgentRegistry",
  "AgentCapabilityProjection", "IFabrCoreRegistry", "GetAgentHealth",
  "SwarmDefinition", "ISwarmDefinitionProvider", "FileSwarmDefinitionProvider",
  "fabrcore-swarm.json", "swarm definition", "swarm scope",
  "SwarmWorkerTools", "SwarmPlannerTools", "SwarmSupervisorTools", "SwarmOrchestratorDecisionTools",
  "AddSwarmOrchestration", "SwarmClientExtensions", "CreateSwarmAsync",
  "DestroySwarmAsync", "PlanAwaitingApproval", "swarm-plan-awaiting-approval",
  "per-user swarm", "client-side swarm", "Blazor swarm", "swarm-orchestrator", "swarm-planner",
  "swarm-supervisor", "swarm-factory", "task dependency", "execution wave", "roadblock",
  "human-in-the-loop", "approval gate", "planning phase", "execution phase", "agent swarm",
  "DependencyResolver", "ProgressTracker", "drive loop", "PendingAck", "two-phase ack",
  "reasoning turn", "ListAvailableAgents", "ClientAgentGuidance", "swarm-task-status".
  Do NOT use for: single-agent task planning — use the TaskAgent directly.
  Do NOT use for: agent lifecycle basics — use fabrcore-agent.
  Do NOT use for: plugin/tool development basics — use fabrcore-plugins-tools.
  Do NOT use for: messaging primitives — use fabrcore-messaging.
allowed-tools: "Bash(dotnet:*) Bash(mkdir:*) Bash(ls:*) Bash(pwsh:*) Bash(powershell:*) Bash(git:*) Bash(dir:*)"
metadata:
  author: FabrCore
  version: 4.0.0
---

# FabrCore Swarm Orchestration

The swarm plans complex workflows and dispatches tasks to **client-provided agents**. Clients build their own domain-aware agents and register their capabilities with the swarm. The swarm picks which agent does what, runs them in parallel waves, coordinates their results via a shared blackboard, and handles failures + human-in-the-loop escalation.

The key architectural shift: **the swarm does not build client agents.** Clients own and operate the domain agents. A permanent **worker agent** (with its own LLM) is paired 1:1 with each client agent — it reasons about tasks, delegates domain work to the client via `DelegateToClientAgent`, and manages blackboard coordination. All swarm agents are permanent and live indefinitely.

## Quick Reference

| Component | Type | Purpose |
|---|---|---|
| `SwarmOrchestratorAgent` | Agent (permanent) | Entry point. Owns lifecycle, approval gate, drive loop, reasoning turns. Provisions workers on init. |
| `SwarmPlannerAgent` | Agent (permanent) | Builds plans by assigning tasks to client agents (LLM tool calls) |
| `SwarmSupervisorAgent` | Agent (permanent, single) | **One per swarm.** Pushes tasks to workers, monitors progress, plan-aware. Resets between plans. |
| `SwarmWorkerAgent` | Agent (permanent, LLM) | **One per client agent.** Has own LLM, delegates to paired client agent via `DelegateToClientAgent`. Accumulates chat history across tasks/plans. |
| `SwarmBlackboardAgent` | Agent (per-plan) | Per-plan KV store with push events for sibling coordination |
| `SwarmFactoryAgent` | Agent (permanent) | Registry discovery surface (no longer creates agents dynamically) |
| `SwarmAgentCapability` | Model | Planner-facing projection: alias, description, plugins, tools, notes. Built at runtime from `IFabrCoreRegistry` + `GetAgentHealth`; never registered directly. |
| `SwarmAgentDirectory` | Model | Pre-existing helper agents that client agents can query (NOT executors) |
| `SwarmPlannerTools` | Plugin | Planner LLM tools: `CreateTask(agentAlias)`, `ListAvailableAgents`, `AddDependency`, `FinalizePlan`, `QuerySubjectMatterExpert` |
| `SwarmOrchestratorDecisionTools` | Plugin | Orchestrator reasoning tools: `ProceedWithWave`, `RequestReplan`, `ConsultSME`, `AskHuman`, `RetryTask`, `SkipTask`, `CompletePlanNow` |
| `SwarmWorkerTools` | Plugin (internal) | Worker LLM tools: `DelegateToClientAgent`, `ConsultSubjectMatterExpert`, progress, roadblock, complete, blackboard read/write/wait |
| `SmeConsultationService` | Utility | Queries SME agents sequentially or in parallel. Stateless helper with 30s per-SME timeout. |
| `DependencyResolver` | Utility | Topological sort, ready-task detection, GetTimedOutTasks, cycle validation |
| `ProgressTracker` | Utility | Plan summaries, formatting, completion percentage |
| `AgentCapabilityProjection` | Utility | Builds `SwarmAgentCapability` from `RegistryEntry` + `AgentHealthStatus` |
| `TaskDispatcher` | Utility | Resolves AssignedAgentAlias → pre-existing worker handle, dispatches task assignment |
| `ISwarmHumanInterface` | Interface | EscalateToHumanAsync, AskHumanAsync, RequestApprovalAsync, NotifyProgressAsync |
| `ISwarmAgentRegistry` | Interface | Registry of client agent capabilities (interface only, no DI singleton) |
| `LiveAgentRegistry` | Class | `ISwarmAgentRegistry` impl that queries `IFabrCoreRegistry` + `GetAgentHealth` per planning turn |
| `SwarmDefinition` | Model | Named scope: `AgentHandles` (bare aliases), `SubjectMatterExperts` (bare aliases), `AgentDirectory`, prompts, termination policy |
| `SwarmTask` | Model | Task with `AssignedAgentAlias`, dependencies, InputContext, RequiresApproval |
| `SwarmTaskStatus` | Enum | Pending → Assigned → InProgress → PendingAck → Completed (+ Failed/Skipped/AwaitingInput) |
| `SwarmPromptFoundation` | Static | Prompt overlays + `ClientAgentGuidance` (importable text for client agents) |
| `AddSwarmOrchestration(options)` | DI | Register the swarm services (the only swarm DI call the client makes) |
| `CreateSwarmAsync(definitionName)` | Client API | Spin up the permanent constellation: orchestrator + planner + factory + supervisor (+ workers provisioned by orchestrator). Pass `forceReconfigure: true` after deployments. |

## Architectural Model

```
USER REQUEST
    │
    ▼
┌──────────────────────────────────────────────────────────┐
│ SwarmOrchestratorAgent  (permanent, one per swarm)       │
│   - Owns plan lifecycle, planning, approval, synthesis   │
│   - Drive loop (Orleans timer, default 5s)               │
│   - Reasoning turns on failure / stall                   │
│   - HITL wiring                                          │
│   - Provisions workers on init (1 per AgentHandle)       │
└──────────────────────────────────────────────────────────┘
    │
    ├─── Phase 1: Planning ───────────────────────────────►
    │    SwarmPlannerAgent (LLM, blocking)
    │    → QuerySubjectMatterExpert (if request is ambiguous)
    │    → ListAvailableAgents → see registered client agents + SMEs
    │    → CreateTask(description, agentAlias) × N
    │    → AddDependency between tasks
    │    → FinalizePlan → validates every task has an agent
    │
    ├─── Phase 2: Approval gate ──────────────────────────►
    │    Plan sent to user (PlanAwaitingApproval message)
    │
    ├─── Phase 3: Execution ──────────────────────────────►
    │    Provision SwarmBlackboardAgent for this plan
    │    Register worker handles with supervisor
    │    Start drive-loop timer
    │
    │    For each ready wave:
    │      SwarmSupervisorAgent (permanent, single)
    │        → TaskDispatcher routes to pre-existing workers
    │
    │           SwarmWorkerAgent (permanent, LLM, 1:1 with client)
    │             1. Subscribed to blackboard (per plan)
    │             2. LLM reasons about task
    │             3. Calls DelegateToClientAgent tool:
    │
    │                ┌─────────────────────────────────┐
    │                │ CLIENT AGENT (built by client)  │
    │                │   - Receives plain AgentMessage │
    │                │   - Does domain work            │
    │                │   - Replies with result OR sets │
    │                │     swarm-task-status=roadblock │
    │                └─────────────────────────────────┘
    │
    │             4. Worker LLM processes client response
    │             5. If blocked: ConsultSubjectMatterExpert first
    │             6. Calls CompleteTask or ReportRoadblock
    │             7. Waits idle for next task assignment
    │
    │      Roadblock escalation chain:
    │        Worker (ConsultSME) → Supervisor (ConsultSME) →
    │        Orchestrator (ConsultSME) → Human (last resort)
    │
    │      Two-phase ack:
    │        worker complete → supervisor PendingAck →
    │        SupervisorReport → orchestrator merges →
    │        SupervisorReportAck → supervisor finalizes Completed
    │
    └─── Phase 4: Termination ────────────────────────────►
         CompletePlan: terminate blackboard, unsubscribe
         workers, synthesize FinalResult, deliver to caller.
         Workers + supervisor stay alive for next plan.
```

## When To Use This Skill

- Designing or debugging a multi-agent swarm
- Building a client agent that participates in a swarm (use `ClientAgentGuidance`)
- Listing client agent aliases in `fabrcore-swarm.json` under `SwarmDefinition.AgentHandles`
- Authoring or editing `fabrcore-swarm.json` definitions
- Diagnosing roadblocks, failed dispatches, or stuck plans
- Working with the blackboard for shared coordination data
- Wiring custom HITL via `ISwarmHumanInterface`

## When NOT To Use This Skill

- Building a single agent that acts on its own — use `fabrcore-agent` directly
- Plugin or standalone tool development — use `fabrcore-plugins-tools`
- Basic FabrCore messaging primitives — use `fabrcore-messaging`
- Orleans configuration — use `fabrcore-orleans`

---

## The Client Contract

Three things a client must do to use the swarm.

### 1. Build domain agents normally

Just regular FabrCore agents. They know nothing about the swarm.

```csharp
using FabrCore.Core;
using FabrCore.Experimental.Swarm.Configuration;
using FabrCore.Sdk;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;

[AgentAlias("pipe-expert")]
[Description("Manages pipe manufacturing workflows")]
public class PipeExpertAgent : FabrCoreAgentProxy
{
    private AIAgent? _agent;
    private AgentSession? _session;

    public PipeExpertAgent(
        AgentConfiguration config,
        IServiceProvider serviceProvider,
        IFabrCoreAgentHost fabrcoreAgentHost)
        : base(config, serviceProvider, fabrcoreAgentHost) { }

    public override async Task OnInitialize()
    {
        var tools = await ResolveConfiguredToolsAsync();
        var result = await CreateChatClientAgent("default",
            threadId: config.Handle ?? fabrcoreAgentHost.GetHandle(),
            tools: tools);
        _agent = result.Agent;
        _session = result.Session;
    }

    public override async Task<AgentMessage> OnMessage(AgentMessage message)
    {
        var response = message.Response();
        var state = message.State ?? new();

        // Read swarm task assignment context (optional fields)
        var inputContext = state.GetValueOrDefault("inputContext", "");
        var blackboardHandle = state.GetValueOrDefault("blackboardHandle", "");

        // Optional: import the swarm guidance into your prompt
        var prompt = $"""
            {SwarmPromptFoundation.ClientAgentGuidance}

            Task: {message.Message}
            {(string.IsNullOrEmpty(inputContext) ? "" : $"\nContext from prior tasks:\n{inputContext}")}
            """;

        var result = await _agent!.RunAsync(new ChatMessage(ChatRole.User, prompt), _session!);
        response.Message = result.Messages.Last().Text ?? "";

        // Default reply convention: no special state = successful completion.
        // To signal a roadblock instead:
        //   response.State["swarm-task-status"] = "roadblock";
        //   response.State["swarm-roadblock-question"] = "...";
        //   response.State["swarm-roadblock-type"] = "NeedsInput";
        return response;
    }
}
```

### 2. Configure swarm orchestration and list agent aliases in JSON

```csharp
builder.Services.AddSwarmOrchestration(options =>
{
    options.PlannerModelName = "default";
    options.MaxTasksPerSupervisor = 10;
    options.SwarmDefinitionFilePath = "fabrcore-swarm.json";
    options.DriveLoopInterval = TimeSpan.FromSeconds(5);
    options.DefaultTermination.MaxWallClockTime = TimeSpan.FromHours(4);
    options.DefaultTermination.MaxTaskTime = TimeSpan.FromMinutes(30);
    options.DefaultTermination.MaxTaskRetries = 3;
});
```

No C# agent registration. List the aliases in `fabrcore-swarm.json`:

```json
{
  "SwarmDefinitions": [
    {
      "Name": "pipe-manufacturing",
      "PlannerSystemPrompt": "You plan pipe manufacturing workflows.",
      "AgentHandles": [
        "pipe-expert",
        "inventory-analyst"
      ],
      "SubjectMatterExperts": [],
      "AgentDirectory": []
    }
  ]
}
```

The swarm's `LiveAgentRegistry` discovers each agent's capabilities at planning time:
- **`IFabrCoreRegistry.GetAgentTypes()`** — class-level metadata from `[Description]`, `[FabrCoreCapabilities]`, and `[FabrCoreNote]` attributes on the agent class
- **`IFabrCoreAgentHost.GetAgentHealth("{owner}:{alias}", Detailed)`** — runtime plugins, tools, model, and system prompt from the live grain

The two sources are merged via `AgentCapabilityProjection.Build(alias, registryEntry, health)` into a `SwarmAgentCapability` the planner's LLM sees in `ListAvailableAgents`.

Decorate your agent class with `[FabrCoreCapabilities]` and `[FabrCoreNote]` to give the planner the best possible guidance. `[FabrCoreNote]` is especially valuable for "do NOT use for X" constraints.

### 3. Bring agents online at startup

```csharp
var app = builder.Build();
app.UseFabrCoreServer();

var agentService = app.Services.GetRequiredService<IFabrCoreAgentService>();

await agentService.ConfigureAgentAsync("system", new AgentConfiguration
{
    Handle = "pipe-expert",
    AgentType = "pipe-expert",
    Models = "default",
    Plugins = ["job", "pipe-ops", "plate-search"]
});

await agentService.ConfigureAgentAsync("system", new AgentConfiguration
{
    Handle = "inventory-analyst",
    AgentType = "inventory-analyst",
    Models = "default",
    Plugins = ["inventory-search"]
});

app.Run();
```

The swarm will dispatch tasks to `{owner}:{alias}` (e.g. `system:pipe-expert`). That handle must be reachable when the worker tries to delegate a task.

---

## ForceReconfigure (after deployments)

All swarm agents (orchestrator, planner, supervisor, workers) are **permanent** — they live indefinitely as Orleans grains. After deploying new code that changes agent behavior (updated tools, prompts, OnInitialize logic), existing grains keep their old initialization until forced to re-initialize.

Pass `forceReconfigure: true` to `CreateSwarmAsync` after deploying new code:

```csharp
// Normal startup — agents reuse existing state if already active
var handle = await context.CreateSwarmAsync("jobops");

// After a deployment — force all agents to re-initialize with new code
var handle = await context.CreateSwarmAsync("jobops", forceReconfigure: true);
```

This propagates `ForceReconfigure = true` to every `AgentConfiguration` in the constellation (planner, factory, supervisor, orchestrator, and all worker agents). Each grain re-runs `OnInitialize`, picking up updated agent types, tools, and prompts.

**When to use:**
- After pushing new code that changes any swarm agent behavior
- After updating `fabrcore-swarm.json` (new agent handles, model names, termination policy)
- When workers need to pick up new plugins or tool implementations

**When NOT to use:**
- Normal runtime — agents should reuse their existing LLM sessions and accumulated state
- Between plans on the same deployment — the permanent agents handle this automatically

---

## Reply Convention for Client Agents

Client agents that participate in a swarm should follow this convention.

### Successful completion (default)

```csharp
response.Message = "Allocated plate 42790 to pipe 882f3d5a";
return response;
```

No special state. The adapter worker treats any reply without `swarm-task-status` as a completion.

### Roadblock

```csharp
response.Message = "Cannot allocate — no plates match the required wall thickness";
response.State ??= new Dictionary<string, string>();
response.State["swarm-task-status"] = "roadblock";
response.State["swarm-roadblock-question"] = "Should I use a different tolerance?";
response.State["swarm-roadblock-type"] = "NeedsInput";  // or MissingData, ToolFailure, CapabilityGap
return response;
```

The adapter calls `ReportRoadblock` instead of `CompleteTask`. The orchestrator escalates the question to the human and pauses the task in `AwaitingInput` until a response arrives.

When in doubt, **prefer roadblock over fake completion**. A roadblock is recoverable; a false completion poisons every downstream task that depends on it.

---

## Optional: Blackboard Access for Client Agents

The adapter worker passes the per-plan blackboard handle in the assignment state:

```csharp
var blackboardHandle = state.GetValueOrDefault("blackboardHandle", "");
```

Client agents can talk to the blackboard directly via this handle. Two common patterns:

### Write large data to the blackboard, return a short summary

```csharp
if (!string.IsNullOrEmpty(blackboardHandle))
{
    await fabrcoreAgentHost.SendAndReceiveMessage(new AgentMessage
    {
        ToHandle = blackboardHandle,
        MessageType = SwarmMessageTypes.BlackboardWrite,
        Kind = MessageKind.Request,
        Message = "Write job 4 raw data",
        State = new Dictionary<string, string>
        {
            ["key"] = "job-4-raw",
            ["value"] = jsonPayload
        }
    });
}

response.Message = "Wrote job-4-raw to blackboard: 14 pipes, 42 plate assignments, 8 CANS";
return response;
```

### Read what a sibling wrote

```csharp
var readReply = await fabrcoreAgentHost.SendAndReceiveMessage(new AgentMessage
{
    ToHandle = blackboardHandle,
    MessageType = SwarmMessageTypes.BlackboardRead,
    Kind = MessageKind.Request,
    Message = "Read job 4 data",
    State = new Dictionary<string, string> { ["key"] = "job-4-raw" }
});

var jobData = readReply.Message;
```

If a client agent does not need shared data, ignore the blackboard handle entirely. It's optional.

---

## SwarmDefinition (`fabrcore-swarm.json`)

```json
{
  "SwarmDefinitions": [
    {
      "Name": "pipe-manufacturing",
      "Description": "Swarm for pipe manufacturing workflows",
      "PlannerSystemPrompt": "You plan pipe manufacturing workflows.",
      "PlannerModelName": "default",
      "MaxTasksPerSupervisor": 10,
      "AgentHandles": [
        "pipe-expert",
        "inventory-analyst"
      ],
      "SubjectMatterExperts": [
        "manufacturing-knowledge-agent"
      ],
      "AgentDirectory": [
        { "Handle": "system:knowledge-agent", "Description": "Search the knowledge base for specs and procedures" }
      ],
      "WorkerModelName": "default",
      "WorkerSystemPromptOverlay": null,
      "Termination": {
        "MaxTotalIterations": 200,
        "MaxWallClockTime": "04:00:00",
        "MaxTaskTime": "00:30:00",
        "MaxTaskRetries": 3,
        "MaxRoadblocks": 10,
        "MaxReplanAttempts": 5
      }
    }
  ]
}
```

Field reference:

| Field | Purpose |
|---|---|
| `AgentHandles` | Bare aliases (no `owner:` prefix) of the client agents the planner can assign tasks to in this scope. Each alias must match a client agent class (`[AgentAlias]`) that the client brings online at startup via `ConfigureAgentAsync`. |
| `SubjectMatterExperts` | Bare aliases of client agents designated as domain knowledge sources. The swarm consults these BEFORE escalating to the human when workers, the planner, or the orchestrator encounter roadblocks or ambiguities. NOT task executors. |
| `AgentDirectory` | Pre-existing helper agents (knowledge bases, ERP lookups). Client agents can query these mid-task. NOT executors. |
| `WorkerModelName` | Optional LLM model name for worker agents (from fabrcore.json ModelConfigurations). Workers are long-lived agents with their own LLM. Null = use "default". |
| `WorkerSystemPromptOverlay` | Optional text forwarded to client agents in the assignment state as `clientAgentGuidance`. Clients can include it in their prompt if they want extra swarm-friendly guidance. |
| `Termination.MaxTaskTime` | Per-task timeout. Enforced by the drive loop. |
| `Termination.MaxTaskRetries` | Number of retries on transient failures. Enforced by the drive loop. |

---

## Orchestration Mechanics (Important)

### Drive loop

A non-persistent Orleans timer fires every `SwarmOptions.DriveLoopInterval` while a plan is `Executing`. Each tick:

1. Checks termination policy
2. Reclaims tasks past `MaxTaskTime` (marks Failed)
3. Retries Failed tasks under `MaxTaskRetries`
4. Sends `NotifyProgressAsync` (throttled to every 3rd tick)
5. Calls `ProcessNextWave`
6. Detects stall and triggers a reasoning turn

The drive loop is a safety net. Most progression is still message-driven (supervisor reports, worker completions). The loop guarantees the swarm cannot get permanently stuck waiting for a message that never arrives.

### Two-phase completion ack

```
adapter -> CompleteTask -> supervisor marks PendingAck (NOT Completed)
supervisor: wave done -> SupervisorReport to orchestrator
orchestrator: merges (PendingAck -> Completed in plan), increments TotalTasksCompleted
orchestrator -> SupervisorReportAck with merged task IDs
supervisor: transitions PendingAck -> Completed for acked IDs
```

A dropped report leaves tasks in `PendingAck`. The drive loop's next tick re-sends the report. The orchestrator's merge path is idempotent — re-acking an already-completed task is a no-op.

### Orchestrator reasoning turn

The orchestrator has its own LLM (via the existing planner config) and `SwarmOrchestratorDecisionTools`. It runs at:

- **Task failure** — picks one of: `ProceedWithWave`, `RequestReplan`, `ConsultSME`, `AskHuman`, `RetryTask`, `SkipTask`, `CompletePlanNow`
- **Stall detection** — same tools

The orchestrator prefers `ConsultSME` over `AskHuman` when the problem is a knowledge gap or domain question. If SMEs cannot help, the orchestrator falls through to `AskHuman` automatically.

The decision is captured by `SwarmOrchestratorDecisionTools.LastDecision` and dispatched by `ApplyDecision`. If the LLM fails to call any tool, falls back to auto-replan.

### Roadblock guard

`SwarmWorkerTools` has a `RoadblockReported` flag set inside `ReportRoadblock`. The adapter worker checks this before calling `CompleteTask` so a task that already escalated cannot be silently overwritten with a fake completion.

### Verbatim final results

`CompletePlan` builds the FinalResult by concatenating completed task results **without truncation**. The swarm's deliverable is the actual output of every completed task.

---

## Subject Matter Experts (SME)

SME agents are client-provided knowledge agents that the swarm consults **before escalating to the human**. They are configured per-swarm via `SubjectMatterExperts` in `fabrcore-swarm.json` — bare aliases just like `AgentHandles`. The swarm queries them using `SwarmMessageTypes.SmeConsultation` messages.

### Escalation Chain

```
Worker hits roadblock
  → Worker calls ConsultSubjectMatterExpert (tool)
    → If SME answers: worker continues, no escalation
    → If no answer: worker calls ReportRoadblock

Supervisor receives roadblock
  → Supervisor calls SmeConsultationService.ConsultAsync
    → If SME answers: sends RoadblockResolution to worker, skips orchestrator
    → If no answer: escalates to orchestrator

Orchestrator receives roadblock or reasoning turn picks ConsultSME
  → Orchestrator calls SmeConsultationService.ConsultAsync
    → If SME answers: resolves roadblock, resumes execution
    → If no answer: escalates to human via ISwarmHumanInterface
```

### SME Consultation at Each Level

| Level | Tool / Method | When | Behavior |
|---|---|---|---|
| **Planner** | `QuerySubjectMatterExpert` (planner tool) | Before building the plan, when request is ambiguous | Queries all SMEs in parallel (`ConsultAllAsync`), returns combined answers to planner LLM |
| **Worker** | `ConsultSubjectMatterExpert` (worker tool) | During task execution, before reporting a roadblock | Queries SMEs sequentially (`ConsultAsync`), returns first answer |
| **Supervisor** | `SmeConsultationService.ConsultAsync` (automatic) | When a worker reports a roadblock | Tries SMEs before escalating to orchestrator. If resolved, sends `RoadblockResolution` directly to worker. |
| **Orchestrator** | `ConsultSME` (decision tool) | During reasoning turn on failure/stall | LLM picks `ConsultSME` when it judges the problem is a knowledge gap. Falls through to `AskHuman` if SMEs can't help. |
| **Orchestrator** | `SmeConsultationService.ConsultAsync` (automatic) | When a roadblock is escalated from supervisor | Tries SMEs before calling `ISwarmHumanInterface.EscalateToHumanAsync`. |

### SmeConsultationService

Stateless helper (`Engine/SmeConsultationService.cs`), instantiated per-use like `TaskDispatcher`.

```csharp
var smeService = new SmeConsultationService(agentHost, smeAliases, owner, logger);

// Sequential — returns first successful answer
var result = await smeService.ConsultAsync("What is the minimum wall thickness for 8-inch pipe?");

// Parallel — returns all answers (used by planner for multiple perspectives)
var results = await smeService.ConsultAllAsync("What data does this job need?");
```

Each SME query has a **30-second timeout**. Dead or slow SMEs are logged and skipped.

### SME Agent Reply Convention

SME agents receive `MessageType = "swarm-sme-consultation"` with the question in `Message` and optional `State["context"]`. They should reply with the answer in `response.Message`. Optionally set `response.State["sme-status"] = "answered"` or `"unknown"`. Any non-empty response that isn't explicitly `"unknown"` is treated as an answer.

### Configuration

```json
{
  "SwarmDefinitions": [
    {
      "Name": "jobops",
      "AgentHandles": ["jobops-job-planning-agent"],
      "SubjectMatterExperts": ["jobops-knowledge-agent"],
      "AgentDirectory": []
    }
  ]
}
```

The client must bring SME agents online at startup via `ConfigureAgentAsync`, just like task executor agents. SME agents appear in the registry and support `GetHealth`.

---

## ISwarmHumanInterface

```csharp
public interface ISwarmHumanInterface
{
    Task EscalateToHumanAsync(SwarmRoadblock roadblock, SwarmPlan plan);
    Task AskHumanAsync(string question, SwarmPlan plan, string? taskId = null);
    Task<bool> RequestApprovalAsync(string description, SwarmTask task, SwarmPlan plan);
    Task NotifyProgressAsync(SwarmPlanSummary summary);
}
```

| Method | When called |
|---|---|
| `EscalateToHumanAsync` | Worker roadblock after SME consultation failed at both supervisor and orchestrator levels |
| `AskHumanAsync` | Orchestrator's reasoning turn picked `AskHuman` (or `ConsultSME` failed and fell through) |
| `RequestApprovalAsync` | About to dispatch a task with `RequiresApproval == true`. Return false → task marked Skipped. |
| `NotifyProgressAsync` | Drive loop, throttled (every 3rd tick) |

The `DefaultSwarmHumanInterface` routes everything through `plan.CallerHandle` via `SendMessage`. Override and register your own implementation to wire a custom UI.

---

## Common Patterns

### Linear pipeline

```
Task 1: Fetch job 4 (agent: job-fetcher)
Task 2: Analyze readiness (agent: pipe-expert)  depends on Task 1
Task 3: Allocate plates (agent: inventory-analyst)  depends on Task 2
Task 4: Verify (agent: pipe-expert)  depends on Task 3
```

The planner calls `CreateTask` four times with `dependsOn` linking them. Each runs in its own wave because they're sequential.

### Fan-out wave

```
Task 1: Fetch job 4 (agent: job-fetcher)
Task 2a: Validate pipes (agent: pipe-expert)         depends on 1
Task 2b: Validate plates (agent: inventory-analyst)  depends on 1
Task 2c: Validate schedules (agent: scheduler)       depends on 1
Task 3: Aggregate validations (agent: pipe-expert)   depends on 2a, 2b, 2c
```

Tasks 2a/2b/2c run in parallel in the same wave.

### Coordinated handoff via blackboard

When two agents in the same wave need to share data:

- Worker A writes `customer-list` to the blackboard
- Worker B calls `WaitForBlackboardKey("customer-list", 60)` and proceeds when it appears

This is for in-wave coordination only — for cross-wave data, use dependencies.

### High-risk task with approval gate

```csharp
// Planner sets RequiresApproval = true on the destructive task:
CreateTask(
    description: "Delete all archived jobs older than 2025",
    agentAlias: "archive-cleaner",
    requiresApproval: true);
```

Before dispatching, the orchestrator calls `ISwarmHumanInterface.RequestApprovalAsync`. If the human denies, the task is marked Skipped and the wave proceeds.

---

## Diagnosing Common Issues

| Symptom | Cause | Fix |
|---|---|---|
| Task fails with "Unknown agent alias" | Alias not in `SwarmDefinition.AgentHandles`, OR agent was not brought online via `ConfigureAgentAsync` (so `GetAgentHealth.IsConfigured == false` and `LiveAgentRegistry` skipped it) | Add the alias to `AgentHandles` in `fabrcore-swarm.json` AND add the startup `ConfigureAgentAsync` call |
| Task fails with "client agent handle not resolved" | Client agent was not brought online via `ConfigureAgentAsync` at startup | Add the startup code that creates `{owner}:{alias}` |
| Plan stuck in `Executing`, no progress | Supervisor report dropped or worker hung | Drive loop should recover within `DriveLoopInterval` + `MaxTaskTime`; check logs for `SWARM LOOP` messages |
| All tasks fail with truncated input | Client agent's LLM output was truncated re-transcribing a tool result | Update the client agent to write large data to the blackboard and return a short summary |
| Roadblock task gets marked Completed anyway | The adapter's `RoadblockReported` guard didn't fire | Verify the client agent is setting `response.State["swarm-task-status"] = "roadblock"` before returning |
| Plan never reaches user with final result | `plan.CallerHandle` not set, or the caller agent isn't message-able | Verify `CreateSwarmAsync` was used (it sets the caller handle automatically) |
| Planner creates tasks with no agent | Old code path or missing alias validation | `FinalizePlan` should reject this — verify you're on the current version |
| Workers not receiving tasks after deployment | Grains still running old `OnInitialize` | Pass `forceReconfigure: true` to `CreateSwarmAsync` |
| Worker tools return errors about missing client handle | Worker grain was not re-initialized after code change | Pass `forceReconfigure: true` to `CreateSwarmAsync` |

---

## Other docs

- `references/swarm-architecture.md` — deep architecture reference (state machine, message flow, registry scoping, SME consultation flow)
- `references/message-protocol.md` — full message type catalog and routing (includes SME consultation messages)
- `assets/client-agent-template.cs` — minimal client agent template
- `assets/swarm-registration-template.cs` — DI wiring template
- `assets/fabrcore-swarm-example.json` — example swarm definition file
- `README.md` (in the package root) — user-facing intro and getting started
