# Producer Side

Register agent-side services:

```csharp
services.AddFabrCoreSurfaceServices(options =>
{
    options.DefinitionFilePath = "fabrcore-surface.json";
    options.DefaultSurfaceDefinitionName = "default";
    options.DefaultPlanningModelName = "planner";
});
```

For all-in-one apps, prefer:

```csharp
builder.AddFabrCoreSurfaceFromConfig("fabrcore-surface.json", "crm-demo");
builder.Services.AddFabrCoreSurfaceComponents();
```

Use deterministic JSON when possible:

```csharp
await surfaceService.RenderAsync(envelope, sourceMessage);
```

Use planning when the configured model should create a display-only envelope:

```csharp
await surfaceService.PlanAndRenderAsync("Show an approval card", sourceMessage);
```

Planning responses must include one fenced block named `fabrcore-adaptive-card-surface`. Planner-generated cards must not include `Action.Execute`, `Action.Submit`, or FabrCore action routing metadata.

The built-in `surface` agent resolves render targets in this order:

1. explicit `targetHandle` passed to `RenderAsync`
2. `AgentMessage.Args["targetHandle"]`
3. `AgentMessage.Args["surface:TargetHandle"]`
4. `envelope.metadata.targetHandle`
5. normal response routing

## Direct Envelope Example

```csharp
var envelope = new AdaptiveCardSurfaceEnvelope
{
    Id = "approval-card",
    Card = JsonDocument.Parse("""
        {
          "type": "AdaptiveCard",
          "version": "1.6",
          "body": [
            { "type": "TextBlock", "text": "Approve ${requestName}?" }
          ],
          "actions": [
            {
              "type": "Action.Execute",
              "title": "Approve",
              "verb": "approve",
              "data": {
                "routeTo": "both",
                "messageTemplate": "approved {requestId}",
                "requestId": "${requestId}"
              }
            }
          ]
        }
        """).RootElement.Clone(),
    Data = JsonDocument.Parse("""{ "requestId": "REQ-1001", "requestName": "Request REQ-1001" }""").RootElement.Clone()
};
```

## Agent-Owned Actions

Use deterministic `RenderAsync` envelopes when a card needs executable actions. The producing agent owns the verbs it emits and receives clicks as `ui.action` messages with the structured action event in `AgentMessage.Data`.

Planner-generated cards are display-only. If a planned view needs follow-up work, the planner should summarize the state and let the principal continue through normal chat.

## Planning Config

`fabrcore-surface.json` only needs model/profile fields for the default Surface policy:

```json
{
  "surfaces": [
    {
      "name": "default",
      "planningModelName": "planner"
    }
  ]
}
```

Optional policy overrides are available when an app needs them:

- `maxAdaptiveCardVersion`: defaults to `1.6`.
- `allowedActionTypes`: defaults to standard Adaptive Card actions (`Execute`, `Submit`, `OpenUrl`, `ShowCard`, `ToggleVisibility`).
- `allowHttpUrls`: defaults to `false`; HTTPS URLs are allowed.
- `allowedTargetAgents`: only matters when deterministic agent-authored actions set `data.targetAgent` and unknown target agents are disabled.
