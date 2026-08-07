---
name: fabrcore-oss-aug2026
description: >
  Maintain the FabrCore OSS and Forge repository split introduced in August 2026. Use when
  moving or changing projects across C:\repos\FabrCore and C:\repos\fabrcore-v365, deciding
  whether code belongs in OSS or Forge, updating canonical blueprints or squads, changing the
  Cloud Server/connect protocol, consuming OSS packages from the commercial solution, updating
  migration-era build/release files, or preventing old V365 architecture from being reintroduced.
---

# FabrCore OSS August 2026

Preserve a credible standalone OSS platform while keeping hosted administration, fleet
operations, and commercial adapters in Forge.

## Start here

1. Read [references/boundary.md](references/boundary.md).
2. Read `C:\repos\FabrCore\docs\oss-platform-plan.md` for the design rationale.
3. Read `C:\repos\fabrcore-v365\docs\oss-migration.md` when changing the commercial repo.
4. Inspect both worktrees before editing. Preserve unrelated and pre-existing changes.
5. Treat current source and tests as authoritative when a historical plan disagrees with code.

## Repository rule

- Put open runtime, protocol, developer tooling, and self-hosting features in
  `C:\repos\FabrCore`.
- Keep Forge, hosted admin UX, fleet workflows, commercial adapters, and commercial-only
  services in `C:\repos\fabrcore-v365`.
- Never restore migrated project copies to the commercial solution.
- Make open protocol changes in OSS first, pack them, then validate the commercial consumer
  against packages.

## Architecture invariants

- Use `FabrCoreBlueprint` as the canonical blueprint envelope.
- Apply blueprints through the Host-owned CRUD/apply path. Expand package-owned extensions with
  `IBlueprintExpander`.
- Use the top-level `squads` extension array.
- Use `SurfaceSquadType` (`orchestrator`, `task`), `squad.*` messages, and `squad-*` handles.
- Do not reintroduce the removed Swarm runtime (`SurfaceSquadType.Swarm`, the `"swarm"`
  extension, `swarm.*` messages), and do not add `SwarmV2`, `swarm2.*`, `squad2-*`,
  `surface.squads`, or parallel old/new shapes.
- Keep Memory and GraphRAG contracts in `FabrCore.Services.Contracts`.
- Do not link-compile contracts or add type forwarders to the OSS service packages.
- Keep the OSS GraphRAG markdown converter vendor-neutral. Put Vulcan365 conversion in
  `FabrCore.Services.GraphRag.Vulcan365` in the commercial repo.
- Protect Memory and GraphRAG admin endpoints with the `FabrCoreAdmin` bearer policy.
- Keep Cloud Server v2 remote administration outbound-only. Execute commands only against the
  required `FabrCore:HostUrl` with `FabrCore:CloudServer:ApiKey` and strict path/body/header
  controls. Accept that key through the `FabrCoreAdmin` policy only when Cloud Server remote
  administration is enabled. Log a startup warning when a non-loopback host URL carries the key.

## Product boundary

Do not weaken OSS to manufacture commercial value. OSS must remain usable with local
configuration, blueprint provisioning, ACL enforcement, Memory, GraphRAG, and Surface squads.

Forge owns the monetizable operating experience:

- interactive management and admin pages;
- hosted identity and a configured free single-cluster target;
- multi-cluster/fleet selection and entitlement seams;
- environment overlays, rollback, and fleet blueprint distribution;
- hosted monitoring, incident, and verifiable-execution workflows;
- commercial conversion adapters and support.

`FabrCore.Surface.Admin` remains commercial. Base `FabrCore.Surface` remains OSS and must not
gain interactive create/manage routes that duplicate Forge.

## Change workflow

1. Classify every changed project and API with the boundary matrix.
2. Change OSS contracts and implementation first.
3. Update OSS tests, docs, samples, package lists, and release scripts together.
4. Pack the OSS version line to `C:\repos\nuget`.
5. Point the commercial build at that package version with
   `UseLocalFabrCoreSource=false`.
