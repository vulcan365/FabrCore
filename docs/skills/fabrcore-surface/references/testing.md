# Testing

Surface tests should cover:

- `AdaptiveCardSurfaceEnvelope` serialization and deserialization.
- fenced `fabrcore-adaptive-card-surface` extraction.
- card template expansion with `data`.
- validation for version, action type, URL, payload size, and nesting depth.
- `Action.Execute` and `Action.Submit` routing to app and agent.
- client-only actions such as `Action.OpenUrl`, `Action.ShowCard`, and `Action.ToggleVisibility`.
- producer-side and consumer-side DI registration.

Run:

```powershell
dotnet test C:\repos\FabrCore\src\FabrCore.sln
```

For Surface-only iteration, run:

```powershell
dotnet test C:\repos\FabrCore\src\FabrCore.Surface.Tests\FabrCore.Surface.Tests.csproj
```

Before calling a Surface refactor complete, also run:

```powershell
dotnet build C:\repos\FabrCore\src\FabrCore.sln
```
