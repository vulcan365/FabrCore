# Moving FabrCore.Services.Memory & FabrCore.Services.GraphRag into OSS FabrCore

> Status: **Proposal** (2026-07-29). Source-verified against `C:\repos\FabrCore-V365` (Memory ~7.9K LOC, GraphRag ~12.9K LOC, both v0.5.0) and this repo (v1.4.1 tags, MinVer). Companion to [harness-adoption-plan.md](harness-adoption-plan.md) — this plan **supersedes** its §8 statement that the memory engine stays commercial.
>
> **Decisions this plan encodes:** (1) both services move into this repo as **optional packages**; (2) **SQL Server is the only storage provider** — accepted, no provider abstraction will be attempted; (3) core FabrCore must remain fully usable with **zero SQL dependency**; (4) dev experience is a first-class goal.

---

## 1. The core answer: optional packages, never referenced by core

FabrCore already has the exact architecture this needs, in two places:

- **`FabrCore.Services.Microsoft365Copilot`** — the packaging template: an OSS `Services.*` project in `src\FabrCore.sln`, opt-in via `AddMicrosoft365Copilot(...)`, never referenced by Core/Sdk/Host.
- **`FabrCore.Host.SqlServer` / `.AzureStorage`** — the optional-storage template: discovered only when referenced, **fail-fast with an error naming the exact package to add** (`FabrCoreHostExtensions.cs:764-825`), idempotent auto-DDL (`OrleansSqlServerInitializer`), and the same `Microsoft.Data.SqlClient 7.0.2` pin both services already use.

Memory and GraphRag slot into this pattern as `src\FabrCore.Services.Memory` and `src\FabrCore.Services.GraphRag` (+ contracts packages, §4). **Nothing in Core/Sdk/Host ever references them**, so:

| Scenario | Behavior |
| --- | --- |
| Package not referenced | FabrCore runs exactly as today — Localhost/AzureStorage clustering, zero SQL anywhere |
| Package referenced, service not registered | Inert — assemblies load, plugins are discoverable but unconfigured; nothing touches SQL |
| Registered (`AddAgentMemoryServices("MemoryDb")` / `AddGraphRagServices("GraphRagDb")`), connection string present | Hosted service auto-creates/migrates the schema (`mem` / `grag`) on startup under `sp_getapplock`; health check tagged `"ready"` reports status |
| Registered, connection string missing | **Fail-fast at startup with a clear message** — the house style (`IFabrCoreOrleansProvider` doc-comment: "fail fast with a clear error"). Explicit registration = explicit intent |
| Registered, embeddings model missing | Memory: startup-fatal unless `AllowStartupWithoutEmbeddings=true` (existing knob). GraphRag: first search throws; ingestion degrades to chunk-only when the chat model is absent |

There is deliberately **no provider seam and no in-memory fallback** inside the services. GraphRag's SQL Server 2025 features (`AS NODE`/`AS EDGE`, `MATCH()`, `$node_id` at ~100+ sites, `VECTOR(1536)`, `VECTOR_DISTANCE`, `sp_getapplock`, `MERGE`, `OPENJSON`) are woven through every major file; Memory is the same minus graph `MATCH`. The honest contract is: **"reference this package ⇒ you need SQL Server 2025 (or Azure SQL with VECTOR)."** Faking it with an in-memory provider would ship a worse product to make a dependency feel smaller than it is.

---

## 2. What's moving (verified inventory)

**FabrCore.Services.Memory** (~7.9K LOC + tests): scoped SQL knowledge-graph memory — `mem` schema (SQL Graph nodes/edges, `VECTOR(1536)` chunks), three temperatures + bounded hot index, LLM entity-merge/dedup/contradiction-resolution/staleness lifecycle, plan-driven hybrid recall, synthetic imagining, `[PluginAlias("agent-memory")]` with 8 tools, `MemoryAwareCompactionService` (conversation→memory extraction), admin service + audit. Three-tier tests incl. live-model quality gates (Recall@2=100%, MRR≥0.75).

