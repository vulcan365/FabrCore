# Architecture

## What Worker is, in one sentence

A per-turn pipeline addon that extracts a task list from the user message, judges feasibility against the host's tools, runs the host's LLM, then validates the response against the task list — with SME consultation and an LLM-driven internal advisor at intake and validate stages.

## What Worker is NOT

- **Not an agent.** No `FabrCoreAgentProxy` subclass, no `AgentAlias`, no `OnMessage` loop. The host agent calls Worker; Worker never receives messages directly.
- **Not a planner.** Single LLM turn per pipeline run (plus the optional retry). No multi-step plan, no delegation to other agents.
- **Not a plugin.** No LLM-callable tools. The host's code drives every stage.
- **Not stateful across silos.** The task list lives on the in-memory `WorkerService` instance for the duration of a turn. Worker does not persist grain state.

## Layer map

```
Configuration/  — JSON-backed named definitions, DI options, registration helper
Abstractions/   — public interfaces (IWorkerService, IWorkerProvider, IWorkerDefinitionProvider, WorkerAgentFactory)
Model/          — value/data types (WorkerTask, WorkerTaskList, WorkerCapability, SmeReference, result records, enums)
Brain/          — LLM drivers (TaskExtractor, ResponseValidator, SmeRouter, InternalAdvisor) + WorkerEnvelope parser
Coordination/   — WorkerSmeConsultationService (parallel SME query with timeout)
Services/       — WorkerService (per-handle facade) + WorkerProvider (singleton factory + cache)
```

## Stage-by-stage flow

```
                            ┌─────────────────────────┐
   AgentMessage  ─────────► │   PreProcessAsync       │
                            │  1. Extract task list   │
                            │     (capabilities ─►    │
                            │      feasibility per t.)│
                            │  2. Gather advice:      │
                            │     a. SME router       │
                            │     b. SMEs (subset)    │
                            │     c. Internal advisor │
                            │  3. Build PromptOverlay │
                            └────────────┬────────────┘
                                         │
                                         ▼
                            ┌─────────────────────────┐
                            │  HOST RUNS MAIN LLM     │
                            │  (host owns _session)   │
                            └────────────┬────────────┘
                                         │
                                         ▼
                            ┌─────────────────────────┐
                            │   ValidateAsync         │
                            │  1. Per-task verdict    │
                            │     (Q vs A semantics)  │
                            │  2. If unsatisfied:     │
                            │     gather gap advice   │
                            │     (router → SMEs →    │
                            │      advisor)           │
                            │  3. SuggestedFollowUp   │
                            └────────────┬────────────┘
                                         │
                              IsSatisfied? ─── yes ──► response
                                   │
                                   no
                                   │
                                   ▼
                            ┌─────────────────────────┐
                            │  RetryWithGuidanceAsync  │
                            │  (host runs retry LLM,  │
                            │   one shot only)        │
                            └────────────┬────────────┘
                                         │
                                         ▼
                                      response
```

## State boundaries

| State | Lifetime | Where it lives |
| --- | --- | --- |
| `WorkerTaskList` (current turn) | One turn — replaced on next `PreProcessAsync` | `WorkerService._current` (private, lock-guarded) |
| Per-model `AIAgent` cache | Lifetime of `WorkerService` instance | `WorkerService._agentCache` (`ConcurrentDictionary`) |
| `IWorkerService` per host handle | Lifetime of `WorkerProvider` (singleton) | `WorkerProvider._services` (`ConcurrentDictionary`) |
| `WorkerDefinition` | Lifetime of process (cached on first read) | `FileWorkerDefinitionProvider._definitions` |
| Host's main `AgentSession` | Host's responsibility | Host's agent class — Worker never reads or writes it |

**Critical invariant:** Worker analysis LLM calls create a **fresh** `AgentSession` per call (`analysisAgent.CreateSessionAsync()`). This isolates extractor/validator/router/advisor reasoning from prior conversational turns and prevents pollution of the host's main session. Pattern lifted directly from `GoalSatisfactionValidator.ValidateAsync` in TaskAgent.

## Advice gathering (the two-stage helper)

The same `GatherAdviceAsync` method runs at both PreProcess and Validate:

```
GatherAdviceAsync(stage):
  smesUsedThisStage = (stage flag is true) AND (any SMEs configured)
  combined = []

  if smesUsedThisStage:
    routed = RouteSmesByRelevance
              ? SmeRouter.SelectRelevantAsync(taskList, smes, routerAgent)
              : smes
    smeAdvice = WorkerSmeConsultationService.ConsultAsync(routed, question)
    combined += smeAdvice

  if InternalAdvisorOn<Stage> AND ShouldRunAdvisor(AdvisorMode, smesUsedThisStage, combined.Count):
    advice = InternalAdvisor.AdviseXxx(...)
    if advice != null: combined.Add(advice)

  return combined
```

`ShouldRunAdvisor` decision matrix:

| `AdvisorMode` | Runs advisor when |
| --- | --- |
| `Disabled` | Never |
| `Always` | Always (when stage flag is true) |
| `OnlyWhenNoSmes` | The stage isn't consulting SMEs at all (flag off OR empty list) |
| `OnlyWhenSmesProduceNothing` *(default)* | SMEs weren't consulted, OR SMEs answered nothing useful |

## Model resolution

