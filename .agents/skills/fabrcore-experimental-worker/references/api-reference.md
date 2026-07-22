# API Reference

## Namespaces

| Namespace | Contents |
| --- | --- |
| `FabrCore.Experimental.Worker.Abstractions` | `IWorkerService`, `IWorkerProvider`, `IWorkerDefinitionProvider`, `WorkerAgentFactory` |
| `FabrCore.Experimental.Worker.Configuration` | `WorkerDefinition`, `WorkerDefinitionFile`, `WorkerOptions`, `FileWorkerDefinitionProvider`, `WorkerServiceExtensions`, `InternalAdvisorMode` |
| `FabrCore.Experimental.Worker.Model` | `WorkerTask`, `WorkerTaskList`, `WorkerTaskKind`, `WorkerTaskStatus`, `WorkerTaskFeasibility`, `WorkerCapability`, `SmeReference`, `WorkerSmeAdvice`, `WorkerAdviceSource`, `WorkerPreProcessResult`, `WorkerValidationResult`, `WorkerProcessResult`, `WorkerStageContext` |
| `FabrCore.Experimental.Worker.Brain` *(internal)* | `TaskExtractor`, `ResponseValidator`, `SmeRouter`, `InternalAdvisor`, `WorkerEnvelope` |
| `FabrCore.Experimental.Worker.Coordination` *(internal)* | `WorkerSmeConsultationService` |
| `FabrCore.Experimental.Worker.Services` *(internal)* | `WorkerService`, `WorkerProvider` |

> Brain / Coordination / Services types are intentionally `internal`. Consumers do not construct them directly — `IWorkerProvider` does.

## `IWorkerProvider`

Singleton factory. Returns per-handle scoped service instances; cached so repeat calls return the same instance.

```csharp
public interface IWorkerProvider
{
    Task<IWorkerService> GetWorkerServiceAsync(
        IFabrCoreAgentHost agentHost,
        string agentHandle,
        string? configName,
        AIAgent analysisAgent,
        IReadOnlyList<WorkerCapability>? capabilities = null,
        WorkerAgentFactory? agentFactory = null,
        CancellationToken ct = default);
}
```

| Parameter | Notes |
| --- | --- |
| `agentHost` | Required. The host agent's `IFabrCoreAgentHost` — Worker uses it for SME message routing. |
| `agentHandle` | Required. Owner-prefixed handle (`config.Handle` or `fabrcoreAgentHost.GetHandle()`). Scopes the cache. |
| `configName` | Name of a `WorkerDefinition` in `fabrcore-worker.json`. Null or unknown → empty default definition. |
| `analysisAgent` | Required. The host's `AIAgent` — used for any stage with no model-name override. Worker creates fresh sessions off this; the host's main `AgentSession` is untouched. |
| `capabilities` | Optional. Host's tool/plugin inventory. Drives feasibility judgments and the advisor prompt. Can be overridden per-call on `PreProcessAsync` / `ProcessAsync`. |
| `agentFactory` | Optional. `Func<modelName, ct, Task<AIAgent>>`. Required if any `WorkerDefinition` field uses a per-stage model name. When null, model overrides are silently ignored. |

## `IWorkerService`

```csharp
public interface IWorkerService
{
    string AgentHandle { get; }
    WorkerTaskList CurrentTaskList { get; }

    Task<WorkerPreProcessResult> PreProcessAsync(
        AgentMessage message,
        IReadOnlyList<WorkerCapability>? capabilitiesOverride = null,
        CancellationToken ct = default);

    Task<WorkerValidationResult> ValidateAsync(
        AgentMessage message,
        string response,
        CancellationToken ct = default);

    Task<string> RetryWithGuidanceAsync(
        AgentMessage message,
        string priorResponse,
        WorkerValidationResult validation,
        Func<WorkerStageContext, CancellationToken, Task<string>> retryRunner,
        CancellationToken ct = default);

    Task<WorkerProcessResult> ProcessAsync(
        AgentMessage message,
        Func<WorkerStageContext, CancellationToken, Task<string>> agentRunner,
        IReadOnlyList<WorkerCapability>? capabilitiesOverride = null,
        CancellationToken ct = default);
}
```

### `PreProcessAsync` — stage 1, intake

Replaces `CurrentTaskList`. Calls `TaskExtractor` (returns tasks with feasibility), then `GatherAdviceAsync(stage: PreProcess)`. Returns `WorkerPreProcessResult` with a ready-to-append `PromptOverlay` string.

### `ValidateAsync` — stage 2, pre-release

Mutates each `WorkerTask.Status` in `CurrentTaskList`. Calls `ResponseValidator`. Validation is fail-closed: Worker resets task status before projection, ignores malformed task results, marks any task without a matching validator result as `Unsatisfied`, and synthesizes gaps when needed. If unsatisfied, calls `GatherAdviceAsync(stage: Validate)` to build `SuggestedFollowUp`. Returns `WorkerValidationResult`.

