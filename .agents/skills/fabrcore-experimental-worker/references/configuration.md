# Configuration Reference

Worker has two configuration surfaces:

1. **`WorkerOptions`** — host-process-level defaults, set via the `AddWorkerServices` callback in `Program.cs`.
2. **`WorkerDefinition`** — named per-agent profiles, stored in `fabrcore-worker.json`, picked at runtime via `config.Args["WorkerDefinition"]`.

## `WorkerOptions`

Set in `Program.cs`:

```csharp
services.AddWorkerServices(options =>
{
    options.DefinitionFilePath = "fabrcore-worker.json";
    options.MaxExtractedTasks = 12;
    options.SmeConsultationTimeout = TimeSpan.FromSeconds(30);
    options.AbsoluteMaxRetries = 2;
    // Best practice: set these to model names from fabrcore.json so Worker
    // uses clean tool-less agents created by WorkerAgentFactory.
    // If you only have one model entry, use "default" for all four.
    options.DefaultExtractionModelName = "fast";
    options.DefaultValidationModelName = "planner";
    options.DefaultSmeRouterModelName = "fast";
    options.DefaultInternalAdvisorModelName = "planner";
    options.EnrichSmeMetadataFromRegistry = true;
});
```

| Field | Type | Default | Notes |
| --- | --- | --- | --- |
| `DefinitionFilePath` | `string` | `"fabrcore-worker.json"` | Path resolved relative to the process working directory. File missing → empty definition set (logged). |
| `SmeConsultationTimeout` | `TimeSpan` | 30s | Per-SME timeout. SMEs that exceed are logged and dropped. |
| `MaxExtractedTasks` | `int` | 12 | Hard cap on extracted task list length. The extractor LLM is told this number; tasks beyond the cap are dropped. |
| `AbsoluteMaxRetries` | `int` | 2 | Ceiling on `WorkerDefinition.MaxRetries`. `ProcessAsync` uses `min(definition.MaxRetries, AbsoluteMaxRetries)`. |
| `DefaultExtractionModelName` | `string?` | `null` | Fallback model name for the extractor. Null = use the host's analysis agent. Recommended: a fast model name, or `"default"` in single-model systems. |
| `DefaultValidationModelName` | `string?` | `null` | Fallback model name for the validator. Recommended: a planner-tier model, or `"default"` in single-model systems. Validation is load-bearing. |
| `DefaultSmeRouterModelName` | `string?` | `null` | Fallback for the SME router. Falls through to the extractor's chain when null. Recommended: fast tier. |
| `DefaultInternalAdvisorModelName` | `string?` | `null` | Fallback for the internal advisor. Falls through to the validator's chain when null. Recommended: planner tier. |
| `EnrichSmeMetadataFromRegistry` | `bool` | `true` | When true, missing `SmeReference.Description` / `GoodFor` are filled from `IFabrCoreRegistry` (`[Description]` and `[FabrCoreCapabilities]`) on the SME's agent type. |

## `fabrcore-worker.json`

Wire format:

```jsonc
{
  "Workers": [
    { "Name": "default", /* ... */ },
    { "Name": "with-smes-and-tiered-models", /* ... */ }
  ]
}
```

The host agent picks one via `config.Args["WorkerDefinition"] = "<Name>"`. Missing arg → `(default)` empty definition. Unknown name → empty definition + warning.

### `WorkerDefinition` fields

