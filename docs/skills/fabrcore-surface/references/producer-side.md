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

Use planning when the configured model should create the envelope:

```csharp
await surfaceService.PlanAndRenderAsync("Show an approval card", sourceMessage);
```

Planning responses must include one fenced block named `fabrcore-adaptive-card-surface`.

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

## Required Planner Actions

Use `requiredActions` in `fabrcore-surface.json` when planners must include actions for repeated records:

```json
{
  "requiredActions": [
    {
      "appliesTo": "customer",
      "title": "View",
      "verb": "crm.customer.view",
      "routeTo": "agent",
      "targetAgent": "crm-agent",
      "idField": "customerId",
      "messageTemplate": "show me the customer view for customer {customerId}"
    }
  ]
}
```

## Planning Config

`fabrcore-surface.json` config controls model selection and Adaptive Card policy:

```json
{
  "surfaces": [
    {
      "name": "default",
      "planningModelName": "planner",
      "maxAdaptiveCardVersion": "1.6",
      "allowedActionTypes": [
        "Action.Execute",
        "Action.Submit",
        "Action.OpenUrl",
        "Action.ShowCard",
        "Action.ToggleVisibility"
      ],
      "allowAnyActionVerb": true,
      "allowHttpUrls": false
    }
  ]
}
```
