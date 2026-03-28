---
name: fabrcore-chatdock
description: >
  FabrCore ChatDock component — floating icon chat panel overlay for Blazor Server, all parameters,
  positions, scoped ChatDock, multiple ChatDocks with ChatDockManager, agent lifecycle, and customization.
  Triggers on: "ChatDock", "ChatDockPosition", "ChatDockManager", "chat panel", "floating chat",
  "chat overlay", "chat icon", "chat UI component", "ChatDock position", "multiple ChatDocks",
  "LazyLoad chat", "OnMessageReceived", "OnMessageSent", "chat dock", "BottomRight", "BottomLeft",
  "chat component", "fabrcore.css".
  Do NOT use for: client setup (AddFabrCoreClient, ClientContext) — use fabrcore-client.
  Do NOT use for: agent development — use fabrcore-agent.
allowed-tools: "Bash(dotnet:*) Bash(mkdir:*) Bash(ls:*) Bash(pwsh:*) Bash(powershell:*) Bash(git:*) Bash(dir:*)"
---

# FabrCore ChatDock Component

The `ChatDock` is a **floating icon button** that expands into a chat panel overlay. When collapsed, it shows a small circular icon (36x36px). When clicked, a chat panel slides in from the configured position. The panel is moved to `document.body` via JS to escape CSS stacking contexts.

## Basic Usage

```razor
@using FabrCore.Client.Components

<ChatDock UserHandle="user1"
          AgentHandle="assistant"
          AgentType="my-agent"
          SystemPrompt="You are a helpful assistant."
          Title="Assistant" />
```

## All Parameters

| Parameter | Type | Required | Default | Description |
|-----------|------|----------|---------|-------------|
| `UserHandle` | `string` | Yes | — | User/client identifier |
| `AgentHandle` | `string` | Yes | — | Agent instance handle (unique per agent) |
| `AgentType` | `string` | Yes | — | Agent type matching `[AgentAlias]` |
| `SystemPrompt` | `string` | No | "You are a helpful AI assistant." | System instructions |
| `Title` | `string` | No | "Assistant" | Display title in panel header |
| `Icon` | `string` | No | "bi bi-chat-dots" | Bootstrap icon class for the floating button |
| `WelcomeMessage` | `string` | No | "How can I help you today?" | Empty state message |
| `Tooltip` | `string` | No | "Open chat" | Hover tooltip on icon |
| `Position` | `ChatDockPosition` | No | `BottomRight` | Panel position (see below) |
| `AdditionalArgs` | `Dictionary<string,string>` | No | null | Extra args for AgentConfiguration |
| `LazyLoad` | `bool` | No | false | Defer agent creation until first expand |
| `Plugins` | `List<string>` | No | null | Plugin aliases to enable |
| `Tools` | `List<string>` | No | null | Standalone tool aliases to enable |
| `OnMessageReceived` | `Func<AgentMessage, Task<bool>>` | No | null | Callback when agent responds. Return `true` to display, `false` to suppress. |
| `OnMessageSent` | `EventCallback<string>` | No | — | Callback when user sends a message |

## Positions

```csharp
public enum ChatDockPosition
{
    BottomRight,    // Floating icon bottom-right, panel slides up (default)
    BottomLeft,     // Floating icon bottom-left, panel slides up
    Right,          // Floating icon right edge, panel slides in from right (full height)
    Left            // Floating icon left edge, panel slides in from left (full height)
}
```

**Responsive:** On mobile (<480px), the panel expands to full width regardless of position.

## Visual States

- **Collapsed**: Circular icon with Bootstrap icon class. Color indicates state:
  - Blue: connected
  - Green pulsing: open
  - Orange pulsing: unread messages
  - Gray: lazy/not loaded
- **Expanded**: Full chat panel with header (title + clear/minimize buttons), scrollable message area with markdown rendering, thinking/typing indicators, and input area with send button.

## Scoped ChatDock

Scope an agent to a specific context by embedding IDs in the handle and system prompt:

```razor
<ChatDock UserHandle="user1"
          AgentHandle="@($"project-{ProjectId}")"
          AgentType="project-agent"
          SystemPrompt="@($"You manage project {ProjectId}. Use this ID automatically.")"
          Title="Project Assistant"
          OnMessageSent="@(async (msg) => { await RefreshData(); StateHasChanged(); })"
          Position="ChatDockPosition.Right" />
```

## Multiple ChatDocks

Use `ChatDockManager` (registered via `AddFabrCoreClientComponents()`) to coordinate multiple instances — **only one can be expanded at a time**:

```razor
<CascadingValue Value="chatDockManager">
    <ChatDock UserHandle="user1" AgentHandle="coding-agent"
              AgentType="code-reviewer" Title="Code Review"
              Position="ChatDockPosition.BottomRight" />
    <ChatDock UserHandle="user1" AgentHandle="writing-agent"
              AgentType="writer" Title="Writing Help"
              Position="ChatDockPosition.BottomLeft" />
</CascadingValue>

@code {
    [Inject] ChatDockManager chatDockManager { get; set; } = default!;
}
```

## Agent Lifecycle

ChatDock manages the full agent lifecycle internally:

1. **Connect**: Gets or creates `IClientContext` via `IClientContextFactory`, subscribes to `AgentMessageReceived`
2. **Check Existing Agent**: Calls `IsAgentTracked()` then `GetAgentHealth()` to check if already configured. For cross-owner agents (handle contains `:`), goes straight to `GetAgentHealth` — the agent won't be in the user's tracked list.
3. **Create Agent (if needed)**: Only calls `context.CreateAgent(agentConfig)` if `IsConfigured == false` **and the agent is owned by the current user** (bare alias handle). Cross-owner agents must be created server-side.
4. **Send Messages**: Uses `context.SendMessage()` (fire-and-forget). Responses arrive via `AgentMessageReceived`.
5. **Message Filtering**: Filters by `FromHandle` (must match expected agent) and `ToHandle` (must be UserHandle). System messages (`_status`, `_error`) handled internally.
6. **Cleanup**: `IDisposable` — unregisters from DockManager, unsubscribes events, disposes context.

## Cross-Owner Agents in ChatDock

```razor
<ChatDock UserHandle="@userId"
          AgentHandle="system:automation_agent-123"
          AgentType="automation-agent" />
```

When `AgentHandle` contains a colon:
- **No auto-creation** — displays error if not configured
- **Response matching** — uses full handle as `FromHandle`
- **ACL enforcement** — `ClientGrain` verifies `Message` permission

## ChatDock Features

- **Markdown rendering** — Uses Markdig for rich message display
- **Thinking indicators** — Shows when agent is processing (auto-fades after 5s)
- **Typing indicators** — Bouncing dots animation while waiting
- **Health status** — Displays agent health (Healthy/Degraded/Unhealthy/NotConfigured)
- **Lazy loading** — Defer agent creation until first opened (`LazyLoad="true"`)
- **Unread badges** — Shows unread message count when minimized
- **Keyboard shortcuts** — Enter to send, Shift+Enter for newline
- **Panel escape** — Panel moved to `document.body` via JS to avoid stacking context issues

## CSS Customization

```css
--chat-dock-primary: #3b82f6;
--chat-dock-width: 380px;
--chat-dock-icon-size: 36px;
```

## Static Assets

ChatDock requires FabrCore.Client static assets:

```html
<!-- In App.razor or _Host.cshtml -->
<link href="_content/FabrCore.Client/fabrcore.css" rel="stylesheet" />
```

The JS module (`fabrcore.js`) is loaded dynamically by the component.
