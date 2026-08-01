# FabrCore OSS Platform Plan — everything but Forge, and the Forge business model

> Status: **Proposal** (2026-07-29). Source-verified against `C:\repos\FabrCore-V365` and this repo. This is the umbrella plan for consolidating the commercial repo into OSS FabrCore; it incorporates and sequences [memory-graphrag-oss-plan.md](memory-graphrag-oss-plan.md) (Memory/GraphRag move) and aligns with [harness-adoption-plan.md](harness-adoption-plan.md) (native harness). Where those docs' "stays commercial" lists conflict with this one, **this doc wins**.
>
> **The business model this plan implements:**
> - **OSS FabrCore = everything a developer needs to build agents and chat with them, standalone.** Runtime + SDK + native harness + Memory + GraphRag (optional SQL packages) + the Surface web UI (chat, Command Center, **blueprint-driven agent/squad configuration**) + M365 channel + sample app. No account required, no cloud dependency, no SQL required for the core.
> - **Forge (commercial) = operate and govern.** The cloud config server *plus the admin console* — `FabrCore.Surface.Admin` migrates into the Forge product rather than OSS. Forge ships **cloud-hosted, or on-prem for enterprise** — same console, same open protocol. Funnel: build free & standalone → create a Forge account → free single-cluster admin tier → paid multi-cluster, ACL/team management, incident workflows, config distribution.
> - **Boundary principle: develop/create/chat = OSS; operate/govern/fleet = Forge. Admin *APIs* and protocols stay open; the *console* is the product.**
> - **Refinements (2026-07-29):** (a) SwarmV2 ships OSS as simply **Swarm** — legacy v1 retired at import, V2 renamed (§3 Swarm consolidation); (b) OSS Surface has **no interactive agent-creation UI** — agents and squads are defined declaratively via **blueprints** that Surface applies; creation/management UX belongs to Forge (§3 blueprint-only configuration); (c) **DataIntelligence does not move** — it stays commercial and separate; (d) one OSS **`FabrCore.Services.Contracts`** package (identity reused) replaces the two planned per-service contract packages, with commercial `FabrCore.Forge.Contracts` extracted (§4); (e) **one blueprint experience** — a single canonical blueprint document owned by Core, one apply path, one storage (§5).

---

## 1. Component disposition

| Component (V365) | Disposition | Notes |
| --- | --- | --- |
| `FabrCore.Services.Memory` (+ Tests, contracts slice) | **OSS** | Per [memory-graphrag-oss-plan.md](memory-graphrag-oss-plan.md); optional SQL-backed package |
| `FabrCore.Services.GraphRag` (+ Tests, contracts slice) | **OSS** | Same; Vulcan365 markdown endpoint stripped to abstraction |
| `FabrCore.Surface` (+ Tests) | **OSS** | Chat/workspace/Command Center (**blueprint configuration only — interactive creation wizards removed**, §3) and **squads — SwarmV2 renamed to plain Swarm at import, v1 retired** (§3); zero Forge coupling verified (only `FabrCore.Sdk`/`Client.Orleans` NuGets, AdaptiveCards, Markdig; pure Blazor + own CSS, no UI kit) |
| `FabrCore.Surface.AzureStorage` / `.SqlServer` | **Delete** | Dead shells — untracked `bin/obj` only, not in the solution; they were Orleans clustering shims already replaced by discovery-based `AddFabrCoreSurfaceAsync` (non-localhost clustering deliberately throws) |
| `FabrCore.Experimental.SurfaceApp` (+ Tests) | **OSS as the sample/reference app** | Single-F5 Blazor Server app hosting FabrCore in-process; **drop its `FabrCore.Surface.Admin` reference** (admin goes to Forge); scrub secrets (§4.3) |
| `FabrCore.Surface.Admin` (+ Tests) | **Commercial → migrates into the Forge product** | ~7.6K LOC of admin Razor (7 routes); already binds only Contracts + Surface (test-enforced) — becomes Forge's console over remote admin APIs (§6) |
| `FabrCore.Services.Contracts` | **Identity moves OSS, trimmed** | Keeps the Memory + GraphRag admin slices as the **single** open-protocol package (§4); the Forge slice is extracted to a new commercial `FabrCore.Forge.Contracts` |
| `FabrCore.Services.DataIntelligence` | **Stays commercial, separate** | Decision 2026-07-29: does not move. Standalone EF-Core specification library with no coupling to Surface/Forge/Memory/GraphRag — remains a V365 package on its own track |
| `FabrCore.Forge` / `Forge.App` / `Forge.Tests` | **Commercial — the product** | Cloud config server (server side of the OSS `cloud-server-protocol.md`; DTOs already public in `FabrCore.Core`) + admin console host |
| `FabrCore-V365.AppHost` / `ServiceDefaults` | **Fork** | OSS gets a one-project Aspire host (SurfaceApp only) + a copy of the stock ServiceDefaults; commercial keeps the Forge pair |
| `build\*.yml` | 6 live pack pipelines retire (OSS publishes via existing tag→nuget.org workflow); 4 stale ones (taskagent/agentframework/swarm/worker — projects deleted) just delete; `forge.yml` + docker/k8s stay commercial | |

