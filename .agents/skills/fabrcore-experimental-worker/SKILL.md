---
name: fabrcore-experimental-worker
description: >
  FabrCore worker service — a pipeline addon (not an agent) that makes any host
  FabrCoreAgentProxy more resilient at finishing what the user actually asked for.
  Extracts a per-turn task list from the user message, judges each task's feasibility
  against the host's tool/plugin inventory, optionally consults peer SMEs (with
  LLM-driven relevance routing), runs an internal LLM-driven advisor as
  fallback/supplement when SMEs are absent or unhelpful, then validates the
  host's response against the task list and surfaces gaps for a bounded
  replan/retry loop. Service-only (no plugin, no agent subclass). Config-driven via
  fabrcore-worker.json with named WorkerDefinition entries, per-stage model
  overrides, and SmeReference metadata (Alias / Description / GoodFor).
  Triggers on: "worker service", "WorkerService", "IWorkerService", "IWorkerProvider",
  "AddWorkerServices", "WorkerDefinition", "fabrcore-worker.json",
  "PreProcessAsync", "ValidateAsync", "ProcessAsync", "RunLoopAsync", "RetryWithGuidanceAsync",
  "WorkerTask", "WorkerTaskList", "WorkerTaskKind", "WorkerTaskStatus",
  "WorkerTaskFeasibility", "WorkerCapability", "WorkerStageContext",
  "WorkerToolInventory", "WorkerLoopState",
  "WorkerLoopPhase", "WorkerEffortLevel", "WorkerClarificationStatus",
  "WorkerToolInventoryStatus", "WorkerValidationState", "WorkerLoopContext",
  "WorkerLoopResult", "WorkerPreProcessResult", "WorkerValidationResult", "WorkerProcessResult",
  "WorkerSmeAdvice", "WorkerAdviceSource", "SmeReference", "SmeRouter",
  "InternalAdvisor", "InternalAdvisorMode", "RouteSmesByRelevance",
  "WorkerSmeConsultationService", "WorkerAgentFactory", "ConsultSmesOnPreProcess",
  "ConsultSmesOnValidate", "InternalAdvisorOnPreProcess",
  "InternalAdvisorOnValidate", "TaskExtractionModelName", "ValidationModelName",
  "SmeRouterModelName", "InternalAdvisorModelName", "FabrCore.Experimental.Worker",
  "task list extraction", "response validation", "agent pipeline resilience",
  "agent retry on validation failure", "FabrCore SME consultation",
  "agent self-consultation", "agent capability feasibility", "agent task tracking",
  "tool inventory", "AITool inventory", "clarification", "replanning", "structured reasoning flags".
  Do NOT use for: multi-step planning across multiple client agents — use
  fabrcore-agents-taskagent (Worker is a lightweight bounded loop, not the heavyweight planner).
  Do NOT use for: long-term durable knowledge — use fabrcore-experimental-memory.
  Do NOT use for: general agent lifecycle (OnInitialize / OnMessage) — use
  fabrcore-agent. Worker is consumed BY an agent, not built AS one.
allowed-tools: "Bash(dotnet:*) Bash(mkdir:*) Bash(ls:*) Bash(pwsh:*) Bash(powershell:*) Bash(git:*) Bash(dir:*)"
---

# FabrCore Experimental Worker

A pipeline addon for FabrCore agents. Drops into an existing agent's `OnMessage` and makes it **more resilient at actually finishing what the user asked for**. The project is `FabrCore.Experimental.Worker` (NuGet: `FabrCore.Host`, target `net10.0`).

Worker is a **lightweight bounded loop controller** that lives inside another agent's pipeline rather than replacing it. It captures the parent agent's `AITool` inventory, extracts and tracks tasks, classifies effort, exposes structured reasoning flags, validates the host's answer, and can perform bounded replan/retry guidance. Worker does not expose raw chain-of-thought.

## Mental model

The host agent stays in full control:

