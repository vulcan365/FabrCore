# SurfaceChatLink

`SurfaceChatLink` is the Surface-owned ChatDock-like component for pages that are not the `/surface` command center. It renders a floating icon button that opens a chat panel for a Surface agent.

Use it instead of referencing `FabrCore.Client.ChatDock`. `FabrCore.Surface` must remain independent from `FabrCore.Client`.

The icon itself is not viewport-fixed. It renders inline wherever the component is placed in the parent page. `Position` controls the opened chat panel only.

## Basic Usage

```razor
@using FabrCore.Surface.Components

<SurfaceChatLink AgentHandle="assistant"
                 Title="Assistant"
                 Tooltip="Open assistant"
                 OnMessageReceived="HandleAgentMessageAsync"
                 InitialSize="SurfaceChatLinkSize.Medium"
                 Position="SurfaceChatLinkPosition.BottomRight" />
```

`AgentHandle` can be a bare alias (`assistant`) or a full handle (`principal1:assistant`, `system:assistant`). Bare aliases are resolved against the current principal id from `ISurfacePrincipalContextProvider`.

When `AgentHandle` is omitted, the link follows the currently selected agent in the scoped `SurfaceWorkspaceService`. This is useful in layouts that show `/surface` side by side with other content.

## Registration

Register Surface components and include the stylesheet:

```csharp
builder.AddFabrCoreSurface(options =>
{
    options.EnableAgentChat = true;
    options.EnableAdaptiveCards = true;
});

builder.Services.AddFabrCoreSurfaceComponents();
```

```html
<link href="_content/FabrCore.Surface/surface.css" rel="stylesheet" />
```

`SurfaceChatLink` uses the same principal-bound `SurfaceWorkspaceService` as `/surface`, so no additional component-specific service registration is required.

## Shared Transcript

`/surface`, `SurfaceChatLink`, and `SurfaceNotify` must use the same principal-scoped `SurfaceWorkspaceService`. `/surface` and `SurfaceChatLink` render the same chat history for a given principal and agent. The shared transcript includes:

- principal messages sent from `/surface`
- principal messages sent from `SurfaceChatLink`
- agent chat replies
- `_error` messages
- Adaptive Card `ui.render` messages

System/control messages such as `_thinking` and `_status` update activity state instead of becoming normal chat bubbles.

For implementation work, prefer `SurfaceWorkspaceService.GetTimelineForAgent(handle)` over filtering `Timeline` ad hoc. Send from a link with `SurfaceWorkspaceService.SendChatAsync(message, targetAgentHandle)` so the command-center selected agent is not changed. Read aggregate notification state through `TotalUnreadCount` and `GetUnreadSummaries()` instead of recomputing unread counts in components.

## Parent Message Interception

Use `OnMessageReceived` when the page hosting the link needs to react to agent messages. This mirrors the old ChatDock hook.

```razor
<SurfaceChatLink AgentHandle="crm-agent"
                 Title="CRM Assistant"
                 OnMessageReceived="HandleAgentMessageAsync" />

@code {
    private async Task<bool> HandleAgentMessageAsync(AgentMessage message)
    {
        if (string.Equals(message.MessageType, "ui-update", StringComparison.OrdinalIgnoreCase))
        {
            await RefreshSectionAsync(message.Message);
            return false;
        }

        if (string.Equals(message.MessageType, "data-changed", StringComparison.OrdinalIgnoreCase))
        {
            await ReloadDataAsync();
            return false;
        }

        return true;
    }
}
```

The callback receives the full `AgentMessage`, including `MessageType`, `Message`, `Args`, `Data`, and handles. Return `true` to display the message in the link panel. Return `false` to suppress the message in that link panel after the parent has handled it.

Suppression is local to `SurfaceChatLink`. The shared `SurfaceWorkspaceService` timeline still records the message so `/surface` can show the complete command-center history.

## Adaptive Cards

`SurfaceChatLink` renders `SurfaceTimelineItemKind.AdaptiveCard` entries with `SurfaceAdaptiveCardHost`. This keeps card rendering, validation, and action dispatch identical to `/surface`.

If a card action should round-trip to an agent, use the normal Surface action routing fields in action `data`, such as `routeTo`, `targetAgent`, and `messageTemplate`.

## State Cues

The link follows ChatDock-style visual state:

- connected: initialized and ready
- open: panel expanded
- unread: visible incoming transcript items arrived while the link was collapsed

The `/surface` agent list and `SurfaceNotify` also display unread state in chat mode. Selecting an agent in `/surface`, opening a link for that agent, or opening a target from `SurfaceNotify` clears that agent's unread count. `SurfaceNotify` right-click inspection alone does not clear unread counts.

## SurfaceNotify

`SurfaceNotify` is the Surface-owned notification component for app headers. It renders an inline icon button with a numeric unread badge, left-click navigation to `/surface`, and a right-click unread menu.