### `RetryWithGuidanceAsync` — explicit one-shot retry

Invokes the host-supplied `retryRunner` with a `WorkerStageContext` that has `RetryGuidance` set (`SuggestedFollowUp` from validation). Returns the new response. The host is responsible for running its LLM inside `retryRunner`. Worker does not re-validate — call `ValidateAsync` again yourself if you care.

`RetryGuidance` includes tracked tasks, gaps, SME/advisor advice, the prior failed response, and current capabilities when available. It instructs the host LLM to make concrete tool-backed recovery attempts before producing the next user-facing answer.

### `ProcessAsync` — convenience wrapper

PreProcess → `agentRunner(ctx)` → sanitize response → Validate → optional retry → sanitize response → return. Retry behavior driven by `definition.AutoRetryOnValidationFailure` and `Min(definition.MaxRetries, options.AbsoluteMaxRetries)`.

Sanitization strips completed `fabrcore-worker-envelope` fences, unterminated worker fences, and trailing worker-shaped JSON from `FinalResponse`. This prevents internal analysis envelopes from leaking to clients if a host accidentally lets worker prompt formats bleed into the main response.

`agentRunner` receives a `WorkerStageContext`:
- First pass: `PromptOverlay` populated, `RetryGuidance` empty, `RetryCount = 0`
- Retry pass: `PromptOverlay` empty, `RetryGuidance` populated, `RetryCount > 0`

## `WorkerAgentFactory`

```csharp
public delegate Task<AIAgent> WorkerAgentFactory(string modelName, CancellationToken ct);
```

Typically wired to `FabrCoreAgentProxy.CreateChatClientAgent`:

```csharp
agentFactory: async (modelName, ct) =>
    (await CreateChatClientAgent(modelName, $"{config.Handle}:worker:{modelName}", tools: null)).Agent
```

Returned agents are cached per model name for the lifetime of the `IWorkerService` instance. The host need not memoize.

## Model types

### `WorkerTaskKind`

```csharp
public enum WorkerTaskKind { Unknown, Question, Action }
```

| Kind | Satisfaction rule |
| --- | --- |
| `Question` | Satisfied when any answer is reported — value-agnostic. Default-loose. |
| `Action` | Satisfied only when the response confirms state change. Default-pessimistic. |
| `Unknown` | Validated loosely as Question. |

### `WorkerTaskStatus`

```csharp
public enum WorkerTaskStatus { Pending, Addressed, Unsatisfied, Skipped }
```

Extractor always emits `Pending`. Validator transitions to `Addressed` or `Unsatisfied`.

### `WorkerTaskFeasibility`

```csharp
public enum WorkerTaskFeasibility { Unknown, Feasible, PartiallyFeasible, NotFeasible }
```

Set by the extractor when a `WorkerCapability` inventory is supplied. `Unknown` when no inventory was provided. Question tasks default to `Feasible` against general LLM knowledge; Action tasks with no matching tool default to `NotFeasible`.

### `WorkerTask`

```csharp
public sealed class WorkerTask
{
    public Guid Id { get; set; }
    public string Description { get; set; }
    public WorkerTaskKind Kind { get; set; }
    public WorkerTaskStatus Status { get; set; }
    public string? SourceQuote { get; set; }
    public string? UnsatisfiedReason { get; set; }
    public WorkerTaskFeasibility Feasibility { get; set; }
    public string? FeasibilityReason { get; set; }
}
```

### `WorkerTaskList`

```csharp
public sealed class WorkerTaskList
{
    public List<WorkerTask> Tasks { get; set; }
    public bool IsFullyAddressed { get; }            // all Addressed | Skipped
    public IEnumerable<WorkerTask> Pending();
    public IEnumerable<WorkerTask> Unsatisfied();
    public IEnumerable<WorkerTask> Addressed();
}
```

### `WorkerCapability`

```csharp
public sealed record WorkerCapability(string Name, string Description)
{
    public static WorkerCapability FromAITool(AITool tool);
}
```

### `SmeReference`

Polymorphic JSON shape: bare string OR object form.

```csharp
public sealed class SmeReference
{
    public string Alias { get; set; }            // "alias" or "owner:alias"
    public string? Description { get; set; }
    public string? GoodFor { get; set; }         // hint for the router LLM
    public bool HasRoutingMetadata { get; }      // true if Description or GoodFor set
}
```

```jsonc
"SubjectMatterExperts": [
  "bare-alias",
  { "Alias": "expert-x", "Description": "...", "GoodFor": "..." }
]
```

### `WorkerAdviceSource`

```csharp
public enum WorkerAdviceSource { SubjectMatterExpert, InternalAdvisor }
```