1. User message arrives at the host's `OnMessage`.
2. Host calls `IWorkerService.RunLoopAsync`, `ProcessAsync`, or explicit `PreProcessAsync` — Worker captures the effective `AITool` + `WorkerCapability` inventory, extracts a task list, judges feasibility, classifies effort, and (when configured) gathers SME and/or internal-advisor intake guidance.
3. Host runs its own main LLM (`AIAgent.RunStreamingAsync`) — Worker is NOT involved here. The host owns the main `AgentSession`.
4. Host calls `IWorkerService.ValidateAsync(message, response)` — Worker checks each tracked task against the response. Validation is fail-closed: every tracked task must get a matching verdict by task id, or Worker treats it as unsatisfied. If unsatisfied, it consults SMEs and/or the internal advisor for gap-closing advice.
5. Host decides what to do with a failed validation:
   - call `RetryWithGuidanceAsync` for explicit control, **or**
   - let `RunLoopAsync` / `ProcessAsync` handle bounded replan/retry using `WorkerDefinition.MaxRetries`.

Worker **never** mutates the host's main `AgentSession`. Every internal LLM call (extractor, validator, router, advisor) creates a fresh ad-hoc session via `analysisAgent.CreateSessionAsync()`.

`ProcessAsync` defensively strips `fabrcore-worker-envelope` blocks and trailing worker JSON from user-facing responses. This is only a last-resort safety net; the correct integration still uses tool-less analysis agents for Worker brain calls.

Use `CurrentLoopState` for observable reasoning state: `Phase`, `EffortLevel`, `ClarificationStatus`, `ToolInventoryStatus`, retry attempt, validation state, and a short `ReasoningSummary`. This is the supported inspection point for "what Worker thinks" as the loop progresses.

## Architecture

```
+--------------------------------------------------------------------+
|              Host Agent (any FabrCoreAgentProxy)                    |
|  OnInitialize: provider.GetWorkerServiceAsync(..., tools: myTools,  |
|                 capabilities: domainCaps, agentFactory: factory)    |
|  OnMessage:    result = worker.RunLoopAsync(message, runner, tools) |
|                host runner executes LLM/tools with Worker guidance  |
+----------------------------+---------------------------------------+
                             |
                  +----------v-----------+
                  |   IWorkerProvider     |  Factory (singleton in DI)
                  |  per-handle cache     |
                  +----------+-----------+
                             |
                  +----------v-----------+
                  |    IWorkerService     |  Scoped to agentHandle
                  |  PreProcessAsync      |   stage 1 — intake
                  |  ValidateAsync        |   stage 2 — pre-release
                  |  RetryWithGuidanceAsync|  explicit retry helper
                  |  RunLoopAsync         |   bounded loop controller
                  |  ProcessAsync         |   compatibility wrapper
                  |  CurrentTaskList      |   live state
                  |  CurrentLoopState     |   reasoning flags
                  +----------+-----------+
                             |
       +---------------------+---------------------------+
       |                     |                            |
  +----v-----+         +----v------+              +-----v------+
  |TaskExtra-|         |Response   |              |SmeRouter   |
  |ctor      |         |Validator  |              |Internal-   |
  |          |         |           |              |Advisor     |
  |  task    |         |  per-task |              | SME subset |
  |  list +  |         |  verdicts |              | + LLM      |
  | feasibi- |         | + gaps    |              |   self-help|
  | lity     |         |           |              |            |
  +----------+         +-----------+              +------------+
                                                        |
                                  +---------------------v-------+
                                  | WorkerSmeConsultationService |
                                  |  parallel SME query, 30s     |
                                  |  timeout, MessageType =      |
                                  |  "swarm-sme-consultation"    |
                                  +-------------------------------+
```

## Service registration

