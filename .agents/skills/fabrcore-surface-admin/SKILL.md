---
name: fabrcore-surface-admin
description: Build, use, extend, or troubleshoot FabrCore.Surface.Admin, the optional FabrCore Surface Razor class library for admin-only pages such as /surface/agents, /surface/admin, /surface/admin/verifiable-execution, /surface/admin/incidents, agent operations, monitor views, GraphRAG administration, Surface blueprint administration, blueprint builder workflows, verifiable execution, and incident investigation. Use when working on the Admin RCL split from FabrCore.Surface or wiring admin routes/components into a Blazor host.
---

# FabrCore Surface Admin

`FabrCore.Surface.Admin` is the opt-in administrative Razor class library for Surface. Use it for pages and workflows that typical Surface principals should not receive from the base `FabrCore.Surface` package.

## Boundaries

- Keep `/surface` chat, `SurfaceChatLink`, `SurfaceNotify`, Adaptive Card rendering, contracts, validation, and shared command-center services in `FabrCore.Surface`.
- Keep `/surface/agents`, `/surface/admin`, `/surface/admin/verifiable-execution`, and `/surface/admin/incidents` routable pages in `FabrCore.Surface.Admin`.
- Reuse shared services from `FabrCore.Surface.CommandCenter`, `FabrCore.Surface.Identity`, `FabrCore.Surface.Services`, and `FabrCore.Surface.Ai.*` instead of duplicating principal context, squad, or blueprint logic.
- Do not hardcode admin links into the base `/surface` command center. Use the shared Surface navigation components; `AddFabrCoreSurfaceAdminComponents()` is the opt-in that enables Agents, Admin, Execution, and Incidents links.
- Keep repo-local skills under `skills/**` excluded from package output, matching `FabrCore.Surface`.

## Host Integration

Admin-capable hosts should reference both packages and explicitly opt into admin services and routes:

```csharp
using FabrCore.Surface;
using FabrCore.Surface.Admin;

builder.AddFabrCoreSurfaceFromConfig("fabrcore-surface.json", "default");
builder.Services.AddFabrCoreSurfaceAdminComponents();

app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode()
    .AddFabrCoreSurfaceAdminRoutes();
```

`AddFabrCoreSurfaceAdminComponents()` calls the base Surface component registration, then enables admin navigation. `AddFabrCoreSurfaceAdminRoutes()` maps both the Admin route assembly and the base Surface route assembly, so it is enough for Admin-capable hosts. The FabrCore Surface route extensions are idempotent: chaining `.AddFabrCoreSurfaceRoutes()` and `.AddFabrCoreSurfaceAdminRoutes()` on the same `MapRazorComponents` builder, in either order or repeatedly, registers each assembly once. Never register the Surface or Admin component assemblies through the raw `.AddAdditionalAssemblies(...)` API — Razor component discovery throws `InvalidOperationException: Assembly already defined` at startup when a component assembly is added twice, and the raw API does not dedupe.

The app router must search both assemblies for enhanced navigation:

```razor
<Router AppAssembly="typeof(Program).Assembly"
        AdditionalAssemblies="[typeof(FabrCore.Surface.Components.SurfaceCommandCenter).Assembly, typeof(FabrCore.Surface.Admin.Components.SurfaceAdminPage).Assembly]">
```

## Pages

- `Components/SurfaceAgentsPage.razor` owns `/surface/agents`: agent catalog, running agent operations, monitoring, token usage, messages, events, errors, and capture configuration.
- `Components/SurfaceAdminPage.razor` owns `/surface/admin`: GraphRAG admin, document ingestion, scope/query/status views, Surface blueprint import/export/apply, and blueprint builder workflows.
- `Components/SurfaceVerifiableExecutionPage.razor` and `SurfaceVerifiableExecutionDetailPage.razor` own `/surface/admin/verifiable-execution` and trace detail routes.
- `Components/SurfaceIncidentWorkbenchPage.razor` owns `/surface/admin/incidents`.

## Navigation Behavior

Surface and Admin can be loaded independently by consuming hosts:

- Surface-only hosts call `AddFabrCoreSurfaceComponents()` and `.AddFabrCoreSurfaceRoutes()`. Shared nav shows Chat only.
- Admin-capable hosts call `AddFabrCoreSurfaceAdminComponents()` and `.AddFabrCoreSurfaceAdminRoutes()`. Shared nav shows Chat plus Agents, Admin, Execution, and Incidents.
- Admin pages should use `SurfaceTopbarNav` and `SurfaceCommandRail`; do not duplicate nav markup in each page.

## Principal-Handle Work

The admin package is the right place for cross-principal or selected-principal administration.

- Prefer a freeform principal-handle textbox until the host exposes a trusted principal directory.
- Preserve current-principal fallback through `ISurfacePrincipalContextProvider`.
- Keep principal-handle values explicit in Admin code. When calling current FabrCore host APIs, still send the exact upstream headers `x-user` and `x-user-handle`; do not invent `x-principal` headers until FabrCore changes the wire contract.
- Treat "all principals" views as admin-only and avoid adding them to base Surface UI.

## Principal Naming

- Use `SurfacePrincipalContext`, `ISurfacePrincipalContextProvider`, `PrincipalId`, and `PrincipalHandle` in Admin-facing code and UI.
- Use `ISurfacePrincipalContext` / `ISurfacePrincipalContextFactory` for Surface message context APIs.
- Do not add new Admin APIs, route query parameters, labels, or tests that use Surface `Owner*`, `UserHandle`, or `ClientContext` naming.
- Query parameters should use `?principal=...`, not `?user=...`.
- Upstream FabrCore still exposes `IFabrCoreAgentHost.GetUserHandle()` and host API headers named `x-user` / `x-user-handle`; keep those names only at the boundary and assign their values to local `principal*` variables.

## Validation

For local changes, run:

```powershell
dotnet build src\FabrCore.Surface.Admin\FabrCore.Surface.Admin.csproj
dotnet build src\FabrCore.Experimental.SurfaceApp\FabrCore.Experimental.SurfaceApp.csproj
dotnet pack src\FabrCore.Surface.Admin\FabrCore.Surface.Admin.csproj --configuration Release --output C:\tmp\fabrcore-pack-check
```

Run `dotnet test src\FabrCore.Surface.Tests\FabrCore.Surface.Tests.csproj` when shared Surface behavior changes. If those tests fail in Orleans host loading before reaching admin assertions, report the exact `TypeLoadException` or host error separately from Admin RCL build status.
