---
name: fabrcore-surface
description: Build, use, extend, or troubleshoot FabrCore.Surface, the FabrCore Razor class library for Adaptive Card rendering, SurfaceChatLink, SurfaceNotify, command-center chat, unread notifications, and Blazor app integration.
---

# FabrCore Surface

Use this skill when working with `FabrCore.Surface`, `DynamicAgentSurface`, `SurfaceChatLink`, `SurfaceNotify`, `/surface` command-center chat, unread notifications, `ui.render` / `ui.action` messages, Adaptive Card Surface envelopes, Adaptive Card template + data rendering, planning config, action routing, or Blazor app integration.

## Shape

Surface is now an Adaptive Cards bridge:

- Agents, plugins, and services produce `AdaptiveCardSurfaceEnvelope` payloads.
- Blazor apps receive `ui.render` messages and render Adaptive Cards.
- Surface keeps FabrCore message delivery, action routing, host policy, validation, and principal context.
- Surface-facing APIs now use Principal terminology: `ISurfacePrincipalContext`, `ISurfacePrincipalContextFactory`, `SurfacePrincipalContext`, `ISurfacePrincipalContextProvider`, `PrincipalId`, `PrincipalHandle`, and `SurfaceTimelineItemKind.Principal`. Do not use the removed `SurfaceClientContext`, `ISurfaceClientContext`, `SurfaceOwnerContext`, `OwnerId`, or `UserHandle` Surface APIs.
- Surface chat surfaces (`/surface`, `SurfaceChatLink`, and `SurfaceNotify`) share one principal-scoped `SurfaceWorkspaceService` transcript/unread model so chat messages, agent replies, Adaptive Card render messages, and unread counts stay consistent.
- `FabrCore.Surface.Admin` owns the optional `/surface/agents`, `/surface/admin`, `/surface/admin/verifiable-execution`, and `/surface/admin/incidents` operations pages.
- Base Surface navigation renders only Surface-owned links. The Agents, Admin, Execution, and Incidents links are shown only after the host registers `FabrCore.Surface.Admin`.
- Surface does not define custom UI kinds, custom form schemas, custom collection renderers, Razor snippets, or registered-screen renderers.
- LLM planning is instructed to produce display-only Adaptive Card envelopes directly; Surface does not provide a custom JSON schema to the LLM.

## Core Rules

- Keep `FabrCore.Surface` independent from `FabrCore.Client`; do not add a project or package reference to `FabrCore.Client`.
- Prefer deterministic `AdaptiveCardSurfaceEnvelope` JSON when agents already have the data and layout intent.
- Use `PlanAndRenderAsync` only when a configured model should create a display-only Adaptive Card envelope.
- Treat Adaptive Cards as the only Surface UI format.
- Put business side effects behind the agent or trusted app handler that owns the card action.
- Keep action routing explicit in deterministic agent-authored action `data` or envelope `metadata`.
- Validate cards after template expansion.
- Prefer `builder.AddFabrCoreSurfaceFromConfig("fabrcore-surface.json", "profile-name")` for all-in-one demo apps.
- Use `SurfaceActions` helpers for deterministic agent-authored routed Adaptive Card actions instead of hand-assembling the FabrCore action data shape.
- Use `SurfaceChatLink` for a ChatDock-like chat entry point outside the `/surface` route; use `SurfaceNotify` for a compact header notification icon. Do not depend on `FabrCore.Client.ChatDock`.
- Do not add admin links to base Surface UI. Use the shared Surface navigation components and let `AddFabrCoreSurfaceAdminComponents()` opt into admin navigation.

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

Producers can call `ISurfaceService.RenderAsync(envelope, sourceMessage)` with deterministic JSON, or `PlanAndRenderAsync(prompt, sourceMessage)` when a configured planning model should produce a display-only Adaptive Card envelope.

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

The packaged `/surface` page declares `InteractiveServer` render mode directly. Consumers still need
`AddInteractiveServerComponents()`, `.AddInteractiveServerRenderMode()`, and the Blazor web script,
but they do not need to mark the entire host `<Routes>` component interactive just for Surface page
buttons.

Surface-only hosts should map `.AddFabrCoreSurfaceRoutes()` and add only
`typeof(FabrCore.Surface.Components.SurfaceCommandCenter).Assembly` to the router's
`AdditionalAssemblies` list. In this mode the Surface nav shows Chat only; it does not show
Agents, Admin, Execution, or Incidents.

