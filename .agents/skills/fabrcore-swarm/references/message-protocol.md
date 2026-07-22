# Swarm Message Protocol Reference

All constants are in `FabrCore.Experimental.Swarm.SwarmMessageTypes`, `FabrCore.Experimental.Swarm.SwarmChannels`, `FabrCore.Experimental.Swarm.SwarmEventTypes`, and `FabrCore.Experimental.Swarm.SwarmEventNamespaces`.

## Channels

| Channel | Source | Purpose |
|---|---|---|
| `swarm-user-input` | Client (explicit, set by `SwarmClientExtensions`) | User commands (approval, questions, "status", "pause") |
| `swarm-execution-loop` | Self-message from orchestrator | Trigger immediate next-wave processing |
| `supervisor-report` | Supervisors | Wave completion, roadblock escalation, ack |
| `worker-report` | Worker agents | Progress, completion, roadblock |
| `roadblock-response` | Client (HITL) | Answer to a roadblock question |

Messages without a channel are treated as user input (if from an end user) or relayed (if from a system message like `_status` / `_error`).

## Message types

### Planning phase

| Type | Direction | Purpose |
|---|---|---|
| `swarm-plan-request` | Orchestrator → Planner | Build a plan for the user request |
| `swarm-plan-awaiting-approval` | Orchestrator → User | Plan ready for review |
| `swarm-planning-roadblock` | Orchestrator → User | Planner could not build a plan (no allowed agents, ambiguous request) |
| `swarm-planning-query` | Planner → Worker (deprecated) | Was used for scout queries; not used in current adapter model |
| `swarm-discover` | Any → Factory | Returns the registered agent capabilities |
| `swarm-replan-request` | Orchestrator → Planner | Adjust the existing plan |

### Execution phase

| Type | Direction | Purpose |
|---|---|---|
| `swarm-task-batch` | Orchestrator → Supervisor | Dispatch a wave of ready tasks |
| `swarm-task-assignment` | Supervisor → Worker | Assign one task. The worker's LLM reasons about it and delegates to its paired client agent via DelegateToClientAgent. |
| `swarm-task-progress` | Worker → Supervisor | Optional progress heartbeat |
| `swarm-task-complete` | Worker → Supervisor | Task finished successfully |
| `swarm-roadblock` | Worker → Supervisor | Worker reported a roadblock |
| `swarm-supervisor-report` | Supervisor → Orchestrator | Wave done; carries all task states (PendingAck, Completed, Failed, Skipped) |
| `swarm-supervisor-report-ack` | Orchestrator → Supervisor | Lists merged task IDs; supervisor finalizes PendingAck → Completed |
| `swarm-request-supervisor-state` | Orchestrator → Supervisor | Pull state for reconciliation (future use) |
| `swarm-supervisor-state-snapshot` | Supervisor → Orchestrator | Reply to the pull |
| `swarm-roadblock-escalation` | Supervisor → Orchestrator → User | Escalation chain for human input |
| `swarm-roadblock-response` | User → Orchestrator | Human's answer |
| `swarm-roadblock-resolution` | Orchestrator → Supervisor → Adapter Worker → Client Agent | Routed answer to the waiting agent |

### SME Consultation

| Type | Direction | Purpose |
|---|---|---|
| `swarm-sme-consultation` | Worker / Supervisor / Orchestrator / Planner → SME Agent | Query a Subject Matter Expert for knowledge. Question in `Message`, optional `State["context"]`. SME replies with answer in `response.Message` and optional `State["sme-status"]` (`"answered"` or `"unknown"`). |

### Blackboard

| Type | Direction | Purpose |
|---|---|---|
| `swarm-blackboard-read` | Adapter Worker / Client Agent → Blackboard | Read a value by key |
| `swarm-blackboard-write` | Adapter Worker / Client Agent → Blackboard | Write a value; triggers a `BlackboardUpdated` event to other subscribers |
| `swarm-blackboard-list` | Adapter Worker / Client Agent → Blackboard | List all keys |
| `swarm-blackboard-subscribe` | Supervisor → Worker / Worker → Blackboard | Subscribe to push events (per plan) |
| `swarm-blackboard-unsubscribe` | Supervisor → Worker / Worker → Blackboard | Unsubscribe (at plan end) |
| `swarm-blackboard-terminate` | Orchestrator → Blackboard | Plan is ending — clear state and prepare to deactivate |