| Field | Type | Default | Notes |
| --- | --- | --- | --- |
| `Name` | `string` | `""` | Lookup key. Case-insensitive match. |
| `Description` | `string` | `""` | Human description. Not used at runtime. |
| `SubjectMatterExperts` | `SmeReference[]` | `[]` | See [SmeReference shape](#smereference-shape). Empty list disables peer-SME consultation regardless of flags below. |
| `RouteSmesByRelevance` | `bool` | `true` | Run `SmeRouter` LLM call to pick relevant SMEs. Auto-bypassed for 0/1 SMEs or when no SME has routing metadata. |
| `ConsultSmesOnPreProcess` | `bool` | `false` | Consult SMEs at intake. |
| `ConsultSmesOnValidate` | `bool` | `true` | Consult SMEs when validation fails — advice flows into `SuggestedFollowUp`. |
| `SkipSmesForConfirmationOnlyGaps` | `bool` | `true` | When validation only needs an explicit completion confirmation, skip peer-SME consultation and let local retry guidance handle it. SME calls still run for partial progress, constraints, errors, or missing work. |
| `InternalAdvisorOnPreProcess` | `bool` | `false` | Run the LLM self-advisor at intake. |
| `InternalAdvisorOnValidate` | `bool` | `true` | Run the LLM self-advisor when validation fails. |
| `AdvisorMode` | `InternalAdvisorMode` | `OnlyWhenSmesProduceNothing` | See [AdvisorMode](#advisormode). |
| `AutoRetryOnValidationFailure` | `bool` | `true` | `ProcessAsync` only. When `false`, the host decides what to do with a failed validation. |
| `MaxRetries` | `int` | `1` | `ProcessAsync` retry budget. Capped by `WorkerOptions.AbsoluteMaxRetries`. |
| `TaskExtractionModelName` | `string?` | `null` | Per-stage model override. Requires a `WorkerAgentFactory` at construction. |
| `ValidationModelName` | `string?` | `null` | Per-stage model override. |
| `SmeRouterModelName` | `string?` | `null` | Per-stage model override. Falls through to extractor's chain. |
| `InternalAdvisorModelName` | `string?` | `null` | Per-stage model override. Falls through to validator's chain. |

### `SmeReference` shape

Both forms parse:

**Bare string** (shorthand, no routing metadata):

```jsonc
"SubjectMatterExperts": ["sme-a", "owner:cross-domain-sme"]
```

**Object form** (with routing metadata):

```jsonc
"SubjectMatterExperts": [
  {
    "Alias": "compliance-sme",
    "Description": "Internal compliance officer agent",
    "GoodFor": "regulatory limits, KYC rules, sanctions screening, data-retention policy"
  }
]
```

Cross-owner aliases use `"owner:alias"`. Bare aliases auto-pick up the host agent's owner prefix at consultation time.

When `Description` or `GoodFor` are missing, Worker falls back to the SME agent's class-level `[Description]` and `[FabrCoreCapabilities]` attributes via `IFabrCoreRegistry` (if registered and `WorkerOptions.EnrichSmeMetadataFromRegistry` is `true`).

### `AdvisorMode`

Controls when the internal advisor runs relative to SME consultation. Enum values:

| Mode | Behavior |
| --- | --- |
| `Disabled` | Never runs the advisor. |
| `OnlyWhenNoSmes` | Runs only if the stage's `ConsultSmesOn...` flag is false **or** `SubjectMatterExperts` is empty. |
| `OnlyWhenSmesProduceNothing` *(default)* | Runs whenever SME consultation produced zero answers — either the stage didn't consult SMEs at all, or every queried SME timed out / answered `sme-status=unknown`. |
| `Always` | Runs alongside SMEs every time the stage's `InternalAdvisorOn...` flag is true. Diversity signal mode. |

The flag (`InternalAdvisorOnPreProcess` / `InternalAdvisorOnValidate`) gates whether the advisor *can* run at that stage; the mode gates whether it *does* run given the SME outcome.

## Worked configurations

### Safe default config — no SMEs, advisor on validate only

```jsonc
{
  "Workers": [
    {
      "Name": "default",
      "SubjectMatterExperts": [],
      "ConsultSmesOnPreProcess": false,
      "ConsultSmesOnValidate": true,
      "SkipSmesForConfirmationOnlyGaps": true,
      "InternalAdvisorOnPreProcess": false,
      "InternalAdvisorOnValidate": true,
      "AdvisorMode": "OnlyWhenSmesProduceNothing",
      "AutoRetryOnValidationFailure": true,
      "MaxRetries": 1,
      "TaskExtractionModelName": "fast",
      "ValidationModelName": "planner",
      "SmeRouterModelName": "fast",
      "InternalAdvisorModelName": "planner"
    }
  ]
}
```

Behavior:
- PreProcess: extractor only. No SMEs, no advisor.
- Validate: validator runs. On failure, advisor runs (because no SMEs means "SMEs produced nothing" by mode rule).
- Auto-retry once with the advisor's gap guidance.

If your `fabrcore.json` has only one model entry, use `"default"` for all four `*ModelName` fields. Do not use `null` unless you intentionally created and passed a separate tool-less `analysisAgent`.

### Full SMEs with relevance routing and tiered models

```jsonc
{
  "Workers": [
    {
      "Name": "compliance-flavored",
      "SubjectMatterExperts": [
        "general-knowledge-sme",
        {
          "Alias": "compliance-sme",
          "Description": "Internal compliance officer agent",
          "GoodFor": "regulatory limits, KYC, sanctions screening"
        },
        {
          "Alias": "owner:cross-domain-sme",
          "Description": "Shared cross-team architect agent",
          "GoodFor": "system integration questions, deprecation timelines"
        }
      ],
      "RouteSmesByRelevance": true,
      "ConsultSmesOnPreProcess": true,
      "ConsultSmesOnValidate": true,
      "SkipSmesForConfirmationOnlyGaps": true,
      "InternalAdvisorOnPreProcess": true,
      "InternalAdvisorOnValidate": true,
      "AdvisorMode": "Always",
      "AutoRetryOnValidationFailure": true,
      "MaxRetries": 1,
      "TaskExtractionModelName": "fast",
      "ValidationModelName": "planner",
      "SmeRouterModelName": "fast",
      "InternalAdvisorModelName": "planner"
    }
  ]
}
```

Behavior:
- PreProcess: router picks relevant SMEs from the 3 configured, consults them, also runs the advisor as a parallel signal.
- Validate: validator runs. On failure, router + SMEs + advisor combine into `SuggestedFollowUp`.
- Auto-retry once.

### Advisor-only — no SMEs at all

```jsonc
{
  "Workers": [
    {
      "Name": "advisor-only",
      "SubjectMatterExperts": [],
      "ConsultSmesOnPreProcess": false,
      "ConsultSmesOnValidate": false,
      "SkipSmesForConfirmationOnlyGaps": true,
      "InternalAdvisorOnPreProcess": true,
      "InternalAdvisorOnValidate": true,
      "AdvisorMode": "Always",
      "AutoRetryOnValidationFailure": true,
      "MaxRetries": 1
    }
  ]
}
```

Use when you have no peer SMEs but want LLM-driven self-consultation at every stage.

### Worker as silent validator — no advice, just gap detection

```jsonc
{
  "Workers": [
    {
      "Name": "silent-validator",
      "SubjectMatterExperts": [],
      "ConsultSmesOnPreProcess": false,
      "ConsultSmesOnValidate": false,
      "SkipSmesForConfirmationOnlyGaps": true,
      "InternalAdvisorOnPreProcess": false,
      "InternalAdvisorOnValidate": false,
      "AdvisorMode": "Disabled",
      "AutoRetryOnValidationFailure": false,
      "MaxRetries": 0
    }
  ]
}
```

Use when you just want Worker's task tracking + validation verdict for telemetry / logging, with no advice and no retries. The host inspects `ValidationResult.IsSatisfied` and decides what to do.

## Picking model tiers

| Stage | Suggested tier | Why |
| --- | --- | --- |
| Task extraction | `fast` | Structured extraction is a cheap call; runs every turn. |
| Validation | `planner` | Load-bearing. A stronger model catches more real misses. False positives ("satisfied" when not) are the bug Worker exists to prevent. |
| SME router | `fast` | One filter call before fan-out. |
| Internal advisor | `planner` | Quality of advice matters — bad guidance produces bad retries. |

If your host has a single model tier configured, set all four `*ModelName` fields to `"default"` and provide a `WorkerAgentFactory` that creates tool-less agents. Leave fields null only when the `analysisAgent` you pass to `GetWorkerServiceAsync` is already separate from the host's main tool-laden agent.