Admin-capable hosts can reference `FabrCore.Surface.Admin`, call
`AddFabrCoreSurfaceAdminComponents()`, map `.AddFabrCoreSurfaceAdminRoutes()`, and add
`typeof(FabrCore.Surface.Admin.Components.SurfaceAdminPage).Assembly` to the router's
`AdditionalAssemblies` list. `AddFabrCoreSurfaceAdminRoutes()` also maps the base Surface route
assembly, so an Admin-enabled host needs only that one route extension. The Surface route
extensions are idempotent — chaining `.AddFabrCoreSurfaceRoutes()` and
`.AddFabrCoreSurfaceAdminRoutes()` together is safe — but never add the Surface or Admin component
assemblies through the raw `.AddAdditionalAssemblies(...)` API, which throws
`Assembly already defined` at startup on duplicates. Admin registration
enables both the Chat link and the admin-owned Agents, Admin, Execution, and Incidents links.

```razor
<DynamicAgentSurface PrincipalHandle="@userId" />
```

Embed ChatDock-like chat anywhere in a Blazor app with `SurfaceChatLink`:

```razor
@using FabrCore.Surface.Components

<SurfaceChatLink AgentHandle="assistant"
                 Title="Assistant"
                 OnMessageReceived="HandleAgentMessageAsync"
                 Position="SurfaceChatLinkPosition.BottomRight" />
```

If `AgentHandle` is omitted, the link follows the currently selected `/surface` agent for that principal circuit. If `AgentHandle` is a bare alias, Surface prefixes it with the current principal id. The link renders the same transcript and Adaptive Card messages as `/surface`.
Use `OnMessageReceived` when the embedding page should react to agent messages such as `ui-update` or `data-changed`; return `false` to keep that message out of the link panel while leaving the shared `/surface` transcript intact.
The link icon renders inline wherever the component is placed. `Position` controls only the opened chat panel location.

For page-scoped agent provisioning, pass a `CreateAgent` delegate. The link shows a Create action, never auto-creates on load, and enables chat as soon as the target resolves to a healthy configured agent. This supports agents provisioned outside the link, such as page startup code that calls `IFabrCoreAgentService.ConfigureAgentAsync`. Set `AllowExternalAgent="false"` when the principal must explicitly create the agent through the link before chat is enabled. Use `AllowReset="true"` when the link should also expose the soft reset action via `ISurfacePrincipalContext.ResetAgent`. Hard eviction/delete is deliberately not exposed by `SurfaceChatLink`.

```razor
<SurfaceChatLink AgentHandle="assistant"
                 Title="Assistant"
                 CreateAgent="CreateAssistantAsync"
                 AllowExternalAgent="true"
                 AllowReset="true" />

@code {
    private Task<AgentHealthStatus> CreateAssistantAsync(
        SurfaceChatLinkCreateAgentContext context)
    {
        return context.PrincipalContext.CreateAgent(new AgentConfiguration
        {
            Handle = context.AgentAlias,
            AgentType = "assistant-agent",
            Models = "default",
            Description = "Page assistant",
            ForceReconfigure = false
        });
    }
}
```

`SurfaceChatLinkCreateAgentContext` provides `Principal`, `PrincipalContext`, `AgentAlias`, `AgentHandle`, and `Workspace` so page code can use route parameters, tenant context, or page services while keeping the lifecycle UI inside the component.

Embed a compact header notification icon with `SurfaceNotify`:

```razor
@using FabrCore.Surface.Components

<SurfaceNotify />
```

`SurfaceNotify` shows the total unread count across Surface agents and squads. Left click navigates to `/surface`; right click opens a compact unread menu with per-target open/mark-seen actions plus a mark-all-seen action. Clicking outside the menu closes it. Inspecting or closing the menu does not clear unread state.

For app setup details, read [integration.md](references/integration.md) and reuse [blazor-program-snippet.cs](assets/templates/blazor-program-snippet.cs).
For chat-link usage, read [chat-link.md](references/chat-link.md) and reuse [surface-chat-link-snippet.razor](assets/templates/surface-chat-link-snippet.razor).
For notification icon usage, reuse [surface-notify-snippet.razor](assets/templates/surface-notify-snippet.razor).

If `/surface` renders only a page header plus an empty `<div class="fabrcore-surface"></div>`, the app is probably showing a local wrapper around `DynamicAgentSurface`, not the full command center route. Map the Surface route assembly with `.AddFabrCoreSurfaceRoutes()` and make sure the app `Router` includes `typeof(FabrCore.Surface.Components.SurfaceCommandCenter).Assembly`. If the app intentionally embeds `DynamicAgentSurface`, an empty div means no `ui.render` Adaptive Card messages have arrived for that `PrincipalHandle` yet.

The `/surface` command center composer separates delivery from response expectation. By default it uses fire-and-forget delivery while keeping chat messages as `MessageKind.Request`:

