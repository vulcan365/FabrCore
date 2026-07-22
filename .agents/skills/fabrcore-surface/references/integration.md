# Blazor Integration

For all-in-one Host + Blazor apps, register Surface from one config definition:

```csharp
builder.AddFabrCoreSurfaceFromConfig("fabrcore-surface.json", "crm-demo");
builder.Services.AddFabrCoreSurfaceComponents();
```

Map the routable command center from the Surface Razor class library:

```csharp
app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode()
    .AddFabrCoreSurfaceRoutes();
```

The packaged `/surface` page declares `InteractiveServer` render mode on the page itself. A
consuming app does not need to make its entire top-level `<Routes>` component interactive just to
make Surface page buttons work.

Make sure the app router also searches the Surface component assembly:

```razor
<Router AppAssembly="typeof(Program).Assembly"
        AdditionalAssemblies="[typeof(FabrCore.Surface.Components.SurfaceCommandCenter).Assembly]">
```

This router setting preserves enhanced navigation between host-app routes and Surface routes. Direct
requests to Surface pages are still mapped by `.AddFabrCoreSurfaceRoutes()`.

Admin-capable hosts can opt into `FabrCore.Surface.Admin` for `/surface/agents`, `/surface/admin`, `/surface/admin/verifiable-execution`, and `/surface/admin/incidents`:

```csharp
builder.Services.AddFabrCoreSurfaceAdminComponents();

app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode()
    .AddFabrCoreSurfaceAdminRoutes();
```

Also add `typeof(FabrCore.Surface.Admin.Components.SurfaceAdminPage).Assembly` to the router's
`AdditionalAssemblies` list.

`AddFabrCoreSurfaceAdminRoutes()` maps both the Admin route assembly and the base Surface command
center route assembly, so it is the only route extension an Admin-capable host needs. The FabrCore
Surface route extensions are idempotent: chaining `.AddFabrCoreSurfaceRoutes()` and
`.AddFabrCoreSurfaceAdminRoutes()` on the same `MapRazorComponents` builder, in either order or
repeatedly, registers each assembly once. Never register the Surface or Admin component assemblies
through the raw `.AddAdditionalAssemblies(...)` API — Razor component discovery throws
`InvalidOperationException: Assembly already defined` at startup when the same component assembly
is added twice, and the raw API does not dedupe. Always use the FabrCore route extensions for these
assemblies.

Surface navigation is capability-driven:

- `AddFabrCoreSurfaceComponents()` enables only Surface-owned navigation. `/surface` shows Chat and
  does not show Agents, Admin, Execution, or Incidents.
- `AddFabrCoreSurfaceAdminComponents()` calls the Surface component registration and then enables
  Admin navigation. The shared topbar and command rail show Chat plus Agents, Admin, Execution, and
  Incidents.
- If a host maps only Surface routes, do not add the Admin assembly to the router. If a host maps
  Admin routes, add the Admin assembly to the router so enhanced navigation can discover those pages.

This exposes `/surface` as the principal-to-agent command center. The host app remains responsible for authentication and authorization; Surface resolves the principal id through `ISurfacePrincipalContextProvider`.

Command center chat separates delivery from response expectation. By default it uses fire-and-forget delivery while still sending `MessageKind.Request`, so agents can process normal chat work and publish progress, final responses, and Adaptive Card render messages asynchronously:

```csharp
builder.AddFabrCoreSurface(options =>
{
    options.CommandCenterChatDeliveryMode = SurfaceChatDeliveryMode.FireAndForget;
    options.CommandCenterChatMessageKind = SurfaceChatMessageKind.Request;
});
```

Use `SurfaceChatDeliveryMode.RequestResponse` only when the selected agent returns the human-facing chat response directly from `OnMessage` and the command center should append that returned response. Use `SurfaceChatMessageKind.OneWay` only when the receiver is not expected to respond.

## Command Center Feature Config

The full command center exposes `/surface`. It also maps `/surface/create` for the Create Agent builder. Create Agent is feature-gated and disabled by default:

```csharp
builder.AddFabrCoreSurface(options =>
{
    options.FabrCoreHostUrl = "https://fabrcore-host.example.com";
    options.EnableAgentCreate = true;
});
```

`FabrCoreHostUrl` is used for host API calls such as discovery. If `options.FabrCoreHostUrl` is not set, `AddFabrCoreSurface` reads configuration key `FabrCoreHostUrl`, then falls back to `http://localhost:5000`.

When enabled, principals open Create Agent from the Chat panel `...` menu or by visiting `/surface/create`. The builder calls `GET {FabrCoreHostUrl}/fabrcoreapi/Discovery` to discover agent types, plugins, tools, and collisions, then creates the agent through the current `ISurfacePrincipalContext`. The builder supports the full `AgentConfiguration`: handle, type, model, prompt, description, plugins, tools, args, streams, MCP servers, and force reconfigure.

Surface blueprints can provision grouped agents as squads. Use `surface.squads` with `squadType` values `swarm`, `orchestrator`, or `task` for all new grouped-agent blueprint work.