```razor
@using FabrCore.Surface.Components

<SurfaceNotify SurfacePath="/surface"
               Icon="bi bi-bell"
               Tooltip="Open Surface" />
```

Use it in a layout/header, not as a chat composer. It does not render the transcript. It exposes unread state and navigation only.

## Panel Layout

The panel should keep parity with the original ChatDock window:

- header with title, size-cycle button, clear button, and minimize button
- three sizes: small, medium, and large
- bottom-left, bottom-right, left-edge, and right-edge panel positioning
- ChatDock-style principal/agent message bubbles with avatars
- Adaptive Card transcript entries rendered with `SurfaceAdaptiveCardHost`
- input wrapper with send icon button and Enter-to-send behavior

The clear button suppresses the current transcript in that link panel only. It does not erase agent memory and does not remove messages from `/surface`.

## Parameters

Important parameters:

- `AgentHandle`: optional target agent alias/full handle. Omit to follow the selected Surface agent.
- `Title`: optional panel title. Defaults to the agent display name.
- `Icon`: Bootstrap icon class for the floating button. Default is `bi bi-chat-dots`.
- `Tooltip`: collapsed button tooltip.
- `WelcomeMessage`: empty transcript message.
- `Position`: `BottomRight`, `BottomLeft`, `Right`, or `Left`.
- `InitialSize`: `Small`, `Medium`, or `Large`. The principal can cycle sizes from the panel header.
- `OnMessageSent`: callback after a message is accepted by the workspace send path.
- `OnMessageReceived`: callback for incoming agent messages. Return `false` to suppress the message in the link panel after processing it.
- `CreateAgent`: optional manual provisioning delegate. When supplied, the link shows a Create button. The delegate is called only after a principal click.
- `AllowExternalAgent`: defaults to `true`. When enabled, a link with `CreateAgent` accepts an already configured healthy target even if the page or host provisioned it out-of-band. Set it to `false` for strict manual-create flows where an untracked agent should keep chat disabled until the Create action succeeds.
- `AllowReset`: optional reset action for tracked agents. Reset uses the existing `ISurfacePrincipalContext.ResetAgent` path; hard eviction is not exposed by `SurfaceChatLink`.
- `NotCreatedMessage`, `NotReadyMessage`, and `ReadyMessage`: optional lifecycle copy. Defaults use the component `Title`, target display name, or `AgentHandle` instead of generic "Assistant"; custom messages can include `{Agent}` or `{0}` placeholders.

## Page-Scoped Agents

Use `CreateAgent` when the page should supply the app-specific agent recipe but the link should own the chat lifecycle UI. By default, the link also works when the page has already created the agent somewhere else:

```razor
<SurfaceChatLink AgentHandle="customer-assistant"
                 Title="Customer Assistant"
                 CreateAgent="CreateCustomerAssistantAsync"
                 AllowReset="true" />

@code {
    private Task<AgentHealthStatus> CreateCustomerAssistantAsync(
        SurfaceChatLinkCreateAgentContext context)
    {
        return context.PrincipalContext.CreateAgent(new AgentConfiguration
        {
            Handle = context.AgentAlias,
            AgentType = "customer-agent",
            Models = "default",
            Description = "Customer page assistant",
            ForceReconfigure = false
        });
    }
}
```

The component initializes the principal-scoped workspace, checks whether the target is healthy, and shows the create/reset controls above the composer. It does not auto-create on load. If `customer-assistant` is already configured and healthy, the composer is enabled and the Create button is hidden. If you want the previous strict behavior, set `AllowExternalAgent="false"` so the component requires the target to be tracked by the principal context before enabling chat.

## Testing Checklist

When changing `SurfaceChatLink` or shared chat state:

- Verify messages sent from the link appear in `/surface`.
- Verify messages sent from `/surface` appear in the link.
- Verify the icon stays where the component is placed and only the opened panel is positioned.
- Verify small/medium/large panel sizing and clear-panel-history behavior.
- Verify `ui.render` Adaptive Cards render in both views.
- Verify card actions still dispatch through `SurfaceAdaptiveCardHost`.
- Verify `OnMessageReceived` receives custom message types such as `ui-update` and `data-changed`.
- Verify returning `false` from `OnMessageReceived` suppresses the item in the link panel without removing it from `/surface`.
- Verify unread flashing appears for collapsed/non-selected agents and clears when the agent is selected or the link opens.
- Verify manual `CreateAgent` renders the Create button without auto-creating, calls the delegate only on click, refreshes agent state, and enables chat after healthy creation.
- Verify `CreateAgent` with the default `AllowExternalAgent="true"` enables chat for an existing healthy agent created outside the link.
- Verify `AllowExternalAgent="false"` keeps strict manual-create behavior for untracked targets.
- Verify `AllowReset` calls `ResetAgent` for tracked targets and leaves hard eviction out of the component.