**FabrCore.Services.GraphRag** (~12.9K LOC + tests): document/corpus GraphRAG — `grag` schema, ingest (markdown → chunk → batch-embed → batched LLM entity/relationship/taxonomy extraction, two-phase with deadlock retry and provenance GC), scope-enforced vector+graph search (`ScopeKey` mandatory, never an LLM parameter), 5 `[PluginAlias]` plugins (`graph-rag-search/ingest/query/domain/scope`), 2 `[AgentAlias]` agents, migration runner (`M001–M008` under applock), admin service + REST controller (`fabrcoreapi/graphrag/admin/v1`, ~34 endpoints, ACL-gated), audit. Tests incl. gated SQL-integration + Recall@3=100%/MRR≥0.70 evaluation tiers.

Both: zero TODO/FIXME markers, strong XML docs, mature skills documentation (to be ported and de-duplicated — the three copies of the graphrag skill have diverged).

---

## 3. Dependency untangling (the actual engineering)

### 3.1 Memory — clean retarget

The `FabrCore.Host 1.4.1` package reference is **vestigial**: zero `FabrCore.Host` usings, zero Orleans, zero ASP.NET types in the project. Every consumed type lives in **Sdk** (`IEmbeddings`, `IFabrCoreChatClientService`, `FabrCoreChatHistoryProvider`, `CompactionConfig/Result/Service`, `IFabrCorePlugin`, `PluginAliasAttribute`, `GetPluginSetting`) or **Core** (`StoredChatMessage`, `AgentConfiguration`).

- Retarget: `ProjectReference` → `FabrCore.Sdk` (in-repo; Core flows transitively).
- Add the refs Host's `FrameworkReference` was silently supplying: `Microsoft.Extensions.Hosting.Abstractions`, `Microsoft.Extensions.DependencyInjection.Abstractions`, `Microsoft.Extensions.Logging.Abstractions`.
- Keep `Microsoft.Data.SqlClient 7.0.2` (matches `FabrCore.Host.SqlServer`).

### 3.2 GraphRag — three surgical changes

1. **`AclEnforcer`** (the *only* Host types used; 2 null-guarded call sites: `AclLocalGraphRagAdminClient.cs:77`, `GraphRagAdminController.cs:296`): move `AclEnforcer` from `FabrCore.Host.Services` down to `FabrCore.Core.Acl` (where `AclAction` already lives; Host re-exports for back-compat) — then GraphRag targets Sdk. The admin controller keeps `FrameworkReference Microsoft.AspNetCore.App` (precedent: `Services.Microsoft365Copilot` has the same).
2. **Strip the commercial endpoint**: `Vulcan365MarkdownConversionService` hardcodes `https://markdown.vulcan365.ai/convert-doc-intel` as a public default. OSS ships `IMarkdownConversionService` + a no-op/pass-through default (markdown-in works without conversion); the Vulcan365 implementation stays in V365 and registers over the default.
3. **Drop `Dapper`** — referenced but the code is hand-rolled `SqlCommand` throughout (verify with a build, then remove).

### 3.3 Contracts — the split (hardest part, do first)

`FabrCore.Services.Contracts` is a **link-compile aggregator**: it `<Compile Include>`s source files *out of* Memory (11 files), GraphRag (3 globs), and **Forge** (proprietary, stays commercial), and the services `<Compile Remove>` those files and type-forward back (19 forwarders in Memory, 18 in GraphRag). This dies the moment repos diverge, and it can't move wholesale because of Forge.

Resolve into a **single OSS contracts package that keeps the existing identity** (decision 2026-07-29 — rationale in [oss-platform-plan.md](oss-platform-plan.md) §4):

