# Adaptive Card Surface Contracts

## Render Envelope

`AdaptiveCardSurfaceEnvelope` is the only render payload sent in `AgentMessage.Data`.

```json
{
  "version": "2.0",
  "id": "approval-card",
  "card": {
    "type": "AdaptiveCard",
    "version": "1.6",
    "body": [
      { "type": "TextBlock", "text": "Approve ${requestName}?" }
    ],
    "actions": [
      { "type": "Action.Execute", "title": "Approve", "verb": "approve" }
    ]
  },
  "data": {
    "requestName": "Purchase Order 1001"
  }
}
```

Fields:

- `version`: Surface envelope version. Use `2.0`.
- `id`: stable envelope/card id used for render and action correlation.
- `card`: Adaptive Card template JSON. The expanded result must have `type: "AdaptiveCard"`.
- `data`: optional JSON object used by Surface template expansion.
- `metadata`: optional FabrCore routing defaults such as `routeTo`, `targetAgent`, and `messageTemplate`.
- `metadata.targetHandle`: optional direct recipient for the final `ui.render`.

Template bindings use `${path.to.value}`. If the entire JSON string is one binding, Surface preserves the bound value type. For example, `"value": "${amount}"` can become a number, while `"text": "Amount ${amount}"` becomes a string.

## Message Types

- `UiRender = "ui.render"`
- `UiAction = "ui.action"`
- `DataType = "application/vnd.fabrcore.surface.adaptive-card+json"`

## Action Event

Deterministic agent-authored `Action.Execute` and `Action.Submit` actions produce an `AdaptiveCardActionEvent` when routed to an agent. The event includes action type, action id, verb, route, message, target agent, payload, and optional app result.

Planner-generated cards are display-only and must not include `Action.Execute`, `Action.Submit`, or FabrCore routing metadata. Agents that emit executable actions own the corresponding verbs and should handle unknown verbs gracefully.

`Action.OpenUrl`, `Action.ShowCard`, and `Action.ToggleVisibility` are validated and rendered as client-side Adaptive Card behavior, not dispatched as `ui.action`.

## Constants

Use these constants instead of hard-coded strings in app code:

- `SurfaceMessageArgs.TargetHandle`
- `SurfaceMessageArgs.SurfaceTargetHandle`
- `SurfaceMessageArgs.SurfaceConfig`
- `SurfaceActionDataKeys.ActionId`
- `SurfaceActionDataKeys.RouteTo`
- `SurfaceActionDataKeys.TargetAgent`
- `SurfaceActionDataKeys.MessageTemplate`
- `SurfaceDiagnosticArgs.TargetHandle`
- `SurfaceDiagnosticArgs.PlannedActionCount`
- `SurfaceDiagnosticArgs.ValidatedActionCount`
- `SurfaceDiagnosticArgs.RejectedActionCount`
- `SurfaceDiagnosticArgs.RenderedActionCount`

## Fenced LLM Output

Planning responses must include exactly one fenced block:

````markdown
```fabrcore-adaptive-card-surface
{
  "version": "2.0",
  "id": "status-card",
  "card": {
    "type": "AdaptiveCard",
    "version": "1.6",
    "body": []
  }
}
```
````

Planner responses must produce display-only cards. Use deterministic agent-authored envelopes when executable workflow actions are needed.