## Event types

Push events use `EventMessage` with `Type = "swarm.blackboard.updated"` and `Namespace = "swarm-blackboard"`. Subscribed adapter workers receive them in `OnEvent`. The event's `Args` dictionary carries `key`, `version`, and `planId`. The event's `Data` field carries the JSON-serialized `BlackboardEntry`.

## TaskAssignment state shape

The `swarm-task-assignment` message from the supervisor to the adapter worker contains:

| State key | Source | Used by |
|---|---|---|
| `taskId` | Dispatcher | Adapter, client agent (optional) |
| `taskJson` | Dispatcher | Adapter (debugging / state recovery) |
| `supervisorHandle` | Dispatcher | Adapter (for sending TaskComplete / Roadblock) |
| `planId` | Dispatcher | Adapter, client agent (optional) |
| `blackboardHandle` | Dispatcher | Adapter (for subscribing) and client agent (optional, for direct access) |
| `clientAgentHandle` | Dispatcher | Adapter (the target to forward to) |
| `inputContext` | Dispatcher | Adapter (forwards to client agent) — formatted dependency results |
| `clientAgentGuidance` | Dispatcher | Adapter (forwards to client agent) — optional `WorkerSystemPromptOverlay` text |
| `smeAliasesJson` | Dispatcher | Adapter — JSON-serialized `List<string>` of SME bare aliases for `ConsultSubjectMatterExpert` tool |

The adapter forwards a **stripped-down version** to the client agent containing only:

| State key | Purpose |
|---|---|
| `taskId` | Identifier for tracking |
| `planId` | Identifier for cross-references |
| `blackboardHandle` | Optional — for direct blackboard access |
| `inputContext` | Optional — formatted dependency results |
| `clientAgentGuidance` | Optional — extra prompt guidance |

The client agent does NOT see internal swarm state (supervisor handle, taskJson, etc.).

## Reply convention from client agent → adapter worker

The adapter worker reads `clientReply.State["swarm-task-status"]` to decide how to translate the reply:

| State value | Adapter action |
|---|---|
| (missing) or `"complete"` | Calls `_workerTools.CompleteTask(reply.Message)` |
| `"roadblock"` | Calls `_workerTools.ReportRoadblock(reply.Message, type, question)` |

For roadblocks, the adapter also reads:

| State key | Default | Purpose |
|---|---|---|
| `swarm-roadblock-type` | `"NeedsInput"` | One of: `NeedsInput`, `MissingData`, `ToolFailure`, `CapabilityGap`, `Other` |
| `swarm-roadblock-question` | (none) | Specific question for the human |

## SupervisorReport state shape

| State key | Purpose |
|---|---|
| `tasksJson` | Serialized list of all tasks in the wave (any status: PendingAck, Completed, Failed, Skipped) |
| `planId` | Identifier |

## SupervisorReportAck state shape

| State key | Purpose |
|---|---|
| `ackedTaskIds` | JSON-serialized list of task IDs the orchestrator merged |
| `planId` | Identifier |

## BlackboardWrite state shape

| State key | Purpose |
|---|---|
| `key` | The KV store key |
| `value` | The value to write |

The reply from the blackboard agent carries:

| State key | Purpose |
|---|---|
| `version` | The new version of this entry (increments on every write) |
| `entryJson` | The full serialized `BlackboardEntry` |

## BlackboardSubscribe reply shape

When an adapter subscribes, the blackboard returns a snapshot of all current entries:

| State key | Purpose |
|---|---|
| `entriesJson` | JSON-serialized `Dictionary<string, BlackboardEntry>` for cache priming |