- **`FabrCore.Services.Contracts` (OSS, identity reused, trimmed):** one package holding the Memory slice (`IMemoryAdminService` + admin DTOs, `IMemoryAuditLog`/`MemoryAuditEntry`, shared models — `MemoryEntry`, `MemoryRecallResult`, `MemoryIndex`, `MemoryTemperature`, `MemoryType`, `ProceduralSteps`, … — plus `IMemoryAdminClient`/`MemoryAdminRequests`) and the GraphRag slice (`IGraphRagAdminService` + admin DTOs + `SourceDocumentDto`, plus `IGraphRagAdminClient`/`GraphRagAdminRequests`). One package, not two: MinVer versions the whole OSS solution together (per-service contract versioning would never actually exist), the contents are dependency-free POCOs/interfaces, and identity reuse preserves `FabrCore.Surface.Admin`'s existing package reference and namespaces untouched.
- **`FabrCore.Forge.Contracts` (new, commercial):** the Forge slice extracts here; V365's link-compile aggregator dissolves.
- Service packages reference `FabrCore.Services.Contracts` normally — **no link-compile, no type forwarders** in OSS. (If binary compat with existing V365 0.5.0 consumers matters, temporary forwarder shims can live V365-side; preferred: one coordinated bump.)
- `FabrCore.Surface.Admin` (which binds contracts only — its own test asserts it never references the Memory implementation) keeps its reference and imports; only the feed/version changes.

### 3.4 Harness memory socket alignment

[harness-adoption-plan.md](harness-adoption-plan.md) §5.3/§8 put `IAgentMemoryService` abstractions in the **Sdk** so the native harness can expose a memory socket without referencing the engine. That still holds and is now simpler: the dependency direction is `FabrCore.Sdk` (socket interfaces) ← `FabrCore.Services.Contracts` *or* the interfaces move to Sdk and the Contracts package holds only admin/DTO surface. Recommended: **`IAgentMemoryService` + recall models into `FabrCore.Sdk`** (harness needs them), admin surface into the Contracts package. The dead `src\FabrCore.Sdk\Memory\` folder (excluded from compilation, `FabrCore.Sdk.csproj:15`) is deleted in the same change.

### 3.5 Shared SQL primitives (optional, consider)

Memory and GraphRag duplicate patterns (applock-guarded migration runner, `SqlVector` binding, chunker, embedding-fallback helper, audit-log shape). A small internal-shared source package (`FabrCore.Services.Sql.Internal`, source-linked or internal shared project) would de-duplicate — **defer unless it falls out naturally during the move**; don't let refactoring block the migration.

---

## 4. Dev experience design

### 4.1 Golden path (the README story)

```bash
dotnet add package FabrCore.Services.Memory
dotnet add package FabrCore.Services.GraphRag
```

```csharp
// Program.cs — explicit, two lines each
builder.Services.AddAgentMemoryServices("MemoryDb");
builder.Services.AddMemoryAdministration();
builder.Services.AddGraphRagServices("GraphRagDb");
builder.Services.AddGraphRagAdministration();
```

```jsonc
// appsettings.json — both names may point at the SAME database;
// the mem and grag schemas co-habit (applock resources are schema-namespaced)
"ConnectionStrings": {
  "MemoryDb":   "Server=localhost;Database=fabrcore;TrustServerCertificate=True;...",
  "GraphRagDb": "Server=localhost;Database=fabrcore;TrustServerCertificate=True;..."
}
```

```jsonc
// fabrcore.json — memory/graphrag need an embeddings model; the name "embeddings" is the contract
{ "ModelConfigurations": [
    { "Name": "default",    "Provider": "openai", "Model": "gpt-5.2", "ApiKeyAlias": "openai" },
    { "Name": "embeddings", "Provider": "openai", "Model": "text-embedding-4", "ApiKeyAlias": "openai" } ] }
```

```jsonc
// blueprint — plugins are reflection-discovered from the referenced assemblies; zero extra wiring
{ "Handle": "eric:analyst", "AgentType": "...",
  "Plugins": ["agent-memory", "graph-rag-search"] }