```json
{
  "name": "support-workspace",
  "surface": {
    "squads": [
      {
        "squadType": "orchestrator",
        "name": "Support Desk",
        "orchestratorModel": "default",
        "agents": [
          {
            "name": "triage",
            "agentType": "triage-agent",
            "role": "executor"
          }
        ]
      }
    ]
  }
}
```

The command center blueprint builder emits this shape and the apply result reports `SquadsCreated` and `SquadsSkipped`.

Feature flags commonly used by command center deployments:

```csharp
builder.AddFabrCoreSurface(options =>
{
    options.EnableAgentDirectory = true;
    options.EnableAgentChat = true;
    options.EnableLiveStatus = true;
    options.EnableSharedAgents = true;
    options.EnableAdaptiveCards = true;
    options.EnableAgentCreate = false;
    options.EnableDiagnosticsPanel = false;
    options.EnableDiagnostics = false;
});
```

If `/surface/create` shows a disabled-state message, set `EnableAgentCreate = true` in the client deployment and restart the app.

This maps the selected definition into producer planning options and consumer validation/render policy.

For split client-only apps, register Surface in the Blazor/FabrCore app:

```csharp
builder.AddFabrCoreSurface(options =>
{
    options.MaxAdaptiveCardVersion = "1.6";
});

builder.Services.AddFabrCoreSurfaceComponents();
```

Useful policy options:

```csharp
builder.AddFabrCoreSurface(options =>
{
    options.MaxAdaptiveCardVersion = "1.6";
    options.MaxPayloadBytes = 64 * 1024;
    options.MaxDepth = 64;
    options.AllowHttpUrls = false;
    options.EnableDiagnostics = true;
});
```

Add the stylesheet:

```html
<link href="_content/FabrCore.Surface/surface.css" rel="stylesheet" />
```

Render the receiving surface:

```razor
<DynamicAgentSurface PrincipalHandle="@userId" MaxItems="5" />
```

The full command center is available at `/surface` when the app maps the Surface route assembly. It uses the same principal context, adaptive card contracts, validation, renderer, and action dispatcher as `DynamicAgentSurface`.

## SurfaceChatLink

Use `SurfaceChatLink` when a page needs a ChatDock-like floating chat entry point without navigating into `/surface`:

```razor
@using FabrCore.Surface.Components

<SurfaceChatLink AgentHandle="assistant"
                 Title="Assistant"
                 OnMessageReceived="HandleAgentMessageAsync"
                 InitialSize="SurfaceChatLinkSize.Medium"
                 Position="SurfaceChatLinkPosition.BottomRight" />
```

`SurfaceChatLink` uses the principal resolved by `ISurfacePrincipalContextProvider` and the shared scoped `SurfaceWorkspaceService`. It does not create a separate local chat log. Messages sent from the link, messages sent from `/surface`, agent replies, and `ui.render` Adaptive Card messages all appear in the same per-agent transcript.

When `AgentHandle` is omitted, the link follows the currently selected `/surface` agent in the same Blazor circuit. When `AgentHandle` is a bare alias, Surface prefixes it with the current principal id. Use a fully qualified handle such as `system:automation-agent` for shared/cross-principal agents.

The icon renders inline at the component location. `Position` controls the opened chat window, not the icon placement.

The link renders Adaptive Card transcript items with `SurfaceAdaptiveCardHost`, so `Action.Execute` and `Action.Submit` use the same app/agent action dispatch pipeline as `/surface`.

Use `OnMessageReceived` when the page hosting the link needs to process agent messages itself. The callback receives the full `AgentMessage`; return `false` after handling app-only messages such as `ui-update` or `data-changed` if they should not appear in the link panel. The shared `/surface` transcript still records the message.

The link button follows ChatDock-style state cues:

- `is-connected`: the principal workspace has initialized.
- `is-open`: the panel is expanded.
- `has-unread`: visible incoming transcript items arrived while the link was collapsed.

The `/surface` agent list uses the same unread state. Selecting an agent in `/surface`, or opening a `SurfaceChatLink` for that agent, clears that agent's unread count.

The panel keeps ChatDock parity with small/medium/large size cycling, clear-panel-history, minimize, principal/agent bubbles, and an icon send button.

## Principal Naming

Use Principal terminology for all Surface-facing integration points:

- `ISurfacePrincipalContext` / `ISurfacePrincipalContextFactory`
- `ISurfacePrincipalContextProvider`
- `SurfacePrincipalContext` with `PrincipalId`
- `DynamicAgentSurface PrincipalHandle="..."`
- `SurfaceActionContext.PrincipalContext`
- `SurfaceChatLinkCreateAgentContext.Principal` and `.PrincipalContext`

Do not introduce new `SurfaceClientContext`, `SurfaceOwnerContext`, `OwnerId`, or `UserHandle` Surface APIs. At the upstream host boundary, keep FabrCore's current wire names (`userHandle`, `x-user`, `x-user-handle`, `HandleUtilities.ParseHandle(...).UserHandle`, and `IFabrCoreAgentHost.GetUserHandle()`), then map those values into local principal variables.

