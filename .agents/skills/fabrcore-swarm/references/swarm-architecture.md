# Swarm Architecture Reference

Deep reference for the FabrCore swarm orchestration model. Read this when you need to understand how the components fit together at the message level, how the state machine drives execution, or how the registry is scoped.

## Component diagram

```
┌─────────────────────────────────────────────────────────────────┐
│ Per-user swarm (all permanent agents, live indefinitely)        │
│                                                                 │
│   ┌───────────────────────────────────────────────────────┐     │
│   │ SwarmOrchestratorAgent  (permanent)                   │     │
│   │   - AIAgent (planner model) for user Q&A + reasoning  │     │
│   │   - Drive loop (Orleans timer)                        │     │
│   │   - Owns SwarmPlan, BlackboardHandle                  │     │
│   │   - Wires ISwarmHumanInterface                        │     │
│   │   - Provisions workers on init (1:1 with clients)     │     │
│   └───────────────────────────────────────────────────────┘     │
│             │                                  │                │
│             ▼                                  ▼                │
│   ┌──────────────────────┐         ┌────────────────────────┐   │
│   │ SwarmPlannerAgent    │         │ SwarmBlackboardAgent   │   │
│   │   (permanent)        │         │   (per plan, ephemeral)│   │
│   │ - LLM + planner tools│         │ - KV store + events    │   │
│   └──────────────────────┘         └────────────────────────┘   │
│             │                                  │                │
│             ▼                                  ▲                │
│   ┌──────────────────────┐                     │                │
│   │ SwarmSupervisorAgent │                     │                │
│   │   (permanent, single)│                     │                │
│   │ - TaskDispatcher     │                     │                │
│   │ - Two-phase ack      │                     │                │
│   │ - Plan-aware, resets │                     │                │
│   └──────────────────────┘                     │                │
│             │                                  │                │
│             ▼ (routes to worker)               │                │
│   ┌──────────────────────────────────┐         │                │
│   │ SwarmWorkerAgent (permanent, LLM)│ ────────┤ subscribe      │
│   │ - 1:1 with client agent          │         │ (per plan)     │
│   │ - Own LLM + chat history         │         │                │
│   │ - DelegateToClientAgent tool     │         │                │
│   └──────────────────────────────────┘         │                │
│             │                                  │                │
│             ▼ (DelegateToClientAgent)          │                │
└─────────────────────────────────────────────── │ ───────────────┘
              │                                  │
              ▼                                  │
   ┌──────────────────────────────────┐          │
   │ CLIENT AGENT  (built by client)  │ ─────────┘ optional read/write
   │ {owner}:{AssignedAgentAlias}     │
   │ - Owned by the client app        │
   │ - Plain FabrCore agent           │
   │ - Plugins, prompts, logic = own  │
   │ - Knows nothing about swarm      │
   └──────────────────────────────────┘
```

The dashed line is the only optional path: client agents can talk to the blackboard directly via the handle the adapter passes in the assignment state, but they don't have to.

## Lifecycle: from CreateSwarmAsync to plan complete

### 1. CreateSwarmAsync

`SwarmClientExtensions.CreateSwarmAsync(definitionName)` configures four agents scoped to the calling user:

1. `swarm-planner` (used internally by the orchestrator)
2. `swarm-factory` (registry discovery surface)
3. `swarm-supervisor` (single permanent supervisor)
4. `swarm-orchestrator` (the entry point — created last, provisions workers on init)

The orchestrator's `OnInitialize` also provisions one `swarm-worker-{alias}` per `AgentHandle` in the definition. All agents are permanent. Pass `forceReconfigure: true` after deployments to re-initialize.

The orchestrator's `OnInitialize` resolves:
- `SwarmOptions` from DI
- `ISwarmEventSink` (default: `NullSwarmEventSink`)
- `ISwarmHumanInterface` (default: `DefaultSwarmHumanInterface`)
- `IFabrCoreAgentService` (for blackboard provisioning)
- `IAgentMemoryService` (optional, only if `MemoryConnectionStringName` is set)
- The `SwarmDefinition` matching `Args["SwarmDefinition"]` — stored and passed to the planner/supervisor via `definitionJson` in message state
- The orchestrator's own `AIAgent` with `SwarmOrchestratorDecisionTools` registered