### `WorkerSmeAdvice`

Single shape used for both SME answers and internal-advisor output.

```csharp
public sealed record WorkerSmeAdvice(string SmeAlias, string Question, string Answer)
{
    public WorkerAdviceSource Source { get; init; }   // defaults to SubjectMatterExpert
}
```

For advisor entries: `SmeAlias = "internal-advisor"`, `Question = "intake" | "gaps"`.

### `WorkerPreProcessResult`

```csharp
public sealed class WorkerPreProcessResult
{
    public WorkerTaskList TaskList { get; init; }
    public IReadOnlyList<WorkerSmeAdvice> SmeAdvice { get; init; }
    public string PromptOverlay { get; init; }   // append to user prompt verbatim
    public string Reasoning { get; init; }       // free-form reasoning from extractor
}
```

### `WorkerValidationResult`

```csharp
public sealed class WorkerValidationResult
{
    public bool IsSatisfied { get; init; }
    public double Confidence { get; init; }
    public IReadOnlyList<WorkerTask> UnsatisfiedTasks { get; init; }
    public IReadOnlyList<string> Gaps { get; init; }
    public IReadOnlyList<WorkerSmeAdvice> SmeAdvice { get; init; }
    public string Reasoning { get; init; }
    public string SuggestedFollowUp { get; init; }   // ready-to-inject retry prompt
}
```

`SuggestedFollowUp` is intentionally operational, not just descriptive. It contains:
- tracked task ids/status/feasibility,
- validator gaps,
- the prior failed response,
- available capabilities when Worker has them,
- SME/internal-advisor advice,
- a retry execution contract that tells the host to try alternate tool-backed paths before releasing a final answer.

### `WorkerProcessResult`

```csharp
public sealed class WorkerProcessResult
{
    public string FinalResponse { get; init; }
    public WorkerPreProcessResult PreResult { get; init; }
    public WorkerValidationResult ValidationResult { get; init; }
    public int RetryCount { get; init; }
    public bool IsSatisfied { get; }            // == ValidationResult.IsSatisfied
}
```

### `WorkerStageContext`

Passed to `agentRunner` / `retryRunner` callbacks.

```csharp
public sealed class WorkerStageContext
{
    public WorkerTaskList TaskList { get; init; }
    public string PromptOverlay { get; init; }    // first pass
    public IReadOnlyList<WorkerSmeAdvice> SmeAdvice { get; init; }
    public string RetryGuidance { get; init; }    // retry pass
    public int RetryCount { get; init; }
}
```

## Configuration types

See `references/configuration.md` for full field-by-field reference.

```csharp
public class WorkerOptions
{
    public string DefinitionFilePath { get; set; } = "fabrcore-worker.json";
    public TimeSpan SmeConsultationTimeout { get; set; } = TimeSpan.FromSeconds(30);
    public int MaxExtractedTasks { get; set; } = 12;
    public int AbsoluteMaxRetries { get; set; } = 2;
    public string? DefaultExtractionModelName { get; set; }
    public string? DefaultValidationModelName { get; set; }
    public string? DefaultSmeRouterModelName { get; set; }
    public string? DefaultInternalAdvisorModelName { get; set; }
    public bool EnrichSmeMetadataFromRegistry { get; set; } = true;
}

public class WorkerDefinition
{
    public string Name { get; set; }
    public string Description { get; set; }
    public List<SmeReference> SubjectMatterExperts { get; set; }
    public bool RouteSmesByRelevance { get; set; } = true;
    public bool ConsultSmesOnPreProcess { get; set; } = false;
    public bool ConsultSmesOnValidate { get; set; } = true;
    public bool SkipSmesForConfirmationOnlyGaps { get; set; } = true;
    public bool InternalAdvisorOnPreProcess { get; set; } = false;
    public bool InternalAdvisorOnValidate { get; set; } = true;
    public InternalAdvisorMode AdvisorMode { get; set; } = InternalAdvisorMode.OnlyWhenSmesProduceNothing;
    public bool AutoRetryOnValidationFailure { get; set; } = true;
    public int MaxRetries { get; set; } = 1;
    public string? TaskExtractionModelName { get; set; }
    public string? ValidationModelName { get; set; }
    public string? SmeRouterModelName { get; set; }
    public string? InternalAdvisorModelName { get; set; }
}

public enum InternalAdvisorMode
{
    Disabled, OnlyWhenNoSmes, OnlyWhenSmesProduceNothing, Always
}
```

## Registration

```csharp
services.AddWorkerServices(options =>
{
    // ...
});
```

Registers:
- `WorkerOptions` (singleton, populated via the callback)
- `IWorkerDefinitionProvider` → `FileWorkerDefinitionProvider` (singleton)
- `IWorkerProvider` → `WorkerProvider` (singleton)
