# Advice Flow

How the SME router and internal advisor combine to produce the advice list at each pipeline stage.

## The two advice sources

Worker has two sources of pipeline guidance, both producing `WorkerSmeAdvice` entries:

1. **Peer SMEs** — real agents queried via `AgentMessage` with `MessageType = "swarm-sme-consultation"`. Source: `WorkerAdviceSource.SubjectMatterExpert`.
2. **Internal advisor** — an LLM call inside Worker that plays the same role using just the analysis model. Source: `WorkerAdviceSource.InternalAdvisor`.

Downstream rendering treats them uniformly: `sme:<alias>` vs `advisor` prefix in the prompt overlay / follow-up.

## SME relevance routing

Before fanning out to configured SMEs, Worker may filter the list with an LLM call. The router is **bypassed automatically** when:

- There are 0 or 1 SMEs (no choice to make).
- No SME has any `HasRoutingMetadata` (`Description` and `GoodFor` are both null).
- `WorkerDefinition.RouteSmesByRelevance` is `false`.

When the router runs:

1. Builds a prompt with the user message, the extracted task list, and each SME's alias + description + GoodFor.
2. Calls the router model (or its fallback) with a fresh `AgentSession`.
3. Parses a `selected_aliases` array from the response envelope.
4. Returns the subset of `SmeReference[]` matching the selection.

**Conservative fallback** — when the router can't decide (no envelope, empty selection, no matches), it returns the full SME list. False negatives at routing are worse than false positives.

## Internal advisor decision

The advisor runs when:

```
InternalAdvisorOn<Stage> == true
    AND
ShouldRunAdvisor(AdvisorMode, smesUsedThisStage, smeAnswerCount) == true
```

### `smesUsedThisStage`

True when **both**:
- The stage's `ConsultSmesOn<Stage>` flag is `true`, AND
- `SubjectMatterExperts.Count > 0`.

### `ShouldRunAdvisor` matrix

| `AdvisorMode` | `smesUsedThisStage=false` | `smesUsedThisStage=true, smeAnswerCount=0` | `smesUsedThisStage=true, smeAnswerCount>0` |
| --- | --- | --- | --- |
| `Disabled` | ✗ | ✗ | ✗ |
| `OnlyWhenNoSmes` | ✓ | ✗ | ✗ |
| `OnlyWhenSmesProduceNothing` | ✓ | ✓ | ✗ |
| `Always` | ✓ | ✓ | ✓ |

`smeAnswerCount` counts only SMEs that returned non-empty `Message` with `sme-status != "unknown"` — empty answers and "unknown" responses count as zero.

## Stage-by-stage flow

### PreProcess

```
1. TaskExtractor.ExtractAsync(message, prior list, capabilities)
   → WorkerTaskList (each task has Feasibility + reason)

2. Advice collection (GatherAdviceAsync(PreProcess)):

   a. if ConsultSmesOnPreProcess && SubjectMatterExperts.Count > 0:
        routed = RouteSmesByRelevance
                   ? SmeRouter.SelectRelevantAsync(message, tasks, smes, routerAgent)
                   : smes
        smeAdvice = WorkerSmeConsultationService.ConsultAsync(routed, intakeQuestion)

   b. if InternalAdvisorOnPreProcess && ShouldRunAdvisor(AdvisorMode, smesUsedThisStage, smeAdvice.Count):
        advisorAdvice = InternalAdvisor.AdviseIntakeAsync(message, tasks, capabilities, advisorAgent)

   c. combined = smeAdvice + (advisorAdvice if present)

3. BuildPromptOverlay(tasks, combined) → string for the host to append to the user prompt.
```

### Validate

```
1. ResponseValidator.ValidateAsync(message, current task list, response)
   → fail-closed per-task verdict + Gaps + Reasoning

   The validator must return one `task_results` entry for every supplied task
   id. Worker ignores malformed ids such as `"unknown"`, marks any unmatched
   tracked task as `Unsatisfied`, and synthesizes a gap if the model forgot to
   provide one.

2. If IsSatisfied: return verdict — NO advice gathered.

3. Otherwise, advice collection (GatherAdviceAsync(Validate)):

   a. if ConsultSmesOnValidate && SubjectMatterExperts.Count > 0
      && not confirmation-only:
        routed = router or all SMEs (as above)
        question = "Here's the failed response, here are the gaps. How should the agent close them?"
        smeAdvice = WorkerSmeConsultationService.ConsultAsync(routed, gapQuestion, context: priorResponse)

   b. if InternalAdvisorOnValidate && ShouldRunAdvisor(...):
        advisorAdvice = InternalAdvisor.AdviseGapsAsync(message, tasks, priorResponse, validation, advisorAgent)

   c. combined = smeAdvice + (advisorAdvice if present)

4. BuildSuggestedFollowUp(tasks, gaps, combined, capabilities, priorResponse)
   → string for retry prompt.
```

When `SkipSmesForConfirmationOnlyGaps=true` (default), Worker skips peer SMEs for validation failures that only need explicit completion wording. It still consults SMEs for partial percentages, remaining gaps, constraints, errors, or blocked work. This prevents expensive SME calls for "confirm what you already did" retries.

