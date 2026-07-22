# Testing

Surface tests should cover:

- `AdaptiveCardSurfaceEnvelope` serialization and deserialization.
- fenced `fabrcore-adaptive-card-surface` extraction.
- planner prompts and planner validation keep generated cards display-only.
- card template expansion with `data`.
- validation for version, action type, URL, payload size, and nesting depth.
- deterministic agent-authored `Action.Execute` and `Action.Submit` routing to app and agent.
- unknown business verbs are not blocked by Surface validation and are handled by the receiving agent or app handler.
- client-only actions such as `Action.OpenUrl`, `Action.ShowCard`, and `Action.ToggleVisibility`.
- producer-side and consumer-side DI registration.
- command-center, `SurfaceChatLink`, and `SurfaceNotify` shared transcript/unread behavior.
- explicit-agent chat sends via `SurfaceWorkspaceService.SendChatAsync(message, targetAgentHandle)` without changing the selected `/surface` agent.
- unread state for incoming messages, including total unread summaries, clearing via agent selection, `SurfaceChatLink` expansion, `SurfaceNotify` target open, `MarkAgentSeen(handle)`, and `MarkAllSeen()`.
- Adaptive Card transcript rendering in both `/surface` and `SurfaceChatLink`.
- `SurfaceChatLink.OnMessageReceived` interception for custom message types such as `ui-update` and `data-changed`.
- `SurfaceChatLink.OnMessageReceived` returning `false` suppresses display in the link panel without removing the shared `/surface` transcript item.
- `SurfaceChatLink` icon placement stays inline where rendered; only the panel uses `Position`.
- `SurfaceChatLink` small/medium/large size cycling and clear-panel-history behavior.
- `SurfaceNotify` icon placement stays inline where rendered, left click opens `/surface`, right click opens the menu, outside click closes it, and menu inspection/close does not clear unread state.

Run:

```powershell
dotnet test C:\repos\FabrCore-V365\src\FabrCore-V365.slnx
```

For Surface-only iteration, run:

```powershell
dotnet test C:\repos\FabrCore-V365\src\FabrCore.Surface.Tests\FabrCore.Surface.Tests.csproj
```

Before calling a Surface refactor complete, also run:

```powershell
dotnet build C:\repos\FabrCore-V365\src\FabrCore-V365.slnx
```
