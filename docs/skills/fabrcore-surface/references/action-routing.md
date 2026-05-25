# Adaptive Card Action Routing

Surface supports all standard Adaptive Card actions. Only `Action.Execute` and `Action.Submit` dispatch to FabrCore app/agent handlers.

Routing fields can be placed in action `data`:

```json
{
  "type": "Action.Execute",
  "title": "View",
  "verb": "invoice.view",
  "data": {
    "id": "INV-1001",
    "routeTo": "both",
    "targetAgent": "accounting-agent",
    "messageTemplate": "show invoice {id}"
  }
}
```

The dispatcher also accepts routing defaults from envelope `metadata`, but action `data` should be preferred for action-specific behavior.

Routes:

- `app`: execute `ISurfaceActionRegistry`.
- `agent`: send `ui.action` back to the source or target agent.
- `both`: execute the app registry and send the resulting event to the agent.

`Action.OpenUrl` is validated for safe absolute URLs. `Action.ShowCard` and `Action.ToggleVisibility` are handled by the Adaptive Cards renderer.

## Dispatch Payload

When `Action.Execute` or `Action.Submit` is dispatched, Surface builds a payload from:

- action type
- action title
- `verb`
- action `data`
- collected Adaptive Card input values

The action id is resolved from `actionId`, then `id`, then `verb`, then title, then action type.

`messageTemplate` supports `{name}` replacement from the payload. Use this to give the receiving agent a useful natural-language message while still sending structured JSON.

## Client-Only Actions

Use `Action.OpenUrl` only for safe absolute URLs. HTTP URLs are rejected unless host policy explicitly allows them.

Use `Action.ShowCard` and `Action.ToggleVisibility` for local card interactions; they do not call app handlers or agents.

## Helper API

Use `SurfaceActions` to avoid hand-writing routing metadata:

```csharp
SurfaceActions.ToAgent(
    title: "View",
    verb: "crm.customer.view",
    targetAgent: "crm-agent",
    payload: new { customerId = "CUS-1001" },
    messageTemplate: "show me the customer view for customer {customerId}");
```

Available helpers:

- `SurfaceActions.ToAgent`
- `SurfaceActions.ToApp`
- `SurfaceActions.ToBoth`
- `SurfaceActions.OpenUrl`

Canonical keys are available in `SurfaceActionDataKeys`.
