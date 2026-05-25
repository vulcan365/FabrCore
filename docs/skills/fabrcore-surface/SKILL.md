---
name: fabrcore-surface
description: Build, use, extend, or troubleshoot FabrCore.Surface, the FabrCore Razor class library for Adaptive Card rendering across FabrCore agents and Blazor apps.
---

# FabrCore Surface

Use this skill when working with `FabrCore.Surface`, `DynamicAgentSurface`, `ui.render` / `ui.action` messages, Adaptive Card Surface envelopes, Adaptive Card template + data rendering, planning config, action routing, or Blazor app integration.

## Shape

Surface is now an Adaptive Cards bridge:

- Agents, plugins, and services produce `AdaptiveCardSurfaceEnvelope` payloads.
- Blazor apps receive `ui.render` messages and render Adaptive Cards.
- Surface keeps FabrCore message delivery, action routing, host policy, validation, and user context.
- Surface does not define custom UI kinds, custom form schemas, custom collection renderers, Razor snippets, or registered-screen renderers.
- LLM planning is instructed to produce Adaptive Card envelopes directly; Surface does not provide a custom JSON schema to the LLM.

## Core Rules

- Keep `FabrCore.Surface` independent from `FabrCore.Client`; do not add a project or package reference to `FabrCore.Client`.
- Prefer deterministic `AdaptiveCardSurfaceEnvelope` JSON when agents already have the data and layout intent.
- Use `PlanAndRenderAsync` only when a configured model should create the Adaptive Card envelope.
- Treat Adaptive Cards as the only Surface UI format.
- Put business side effects behind trusted app handlers, usually `ISurfaceActionRegistry`.
- Keep action routing explicit in action `data` or envelope `metadata`.
- Validate cards after template expansion.
- Prefer `builder.AddFabrCoreSurfaceFromConfig("fabrcore-surface.json", "profile-name")` for all-in-one demo apps.
- Use `SurfaceActions` helpers for routed Adaptive Card actions instead of hand-assembling the FabrCore action data shape.

## Producer Side

Register producer services where agents run in split-host scenarios:

```csharp
services.AddFabrCoreSurfaceServices(options =>
{
    options.DefinitionFilePath = "fabrcore-surface.json";
    options.DefaultSurfaceDefinitionName = "default";
    options.DefaultPlanningModelName = "planner";
});
```

For all-in-one Host + Blazor apps, prefer the config-driven path:

```csharp
builder.AddFabrCoreSurfaceFromConfig("fabrcore-surface.json", "crm-demo");
builder.Services.AddFabrCoreSurfaceComponents();
```

Producers can call `ISurfaceService.RenderAsync(envelope, sourceMessage)` with deterministic JSON, or `PlanAndRenderAsync(prompt, sourceMessage)` when a configured planning model should produce the Adaptive Card envelope.

For producer examples, read [producer-side.md](references/producer-side.md) and reuse [render-envelope-snippet.cs](assets/templates/render-envelope-snippet.cs).

## Consumer Side

Register the Blazor/FabrCore client side:

```csharp
builder.AddFabrCoreSurface(options =>
{
    options.MaxAdaptiveCardVersion = "1.6";
});

builder.Services.AddFabrCoreSurfaceComponents();
```

When using `AddFabrCoreSurfaceFromConfig`, policy from the selected `fabrcore-surface.json` definition is applied to consumer validation options and producer planning options.

Add static assets and render:

```html
<link href="_content/FabrCore.Surface/surface.css" rel="stylesheet" />
```

```razor
<DynamicAgentSurface UserHandle="@userId" />
```

For app setup details, read [integration.md](references/integration.md) and reuse [blazor-program-snippet.cs](assets/templates/blazor-program-snippet.cs).

## Contract

Surface renders messages with:

```csharp
MessageType = SurfaceMessageTypes.UiRender
DataType = SurfaceMessageTypes.DataType
Data = JsonSerializer.SerializeToUtf8Bytes(envelope, SurfaceJson.Options)
```

`SurfaceMessageTypes.DataType` is `application/vnd.fabrcore.surface.adaptive-card+json`.

The envelope is:

```json
{
  "version": "2.0",
  "id": "card-id",
  "card": {
    "type": "AdaptiveCard",
    "version": "1.6",
    "body": []
  },
  "data": {},
  "metadata": {}
}
```

## Action Routing

Surface supports standard Adaptive Card actions:

- `Action.Execute` and `Action.Submit` can dispatch to the app, source agent, or both.
- `Action.OpenUrl` is allowed only for safe URLs.
- `Action.ShowCard` and `Action.ToggleVisibility` remain client-side card behavior.

Put FabrCore routing hints in action `data`, for example:

```json
{
  "type": "Action.Execute",
  "title": "View",
  "verb": "invoice.view",
  "data": {
    "id": "INV-1001",
    "routeTo": "agent",
    "messageTemplate": "show invoice {id}"
  }
}
```

For C# helper APIs, use:

```csharp
SurfaceActions.ToAgent(
    title: "View",
    verb: "crm.customer.view",
    targetAgent: "crm-agent",
    payload: new { customerId = customer.Id },
    messageTemplate: "show me the customer view for customer {customerId}");
```

`SurfaceAgent` honors `AgentMessage.Args["targetHandle"]`, `AgentMessage.Args["surface:TargetHandle"]`, and `envelope.metadata.targetHandle` for direct `ui.render` delivery.

## Validation

Surface validates envelope JSON, Adaptive Card type/version, payload size, nesting depth, action type policy, action verb policy, target-agent policy, and URL policy. Domain validation remains the responsibility of app/API handlers.

## Useful Assets

- [contracts.md](references/contracts.md)
- [action-routing.md](references/action-routing.md)
- [integration.md](references/integration.md)
- [producer-side.md](references/producer-side.md)
- [validation-policy.md](references/validation-policy.md)
- [testing.md](references/testing.md)
- [approval-card.json](assets/sample-envelopes/approval-card.json)
- [submit-form-card.json](assets/sample-envelopes/submit-form-card.json)
- [open-url-card.json](assets/sample-envelopes/open-url-card.json)
- [customer-list-card.json](assets/sample-envelopes/customer-list-card.json)
- [fabrcore-surface-config-example.json](assets/templates/fabrcore-surface-config-example.json)
- [render-envelope-snippet.cs](assets/templates/render-envelope-snippet.cs)
- [surface-action-helper-snippet.cs](assets/templates/surface-action-helper-snippet.cs)
