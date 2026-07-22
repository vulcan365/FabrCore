# Structured Envelope Contract (Reference)

The TaskAgent leans on a fenced JSON tail at the end of every LLM and
inter-agent response so deterministic code can read outcomes without parsing
prose. This document captures the schema, parser semantics, and adoption
guidance for client agents and SMEs.

## Contract

A response that carries an envelope ends with this exact fence pattern:

````
<natural prose for humans / chat history>

```fabrcore-envelope
{
  "status": "completed|failed|partial|info",
  "summary": "one-line summary",
  "data": { ... task-specific structured payload ... },
  "confidence": 0.0,
  "follow_ups": ["..."],
  "warnings": ["..."]
}
```
````

## Schema

```csharp
public sealed record StructuredEnvelope
{
    public string Status { get; init; } = "info";
    public string Summary { get; init; } = "";
    public JsonElement? Data { get; init; }
    public double? Confidence { get; init; }
    public List<string> FollowUps { get; init; } = new();
    public List<string> Warnings { get; init; } = new();
}
```

| Field | Required | Type | Notes |
|---|---|---|---|
| `status` | yes | string | `completed` / `failed` / `partial` / `info` (case-insensitive). |
| `summary` | yes | string | Single-line human-readable summary. |
| `data` | no | object | Free-form task-specific payload. The TaskAgent reads named properties for each call site. |
| `confidence` | no | number 0..1 | Used by triage and SME consultation. Below `ClarifyConfidenceThreshold` and the agent asks the user to clarify. |
| `follow_ups` | no | array of strings | Optional next-step suggestions. Surfaced through the journal but not consumed automatically. |
| `warnings` | no | array of strings | Issues the caller should know about. Logged. |

### Status semantics

| Status | TaskAgent treats as | Effect |
|---|---|---|
| `completed` | Success | Task `Status = Completed`, replanner runs `TaskCompleted` trigger. |
| `failed` | Failure | TaskAgent consults SME, retries up to `MaxAttempts`, then triggers `TaskFailed` replan. |
| `partial` | Success this attempt | Task marked Completed, replanner may extend the plan based on `summary` / `warnings`. |
| `info` | Success | Same as completed for delegations; used by triage where there's nothing to "complete". |

## Parser Semantics

`EnvelopeParser` is defensive — it never throws. The two entry points:

```csharp
public static StructuredEnvelope? TryExtract(string? responseText);
public static string StripEnvelope(string? responseText);
```

`TryExtract` algorithm:

1. If text is null/whitespace → return null.
2. Find all `\`\`\`fabrcore-envelope ... \`\`\`` blocks via regex (multiline, case-insensitive).
3. **Take the LAST match** (so quoted examples earlier in prompt text don't win).
4. Try `JsonSerializer.Deserialize<StructuredEnvelope>` with case-insensitive properties, comments-skipped, trailing-commas-allowed.
5. On success → return the envelope.
6. On `JsonException` → return null (graceful degradation).
7. **Fallback** — if no fenced block at all, walk backward from the last `}` matching brace depth to extract a bare trailing JSON object. Try the same deserialization. Returns null if that also fails.

`StripEnvelope` removes every fabrcore-envelope fence (not just the last) and returns the trimmed prose. Used by `DelegationService` to keep `TaskItem.Result` clean for chat history display.

## Where the TaskAgent Asks for Envelopes

| Caller | Recipient | Read fields | Notes |
|---|---|---|---|
| `IntentTriage.ClassifyAsync` | FastModel | `data.intent`, `data.lesson_text`, `data.lesson_category`, `data.focus_hint`, `confidence` | Triage prompt asks for the envelope explicitly. Fallback intent is `GeneralQuestion`. |
| `PlanReplanner.BuildInitialPlanAsync` | PlannerModel | `summary`, `data.tasks[].description`, `data.tasks[].assigned_agent`, `data.tasks[].order` | If `data.tasks` is missing, the agent declines to plan and asks the user to restate. |
| `PlanReplanner.ReplanAsync` | PlannerModel | `data.assessment`, `data.reasoning`, `data.remove_task_ids[]`, `data.add_tasks[].description`, `data.add_tasks[].assigned_agent`, `data.add_tasks[].order` | Missing envelope → `ReplanOutcome("on_track", 0, 0, …)` and the plan is left unchanged. |
| `DelegationService` | Client agent | `status`, `summary`, `data` (free-form), `warnings` | `status: "failed"` → TaskAgent treats the call as a failure. Missing envelope → treated as success with the prose as `Result`. |
| `SmeConsultationService` | SME agent | (any prose; envelope optional) | The consultation result is the prose; envelope `confidence` is informational. |

## Adoption Guidance

### Always emit the envelope when you can

The TaskAgent works without it (graceful fallback) but a missing envelope
forces it to assume success — which can mask client-side failures and trigger
incorrect downstream replans. Every client agent and SME should aim to emit
the envelope on every response.

### Keep `summary` short and informational

It surfaces directly in the agent monitor and journal. Aim for a single
human-readable sentence — what happened from the user's perspective, not the
mechanics.

### Use `data` for structured handoffs

When task #2 needs IDs from task #1, put them in task #1's `data`. The
TaskAgent's delegation context dictionary picks them up via prior-results
summary that the planner sees on every replan.

```jsonc
// Good — downstream tasks can correlate by ID:
"data": { "job_id": "4", "pipe_count": 14, "next_status": "Planning" }

// Bad — same info but unparseable:
"data": { "result": "Created job number 4 with 14 pipes, now in Planning" }
```

### Emit `warnings` for soft failures

If the task succeeded but had caveats (cache miss, partial data, retried
internally), put it in `warnings`. The TaskAgent logs these and includes them
in the planner's replan context — the planner can decide to add a remediation
task.

### Don't put secrets in the envelope

The envelope is logged and can be displayed in chat UIs. API keys, PII, and
session tokens belong nowhere near it.

## Examples

### Successful client-agent delegation

````
Created job 4 with 14 pipes assigned. The job is now in Planning status and
ready for plate allocation.

```fabrcore-envelope
{
  "status": "completed",
  "summary": "Created job 4 with 14 pipes; status: Planning",
  "data": {
    "job_id": "4",
    "pipe_count": 14,
    "next_status": "Planning"
  },
  "confidence": 0.97
}
```
````

### Failed delegation with actionable summary

````
I tried to allocate plates but the warehouse returned no plates matching the
required wall thickness. The job needs a plate spec change before allocation
can proceed.

```fabrcore-envelope
{
  "status": "failed",
  "summary": "No plates match wall_thickness >= 0.5in for job 4",
  "data": {
    "required_thickness": 0.5,
    "max_available": 0.375
  },
  "warnings": ["plate-spec mismatch — escalate to job planner"]
}
```
````

### SME response

````
Standard practice is to allocate plates only after the job is in Released
status, not Planning. Allocating earlier risks reservation churn when the
schedule changes.

```fabrcore-envelope
{
  "status": "info",
  "summary": "Allocate plates only after Released status",
  "confidence": 0.9
}
```
````

### Triage envelope (FastModel)

````
Classified.

```fabrcore-envelope
{
  "status": "info",
  "summary": "Teaching",
  "data": {
    "intent": "Teaching",
    "lesson_text": "All dates must be in ISO 8601 format",
    "lesson_category": "Rule",
    "focus_hint": null
  },
  "confidence": 0.92
}
```
````

### Planner envelope (initial plan)

````
Plan ready.

```fabrcore-envelope
{
  "status": "info",
  "summary": "Build a quote for job 42 in 3 steps",
  "data": {
    "tasks": [
      { "description": "Fetch job 42 line items", "assigned_agent": "data-fetcher", "order": 1 },
      { "description": "Calculate material and labor cost", "assigned_agent": "job-execution-agent", "order": 2 },
      { "description": "Format quote document", "assigned_agent": "writer-agent", "order": 3 }
    ]
  },
  "confidence": 0.85
}
```
````

## FAQ

**Can I emit multiple envelopes in one response?**
Don't. The parser takes the last one — others would be ignored at best, or
parsed as the "wrong" outcome at worst.

**Does the envelope have to be at the very end?**
Not strictly — the regex matches anywhere — but conventionally yes. Put prose
above, envelope at the bottom. Makes parsing visible to humans reviewing logs.

**What if my LLM provider strips fenced code blocks?**
A few providers normalize markdown aggressively. Try wrapping the JSON in a
plain `<envelope>...</envelope>` tag instead and write a thin pre-processor in
your client agent to convert before returning. Or use the bare-JSON fallback —
just put a top-level `{ ... }` at the very end of the response with no fences.
The parser walks backward from the last `}` and tries to deserialize.

**My client agent's chat history shows the envelope JSON. How do I hide it?**
Call `EnvelopeParser.StripEnvelope(responseText)` before passing to your chat
UI or storing in your own conversation history. The TaskAgent already strips
it when storing `TaskItem.Result`.

**Should plugins emit envelopes too?**
Plugins return tool-call results, which the LLM consumes — no envelope needed.
Only the agent's final response back to the TaskAgent should carry the
envelope.