## How advice surfaces to the host's LLM

### PreProcess: `PromptOverlay`

Appended verbatim to the user prompt by the host. Format:

```
[Worker tracked tasks — make sure each is addressed before responding]
  - (Question) Summarize doc X
  - (Action ⚠ NotFeasible) Email it to Jane
      reason: no email-sending tool in inventory

Tasks marked NotFeasible cannot be completed with current tools.
Decline cleanly or escalate rather than pretending the action happened.

[Intake guidance]
  - sme:compliance-sme: ...
  - advisor: ...
```

### Validate failure: `SuggestedFollowUp`

Used by `RetryWithGuidanceAsync` / `ProcessAsync` retry pass. Format:

```
[Worker validation found gaps — recover before answering]

[Tracked tasks]
  - <task-id>: Action / Feasible / Unsatisfied — Complete the requested update — ...

[Prior response that failed validation]
The requested update is still incomplete: ...

[Available capabilities for recovery]
  - perform_requested_update: ...
  - route_authoritative_lookup: ...

[Gaps to close]
  - The email to Jane was not sent
  - The summary lacks one requested section

[Guidance on the gaps]
  - sme:compliance-sme: ...
  - advisor: ...

[Retry execution contract]
Before producing the next user-facing answer, make a concrete recovery attempt:
  - Map each unsatisfied action task to the best available capability or tool.
  - If the first path did not finish the work, try a different tool-supported path, lookup, route, or parameter strategy.
  - Use SME advice as operational guidance, not as the final answer.
  - Do not merely restate that validation failed.
```

The important behavior is that advice should drive action. If an SME says
"route the lookup to the authoritative specialist" and the host has that
route/tool, the retry should use that route rather than repeat the failed
status report.

## Worked decision examples

### Default config + message that's clearly satisfiable by a single SME

Definition: 3 SMEs configured with metadata, `ConsultSmesOnValidate=true`, `AdvisorMode=OnlyWhenSmesProduceNothing`, `RouteSmesByRelevance=true`.

User: *"Email Jane a summary of doc X"*. Agent responds with summary but no email.

Validate:
1. Validator says NotSatisfied (email Action not done).
2. `smesUsedThisStage = true`. Router picks `email-policy-sme` only (1 of 3).
3. Email policy SME answers: "Use Microsoft Graph; the agent should call X with arguments Y."
4. `smeAnswerCount = 1` → mode `OnlyWhenSmesProduceNothing` says skip advisor.
5. `SuggestedFollowUp` contains the SME's guidance.
6. Retry: agent now knows how to send the email.

### No SMEs at all + same message

Definition: empty `SubjectMatterExperts`, `InternalAdvisorOnValidate=true`, `AdvisorMode=OnlyWhenSmesProduceNothing`.

Validate:
1. Validator says NotSatisfied.
2. `smesUsedThisStage = false` (no SMEs).
3. Mode `OnlyWhenSmesProduceNothing` → advisor runs.
4. Advisor produces: "The agent should call the email tool with subject = 'Doc X summary'..."
5. `SuggestedFollowUp` contains advisor guidance.
6. Retry: agent has guidance.

### SMEs all return "unknown" + advisor as fallback

Same as above but with 2 SMEs configured. Both return `sme-status=unknown`.

Validate:
1. Validator says NotSatisfied.
2. `smesUsedThisStage = true`. SMEs consulted. `smeAnswerCount = 0`.
3. Mode `OnlyWhenSmesProduceNothing` → advisor runs.
4. Advisor produces guidance. SuggestedFollowUp contains it.

### `Always` mode — both sources contribute

Same configuration but `AdvisorMode=Always`. Both SMEs answer with useful guidance.

Validate:
1. Validator NotSatisfied.
2. SMEs consulted; both answer.
3. Advisor also runs (mode `Always` does not depend on SME outcome).
4. `SuggestedFollowUp` has 3 entries: 2 sme: lines + 1 advisor: line.

This is the "diversity signal" pattern — useful when SMEs are narrow specialists and the advisor adds a generalist sanity check.

## Cost considerations

Worst case for a single turn under a maximally-configured definition:

| Call | Pre-pass | Retry pass |
| --- | --- | --- |
| Task extraction | 1 | 0 (uses prior list) |
| SME router | 1 (if metadata) | 1 |
| SME fan-out | N (filtered) | N (filtered) |
| Internal advisor | 1 | 1 |
| Response validation | 1 | 1 |

Plus 1–2 host LLM calls. So a "maximalist" turn might be 5+ LLM calls on top of the host's own work. Reduce by:

- Setting `RouteSmesByRelevance=false` if you have ≤ 2 SMEs (skip the router call).
- Keeping `SkipSmesForConfirmationOnlyGaps=true` so wording-only validation gaps do not consult SMEs.
- Setting `AdvisorMode=OnlyWhenSmesProduceNothing` (default) so the advisor only runs when SMEs didn't help.
- Setting `AutoRetryOnValidationFailure=false` if the host wants to handle validation gaps itself (skip the retry pass).
- Using fast-tier models for extraction/router (cheap) and a planner-tier only for validator/advisor.