---

## 2. The end-state OSS solution

```
src\FabrCore.sln (OSS, Apache-2.0 after the §7 license gate)
├─ FabrCore.Core / Sdk / Host                      # runtime, SDK, native harness (harness plan P1)
├─ FabrCore.Host.SqlServer / .AzureStorage         # optional Orleans providers (existing)
├─ FabrCore.Client.Orleans                         # split-client connectivity (existing)
├─ FabrCore.Services.Microsoft365Copilot           # Teams/M365 channel (existing)
├─ FabrCore.Services.Contracts                     # the open admin protocol (Memory + GraphRag + capability doc)
├─ FabrCore.Services.Memory                        # optional; SQL Server 2025
├─ FabrCore.Services.GraphRag                      # optional; SQL Server 2025
├─ FabrCore.Surface                                # web UI: chat, Command Center, squads
├─ samples\FabrCore.SampleApp (ex-SurfaceApp)      # the F5 experience (+ optional Aspire host)
└─ tests (Host, Sdk, Client.Orleans, Memory, GraphRag, Surface, SampleApp)
```

### The dev experience (the point of all of this)

**Tier 0 — no SQL, no cloud, no account.** Clone → copy `fabrcore.sample.json` → `fabrcore.json` with one model key → F5 the sample app → browser opens at `/surface` with **no login** (pluggable identity, dev-fallback principal): the Command Center — agent directory and chat with Adaptive Card rendering. Agents and squads are **defined declaratively in blueprint JSON** (the canonical blueprint document, §5: `AgentConfiguration` entries + the `swarm.squads` extension; tools, plugins, MCP servers, args) and applied through Surface — config-as-code, versionable, reviewable; there is no interactive creation wizard in OSS (that UX is Forge's). Squads (Swarm: triage → plan → supervised wave execution with per-task verification and budgets) are likewise blueprint-defined. The sample ships pre-provisioned demo agents **plus editable, commented blueprint files as the copy-me template**. Everything durable rides in-process Orleans (localhost clustering). **Target: clone → chatting with your own blueprint-defined agent in under 10 minutes.**

**Tier 1 — add memory & knowledge (one docker container).** `docker run … mcr.microsoft.com/mssql/server:2025-latest`, add two connection strings → agents get durable scoped memory (`agent-memory` tools) and document GraphRAG (`graph-rag-*` tools); schemas auto-create on startup. The `/surface/memory`-style admin views are *not* in OSS — admin APIs are (`fabrcoreapi/*/admin/v1`), so curl/scripts work; the console is Forge's free tier.

**Tier 2 — harness agents (native, from the harness plan).** `CreateHarnessAgent(...)` or a zero-code `"AgentType": "harness"` blueprint: todos, plan/execute modes, tool approvals over channels, loops, background agents, mid-turn steering, durable session state — with run-safety budgets always on.

**Tier 3 — production.** Swap Orleans provider by referencing `FabrCore.Host.SqlServer`/`.AzureStorage`; add the M365 package for Teams; scale out. Still no Forge required — config via local `fabrcore.json`.

**Tier 4 — create a Forge account (the commercial funnel).** Connect the cluster (outbound-only, §6.3): free single-cluster tier lights up the hosted console — agent/principal management, memory & knowledge administration, verifiable-execution reporting, incident workbench, live dashboards. Paid tiers: multi-cluster, ACL/team management, config distribution with environment overlays + rollback, support. *ACL enforcement is OSS; ACL management is Forge.*

---

## 3. Surface move mechanics

- **Projects:** `FabrCore.Surface` + `FabrCore.Surface.Tests` (5,934 test lines, 154 facts, no external deps) move as-is; package refs `FabrCore.Sdk`/`Client.Orleans` become in-repo `ProjectReference`s. Delete the two dead storage-shim directories and the two `FabrCore.Surface.csproj.Backup*.tmp` files; consolidate the duplicated root-level vs `Identity\` principal-context types.
- **Sample app (`FabrCore.Experimental.SurfaceApp` → `samples\FabrCore.SampleApp`):** remove the `FabrCore.Surface.Admin` project reference and `AddFabrCoreSurfaceAdminComponents()/AddFabrCoreSurfaceAdminRoutes()` calls; keep conditional Memory (`if MemoryDb connstring → AddAgentMemoryServices`), the M365 package (optional, config-gated), verifiable execution, and the demo bootstrapper (Contoso/CRM content is fiction — safe). Add its orphan test project to the solution.
- **Identity stays pluggable and auth-free by default** — resolution chain: host `PrincipalResolver` delegate → ambient accessor → persisted component state → opt-in headers → claims (if an `AuthenticationStateProvider` exists) → `DevelopmentFallbackPrincipalId`. No Entra/cookie/auth-handler requirement; production hosts bring their own auth and set the resolver. Document this explicitly — it's a genuinely good design and a selling point.
- **Swarm consolidation — v1 retired, V2 renamed to Swarm (decision 2026-07-29).** The legacy `Ai\Swarm\` v1 implementation (one-shot orchestrator + free-text planner, zero unit tests) is retired at import. The SwarmV2 implementation becomes simply **Swarm**: public types drop the suffix (`SurfaceSquadServiceV2` → `SurfaceSquadService`, `SurfaceSwarmV2*` → `SurfaceSwarm*`), wire vocabulary `swarm2.*` → `swarm.*`, handle prefixes `squad2-` → `squad-`, and `SurfaceSquadType.SwarmV2` becomes the single `Swarm` type (the `Orchestrator` and `Task` squad types are unaffected). The rename is cheap at the import boundary — the public API has zero consumers yet. V365's own deployments need a one-time stored-squad migration; the existing interop layer (which already maps V1↔V2 shapes and stamps both arg names) serves as the transition bridge, then gets deleted. Update the 1,104-line Swarm test suite in the same change. Follow-through: write the missing Swarm design/skill doc before publishing (harness plan §4.2 is currently its only prose spec) and port the squad skills under the new naming.
- **Blueprint-only configuration in OSS Surface (decision 2026-07-29).** The `/surface/create` and `/surface/create-squad` interactive wizard flows are **removed from OSS** (or reduced to a blueprint upload/apply surface). Agents and squads are defined in **blueprint JSON** — the canonical blueprint document (§5), with squads as the `swarm.squads` extension section — applied via the blueprint pipeline (today `SurfaceBlueprintProvisioner` / `ISurfaceBlueprintClient.ApplyAsync`, converging on the §5 host-side apply path). This is already how the demo bootstrapper provisions everything, so the sample app needs no new machinery. Interactive creation/management UX becomes a Forge console feature. Tier-0 friction is mitigated by shipping well-commented sample blueprints + a documented blueprint reference (and optionally a small CLI verb later).

### 3.1 Secrets & hygiene (gate for any public push)

- `appsettings.Development.json` is **git-tracked with a real-format Forge API key** (`frg_dcfd8e77_…`) and `CloudServer.Enabled=true` pointing at `:5290` — a clean clone tries to call a Forge that isn't there. Fix: rotate the key in Forge, ship the file with `CloudServer` absent/disabled, move any real values to user secrets.
- `fabrcore.cloud-cache.json` is **git-tracked with a live Azure OpenAI tenant endpoint** — delete and gitignore.
- Move code into OSS as **copies without V365 history** (the private repo's history never becomes public, so history-scrubbing is unnecessary) — but rotate the exposed key regardless.
- `fabrcore.sample.json` is clean (placeholders) — it becomes the documented first-run step.

---

## 4. Contracts — final shape (single package, identity reused)

- **OSS: one package — `FabrCore.Services.Contracts` (existing identity, relocated and trimmed).** Contains the Memory + GraphRag admin interfaces, DTOs, `IMemoryAdminClient`/`IGraphRagAdminClient`, and request records — and is the standing home for future open admin surfaces (the capability document from §6 is the obvious next tenant). This is the open protocol the Forge console consumes — publishing it OSS is what makes "the console is the product, the protocol is open" true.
- **Why one package, not two:** MinVer versions the entire OSS solution together on each `v*` tag, so per-service contract versioning would never actually exist; the contents are dependency-free POCOs/interfaces where a shared version bump is a non-event; and **identity reuse minimizes the Forge-console migration** — `FabrCore.Surface.Admin` already references this exact package ID and its namespaces (`FabrCore.Services.{Memory,GraphRag}.Administration`), so its re-point is a feed/version change, not an import rewrite.
- **Commercial: `FabrCore.Forge.Contracts` (new).** The Forge slice (`IForgeAdminService/Client`, DTOs — currently link-compiled from `..\FabrCore.Forge\`) extracts into it; V365's aggregator project dissolves; `build\forge.yml`'s trigger paths on Contracts/ServiceDefaults get reworked.
- No link-compiles and no type-forwarders in OSS; the service packages reference `FabrCore.Services.Contracts` normally. Feed note: the 0.5.0-era package of the same ID on the private Azure Artifacts feed is deprecated at cutover; nuget.org ships ≥ 1.5.0.

---

## 5. One blueprint experience

Today there are **two blueprint layers**: the host's (`AgentConfiguration` documents applied via `POST fabrcoreapi/Agent/blueprint`; `agent-blueprint.json` in the skills docs) and Surface's (`SurfaceBlueprintDocument` with a `surface.squads` extension, stored under the Surface-private storage key `surface/command-center/blueprint`, compiled *client-side* by `SurfaceBlueprintProvisioner` into `AgentConfiguration`s and then posted to the host endpoint). Two schemas, two storage locations, and an implicit client-side compile step — "blueprint" means different things depending on where you're standing. **Decision: one canonical blueprint experience, owned by the core.**

1. **Canonical envelope in `FabrCore.Core`** — `FabrCoreBlueprint`: name/description/version + `Agents: List<AgentConfiguration>` + namespaced `Extensions` sections (generalizing Surface's existing extension pattern — its document is nearly isomorphic already and becomes this type). Harness zero-code agents need nothing new: `_Harness*` args are plain `AgentConfiguration.Args`.
2. **Extension expanders** — an `IBlueprintExpander` contract (extension key → expand into `AgentConfiguration`s + side effects), registered by whichever package owns the domain. The Swarm expander is today's `SurfaceSquadServiceV2.BuildAgentConfigurations` logic relocated out of the client-side provisioner; the extension key renames `surface.squads` → **`swarm.squads`** as part of the S1 Swarm rename.
3. **One apply path, host-side** — `fabrcoreapi/Agent/blueprint` accepts the canonical document and runs registered expanders server-side. Blueprints become first-class host resources (`fabrcoreapi/Blueprint` CRUD, per-principal, versioned), replacing the Surface-private storage key; `SurfaceBlueprintProvisioner` shrinks to a thin client over that API.
4. **Everyone converges on the same document** — the OSS dev's `blueprint.json` on disk, the sample app's bootstrapper, Surface's apply/edit flow, and the Forge console's blueprint management page. The payoff move: **Forge config distribution delivers blueprints fleet-wide** (cloud-server config documents carrying blueprint payloads) — the identical file goes from laptop F5 to fleet rollout.

Work lands in: S1 (extension-key rename), S3b (envelope + expanders + `fabrcoreapi/Blueprint`), S4 (Forge console + fleet distribution).

---

## 6. Forge-hosted admin — the FabrCore enablement work

`Surface.Admin` is architecturally ready to be remote (GraphRag admin has a full `Remote` client over `fabrcoreapi/graphrag/admin/v1`; verifiable execution + incidents are *already always-remote* over `fabrcoreapi/monitor`). The gaps, in priority order:

1. **Memory admin remote parity** — Memory admin is local-DI-only today; `IMemoryAdminClient` exists in Contracts but was never implemented. Work (in the OSS Memory package): a `fabrcoreapi/memory/admin/v1` controller mirroring GraphRag's, + a `RemoteMemoryAdminClient` + the same `Auto|Local|Remote` selector pattern.
2. **Admin authentication** — the admin APIs trust `x-user-handle` headers; unacceptable for a cloud console. Add a first-class admin-auth scheme on the host (cluster-scoped API keys minimum — Forge already mints per-cluster keys — ideally short-lived tokens so Forge users map to cluster principals for `IAuditProvider`/verifiable-execution trails).
3. **The connect channel (the one new architecture)** — Forge must reach customer clusters **without inbound ports**. Extend the existing outbound-only cloud-server protocol (config fetch + heartbeat, spec in [cloud-server-protocol.md](cloud-server-protocol.md)): the cluster maintains an outbound channel (long-poll or WebSocket) over which Forge multiplexes admin API calls. Spec it as v2 of the open protocol; Forge remains the first-party implementation.
4. **Capability discovery** — generalize GraphRag admin's `capabilities` endpoint into a cluster-level capability document (services present, versions, schema versions) rolled into the heartbeat, so the console renders only what the cluster has.

Items 1–2 and 4 are OSS-side work (they harden the open protocol for everyone — a self-hoster's own dashboard benefits identically). Item 3 is protocol-spec OSS + implementation on both sides.

---

## 7. Gates & risks

| Item | Detail |
| --- | --- |
| **License gate (unchanged, still first)** | OSS `LICENSE` file is GPLv3 text vs Apache-2.0 metadata/README/NOTICE — reconcile before any V365 code lands; V365 code is unlicensed → explicit relicense in the import commits |
| **Secrets gate** | §3.1 — rotate the Forge key, scrub the two tracked files, copy-without-history |
| **Contracts split ordering** | Execute inside V365 first (green build) before any cross-repo move — same as the memory/graphrag plan P0 |
| **OSS community builds its own admin UI** | Accepted by design — the paid value is hosted multi-cluster/identity/workflows/support, not pixel exclusivity. Keeping admin APIs open is what makes the OSS product credible |
| **Free-tier scope creep or under-scope** | OSS creates agents via blueprints (config-as-code) — deliberate and sufficient for developers; the free Forge tier is where *interactive* creation and management UX lives, which sharpens the account incentive. It must still include the genuinely useful single-cluster dashboards or the hook fails |
| **Blueprint-only Tier-0 friction** | A JSON-first creation flow raises the entry bar vs a wizard. Mitigation: commented sample blueprints as copy-me templates, a blueprint reference doc, tight validation errors from `ApplyAsync`; optionally a CLI verb later |
| **Swarm rename churn** | v1 retirement + V2→Swarm rename (types, `swarm2.*` vocabulary, `squad2-` handles, squad-type enum) breaks existing V365 deployments' stored squads. One-time migration + the existing interop layer as a transition bridge; land the rename at the import boundary where the public API has zero consumers |
| **Version/feed cutover** | Same as memory/graphrag plan §5: MinVer 1.5.x line, retire ADO pipelines, one-way nuget.org cutover; the 0.5.0 lockstep group dissolves (its stated rationale is already stale — the Surface.Admin→Memory ProjectReference it cites no longer exists) |
| **M365Copilot in the sample** | Keep config-gated so the F5 story never requires a bot registration |

---

## 8. Phases

Phases M0–M4 = [memory-graphrag-oss-plan.md](memory-graphrag-oss-plan.md) P0–P4 (license gate, Contracts split, Memory, GraphRag, DX, V365 re-point) — unchanged, runs first or in parallel where independent.

| Phase | Contents | Size |
| --- | --- | --- |
| **S0 — Gates** | Secrets rotation + scrub (§3.1); Swarm design/skill doc written (post-rename naming); decision log updated in V365 | ~2 days |
| **S1 — Surface into OSS** | `FabrCore.Surface` + Tests into `src\` (project refs, cleanups); **Swarm consolidation** (retire v1, rename V2→Swarm incl. wire vocabulary/handles/enum **and the `surface.squads` → `swarm.squads` blueprint extension key**, update the test suite, V365 stored-squad migration note); **remove interactive creation wizards** (blueprint apply/upload remains); delete dead storage shells; skills ported under the new naming; sln/pack/release-script updates | ~1.5 wk |
| **S2 — Sample app** | `samples\FabrCore.SampleApp`: drop Admin refs, clean configs (`CloudServer` off, `fabrcore.sample.json` flow), **commented sample blueprints as the documented agent/squad-creation path**, optional one-project Aspire host + ServiceDefaults copy, orphan tests into sln; README/docs: the Tier-0→3 dev-experience story (§2) + blueprint reference | ~4–5 days |
| **S3 — Remote admin enablement (OSS side)** | Memory admin controller + `RemoteMemoryAdminClient` + selector; admin auth scheme on the host; capability document in heartbeat (lands in `FabrCore.Services.Contracts`); protocol v2 spec drafted in `docs\cloud-server-protocol.md` | ~1–1.5 wk |
| **S3b — One blueprint experience** | `FabrCoreBlueprint` envelope in Core; `IBlueprintExpander` + server-side expansion in the apply path; Swarm expander relocated from the client-side provisioner; `fabrcoreapi/Blueprint` CRUD replacing the Surface-private storage key; sample + docs updated to the canonical schema (§5) | ~1 wk |
| **S4 — Forge side (commercial repo)** | Surface.Admin migrates into Forge.App hosting (references OSS `FabrCore.Surface` + `FabrCore.Services.Contracts` NuGets, commercial `FabrCore.Forge.Contracts`); connect-channel implementation; free-tier single-cluster experience; blueprint management + fleet blueprint distribution (§5); retire V365 Surface/Admin pack pipelines | commercial workstream |

Harness plan P1–P4 remains its own track; its memory socket (§5.3) binds to the now-OSS Memory engine.

---

## Appendix: source anchors (new to this doc)

`C:\repos\FabrCore-V365\src\FabrCore.Surface\{FabrCore.Surface.csproj, FabrCoreSurfaceExtensions.cs, Services\SurfaceOptions.cs, Identity\DefaultSurfacePrincipalContextProvider.cs, Components\SurfaceCommandCenter.razor, CommandCenter\*Client.cs}`; `src\FabrCore.Surface.Admin\{FabrCore.Surface.Admin.csproj, FabrCoreSurfaceAdminExtensions.cs, GraphRag\{GraphRagAdminClientSelector.cs, RemoteGraphRagAdminClient.cs}, Components\*.razor}` (route inventory: `/surface/knowledge`, `/surface/management[/blueprints|/agents|/principals]`, `/surface/memory`, `/surface/execution[/{TraceId}|/incidents]`); `src\FabrCore.Experimental.SurfaceApp\{Program.cs, appsettings.Development.json (tracked secrets), fabrcore.cloud-cache.json (tracked endpoint), fabrcore.sample.json, Surface\SurfaceDemoBlueprintFactory.cs}`; `src\FabrCore.Forge\{README.md, CloudServer\ForgeCloudServerController.cs, Administration\ForgeAdminController.cs}`; `src\FabrCore.Services.Contracts\FabrCore.Services.Contracts.csproj` (Forge link-compile lines 30-31); OSS: `docs\cloud-server-protocol.md`, `src\FabrCore.Host\Services\CloudServer*` (client side).