```

Schema creation is **automatic and idempotent** on startup (both services already do this under `sp_getapplock`) — no manual DDL, ever. Startup failures name the missing piece explicitly ("connection string 'MemoryDb' not found — add it or remove AddAgentMemoryServices").

Additionally, formalize the conditional pattern V365's SurfaceApp already uses, for config-driven hosts:

```csharp
builder.Services.TryAddAgentMemoryServices(builder.Configuration, "MemoryDb");   // registers only if the
builder.Services.TryAddGraphRagServices(builder.Configuration, "GraphRagDb");    // connstring exists; logs loudly either way
```

### 4.2 Local SQL Server 2025 story (the steep part, made flat)

There is **no existing dev-provisioning pattern to copy** (the V365 AppHost is 13 lines and provisions nothing) — build it here:

- **Docker one-liner** in the README (the primary path):
  ```bash
  docker run -e "ACCEPT_EULA=Y" -e "MSSQL_SA_PASSWORD=<YourStrong@Passw0rd>" -p 1433:1433 -d mcr.microsoft.com/mssql/server:2025-latest
  ```
  Must be a **2025** image — `VECTOR`/`VECTOR_DISTANCE`/graph `MATCH` are hard requirements. Document free options: SQL Server 2025 Developer/Express editions, and Azure SQL Database (**verify** VECTOR + graph feature availability/tier on Azure SQL before claiming it in docs).
- **Optional Aspire sample** (`samples/` — not a core project): `builder.AddSqlServer("sql").WithImage(...2025...).AddDatabase("fabrcore")` + `WithReference`/`WaitFor`, giving an F5 experience where SQL, schema init, and the host come up together.
- **CI**: GitHub Actions runs the SQL-integration test tiers against an `mssql/server:2025` service container; the existing `FABRCORE_*_TEST_*` env-var gating (tests `Assert.Inconclusive` when unset) already makes these tests safe for forks without SQL.

### 4.3 Observability & health

- Each service registers an `IHealthCheck` tagged `"ready"` (existing hook: `AddHealthChecks()` + `/health/ready` in `FabrCoreHostExtensions.cs:337-345,594`) reporting schema version, connectivity, and embeddings availability.
- Surface the same in the `HealthController` aggregate so `fabrcoreapi/health` shows "memory: ready (schema v…, 1,204 memories)" — cheap, high-perceived-quality DX.

### 4.4 Docs & skills

Port the `fabrcore-services-memory` and `fabrcore-graphrag` skills into this repo's `.agents\skills\` as the single source of truth (**reconcile the three diverged copies first**), fix the known drift (snake_case tool names in docs vs PascalCase runtime names; `MemoryTemperature.cs` comment referencing `grag` instead of `mem`; stale option names in XML docs), and add a top-level `docs/memory-and-graphrag.md` quickstart matching §4.1.

---

## 5. Versioning, CI, licensing

### 5.1 Versioning & packaging

- Add 4 projects (+2 test projects) to `src\FabrCore.sln`; drop V365's hard-coded `<Version>0.5.0</Version>` — MinVer + `v*` tags take over (next release ≥ v1.5.0). Update `Pack-Local.ps1`'s expected-package list (7 → 11) and the release scripts.
- Publishing: the existing tag-push → `publish-nuget.yml` → nuget.org flow covers the new packages automatically once they're in the solution. **Retire** the ADO pipeline `build\fabrcore-services-memory.yml`; the package identities `FabrCore.Services.Memory`/`.GraphRag` move to nuget.org — coordinate so the private Azure Artifacts feed stops publishing them (identity collision = restore chaos).

### 5.2 Licensing — **gate, resolve before any code moves**

- The OSS repo's metadata is self-contradictory today: `src\Directory.Build.props` says `Apache-2.0`, README says Apache-2.0, `NOTICE` is Apache-style — but the `LICENSE` file contains the **full GPLv3 text**. Reconcile to the intended license (everything else points to Apache-2.0) before importing new code.
- V365 has **no license file and no license metadata** — the moved code is currently all-rights-reserved by default. Moving it into OSS is an explicit relicensing act by Vulcan365 LLC (sole owner, so mechanically simple — but make it deliberate: add headers/metadata in the same commit).

### 5.3 V365 re-pointing (the other side of the move)

- `FabrCore.Experimental.SurfaceApp`: swap `ProjectReference` → NuGet `FabrCore.Services.Memory` (its conditional `MemoryDb` registration keeps working as-is).
- `FabrCore.Surface.Admin`: re-point to the single OSS `FabrCore.Services.Contracts` (it never referenced the implementations; same identity, new feed/version).
- Bump all `FabrCore.Host`/`Sdk` pins (currently 1.4.1) and the moved-package refs together in one coordinated 1.5.x update — mind NuGet downgrade errors.
- Tests: `FabrCore.Services.Memory.Tests` and `FabrCore.Services.GraphRag.Tests` move with their projects (keeping `InternalsVisibleTo`). The **legacy** copies in V365 `FabrCore.Tests\Memory\*` (5 files) and `FabrCore.Tests\GraphRagAgent\*` (2,412 LOC, tests internals) are superseded — **retire them** rather than solving cross-repo `InternalsVisibleTo`; port any unique cases into the moved test projects.
- Hygiene while there: remove the dangling `ProjectReference`s in `FabrCore.Tests.csproj` to the deleted `FabrCore.Experimental.Swarm`/`FabrCore.Agents.TaskAgent`; delete the dead `FabrCore.Services.GraphRag.Contracts` bin/obj shell.

### 5.4 What stays commercial

> **Superseded 2026-07-29 by [oss-platform-plan.md](oss-platform-plan.md):** `FabrCore.Surface` (including SwarmV2/squads) also moves OSS; `FabrCore.Surface.Admin` migrates into the **Forge** product rather than OSS. Final commercial set: Forge (cloud config server + hosted admin console, freemium single-cluster → paid multi-cluster/ACL), the Forge Contracts slice, and the Vulcan365 markdown-conversion implementation. The remote admin clients ship where their pages ship (Forge), while the admin *APIs and contracts* are OSS — "the protocol is open, the console is the product."

---

## 6. Risks

| Risk | Mitigation |
| --- | --- |
| **License contradiction in OSS repo** (GPLv3 file vs Apache-2.0 metadata) — importing code before fixing it creates provenance ambiguity | §5.2 gate: reconcile first, in its own commit, before any V365 code lands |
| **Contracts split breaks V365 build** mid-migration (link-compile aggregator) | Do the split as step one *inside V365* (create the two contract packages, re-point, green build) before moving anything cross-repo |
| **SQL Server 2025 floor** — Azure SQL VECTOR/graph availability and Express edition limits unverified | Verify before documenting; state the floor bluntly in README ("SQL Server 2025+ / Azure SQL with VECTOR support") |
| **Package identity/feed collision** — same IDs on private ADO feed (0.5.0) and nuget.org (1.5.x) | One-way cutover: last ADO publish is deprecated in the feed notes; V365 `nuget.config` ordering already prefers explicit sources |
| **Version downgrade errors** in V365 after re-point | Single coordinated 1.5.x bump across all pins; no mixed-line states |
| **CI cost/flakiness of SQL-integration tests** | Keep the env-var gating + `Assert.Inconclusive` pattern; run SQL tiers in a dedicated workflow with the 2025 service container, not on every PR if too slow |
| **GraphRag maintenance weight** (12.9K LOC, 28% in one file) lands on the OSS repo | Accept — it comes with 4.6K LOC of tests and quality gates; schedule the `KnowledgeIngestionService` decomposition as future hygiene, not a move blocker |
| **Doc drift shipping to the public** (snake_case tool names, diverged skill copies) | §4.4 fixes are part of the move's definition of done |

---

## 7. Phased plan (each independently shippable)

| Phase | Contents | Size |
| --- | --- | --- |
| **P0 — Gates** | Reconcile OSS LICENSE ↔ Apache-2.0 metadata; relicense decision recorded; verify Azure SQL VECTOR/graph + 2025 Express claims; Contracts split executed **inside V365** (single OSS-bound `FabrCore.Services.Contracts` retaining the Memory + GraphRag slices; Forge slice extracted to `FabrCore.Forge.Contracts`; green build) | ~2–3 days |
| **P1 — Move Memory** | Project + tests into `src\`; retarget to Sdk (+ Abstractions refs); `IAgentMemoryService`+recall models into Sdk (harness socket, §3.4) and delete dead `Sdk\Memory\`; contracts package; sln/pack/release-script updates; health check; skills/docs ported; CI SQL tier | ~1 wk |
| **P2 — Move GraphRag** | `AclEnforcer` → `FabrCore.Core.Acl`; markdown-conversion abstraction (Vulcan365 impl stays commercial); drop Dapper; project + tests + contracts into `src\`; controller ships with `FrameworkReference` (M365Copilot precedent); health check; skills/docs ported (dedupe the three copies) | ~1–1.5 wk |
| **P3 — DX polish** | `TryAdd*` conditional registration helpers; README quickstart + docker one-liner; optional Aspire sample; `HealthController` aggregation; blueprint samples (`"Plugins": ["agent-memory", "graph-rag-search"]`); harness memory socket verified against the now-OSS engine | ~3–4 days |
| **P4 — V365 re-point** | NuGet re-pointing + coordinated 1.5.x pin bump; retire ADO pipeline + legacy test copies; feed cutover; V365 hygiene (dangling refs, dead Contracts shell) | ~2–3 days |

**Definition of done for the DX goal:** a new user with Docker and an OpenAI key goes from `git clone` to an agent that saves and recalls memories in **under 15 minutes**, following only the README — and a user who never adds the packages never sees the word SQL.

---

## Appendix: source anchors

**V365** (`C:\repos\FabrCore-V365\src\`): `FabrCore.Services.Memory\{FabrCore.Services.Memory.csproj, Configuration\MemoryServiceExtensions.cs, Services\MemorySchemaInitializer.cs, MemoryContractTypeForwarders.cs}`; `FabrCore.Services.GraphRag\{FabrCore.Services.GraphRag.csproj, GraphRagServiceExtensions.cs, GraphRagSchemaInitializer.cs, Migrations\GraphRagMigrationRunner.cs, Administration\GraphRagAdminController.cs, Administration\AclLocalGraphRagAdminClient.cs, Services\Vulcan365MarkdownConversionService.cs, GraphRagContractTypeForwarders.cs}`; `FabrCore.Services.Contracts\FabrCore.Services.Contracts.csproj` (link-compile lines 13-33); `FabrCore.Experimental.SurfaceApp\Program.cs:64-70` (conditional registration pattern).

**OSS** (this repo): `src\FabrCore.Sdk\Embeddings.cs` (`IEmbeddings`); `src\FabrCore.Host\FabrCoreHostExtensions.cs:458` (embeddings DI), `:764-825` (optional-assembly discovery), `:337-345,594` (health checks); `src\FabrCore.Host\Api\Controllers\{EmbeddingsController.cs, ModelConfigController.cs, HealthController.cs}`; `src\FabrCore.Host.SqlServer\{FabrCore.Host.SqlServer.csproj, OrleansSqlServerInitializer.cs}`; `src\Directory.Build.props` (MinVer/Apache-2.0 metadata); `.github\workflows\publish-nuget.yml`; `scripts\Pack-Local.ps1`; `LICENSE` (currently GPLv3 text — §5.2 gate).