```csharp
using FabrCore.Experimental.Worker.Configuration;

services.AddWorkerServices(options =>
{
    options.DefinitionFilePath = "fabrcore-worker.json";   // default
    options.MaxExtractedTasks = 12;                        // cap on extracted task list
    options.SmeConsultationTimeout = TimeSpan.FromSeconds(30);
    options.AbsoluteMaxRetries = 2;                        // ceiling on definition.MaxRetries
    options.EnrichSmeMetadataFromRegistry = true;          // pull SME [Description] from IFabrCoreRegistry

    // Fallback model names. Best practice: set these so WorkerAgentFactory
    // creates clean, tool-less analysis agents. Use "default" for all four
    // if your fabrcore.json has a single model entry.
    options.DefaultExtractionModelName = "fast";
    options.DefaultValidationModelName = "planner";
    options.DefaultSmeRouterModelName = "fast";
    options.DefaultInternalAdvisorModelName = "planner";
});
```

This registers:
- `IWorkerDefinitionProvider` → `FileWorkerDefinitionProvider` (reads `fabrcore-worker.json`)
- `IWorkerProvider` → `WorkerProvider` (singleton; caches one `IWorkerService` per host agent handle)

**Prerequisites:**
- `AddFabrCoreServer()` with at least one model in `fabrcore.json`
- `fabrcore-worker.json` in the working directory (or the path set on `WorkerOptions.DefinitionFilePath`)

## Quick start — `RunLoopAsync` / `ProcessAsync`

Prefer passing the parent agent's raw `AITool` list to Worker. Worker normalizes it into `WorkerToolInventory`, merges custom `WorkerCapability` entries, and keeps `CurrentLoopState` updated while the bounded loop progresses.

```csharp
public class MyAgent : FabrCoreAgentProxy
{
    private IWorkerService? _worker;
    private AIAgent? _agent;
    private AgentSession? _session;

    public override async Task OnInitialize()
    {
        var result = await CreateChatClientAgent("default", $"{config.Handle}:main", tools: myTools);
        _agent = result.Agent;
        _session = result.Session;

        var provider = serviceProvider.GetRequiredService<IWorkerProvider>();
        _worker = await provider.GetWorkerServiceAsync(
            agentHost: fabrcoreAgentHost,
            agentHandle: fabrcoreAgentHost.GetHandle(),
            configName: config.Args?.GetValueOrDefault("WorkerDefinition"),
            analysisAgent: _agent!,
            tools: myTools?.ToList(),
            capabilities: [new WorkerCapability("domain-rules", "Rules the host can apply without external tools")],
            agentFactory: async (modelName, ct) =>
                (await CreateChatClientAgent(modelName, $"{config.Handle}:worker:{modelName}", tools: null)).Agent);
    }

    public override async Task<AgentMessage> OnMessage(AgentMessage message)
    {
        var result = await _worker!.RunLoopAsync(message,
            agentRunner: async (ctx, ct) =>
            {
                // Worker tells you what to inject; you run the LLM.
                var overlay = ctx.RetryCount == 0 ? ctx.PromptOverlay : ctx.RetryGuidance;
                var chat = new ChatMessage(ChatRole.User, message.Message + overlay);
                var r = "";
                await foreach (var u in _agent!.RunStreamingAsync(chat, _session!, cancellationToken: ct))
                    r += u.Text;
                return r;
            });

        var state = _worker.CurrentLoopState;
        _logger.LogInformation("Worker phase={Phase} effort={Effort} tools={Tools}",
            state.Phase, state.EffortLevel, state.ToolInventoryStatus);

        return message.Response(result.FinalResponse);
    }
}
```

`ProcessAsync` remains available when the host already has a `WorkerStageContext` runner. Capability-only calls remain compatible, but new integrations should pass `AITool` inventory at creation time and use per-run `toolsOverride` when dynamic tools change.

## Quick start — explicit stages

When you want full control over what happens between stages:

```csharp
public override async Task<AgentMessage> OnMessage(AgentMessage message)
{
    var pre = await _worker!.PreProcessAsync(message);

    var chat = new ChatMessage(ChatRole.User, message.Message + pre.PromptOverlay);
    var response = "";
    await foreach (var u in _agent!.RunStreamingAsync(chat, _session!))
        response += u.Text;

    var validation = await _worker.ValidateAsync(message, response);
    if (!validation.IsSatisfied)
    {
        response = await _worker.RetryWithGuidanceAsync(message, response, validation,
            retryRunner: async (ctx, ct) =>
            {
                var retryChat = new ChatMessage(ChatRole.User, message.Message + ctx.RetryGuidance);
                var r = "";
                await foreach (var u in _agent.RunStreamingAsync(retryChat, _session, cancellationToken: ct))
                    r += u.Text;
                return r;
            });

        validation = await _worker.ValidateAsync(message, response);
    }

    return message.Response(response);
}
```

See `assets/worker-host-agent-explicit-stages-template.cs` for the full template. Prefer `ProcessAsync` unless you need explicit stage control; it includes the safest response sanitization path by default.

## Current safety behavior

- `WorkerToolInventory` records whether tools were `Missing`, explicitly `Empty`, creation-time `Available`, or per-run `Overridden`.
- `CurrentLoopState` records phase, effort, clarification, retry, replan, and validation flags as structured state only.
- `ResponseValidator` now rejects malformed validator envelopes by failing closed. If the model returns `id: "unknown"` or omits a tracked task id, the task is marked `Unsatisfied` and a gap is generated.
- Retry guidance now includes tracked task status, the failed prior response, available capabilities, SME/advisor guidance, and an execution contract: try concrete tool-backed recovery paths before giving the final answer.
- `ProcessAsync` strips completed worker fences, unterminated worker fences, and trailing worker-shaped JSON from `FinalResponse`.
- `RetryWithGuidanceAsync` supplies guidance only; hosts using explicit stages should revalidate after retry and should avoid releasing raw analysis-shaped output.

## When to choose which

| Choose `ProcessAsync` when… | Choose explicit stages when… |
| --- | --- |
| One LLM call per turn is the norm | Mid-turn streaming back to the user matters |
| Validation behavior is fully config-driven | Different prompts at retry vs. first pass |
| You want the smallest agent code | You want to inspect `pre.PromptOverlay` before deciding to use it |

## When to use Worker at all

| Use Worker | Use TaskAgent | Use Memory |
| --- | --- | --- |
| Single-agent pipelines where the host LLM does the work, just needs validation it actually finished | Multi-step planning across multiple delegated client agents with replan loops | Durable knowledge that should survive past the turn (rules, facts, observations) |
| Per-turn quality net | Long-running goals with state across turns | Long-term recall before/during a turn |
| Service-only, no plugin | Standalone agent with its own message loop | Service + optional plugin |

Worker, TaskAgent, and Memory **compose**. A common pattern is `Memory.RecallAsync` → append to user message → `Worker.ProcessAsync` (which gets to see the recall context in its extraction prompt as part of the user message).

## Where to find more

- `references/architecture.md` — design rationale, type hierarchy, decision history.
- `references/api-reference.md` — every public type and signature.
- `references/configuration.md` — every `fabrcore-worker.json` field, with worked examples.
- `references/advice-flow.md` — the SME router and internal advisor decision tree.
- `references/pitfalls.md` — every footgun encountered to date. READ BEFORE WRITING NEW WORKER USAGE.
- `assets/worker-host-agent-template.cs` — full `FabrCoreAgentProxy` using `ProcessAsync`.
- `assets/worker-host-agent-explicit-stages-template.cs` — same, with explicit PreProcess/Validate.
- `assets/worker-sme-agent-template.cs` — example SME agent that responds to `swarm-sme-consultation` messages.
- `assets/server-registration.cs` — full `Program.cs` snippet.
- `assets/fabrcore-worker-config-example.json` — multi-definition config example with comments.