6. Change Forge, Surface.Admin, or commercial adapters.
7. Update both repositories' `docs/skills` and matching `.agents/skills` copies.
8. Scan for stale names, deleted project references, caches, and credentials.

## Validation

```powershell
dotnet build C:\repos\FabrCore\src\FabrCore.sln -c Release
& C:\repos\FabrCore\scripts\Pack-Local.ps1

# Do not run dotnet test on the mixed OSS solution. Run the VSTest projects
# listed in .github/workflows/publish-nuget.yml individually, then run:
dotnet run --project C:\repos\FabrCore\src\FabrCore.Services.Memory.Tests\FabrCore.Services.Memory.Tests.csproj `
  -c Release --no-build --no-restore
dotnet run --project C:\repos\FabrCore\src\FabrCore.Services.GraphRag.Tests\FabrCore.Services.GraphRag.Tests.csproj `
  -c Release --no-build --no-restore

$ossVersion = "<local-version>"
dotnet restore C:\repos\fabrcore-v365\src\FabrCore-V365.slnx `
  /p:UseLocalFabrCoreSource=false `
  /p:FabrCoreOssVersion=$ossVersion `
  /p:RestoreAdditionalProjectSources=C:\repos\nuget
dotnet build C:\repos\fabrcore-v365\src\FabrCore-V365.slnx -c Release --no-restore `
  /p:UseLocalFabrCoreSource=false `
  /p:FabrCoreOssVersion=$ossVersion

# Forge uses Microsoft.Testing.Platform and must run from the src directory.
Push-Location C:\repos\fabrcore-v365\src
dotnet test FabrCore.Forge.Tests\FabrCore.Forge.Tests.csproj -c Release --no-build `
  /p:UseLocalFabrCoreSource=false /p:FabrCoreOssVersion=$ossVersion
Pop-Location

# Surface.Admin still uses VSTest and must run outside src/global.json.
dotnet test C:\repos\fabrcore-v365\src\FabrCore.Surface.Admin.Tests\FabrCore.Surface.Admin.Tests.csproj `
  -c Release --no-build `
  /p:UseLocalFabrCoreSource=false /p:FabrCoreOssVersion=$ossVersion

dotnet publish C:\repos\FabrCore\samples\FabrCore.SampleApp\FabrCore.SampleApp.csproj `
  -c Release -o C:\repos\FabrCore\artifacts\publish\FabrCore.SampleApp
dotnet publish C:\repos\fabrcore-v365\src\FabrCore.Forge.App\FabrCore.Forge.App.csproj `
  -c Release -o C:\repos\fabrcore-v365\artifacts\publish\FabrCore.Forge.App `
  --no-restore /p:UseAppHost=false `
  /p:UseLocalFabrCoreSource=false /p:FabrCoreOssVersion=$ossVersion
```

The OSS tag workflow publishes only the eleven supported OSS libraries; never pack the whole
solution because it also contains samples. The commercial local pack script publishes only
DataIntelligence and the Vulcan365 GraphRAG adapter. Forge and Surface.Admin ship together as
the Forge application image.

Run Memory and GraphRAG MSTest executable projects with `dotnet run`; their .NET 10 test
runner mode is not covered by an ordinary solution-level `dotnet test`. Report SQL/live skips
explicitly.

Before handoff, run `git diff --check` in both repos and verify:

- no live legacy Swarm identifiers;
- no OSS service type forwarders;
- no commercial project references to removed source directories in package mode;
- no tracked cloud cache;
- no real Forge keys, Entra client secrets, or model credentials.

## Security and release

- Keep OSS licensing Apache-2.0.
- Copy code without importing private repository history.
- Replace exposed values in files, but also require external credential rotation because Git
  history retains committed secrets.
- Coordinate the OSS package release before a Forge build that consumes the new version.
- Do not publish or deploy unless the user explicitly requests it.
