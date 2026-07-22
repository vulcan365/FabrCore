# TaskAgent Architecture (Reference)

A deeper dive into how the TaskAgent is wired internally — useful when
extending behavior, debugging unusual flows, or deciding whether to subclass.

## File Layout

```
src/FabrCore.Agents.TaskAgent/
├── TaskAgent.cs                              The orchestrator agent
├── TaskAgentMessageTypes.cs                  Inter-agent message-type constants
├── Brain/
│   ├── SubconsciousState.cs                  Per-message + long-running state
│   ├── UserIntent.cs                         Intent enum
│   ├── IntentTriage.cs                       FastModel classifier helper
│   └── ReplanAttempt.cs                      Loop-detector record
├── Planning/
│   ├── TaskPlan.cs                           The plan document
│   ├── TaskItem.cs                           One unit of delegated work
│   ├── TaskItemStatus.cs                     Pending / InProgress / Completed / Skipped / Failed
│   ├── ReplanTrigger.cs                      What caused a replan call
│   ├── PlanReplanner.cs                      Single replan entry point + initial-plan builder
│   └── PlanFormatter.cs                      Status formatting + truncation helpers
├── Coordination/
│   ├── AgentCapability.cs                    Projected client-agent metadata
│   ├── AgentCapabilityRegistry.cs            Discovers capabilities at runtime
│   ├── AgentCapabilityProjection.cs          Builds AgentCapability from registry+health
│   ├── SmeConsultationService.cs             SME query helper (parallel, first-wins)
│   └── DelegationService.cs                  Delegate one task to a client agent
├── Configuration/
│   ├── TaskAgentDefinition.cs                Themed scope (clients, SMEs, models, prompts)
│   ├── TaskAgentDefinitionFile.cs            Wire format of fabrcore-taskagent.json
│   ├── ITaskAgentDefinitionProvider.cs       Resolution interface
│   ├── FileTaskAgentDefinitionProvider.cs    Default JSON-file-backed provider
│   ├── TaskAgentOptions.cs                   Host-level defaults
│   └── TaskAgentServiceExtensions.cs         AddTaskAgentServices DI extension
├── Envelope/
│   ├── StructuredEnvelope.cs                 The fabrcore-envelope record shape
│   └── EnvelopeParser.cs                     Defensive extractor (never throws)
└── Learning/
    └── LessonExtractor.cs                    Teaching → IAgentMemoryService.SaveMemoryAsync
```

## State Lifecycles

The TaskAgent persists three logically separate state slots via the standard
FabrCore state API (`SetState` / `GetStateAsync` / `FlushStateAsync`):

### `"task-plan"` — `TaskPlan`

- Created on `HandleNewPlan` via `PlanReplanner.BuildInitialPlanAsync`.
- Mutated by `PlanReplanner.ReplanAsync` on every trigger.
- `IsRunning` flips false when the plan completes or is stopped.
- Cleared (`_currentPlan = null`) inside `CompletePlan` after the final
  result is sent — but the persisted `task-plan` snapshot remains until the
  next `SetState`/`FlushStateAsync` cycle (so a crash mid-completion doesn't
  lose the deliverable).
- On `OnInitialize`, the agent restores it and resumes the self-loop only
  when `IsRunning && !IsPaused && !IsStuck`.

### `"subconscious"` — `SubconsciousState`

Two halves, persisted together but with very different lifetimes:

| Field | Lifetime | Reset by |
|---|---|---|
| `CurrentIntent`, `CurrentIntentReasoning`, `CurrentIntentConfidence`, `ArrivedDuringRunningPlan`, `PendingLessonText`, `PendingLessonCategory`, `SkipPlannerThisTurn` | one user turn | `ResetTurn()` at the top of `HandleUserMessage` |
| `IsPaused`, `LearningModeEnabled`, `CurrentFocus`, `ActiveRuleRefs`, `RecentReplans`, `IsStuck`, `StuckReason`, `LastUserMessageAt` | persisted, cross-turn | explicit handlers |

`ResetTurn()` zeroes only the per-message half, preserving the long-running
half. `PersistStateAndPlan()` writes both halves at the end of every turn.

### Memory partition (per-handle)

Wired via `IAgentMemoryProvider.GetMemoryService(fabrcoreAgentHost.GetHandle())`
in `OnInitialize`. Per-agent isolation is automatic — every memory query
filters by `AgentHandle`. The TaskAgent never crosses partitions; if you want
shared memory across grains, configure them with the same handle (which is
unusual — typically one grain per scope means one partition per scope).

## Message Flow Walkthroughs

### A. New goal, sequential plan, success

```
user → TaskAgent.OnMessage("Build me a quote for job 42")
        IntentTriage(FastModel)               → NewTask, conf 0.9
        BuildInitialPlanAsync(PlannerModel)   → 3 tasks, each assigned
        SetState("task-plan", plan); FlushStateAsync
        SendSelfMessage()
        return "I've built a plan with 3 tasks…"

self-tick → TaskAgent.OnMessage(SelfChannel)
        ProcessNextTask
          drain queue                         (empty)
          pick task #1
          DelegationService.DelegateAsync     → ProseText + Envelope{status:completed}
          task#1.Status = Completed
          ReplanAsync(TaskCompleted)          → on_track, no changes
          SendSelfMessage()

[…repeat for task #2 and #3…]

self-tick → no Pending tasks remain
        GoalCheckAndMaybeComplete
          ReplanAsync(GoalCheck)              → on_track, no new tasks
          CompletePlan
            WorkerModel synthesizes final deliverable
            SendResultToCaller(plan.FinalResult, plan.CallerHandle)
            SaveMemoryAsync(Observation, "Plan: …")
            _currentPlan = null
```