```csharp
builder.AddFabrCoreSurface(options =>
{
    options.CommandCenterChatDeliveryMode = SurfaceChatDeliveryMode.FireAndForget;
    options.CommandCenterChatMessageKind = SurfaceChatMessageKind.Request;
});
```

Use `SurfaceChatDeliveryMode.RequestResponse` only when the command center should await and append the returned `OnMessage` response. Use `SurfaceChatMessageKind.OneWay` only when the receiver is not expected to respond.

The command center defaults to embedded layout mode so Surface fills the host-provided container
instead of owning the browser viewport. Use standalone mode only for apps where `/surface` should
take the whole viewport:

```csharp
builder.AddFabrCoreSurface(options =>
{
    options.CommandCenterLayoutMode = SurfaceCommandCenterLayoutMode.Standalone;
});
```

## Command Center Features

Surface command center features are controlled through `SurfaceOptions`. Keep features disabled unless the host/client deployment should expose them to principals:

```csharp
builder.AddFabrCoreSurface(options =>
{
    // Host API base URL used by discovery and monitor clients.
    // If omitted, AddFabrCoreSurface reads configuration key "FabrCoreHostUrl",
    // then falls back to "http://localhost:5000".
    options.FabrCoreHostUrl = "https://fabrcore-host.example.com";

    options.EnableAgentDirectory = true;
    options.EnableAgentChat = true;
    options.EnableLiveStatus = true;
    options.EnableSharedAgents = true;
    options.EnableAdaptiveCards = true;

    // Feature flag for the Create Agent builder. Default: false.
    options.EnableAgentCreate = true;

    // Shows the right-side diagnostics panel in the command center.
    options.EnableDiagnosticsPanel = true;

    // Adds Surface render/action diagnostics to logs and message args.
    options.EnableDiagnostics = true;

    // Seeds the /surface chat list when no stored principal preferences exist.
    options.DefaultSurfaceAgentHandles.Add("assistant");
});
```

Important flags:

- `FabrCoreHostUrl`: base URL for host API calls such as `/fabrcoreapi/Discovery` and monitor APIs in split deployments.
- `EnableAgentCreate`: enables Create Agent UI and the `/surface/create` route; default is `false`.
- `EnableAgentDirectory`: loads principal-tracked agents into the command center list.
- `EnableSharedAgents`: includes ACL-accessible shared agents.
- `EnableLiveStatus`: refreshes agent health and activity state.
- `EnableAgentChat`: enables the composer and send command flow.
- `EnableAdaptiveCards`: renders `ui.render` Adaptive Card envelopes in chat.
- `EnableDiagnosticsPanel`: shows command center diagnostics UI.
- `EnableDiagnostics`: adds Surface validation/action diagnostics to messages and logs.
- `DefaultSurfaceAgentHandles`: default-pins agent handles or bare aliases into the `/surface` chat list only when principal preferences are missing, such as after localhost storage resets. Existing stored preferences remain authoritative. Bare aliases like `crm-agent` match principal-qualified handles like `demo-principal:crm-agent`.

Create Agent uses host discovery (`GET {FabrCoreHostUrl}/fabrcoreapi/Discovery`) to list agent types, plugins, tools, and alias collisions. The builder still allows manual aliases when discovery fails or an alias is not listed. In the command center, open Create Agent from the Chat panel `...` menu. The direct page route is `/surface/create`; when `EnableAgentCreate` is false, the route shows a disabled-state message instead of the builder.

Host API compatibility note: Surface local code and public APIs should use Principal naming, but current FabrCore host APIs still use `userHandle`, `HandleUtilities.ParseHandle(...).UserHandle`, `IFabrCoreAgentHost.GetUserHandle()`, `x-user`, and `x-user-handle`. Keep those exact upstream names at the transport boundary and assign them to local `principal*` variables when wrapping them in Surface code.

The Create Agent builder produces a full `AgentConfiguration`: `Handle`, `AgentType`, `Models`, `Description`, `SystemPrompt`, `Plugins`, `Tools`, `Streams`, `Args`, `McpServers`, and `ForceReconfigure`. After successful creation, Surface refreshes the agent list, selects the new agent, and returns to chat.

Surface blueprints use `surface.squads` for grouped agent provisioning. A squad has `squadType` set to `swarm`, `orchestrator`, or `task`; use this shape for all new grouped-agent blueprint work. The command center blueprint builder emits `SurfaceSquadDefinition` records and the apply flow reports `SquadsCreated` / `SquadsSkipped`.