See [chat-link.md](chat-link.md) and [surface-chat-link-snippet.razor](../assets/templates/surface-chat-link-snippet.razor) for a reusable page snippet.

## SurfaceNotify

Use `SurfaceNotify` when a consuming app needs a compact Surface notification icon in its own header or toolbar:

```razor
@using FabrCore.Surface.Components

<SurfaceNotify />
```

`SurfaceNotify` uses the principal resolved by `ISurfacePrincipalContextProvider` and the shared scoped `SurfaceWorkspaceService`. It displays `SurfaceWorkspaceService.TotalUnreadCount` across agents and squads and reads details from `GetUnreadSummaries()`.

Left click navigates to `SurfacePath` (default `/surface`) without marking messages seen. Right click opens the unread popup. Clicking outside the popup closes it without clearing unread state. Opening a target from the popup selects the agent/squad, clears that target through existing workspace selection behavior, and navigates to `/surface`. Mark-seen actions call `MarkAgentSeen(handle)` or `MarkAllSeen()`.

Important parameters:

- `SurfacePath`: route to open on left click. Default is `/surface`.
- `Icon`: Bootstrap icon class. Default is `bi bi-bell`.
- `Tooltip`: button tooltip when there are no unread messages.
- `CssClass`: optional wrapper class for host header layout.
- `MaxVisibleItems`: maximum unread rows shown before the menu summarizes the remainder.

See [surface-notify-snippet.razor](../assets/templates/surface-notify-snippet.razor) for a reusable layout snippet.

The component listens for `ui.render` messages with the Adaptive Card Surface media type, expands `card` + `data`, validates the result, and renders through the Adaptive Cards browser renderer.

`DynamicAgentSurface` loads `_content/FabrCore.Surface/adaptiveCardsSurface.js`. The module uses `window.AdaptiveCards` if the app has already loaded the Adaptive Cards renderer; otherwise it loads the renderer from the configured CDN fallback in that JS module.

Register a real `ISurfaceActionRegistry` in the app if Adaptive Card actions should perform trusted app-side work.

## Empty Surface Troubleshooting

If `/surface` renders markup like a page header followed by an empty `<div class="fabrcore-surface"></div>`, the request is not reaching the packaged command center. That markup is the shell for `DynamicAgentSurface`; it only fills after the current `PrincipalHandle` receives `ui.render` messages.

Check for these common causes:

- The host app has its own `@page "/surface"` component shadowing the Surface command center.
- `app.MapRazorComponents<App>()` is missing `.AddFabrCoreSurfaceRoutes()`.
- `Routes.razor` is missing `AdditionalAssemblies="[typeof(FabrCore.Surface.Components.SurfaceCommandCenter).Assembly]"`.
- The app intentionally embedded `<DynamicAgentSurface>` directly, but no agent has sent a `ui.render` message with `DataType = application/vnd.fabrcore.surface.adaptive-card+json` to that principal handle.

The packaged command center should render elements with classes such as `surface-command-center`, `surface-topbar`, `surface-agent-list`, and `surface-agent-workspace`, not just the bare `fabrcore-surface` container.

## Inert Button Troubleshooting

If Surface pages render and top-level links navigate, but tabs, refresh buttons, menus, and send
buttons do nothing, the page was rendered statically without an attached Blazor circuit.

For package versions `0.1.24` and later, the packaged Surface pages opt into `InteractiveServer`
render mode directly. The consuming app still needs the normal Blazor Server infrastructure:

```csharp
builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents();

app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode()
    .AddFabrCoreSurfaceRoutes();
```

The app shell also needs the Blazor web script, usually:

```html
<script src="@Assets["_framework/blazor.web.js"]"></script>
```

Embedded Surface components such as `SurfaceChatLink`, `SurfaceNotify`, or `DynamicAgentSurface`
still run in the render mode of the host page/component that contains them.

Surface-owned command-center links opt out of enhanced navigation, and `SurfaceNotify` force-loads
the Surface path. This keeps packaged Surface routes reachable even when a host app keeps its root
`<Routes>` static or omits Surface from client-side router discovery.

For package versions `0.1.27` and later, the command center supports explicit standalone and embedded
layout modes. Embedded mode is the default: it fills a bounded parent container and does not force
`html`/`body` overflow.

```csharp
builder.AddFabrCoreSurface(options =>
{
    options.CommandCenterLayoutMode = SurfaceCommandCenterLayoutMode.Standalone;
});
```

Use standalone mode only when `/surface` should own the whole browser viewport. Embedded mode is for
apps with their own sidebar, sticky topbar, and content area. The host still needs to provide a
bounded content region, but Surface no longer depends on app-specific class names such as
`.main-wrapper` or `.main-content`.

## Target Handles

The built-in `surface` agent can deliver `ui.render` directly to the intended principal handle when the calling message includes:

```csharp
message.Args["targetHandle"] = "demo-principal";
```

or:

```csharp
message.Args["surface:TargetHandle"] = "demo-principal";
```

An envelope can also set:

```json
"metadata": {
  "targetHandle": "demo-principal"
}
```