### B. New ask arrives mid-delegation

```
self-tick → ProcessNextTask
        drain queue                           (empty)
        pick task #2 → DelegationService.DelegateAsync
          ⏳ awaiting client agent (30 s)

  ┌─ user sends "also include shipping costs in the report" ─┐
  │  FabrCore sees primary in-flight → routes to OnMessageBusy
  │  OnMessageBusy:
  │    not a status query
  │    → _pendingUserInputs.Writer.TryWrite(message)
  │    → return "Got your message — I'll process it at the next checkpoint"
  └────────────────────────────────────────────────────────────┘

        ⏳ delegation completes
        task#2.Status = Completed
        ReplanAsync(TaskCompleted)
        SendSelfMessage()
        return

self-tick → ProcessNextTask
        drain queue:
          queued message: "also include shipping costs…"
          HandleQueuedUserMessage:
            IntentTriage → ModifyPlan, conf 0.85
            HandleModifyOrMergeNew:
              AbsorbedAsks.Add("…")
              ReplanAsync(UserMessage)         → adds task "include shipping costs"
              SendResultToCaller("Merged your update — added 1, removed 0…")
        no more queued
        pick task #3 (newly added) → DelegationService.DelegateAsync
        …continues
```

### C. Teaching mid-plan

```
user → "remember that all dates must be in ISO format"
       (during a running plan, but not mid-await)

OnMessage:
  IntentTriage → Teaching, conf 0.95
                 LessonText = "all dates must be in ISO format"
                 LessonCategory = "Rule"
  HandleTeaching:
    LessonExtractor.ExtractAndSaveAsync
      memory.SaveMemoryAsync(title, MemoryType.Rule, content, …)
      ActiveRuleRefs.Add(memId)
    return "Got it — I'll remember that. (Rule)"

  PersistStateAndPlan
  return ack to user

self-tick continues. The next ReplanAsync call reads
ActiveRuleRefs via memory.RecallAsync → planner sees the rule
in its "Active rules / lessons" prompt block.
```

### D. Replan loop fires

```
[Three rapid ModifyPlan messages within 60 seconds, each contradicting the last.]

After replan #3:
  TrackReplan("user-message", "needs_adjustment")
  RecordReplanAndCheckLoop returns true (3-in-a-row, all "user-message")

  _subconscious.IsStuck = true
  _subconscious.StuckReason =
    "Replanned several times in quick succession — likely going in circles."

  Self-loop ticks no-op (ProcessNextTask returns early on IsStuck).
  Next user message clears IsStuck via the new turn's normal flow.
```

## Service Composition (in `OnInitialize`)

The agent constructs its services in this order:

1. Resolve `TaskAgentOptions` (or instantiate default if missing from DI).
2. Resolve `ITaskAgentDefinitionProvider` (or skip definition load if missing).
3. Load `TaskAgentDefinition` by name from `config.Args["TaskAgentDefinition"]`.
4. Create the three tier `AIAgent` + `AgentSession` pairs via `CreateChatClientAgent`. **No tools** are passed — the TaskAgent is control-plane only.
5. Resolve `IAgentMemoryProvider` (optional) → call `GetMemoryService(handle)` to get the per-grain `IAgentMemoryService`.
6. Resolve `IFabrCoreRegistry` (optional, for class-attribute lookups).
7. Construct stateless coordination services:
   - `SmeConsultationService` (with the definition's SME alias list)
   - `AgentCapabilityRegistry` (with the definition's client-agent alias list)
   - `DelegationService` (just a thin wrapper around `IFabrCoreAgentHost.SendAndReceiveMessage`)
   - `PlanReplanner` (over the planner agent + session)
   - `LessonExtractor` (only if memory is wired)
8. Restore `_subconscious` and `_currentPlan` from grain state.
9. If a plan was running and not paused/stuck, `SendSelfMessage` to resume.

## When To Subclass `TaskAgent`

Common reasons:

- **Custom busy-ack text** — override `OnMessageBusy`, keep the queue + drain.
- **Inject domain tools into the WorkerModel** — override `OnInitialize`, call `ResolveConfiguredToolsAsync`, pass tools to the worker `CreateChatClientAgent` only (leave Fast and Planner tool-free).
- **Custom intent post-processing** — override `HandleUserMessage` after triage to mutate the intent based on domain rules.
- **Custom plan-completion side effects** — override `CompletePlan` to publish events or write to a system of record.

What you should **not** do:

- Don't mutate `_subconscious` or `_currentPlan` from `OnMessageBusy` — only writes to `_pendingUserInputs` are safe.
- Don't call `SendAndReceiveMessage` from `OnMessageBusy` — that re-enters the agent host and can deadlock.
- Don't bypass the queue — even for "trivial" Pause/Stop, queue them and let the next safe checkpoint apply the flag mutation.

## Performance Notes

- Each user turn issues 1 FastModel call (triage). For NewTask / ModifyPlan a PlannerModel call follows. WorkerModel calls only on GeneralQuestion or final synthesis.
- Each task tick issues 1 client-agent delegation (no internal LLM call) plus 1 PlannerModel call (replan).
- `AgentCapabilityRegistry` is constructed fresh per grain, but caches results internally for the lifetime of the instance — agents that come online mid-flight are picked up at the next definition reload (currently silo restart).
- `Channel.CreateUnbounded<AgentMessage>()` has effectively unlimited capacity. If you expect bursts, the queue grows until `ProcessNextTask` drains. There is no backpressure — callers always get the busy ack and never block.