```json
{
  "name": "support-workspace",
  "surface": {
    "squads": [
      {
        "squadType": "task",
        "name": "Ops Desk",
        "orchestratorModel": "default",
        "plannerModel": "default",
        "taskOptions": {
          "workerModelName": "default",
          "plannerModelName": "default",
          "maxTaskAttempts": 2,
          "maxValidationAttempts": 2
        },
        "agents": [
          {
            "name": "data-intel",
            "agentType": "data-intel-agent",
            "role": "executor"
          }
        ]
      }
    ]
  }
}
```

The command center hides internal agents by default. It honors FabrCore registry metadata (`[FabrCoreHidden]`) when `IFabrCoreRegistry` is available, and also applies `SurfaceOptions.HiddenAgentTypes` / `HiddenAgentHandles` as a client-side fallback. The built-in `surface` agent is marked hidden and should not appear unless the principal enables the show-hidden toggle.

The command center treats every underscore-prefixed message type as FabrCore system/control traffic. `_status` and `_thinking` update the selected agent's live activity/presence text instead of rendering as normal chat bubbles; if those messages include human-facing progress text, Surface preserves that text in the activity indicator. `_error` is surfaced as an error event. Normal chat transcript entries should come from non-system message types such as `chat` or adaptive-card `ui.render` envelopes.

Unread chat state is shared between `/surface`, `SurfaceChatLink`, and `SurfaceNotify`. Incoming visible transcript items for a non-selected/non-open agent increment that agent's unread count, flash the agent row in chat mode, update `SurfaceNotify`, and flash the link button. Selecting the agent in `/surface`, expanding a link for the agent, opening a target from `SurfaceNotify`, or calling `SurfaceWorkspaceService.MarkAgentSeen(handle)` clears that target. `SurfaceWorkspaceService.MarkAllSeen()` clears all unread counts.

The command center composer follows chat-client keyboard behavior: Enter sends the command, and Shift+Enter inserts a newline.

`SurfaceChatLink.OnMessageReceived` mirrors ChatDock's parent interception hook. It receives the full incoming `AgentMessage` for the link's target agent. Pages can use `MessageType`, `Message`, `Args`, or `Data` to refresh local UI state, for example when an agent emits `ui-update` with a section id or `data-changed` after mutating app data. Returning `true` displays the message in the link panel; returning `false` suppresses it in the link panel only. The shared workspace timeline remains available to `/surface`.

`SurfaceChatLink` should keep parity with the original ChatDock panel chrome: small/medium/large size cycling, a clear-panel-history button, minimize button, ChatDock-style message bubbles, Adaptive Card transcript rendering, and optional manual Create/Reset controls when `CreateAgent` / `AllowReset` are supplied.

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

- Deterministic agent-authored `Action.Execute` and `Action.Submit` actions can dispatch to the app, source agent, or both.
- Planner-generated cards are display-only and must not include `Action.Execute` or `Action.Submit`.
- `Action.OpenUrl` is allowed only for safe URLs.
- `Action.ShowCard` and `Action.ToggleVisibility` remain client-side card behavior.

For agent-authored cards, put FabrCore routing hints in action `data`, for example:

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

Clicked executable actions are sent as `ui.action` messages with the structured action event in `AgentMessage.Data`. The receiving agent or app handler owns verb handling and should respond gracefully to unknown verbs.

## Validation

Surface validates envelope JSON, Adaptive Card type/version, payload size, nesting depth, action type policy, target-agent policy, URL policy, and planner display-only policy. Domain validation remains the responsibility of agents and app/API handlers.

## Useful Assets

- [contracts.md](references/contracts.md)
- [action-routing.md](references/action-routing.md)
- [integration.md](references/integration.md)
- [chat-link.md](references/chat-link.md)
- [producer-side.md](references/producer-side.md)
- [validation-policy.md](references/validation-policy.md)
- [testing.md](references/testing.md)
- [approval-card.json](assets/sample-envelopes/approval-card.json)
- [submit-form-card.json](assets/sample-envelopes/submit-form-card.json)
- [open-url-card.json](assets/sample-envelopes/open-url-card.json)
- [customer-list-card.json](assets/sample-envelopes/customer-list-card.json)
- [fabrcore-surface-config-example.json](assets/templates/fabrcore-surface-config-example.json)
- [surface-blueprint-squads-example.json](assets/templates/surface-blueprint-squads-example.json)
- [surface-chat-link-snippet.razor](assets/templates/surface-chat-link-snippet.razor)
- [surface-notify-snippet.razor](assets/templates/surface-notify-snippet.razor)
- [render-envelope-snippet.cs](assets/templates/render-envelope-snippet.cs)
- [surface-action-helper-snippet.cs](assets/templates/surface-action-helper-snippet.cs)