Each LLM driver uses a stage-specific model name resolved in this order:

| Stage | Resolution chain |
| --- | --- |
| Extractor | `definition.TaskExtractionModelName` → `options.DefaultExtractionModelName` → host's default `AIAgent` |
| Validator | `definition.ValidationModelName` → `options.DefaultValidationModelName` → host's default `AIAgent` |
| SmeRouter | `definition.SmeRouterModelName` → `options.DefaultSmeRouterModelName` → extractor's chain |
| InternalAdvisor | `definition.InternalAdvisorModelName` → `options.DefaultInternalAdvisorModelName` → validator's chain |

When `agentFactory` is `null`, model-name overrides cannot be honored and Worker falls back to the default analysis agent. If the factory is present but throws while creating a model-specific agent, Worker logs a warning and falls back to the default agent. **Worker never throws on model resolution failure.**

Best practice: configure model names and provide a `WorkerAgentFactory` that creates tool-less agents. If all model names are null and `analysisAgent` is the host's main tool-laden agent, Worker brain calls inherit the host prompt/tools and can leak worker envelope formats into user-facing responses.

## SME metadata enrichment

At construction time `WorkerService` enriches each configured `SmeReference`:

1. Config wins for `Description` and `GoodFor` — never overwritten.
2. For any missing field, look up the SME's class-level metadata via `IFabrCoreRegistry` (best-effort, reflective):
   - `[Description]` attribute → fills `Description`
   - `[FabrCoreCapabilities]` attribute → fills `GoodFor`
3. Registry shape mismatches are silent — the SME is left without enrichment.

Toggle via `WorkerOptions.EnrichSmeMetadataFromRegistry` (default `true`). When `IFabrCoreRegistry` is not in DI, enrichment is skipped automatically.

## SME consultation wire protocol

`WorkerSmeConsultationService` sends an `AgentMessage` with:
- `ToHandle`: `owner:alias` for cross-owner, bare alias auto-prefixed with current owner otherwise
- `MessageType`: `"swarm-sme-consultation"` — matches TaskAgent's wire-level constant so SMEs written for TaskAgent work unchanged
- `Kind`: `MessageKind.Request`
- `Message`: the question
- `State["context"]`: optional additional context (e.g. the prior failed response on validate gaps)

SME replies are read with:
- `response.State["sme-status"]` = `"answered"` (explicit useful answer) or `"unknown"` (couldn't help)
- Implicit answer accepted: non-empty `response.Message` + status not `"unknown"`

Per-call timeout is `WorkerOptions.SmeConsultationTimeout` (default 30s). All SMEs are queried in parallel via `Task.WhenAll`; timeouts/exceptions on one SME are logged and dropped, not propagated.

## Envelope contract

Every brain-layer LLM driver expects a fenced **`fabrcore-worker-envelope`** block at the end of the model's response. `WorkerEnvelope` is self-contained inside this project (no cross-project ref to TaskAgent's `EnvelopeParser`).

```
```fabrcore-worker-envelope
{
  ... structured JSON the driver expects ...
}
```
```

Fallback: if no fenced block is found, `WorkerEnvelope` tries to parse the last balanced `{...}` in the text. If both fail, the driver returns its safe-default value (empty task list / unsatisfied verdict / pass-all SMEs / no advice). **No driver throws on envelope failure.**

`ProcessAsync` also uses the envelope helper defensively on host LLM output: it removes completed worker fences, unterminated worker fences, and trailing worker-shaped JSON before returning `FinalResponse`. This protects clients from prompt-format bleed but does not replace proper analysis-agent isolation.

## Decisions worth knowing before changing things

- **Service-only, no plugin.** Worker exposes no LLM-callable tools. Adding a plugin is feasible but is a real conversation, not a 30-minute change.
- **Two stages, not three.** PreProcess + Validate. No MidStream hook between tokens, no PostReply reflection pass. Add stages by extending `IWorkerService` and following the `GatherAdviceAsync` shape.
- **One retry budget.** `ProcessAsync` calls the host's runner at most twice per turn (`MaxRetries=1` by default, capped by `AbsoluteMaxRetries=2` in options). If you find yourself wanting more, you want `TaskAgent`.
- **Capabilities are host-supplied, not auto-discovered.** `AIAgent` does not expose its tool list after construction. The host maps `ChatClientAgentOptions.Tools` (or whatever source) → `WorkerCapability[]` and hands it in.
- **`WorkerSmeAdvice` is the universal advice shape.** Peer SMEs and the internal advisor both produce these records; downstream rendering treats them uniformly with a `sme:<alias>` vs `advisor` prefix. New advice sources are added by producing more `WorkerSmeAdvice` entries with new `WorkerAdviceSource` values.
- **Defensive on malformed LLM output.** Every brain driver returns a safe default on parse/call failure. None of them propagate exceptions out of a stage.
- **Validation fails closed.** Validator output must match every tracked task id. Unknown or missing ids leave the tracked task unsatisfied and produce a gap.
- **SmeRouter conservative fallback.** When the router can't decide (no envelope, empty selection, no metadata to route on), it returns the full SME list. False negatives at the routing layer are worse than false positives.
- **No grain state.** Worker does not persist anything via `SetState` / `GetStateAsync` / `FlushStateAsync`. Per-turn state is in-memory only. Reactivation = empty task list at next `PreProcessAsync`.
