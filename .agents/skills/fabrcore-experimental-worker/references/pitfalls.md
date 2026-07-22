# Pitfalls and Common Mistakes

Read this before writing your first Worker integration. Every entry is a real failure mode either observed in the codebase or designed against intentionally.

---

## Critical: Wiring the host's tool-laden agent as the analysis agent

**Problem:** Passing `analysisAgent: _agent` (the host's main `AIAgent`, which has the JobV2 / domain-specific system prompt and a full set of tools attached) into `GetWorkerServiceAsync`, while leaving every `WorkerDefinition.*ModelName` field null. The `agentFactory` is never invoked because no model overrides are set, so every Worker brain-layer call (extractor, validator, router, advisor) runs through the tool-laden main agent. Three real costs:

1. **Worker analysis calls may invoke domain tools mid-analysis**, wasting LLM calls and confusing the trace ("why did the router call a mutation tool?").
2. **The fresh `CreateSessionAsync()` Worker promises is weaker than intended** — the system prompt and tool definitions still come from the host's main agent. Prompt-format bleed has been observed (worker-envelope JSON appearing in the user-facing response).
3. **Token cost balloons** — every analysis call carries the full domain system prompt + tool schema as input.

**Wrong:**

```csharp
_worker = await provider.GetWorkerServiceAsync(
    agentHost, agentHandle, configName,
    analysisAgent: _agent,                           // ← tool-laden main agent
    capabilities: caps,
    agentFactory: async (modelName, ct) =>           // ← factory never fires
        (await CreateChatClientAgent(modelName, ..., tools: null)).Agent);
```

```jsonc
{
  "Name": "default",
  "TaskExtractionModelName": null,                   // ← all null, factory never invoked
  "ValidationModelName": null,
  "SmeRouterModelName": null,
  "InternalAdvisorModelName": null
}
```

**Right:** Set per-stage model names so the factory fires and Worker uses clean, tool-less analysis agents. The factory's `tools: null` argument is the key — it produces a fresh `AIAgent` with no domain tools and a generic system prompt for each stage.

```jsonc
{
  "Name": "default",
  "TaskExtractionModelName": "fast",
  "ValidationModelName": "planner",
  "SmeRouterModelName": "fast",
  "InternalAdvisorModelName": "planner"
}
```

Alternatively, when you really do want all stages to share the host agent (test scenarios, single-model setups), explicitly create a separate tool-less analysis agent at `OnInitialize` and pass that:

```csharp
var (analysis, _) = await CreateChatClientAgent("default", $"{config.Handle}:analysis", tools: null);
_worker = await provider.GetWorkerServiceAsync(..., analysisAgent: analysis, ...);
```

The brain-layer prompts now also include "do NOT call any tools" as a defensive measure, but tool-less analysis agents are still the correct fix.

---

## Critical: Envelope JSON leaking into user-facing responses

**Problem:** The agent's final response to the user contains raw `fabrcore-worker-envelope` fenced JSON blocks — full validator output with `is_satisfied`, `confidence`, `reasoning`, `task_results`, `gaps` visible verbatim to the end user. This is the most damaging presentation bug in Worker.

**Root cause:** When the host reuses its main tool-laden `AIAgent` as the Worker analysis agent (see "Wiring the host's tool-laden agent" above), the analysis prompts — which instruct the LLM to produce `fabrcore-worker-envelope` blocks — bleed into the main agent's conversation context via session sharing. On subsequent turns, the main agent's LLM mimics the envelope format in its own responses.

**Defense now built into Worker:** `ProcessAsync` strips completed `fabrcore-worker-envelope` fenced blocks, unterminated worker fences, and trailing worker-shaped JSON from `FinalResponse`. This is a defensive last resort — the blocks should never appear if analysis agents are properly isolated, but the strip catches the case when they're not.

**If you see this in a host using explicit `PreProcessAsync` / `ValidateAsync` instead of `ProcessAsync`:** prefer switching that host to `ProcessAsync`. `WorkerEnvelope` is internal to the Worker assembly, so client agents should not call it directly. If explicit stages are required, add a tiny local sanitizer in the host that removes `fabrcore-worker-envelope` fenced blocks before returning to the user, and re-check your analysis-agent isolation.

**The real fix is still to wire a separate tool-less analysis agent** — see the first pitfall entry. Sanitization is a safety net, not a substitute for isolation.

---

## Critical: Validator returns `id: "unknown"` or omits task results

**Problem:** A validator response says the overall answer is unsatisfied, but its `task_results` entry uses `id: "unknown"` instead of the real `WorkerTask.Id`. Older Worker builds ignored that malformed result, which left the actual task in a stale status and made telemetry/gap handling unreliable.

**Observed shape:**

```json
{
  "is_satisfied": false,
  "task_results": [
    { "id": "unknown", "satisfied": false, "reason": "The response reports that the requested state was not reached." }
  ],
  "gaps": ["The requested state change was not completed."]
}
```

**Defense now built into Worker:** `ResponseValidator` fails closed. Before projecting a verdict, it resets tracked tasks to `Pending`; every supplied task must receive exactly one matching result by id. Missing, null, or unknown ids are ignored with a warning, and unmatched tracked tasks become `Unsatisfied` with a generated gap.

**What client agents should do:** nothing special if they use `ProcessAsync` or call `ValidateAsync` normally. For debugging, inspect `WorkerValidationResult.UnsatisfiedTasks` and `Gaps`; do not trust raw model `task_results` text in monitor output.

---

## Critical: Trying to subclass FabrCoreAgentProxy for Worker

**Problem:** Worker is a service injected into an existing agent, not an agent type. There is no `[AgentAlias("worker")]` to register. Creating a `class WorkerAgent : FabrCoreAgentProxy` and trying to wire `IWorkerService` into its own `OnMessage` is a category error — Worker has nothing to do without a host LLM to wrap.

**Wrong:**

```csharp
[AgentAlias("worker-agent")]
public class WorkerAgent : FabrCoreAgentProxy { /* ... */ }
```

**Right:** Use Worker inside an existing agent.

```csharp
[AgentAlias("my-domain-agent")]
public class MyDomainAgent : FabrCoreAgentProxy
{
    private IWorkerService? _worker;
    // ...
}
```

If you actually want a standalone agent with a planner / executor / replanner loop, use `FabrCore.Agents.TaskAgent` — that's the heavyweight Worker descends from.

---

## Critical: Passing the host's main `AgentSession` to Worker

**Problem:** Worker is given an `AIAgent` (the analysis agent), not an `AgentSession`. Worker creates **fresh** sessions off the agent per analysis call so prior conversation turns don't poison the extractor / validator / router / advisor reasoning. Passing your main session to Worker functions doesn't currently compile — but accidentally sharing it via global state would silently corrupt the analysis.

**Wrong (conceptually):**

```csharp
// Don't ever do this — Worker's prompts are written assuming a fresh session.
analysisAgent.RunAsync(messages, MY_MAIN_SESSION);  // analysis prompts polluted by prior turns
```

**Right:** Hand Worker the `AIAgent`. Internally it calls `analysisAgent.CreateSessionAsync()` for every brain-layer call.

```csharp
_worker = await provider.GetWorkerServiceAsync(
    agentHost, agentHandle, configName,
    analysisAgent: _agent,        // AIAgent only — not the session
    ...);
```

---

## Critical: Forgetting to map your tool inventory into capabilities

**Problem:** `AIAgent` does not expose its tool list after construction (verified in `FabrCore.Host` 0.6.17). If you don't hand Worker a capability list, the extractor sets `Feasibility = Unknown` on every task — the agent won't be told which tasks are actually possible.

**Wrong:**

```csharp
// capabilities omitted entirely — every task gets Feasibility=Unknown
_worker = await provider.GetWorkerServiceAsync(
    agentHost, agentHandle, configName, analysisAgent: _agent);
```

**Right:** Map `ChatClientAgentOptions.Tools` (or whatever source — MCP server list, plugin registry) into `WorkerCapability[]`.

```csharp
var capabilities = (myTools ?? Enumerable.Empty<AITool>())
    .Select(WorkerCapability.FromAITool)
    .ToList();

_worker = await provider.GetWorkerServiceAsync(
    agentHost, agentHandle, configName,
    analysisAgent: _agent,
    capabilities: capabilities);
```

The agent's LLM then sees `⚠ NotFeasible` markers in the prompt overlay and learns to decline rather than pretend the action happened.

---

## Critical: Setting model names without providing an agent factory

**Problem:** When `WorkerDefinition.TaskExtractionModelName` (or any other `*ModelName`) is set but `agentFactory` is null at `GetWorkerServiceAsync` time, the model name is silently ignored — Worker falls back to the host's analysis agent. The behavior looks correct (no error), but you're not actually getting the model you asked for.

**Wrong:**

```jsonc
// fabrcore-worker.json
{
  "Workers": [
    { "Name": "default", "ValidationModelName": "planner", /* ... */ }
  ]
}
```

```csharp
// In OnInitialize — no agentFactory
_worker = await provider.GetWorkerServiceAsync(
    agentHost, agentHandle, configName, analysisAgent: _agent);
// "planner" is silently ignored, _agent is used for validation
```

**Right:** Provide an `agentFactory` whenever any model name is set in any definition you might use.

```csharp
_worker = await provider.GetWorkerServiceAsync(
    agentHost, agentHandle, configName,
    analysisAgent: _agent,
    agentFactory: async (modelName, ct) =>
        (await CreateChatClientAgent(modelName, $"{config.Handle}:worker:{modelName}", tools: null)).Agent);
```

The factory is a small wrapper around `FabrCoreAgentProxy.CreateChatClientAgent`. Cache is internal — the factory is only called once per model name per `WorkerService` instance.

---

## Critical: Calling `ValidateAsync` without `PreProcessAsync` first

**Problem:** `ValidateAsync` reads `CurrentTaskList` — which only has tasks if `PreProcessAsync` ran for this turn. Skipping `PreProcessAsync` gives an empty task list, which always validates as `IsSatisfied=true` — defeating the entire point of Worker.

**Wrong:**

```csharp
public override async Task<AgentMessage> OnMessage(AgentMessage message)
{
    var response = await RunMyLlm(message);
    var validation = await _worker!.ValidateAsync(message, response); // ← Always IsSatisfied=true
    // ...
}
```

**Right:** Always pair them, or use `ProcessAsync` which calls both internally.

```csharp
public override async Task<AgentMessage> OnMessage(AgentMessage message)
{
    var pre = await _worker!.PreProcessAsync(message);
    var response = await RunMyLlm(message.Message + pre.PromptOverlay);
    var validation = await _worker.ValidateAsync(message, response);
    // ...
}
```

---

## High: Retrying real-world constraints instead of escalating

**Problem:** The user asks the agent to bring an external system to a target state. The agent makes partial progress, but a resource, permission, policy, or geometry constraint prevents the remaining change. The validator says `IsSatisfied=false`. `ProcessAsync` retries. The retry runs the same approach and reaches the same partial state. Worker has burned extra LLM and SME calls on a problem that no identical retry can solve — it is a real constraint, not a sequencing bug.

**Why it happens by default:** the validator's strict-on-Action rule says "only explicit completion counts." A status-style response ("still not at 100%") doesn't explain the constraint, so the validator pushes a retry. The retry repeats the failure.

**Mitigations now baked into Worker (since the case-study failure):**

1. **`ResponseValidator` now sees `WorkerTaskFeasibility` per task** and accepts a clean constraint-report as satisfaction when the response names the specific constraint and what would unblock it.
2. **`InternalAdvisor.AdviseGapsAsync`** is now prompted to recognize physical/domain/capability constraints and explicitly tell the agent "this is a constraint, not a retry candidate."
3. **`BuildSuggestedFollowUp`** now includes tracked task status, the prior failed response, available capabilities, SME/advisor guidance, and an explicit recovery contract: map gaps to tools, try alternate tool-backed paths/routes/parameters, then either complete the task or report the exact constraint.
4. **Worker emits a `WORKER ESCALATE` WARN log** when ProcessAsync exhausts the retry budget with gaps still open, so post-incident analysis is easy.

**Still required on the host side** — the agent's domain system prompt must teach it to recognize constraints AND respond with concrete details:

```
COMPLETION-WITH-CONSTRAINTS
- If a step cannot complete because of a resource / policy / permission /
  physical constraint, report:
    - what specifically blocks the step (named values, limits, IDs),
    - what would unblock it (approval, additional input, new resource, etc.).
  Do NOT keep retrying the same approach. State the constraint plainly.
```

Without this prompt rule, the agent will keep trying and the constraint never gets surfaced — Worker can't infer the constraint from the response if the response doesn't mention it.

---

## High: Treating SME advice as the answer instead of operational guidance

**Problem:** The SME responds with something like: "I can't perform that action directly; the caller must invoke its own mutation tool or route the live lookup to the appropriate specialist." The host LLM then repeats failure to the user instead of using that advice to call a mutation tool or route the lookup.

**What changed:** Retry guidance now explicitly says SME advice is operational guidance, not the final answer. If the advice names another tool or agent path and the host has that capability, the retry should invoke or route through that path.

**Still required on the host side:** expose the relevant mutation/routing capability in the host's tools and in the `WorkerCapability` list. If Worker does not see the capability, it can only tell the host to escalate or explain the missing path.

```csharp
var capabilities = tools.Select(WorkerCapability.FromAITool).ToList();
```

Make sure tool descriptions are specific enough for recovery routing. Prefer "Routes a live status lookup to the authoritative specialist" over "Gets info."

---

## High: Spending SME tokens on confirmation-only gaps

**Problem:** The work is already complete, or nearly complete, but validation fails because the host response did not explicitly say the completion condition. If Worker consults a heavyweight SME for that gap, the SME may spend tens of thousands of tokens just to say "confirm it explicitly."

**Example gap:**

```
No explicit confirmation that the requested update was completed.
```

**Defense now built into Worker:** `WorkerDefinition.SkipSmesForConfirmationOnlyGaps` defaults to `true`. When all gaps look confirmation-only, Worker skips peer-SME consultation and relies on local retry guidance. It does **not** skip SMEs for gaps that mention partial progress, percentages, remaining work, constraints, blocked work, errors, or failed operations.

```jsonc
{
  "ConsultSmesOnValidate": true,
  "SkipSmesForConfirmationOnlyGaps": true
}
```

Keep this enabled for high-context SMEs. Turn it off only when your SMEs are cheap and you want them involved in every validation failure.

---

## High: `AdvisorMode=OnlyWhenSmesProduceNothing` muzzles the advisor when SMEs are configured but unhelpful

**Problem:** With curated SMEs that respond generically (sequence-of-operations advice when the actual blocker is a physical constraint), `AdvisorMode=OnlyWhenSmesProduceNothing` (the default) skips the internal advisor on every validation failure — even though the advisor is exactly what would catch the constraint. The mode triggers on `smeAnswerCount == 0`; one SME giving a useless answer keeps the count at 1 and the advisor never runs.

**When this matters:** any pipeline where SMEs are domain-curated but the failure mode is "the work is constrained, not sequence-buggy."

**Fix:** Set `AdvisorMode: "Always"` in the `WorkerDefinition`. The cost is one extra LLM call per validate-failure; the benefit is the advisor sees the task list, feasibility, capabilities, and prior response together and can recognize constraints SMEs miss.

```jsonc
{
  "InternalAdvisorOnValidate": true,
  "AdvisorMode": "Always"
}
```

The default stays `OnlyWhenSmesProduceNothing` to avoid the extra cost for users with well-curated SMEs that consistently produce useful answers — but if you see Worker burning retries on real-world constraints, this is the first knob to turn.

---

## High: Ignoring `pre.PromptOverlay`

**Problem:** Calling `PreProcessAsync` but not appending the returned overlay to the user prompt. The host's LLM never sees the task list, the feasibility verdicts, or the intake advice — extraction happens but its output is invisible to the LLM. Validation will likely fail because the agent didn't know what was being tracked.

**Wrong:**

```csharp
var pre = await _worker!.PreProcessAsync(message);
// PromptOverlay ignored
var chat = new ChatMessage(ChatRole.User, message.Message);
```

**Right:**

```csharp
var pre = await _worker!.PreProcessAsync(message);
var chat = new ChatMessage(ChatRole.User, message.Message + pre.PromptOverlay);
```

If you don't want the overlay to leak into your visible response, that's a separate concern — strip it from the LLM's output before returning, but DO put it in the prompt.

---

## High: Validating a response that includes the prompt overlay

**Problem:** Some hosts construct the LLM input by concatenating `message.Message + pre.PromptOverlay`, then capture *everything echoed back* including the overlay. Passing that combined string to `ValidateAsync` confuses the validator — it may judge tasks satisfied based on the overlay markers rather than the actual response.

**Wrong:**

```csharp
var combinedInput = message.Message + pre.PromptOverlay;
var rawResponse = await RunLlm(combinedInput);
var validation = await _worker.ValidateAsync(message, rawResponse);
// rawResponse may contain echoed overlay markers
```

**Right:** Pass only the LLM's actual response — not the input.

```csharp
var response = "";
await foreach (var u in _agent.RunStreamingAsync(chat, _session))
    response += u.Text;   // ← only the LLM's output

var validation = await _worker.ValidateAsync(message, response);
```

If your LLM is the kind that echoes system content, strip the overlay markers before validation.

---

## High: Putting too much SME metadata in `Description` and skipping `GoodFor`

**Problem:** The router LLM uses `GoodFor` as its primary routing signal because that field is reserved for *concrete topics this SME covers*. Putting everything in `Description` ("This SME is responsible for compliance, regulatory affairs, KYC, sanctions, data retention, audit, and more") makes routing fuzzy.

**Wrong:**

```jsonc
{
  "Alias": "compliance-sme",
  "Description": "This is our compliance SME and it handles all sorts of regulatory things including KYC, sanctions screening, and data retention",
  "GoodFor": null
}
```

**Right:**

```jsonc
{
  "Alias": "compliance-sme",
  "Description": "Internal compliance officer agent",
  "GoodFor": "regulatory limits, KYC rules, sanctions screening, data-retention policy"
}
```

Use `Description` for *what the SME is* and `GoodFor` for *concrete topics it covers*. The router prompt instructs the model to weight `GoodFor` heavily.

---

## High: Disabling internal advisor while ALSO disabling SMEs

**Problem:** If you set `ConsultSmesOnValidate=false`, `InternalAdvisorOnValidate=false`, and `AutoRetryOnValidationFailure=true`, the retry pass has nothing to inject — the host's runner re-invokes with empty `RetryGuidance`. The LLM runs the same prompt twice and probably produces the same wrong answer.

**Either:**
- Enable at least one advice source on validate, OR
- Set `AutoRetryOnValidationFailure=false` and handle validation gaps in your own code.

---

## Medium: Bare-string SME entries for all SMEs

**Problem:** Bare-string SMEs have no `Description` / `GoodFor`. When all SMEs are bare strings, `SmeRouter` is auto-bypassed (no routing signal) and Worker fans out to every SME on every relevant turn — potentially expensive.

If you have ≤ 2 SMEs this is fine. If you have more, give each at least a `GoodFor` so the router can pre-filter.

---

## Medium: Forgetting that `MaxRetries` is capped by `WorkerOptions.AbsoluteMaxRetries`

**Problem:** Setting `MaxRetries: 5` in a `WorkerDefinition` and expecting 5 retries. The effective budget is `min(5, options.AbsoluteMaxRetries)` which defaults to 2.

```csharp
options.AbsoluteMaxRetries = 5;  // raise the ceiling if you really want > 2 retries
```

Worker is intentionally not a replan loop. If you need multi-retry behavior with replanning, you want `TaskAgent`.

---

## Medium: Treating `WorkerSmeAdvice` source as if only SMEs produce it

**Problem:** Logging code that says "SME X answered ..." when the entry might actually be an internal-advisor entry. The `SmeAlias` field is `"internal-advisor"` for those, but custom UI may render it confusingly.

**Right:** Check `WorkerSmeAdvice.Source`.

```csharp
foreach (var a in result.ValidationResult.SmeAdvice)
{
    var label = a.Source == WorkerAdviceSource.InternalAdvisor
        ? "Internal Advisor"
        : $"SME: {a.SmeAlias}";
    _logger.LogInformation("{Label}: {Answer}", label, a.Answer);
}
```

---

## Medium: Routing the same SMEs at PreProcess and Validate without distinguishing the question

**Problem:** Worker's router gets a different question at each stage — intake guidance at PreProcess, gap-closing advice at Validate. The router LLM only sees the user message and task list; if your `GoodFor` strings are generic ("compliance topics"), the same SMEs get selected at both stages. That may be fine. If you want stage-specific routing, write `GoodFor` to describe both stages or split the SME into two.

This is rarely a real problem — just be aware.

---

## Medium: Per-turn capability list changing each call without using `capabilitiesOverride`

**Problem:** Setting capabilities once at `GetWorkerServiceAsync` time but the host's tool inventory actually changes per-turn (e.g., MCP servers connect/disconnect, or context-aware tool gating). The extractor reasons with stale capabilities.

**Right:** Use the per-call override.

```csharp
var currentTools = await GetCurrentlyAvailableTools();   // your dynamic source
var caps = currentTools.Select(WorkerCapability.FromAITool).ToList();

var pre = await _worker!.PreProcessAsync(message, capabilitiesOverride: caps);
```

Override applies for this call only; the construction-time list remains the default.

---

## Low: Not exposing `WorkerService.CurrentTaskList` to your UI

**Problem:** Worker tracks a useful per-turn artifact (task list + feasibility + status), but consumers often only look at the final response. Surfacing the task list in a UI panel — even just for debugging — gives huge insight into what Worker thinks is happening.

`_worker.CurrentTaskList` is safe to read between turns. The list updates during `PreProcessAsync` (replacement) and `ValidateAsync` (per-task status mutation).

---

## Low: Custom `IWorkerDefinitionProvider` that returns a fresh `WorkerDefinition` per call

**Problem:** The default `FileWorkerDefinitionProvider` caches. If you swap in a custom provider that calls a DB / API on every read, `WorkerProvider` calls it once per agent handle (caches the definition implicitly via the cached `IWorkerService`), but reactivating an agent re-resolves. Make sure your custom provider is idempotent or caches itself.

---

## Low: Reading `CurrentTaskList` from a non-host thread

**Problem:** `WorkerService` lock-guards `_current` but doesn't return a deep copy from `CurrentTaskList`. If another thread mutates `Tasks` mid-iteration, you'll see torn state.

**Right:** Snapshot before iterating.

```csharp
var snap = _worker.CurrentTaskList.Tasks.ToList();
foreach (var t in snap) { /* ... */ }
```

In an Orleans grain context this is rarely a real issue since `OnMessage` is serialized — but if you read from a separate IUserInterfaceService, snapshot.