The orchestrator does NOT build a registry. Agent lookups are done ad-hoc by the planner and supervisor via `LiveAgentRegistry(fabrcoreAgentHost, fabrCoreRegistry, definition)`.

It also tries to restore any persisted plan state from previous Orleans grain activations and re-registers the drive-loop timer if the restored plan is `Executing`.

### 2. User sends a request

The orchestrator's `OnMessage` routes by `Channel`. A user request with no swarm-internal channel falls through to `HandleUserMessage`.

If `_currentPlan is null`:
- `HandleUserMessage` calls `StartPlanning`
- `StartPlanning` builds a `SwarmPlan` shell, registers it as `Status = Planning`
- Sends a blocking `SendAndReceiveMessage` to the planner with `MessageType = swarm-plan-request`

### 3. Planning

`SwarmPlannerAgent.HandlePlanRequest`:
1. Deserializes the plan shell from message state
2. Sets the plan on `SwarmPlannerTools` (via `_plannerTools.SetPlan(plan)`)
3. Sets the scoped registry on the tools (via `SetRegistry`)
4. Sets the agent directory (via `SetAgentDirectory`)
5. Sets SME context if `SubjectMatterExperts` configured (via `SetSmeContext`)
6. Composes a prompt: `CoreDirectives + PlannerOverlay + (optional definition prompt) + memory recall + user request`
7. Runs the planner LLM with the planner tools registered
8. Returns the resulting plan in response state

The LLM calls planner tools in this order:
0. `QuerySubjectMatterExpert` (optional) — if the request is ambiguous, queries all configured SMEs for clarification before building the plan. May be called multiple times.
1. `ListAvailableAgents` — sees registered client agents, their advertised plugins/tools, notes, and available SMEs
2. `CreateTask(description, agentAlias, ...)` repeatedly. The tool **validates** that the alias exists in the scoped registry and rejects unknown aliases
3. `AddDependency` between tasks where data flows
4. `FinalizePlan` — validates dependency cycles and that every task has an `AssignedAgentAlias`

If the planner returns 0 tasks, `Status = PlanningRoadblock` and the orchestrator enters conversational mode with the user. If tasks exist, `Status = AwaitingApproval`.

### 4. Approval gate

`HandleApprovalResponse` parses the user's reply:
- `approve` / `approved` / `go` / `execute` / `yes` / `start` → transition to `Executing`, provision blackboard, start drive-loop timer, send self-message to begin first wave
- `cancel` / `reject` / `no` / `stop` → mark `Cancelled`, stop drive loop, clear `_currentPlan`
- Anything else → forwarded to the orchestrator's LLM for Q&A about the plan

### 5. Execution

Two concurrent mechanisms drive progression:

**Message-driven:** supervisor reports, adapter worker completions, roadblock responses, the orchestrator's self-message (`swarm-execution-loop`) all fire `OnMessage` handlers immediately.

**Drive loop (Orleans timer):** registered via `fabrcoreAgentHost.RegisterTimer(DriveLoopTimerName, DriveLoopTimerMessageType, ...)`. Fires every `DriveLoopInterval`. Each tick calls `DriveLoop()`:

```
1. Throttled NotifyProgressAsync (every 3rd tick)
2. CheckTermination — wall clock, iterations, roadblocks
3. GetTimedOutTasks — mark Failed if past MaxTaskTime
4. Retry pass — Failed tasks under MaxTaskRetries get re-queued as Pending
5. ProcessNextWave (the same code path as message-driven progression)
6. Stall detection — if nothing in flight, ready, or pending but plan unfinished, run reasoning turn
```

`ProcessNextWave`:

```
1. CheckTermination
2. GetReadyTasks (dependencies completed)
3. If no ready tasks but unresolved tasks remain → wait for reports
4. PropagateContext (copy completed dependency results into InputContext)
5. Approval gate: tasks with RequiresApproval=true → RequestApprovalAsync. Denied → Skipped.
6. DispatchToSupervisor (single permanent supervisor):
     - Register worker handles with supervisor (once per plan)
     - Send TaskBatch message with all ready tasks + blackboardHandle + definitionJson
     - Mark tasks Assigned, increment TotalTasksDispatched
```

The supervisor's `HandleTaskBatch`:

```
1. Deserialize tasks from state
2. Persist supervisor state
3. Subscribe workers to the plan's blackboard
4. Build TaskDispatcher (registry, owner, workerHandles, clientAgentGuidance, blackboardHandle)
5. For each task → dispatcher.DispatchTask:
     a. Validate task.AssignedAgentAlias is set
     b. Validate alias exists in scoped registry
     c. Resolve pre-existing worker handle for the alias
     d. Send TaskAssignment message with state:
          taskId, taskJson, supervisorHandle, planId, blackboardHandle,
          clientAgentHandle, inputContext (formatted), clientAgentGuidance
     e. Mark task Assigned, increment AttemptCount, set StartedAt
```

### 6. Worker reasons about the task

`SwarmWorkerAgent.HandleTaskAssignment`:

```
1. Read state keys (taskId, supervisorHandle, blackboardHandle, planId, inputContext)
2. _workerTools.SetTaskContext(...) — resets per-task state, preserves blackboard cache
3. Compose prompt with worker directives + task description + input context
4. Run LLM reasoning turn (_agent.RunAsync)
5. LLM calls tools:
     - DelegateToClientAgent(message) → sends to paired client agent, returns result
     - ReadBlackboard / WriteBlackboard — coordinate with siblings
     - ReportProgress — update supervisor
     - CompleteTask(result) — report success
     - ReportRoadblock(description) — escalate
6. Worker stays alive for next task assignment
```

Workers have their own LLM and accumulate chat history across tasks and plans.

### 7. Two-phase completion ack

```
adapter -> _workerTools.CompleteTask(result)
  → sends TaskComplete to supervisor

supervisor.HandleWorkerReport(TaskComplete):
  task.Status = PendingAck    (NOT Completed)
  task.Result = ...
  task.CompletedAt = now

supervisor.CheckWaveCompletion:
  if all tasks in {PendingAck, Completed, Failed, Skipped, Cancelled}:
    send SupervisorReport to orchestrator with serialized _tasks

orchestrator.HandleSupervisorReport:
  for each task in completedTasks:
    if planTask.Status == Completed (already): re-ack idempotently
    else:
      finalStatus = (PendingAck → Completed) or as-is
      plan.TotalTasksCompleted++ (only on Completed)
      _eventSink.OnTaskCompleted / OnTaskFailed
    mergedTaskIds.Add(task.Id)

  if mergedTaskIds.Count > 0:
    send SupervisorReportAck to supervisor with mergedTaskIds

supervisor.HandleSupervisorReportAck:
  for each id in ackedTaskIds:
    if task.Status == PendingAck: task.Status = Completed
```

A dropped report leaves the supervisor's tasks in `PendingAck`. The orchestrator's merge path is idempotent — re-acking an already-completed task is a no-op.

### 8. Reasoning turn on failure or stall

`HandleSupervisorReport` after merging:

```
failedTasks = plan.FlattenTasks().Where(Status == Failed)
if failedTasks.Count > 0:
  situation = "<failure description with task IDs and errors>"
  decision = await RunReasoningTurn("task-failure", situation)
  await ApplyDecision(decision, plan, failedTasks)
```

`DriveLoop` after `ProcessNextWave`:

```
if inFlightBefore == 0 && inFlightAfter == 0 && pending == 0
   && unresolved tasks remain:
  situation = "<stalled task list>"
  decision = await RunReasoningTurn("stall", situation)
  await ApplyDecision(decision, plan, stuckTasks)
```

`RunReasoningTurn`:

```
1. _decisionTools.Reset()
2. Build prompt: CoreDirectives + OrchestratorOverlay + original request +
   FormatPlanStatus + situation
3. _agent.RunAsync(prompt, _session)
4. Return _decisionTools.LastDecision
```

`ApplyDecision`:

```
switch decision.Kind:
  Proceed     -> nothing (next wave runs naturally)
  Replan      -> RequestReplan(rationale)
  ConsultSME  -> SmeConsultationService.ConsultAsync(question)
                 if answered: resolve roadblock + resume
                 if not: fall through to EscalateQuestionToHuman
  AskHuman    -> EscalateQuestionToHuman (synthetic roadblock + ISwarmHumanInterface.AskHumanAsync)
  Retry       -> reset task state → Pending
  Skip        -> task.Status = Skipped
  Complete    -> CompletePlan(terminated=false)
```

### SME Consultation Flow

Subject Matter Expert (SME) agents are client-provided knowledge agents configured via `SwarmDefinition.SubjectMatterExperts` (bare aliases). The swarm consults them at every decision point before falling back to human escalation.

**Escalation chain with SME:**

```
Worker LLM reasons about task
  → Blocked? ConsultSubjectMatterExpert tool (queries SMEs via SmeConsultationService)
    → SME answers → worker continues, no escalation
    → No answer → ReportRoadblock tool → message to supervisor

Supervisor.HandleWorkerReport (Roadblock case)
  → SmeConsultationService.ConsultAsync (tries all configured SMEs)
    → SME answers → RoadblockResolution directly to worker, skip orchestrator
    → No answer → RoadblockEscalation to orchestrator

Orchestrator.HandleEscalatedRoadblock
  → SmeConsultationService.ConsultAsync
    → SME answers → RoadblockResolution back to supervisor/worker
    → No answer → ISwarmHumanInterface.EscalateToHumanAsync (human is LAST resort)

Orchestrator reasoning turn (ConsultSME decision)
  → SmeConsultationService.ConsultAsync
    → SME answers → resolve roadblock or continue
    → No answer → fall through to AskHuman
```

**Planner SME consultation:**

The planner's LLM can call `QuerySubjectMatterExpert` before building the plan. This tool uses `SmeConsultationService.ConsultAllAsync` (queries all SMEs in parallel) to gather multiple perspectives on ambiguous requests. The planner prompt instructs the LLM to consult SMEs when the request is unclear.

**SmeConsultationService** (`Engine/SmeConsultationService.cs`):

Stateless helper, instantiated per-use. Constructor takes `IFabrCoreAgentHost`, `List<string> smeAliases`, `string owner`, `ILogger`.

- `ConsultAsync(question, context?)` — queries SMEs sequentially, returns first successful answer or null
- `ConsultAllAsync(question, context?)` — queries all SMEs in parallel via `Task.WhenAll`, returns all answers
- Each query uses `SendAndReceiveMessage` with `MessageType = SwarmMessageTypes.SmeConsultation`
- 30-second timeout per SME; dead SMEs are logged and skipped

**SME agent reply convention:**

SME agents receive `MessageType = "swarm-sme-consultation"` with the question in `Message` and optional context in `State["context"]`. They reply with the answer in `response.Message`. Optionally set `State["sme-status"] = "answered"` or `"unknown"`. Any non-empty response that isn't explicitly `"unknown"` is treated as an answer.

### 9. Termination

`CompletePlan`:

```
1. StopDriveLoopTimer (UnregisterTimer)
2. TerminateBlackboardAsync (BlackboardTerminate one-way to blackboard agent)
3. plan.Status = Completed (or Failed if terminated=true)
4. plan.CompletedAt = now
5. Build FinalResult VERBATIM:
     for each Completed task: "- {description}: {result ?? "(no result)"}"
6. SetState + FlushState
7. _eventSink.OnPlanCompleted
8. SendResultToCaller(plan.CallerHandle, FinalResult)
9. _currentPlan = null
```

## Registry scoping

There is no DI-registered global agent registry. `LiveAgentRegistry` is built ad-hoc from a `SwarmDefinition` and the current agent host:

```csharp
var registry = new LiveAgentRegistry(
    fabrcoreAgentHost,
    fabrCoreRegistry,   // IFabrCoreRegistry, optional
    definition,         // SwarmDefinition deserialized from state["definitionJson"]
    logger);
```

For each bare alias in `definition.AgentHandles`, `EnsureLoadedAsync` does two lookups:

1. **`IFabrCoreRegistry.GetAgentTypes()`** — scans for a `RegistryEntry` whose `Aliases` contains the target alias. Extracts `Description`, `Capabilities`, and `Notes` (from the agent class's `[Description]`, `[FabrCoreCapabilities]`, and `[FabrCoreNote]` attributes). Optional — skipped if `IFabrCoreRegistry` is not available.
2. **`IFabrCoreAgentHost.GetAgentHealth("{owner}:{alias}", HealthDetailLevel.Detailed)`** — required. If `health.IsConfigured == false` the alias is dropped from the available list with a warning (client forgot to `ConfigureAgentAsync`).

Both are merged via `AgentCapabilityProjection.Build(alias, registryEntry, health)`:

- **Description**: `RegistryEntry.Description` + `RegistryEntry.Capabilities` (appended)
- **Notes**: joined list of `RegistryEntry.Notes` (from multiple `[FabrCoreNote]` attributes)
- **Plugins/Tools**: `health.Configuration.Plugins` / `health.Configuration.Tools` (the live configured state)

Results are cached per `LiveAgentRegistry` instance. The planner creates one per planning turn, the dispatcher creates one per wave. Cache lifetime is naturally short — agents that come online between planning and dispatch are picked up on the next wave.

## State persistence

Each agent persists its state via `SetState` + `FlushStateAsync`:

| Agent | Persisted keys |
|---|---|
| Orchestrator | `swarm-plan` |
| Supervisor | `supervisor-tasks`, `supervisor-orchestrator`, `supervisor-workers`, `supervisor-planId`, `supervisor-blackboardHandle` |
| Blackboard | `blackboard-entries`, `blackboard-subscribers`, `blackboard-plan-id` |
| Worker | LLM chat history (via FabrCore session persistence), blackboard cache (in-memory) |

On Orleans grain reactivation, each agent's `OnInitialize` restores its persisted state inside a try/catch and starts fresh on corruption.

## Termination policy enforcement

`SwarmTerminationPolicy` fields and where they're enforced:

| Field | Default | Where enforced |
|---|---|---|
| `MaxTotalIterations` | 100 | `CheckTermination` (drive loop + ProcessNextWave) — counts `TotalTasksDispatched` |
| `MaxWallClockTime` | 4h | `CheckTermination` — `now - plan.CreatedAt` |
| `MaxTaskTime` | 30m | `DriveLoop` step 2 — `GetTimedOutTasks(plan, now, MaxTaskTime)` |
| `MaxTaskRetries` | 3 | `DriveLoop` step 3 — `task.AttemptCount < MaxTaskRetries` |
| `MaxRoadblocks` | 10 | `CheckTermination` — counts `plan.TotalRoadblocks` |
| `MaxReplanAttempts` | 5 | `RequestReplan` — checks `plan.PlanRevision` before forwarding to planner |

## What is NOT in scope

- **Custom workers.** You don't write workers. Workers are provisioned automatically by the orchestrator (1:1 with each `AgentHandle` in the definition). Client agents are just plain FabrCore agents.
- **Dynamic client agent creation.** The swarm provisions its own internal agents (workers, supervisor) but does not create client agents. Clients bring their domain agents online at application startup.
- **Plugin args per task.** Removed. Client agents are configured at startup with whatever plugin settings they need; the swarm cannot inject per-task plugin configuration.
- **Mid-run agent registration.** Clients list all agent aliases in `fabrcore-swarm.json` (restart required to pick up changes) and bring them online at startup via `ConfigureAgentAsync`. There is no runtime alias hot-reload, but `LiveAgentRegistry` queries live health per planning turn so agents that come online mid-session are picked up on the next plan.
