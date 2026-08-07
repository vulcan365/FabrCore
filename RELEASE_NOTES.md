# FabrCore release notes

These notes cover the current unreleased changes after `v1.6.3` and the tagged repository
history from `v1.0.0` onward. They are derived from Git history and tree diffs, including
changes delivered through merge commits.

[Current comparison: v1.6.3...develop](https://github.com/vulcan365/FabrCore/compare/v1.6.3...develop)

## Unreleased - changes since v1.6.3

Snapshot date: 2026-08-05

### Highlights

- Added a FabrCore-native agent harness built on Microsoft Agent Framework harness primitives.
  Agents can maintain model-managed todo lists, keep working through bounded iteration loops,
  and delegate work to other FabrCore agents while preserving session state across grain
  deactivation.
- Added a two-layer compaction ladder: reversible, per-call context compaction bounds what the
  model sees, while history compaction continues to summarize persisted `MessageThreads`
  between turns.
- Replaced the Surface Swarm runtime with simpler blueprint-defined `orchestrator` and `task`
  squads. Task squads use harness todo, background-agent, and loop primitives to coordinate
  executor and subject-matter-expert members.

### Added

- Added production WebSocket v2 with authenticated one-time tickets, explicit operation/delivery envelopes, durable ordered at-least-once delivery and replay, acknowledgements and gap detection, plus the typed reconnecting `FabrCore.Client.WebSocket` client. Agent creation and Blueprint provisioning remain HTTP-only.

- Added `CreateFabrCoreHarnessAgent` for `FabrCoreAgentProxy` implementations and
  `AsFabrCoreHarnessAgent` for direct `IChatClient` composition.
- Added `FabrCoreHarnessOptions`, `FabrCoreHarnessResult`, `HarnessLoopMode`, `HarnessArgs`,
  `HarnessSessionSnapshot`, `FabrCoreBackgroundAgent`, and `AgentRosterBuilder`.
- Added durable harness `plan` / `execute` modes, `_plan-mode` selection on inbound
  `AgentMessage` values, mode-aware todo looping, and SDK helpers for inspecting or changing mode.
- Added configurable todo, background-delegation, completion-marker, and AI-judge loop modes,
  with a bounded iteration budget and APIs for reporting unfinished work.
- Added durable per-thread harness snapshots for todos and delegation records. Unreadable or
  incompatible snapshots start fresh, and delegations that were in flight during deactivation
  are explicitly reported as lost.
- Added health-probed background-agent rosters, collision-safe delegate names, descriptions and
  capabilities from the FabrCore registry, delegation timeouts, and ACL-governed FabrCore
  messaging.
- Added `ContextCompaction`, `ContextCompactionConfig`, and `CompactionLadder`. The default ladder
  evicts old tool results at 50% of the input budget, truncates old groups at 80%, history-compacts
  at 87% of the context window, applies a projection fuse at 90%, and stops oversized runs at the
  model window.
- Added model settings `ContextCompactionEnabled`, `ContextEvictThreshold`, and
  `ContextTruncateThreshold`, together with `_Context*` agent argument overrides.
- Added resolved-ladder diagnostics and `compaction.history.started`,
  `compaction.history.completed`, and `compaction.history.failed` monitor events.
- Added Surface task-squad contracts and runtime support, including task options for the worker
  model, persona prompt, delegation overlay, delegation timeout, and maximum loop iterations.
- Added focused SDK tests for harness composition, durable sessions, delegation behavior,
  configuration parsing, and compaction-ladder ordering.

### Changed

- Changed Surface blueprints from the nested `swarm.squads` extension to a top-level `squads`
  extension. Supported squad types are now `orchestrator` and `task`.
- Reworked task squads around `SurfaceTaskHarnessAgent`. A task run tracks model-owned todos,
  delegates concurrently to executor members, consults subject-matter-expert members, and loops
  until no todos or delegations remain, subject to the configured iteration cap.
- Changed history compaction to run only before or after a turn. When context compaction is active,
  its default threshold is now 87%; without layer 1, it retains the legacy 75% default.
- Changed read-side projection into a high-watermark fuse when context compaction is configured,
  leaving the cheaper context and history compaction rungs to run first.
- Changed the run-safety scope to stop over-budget work only; it no longer rewrites persisted
  history inside a live tool loop.
- Updated `Microsoft.Agents.AI` and `Microsoft.Agents.AI.OpenAi` from `1.15.0` to `1.16.0`.
- Updated affected test projects to Microsoft.NET.Test.Sdk `18.8.1`, MSTest `4.3.3`, and
  Microsoft.Testing.Extensions.TrxReport `2.3.3`.

### Upgrade and compatibility notes

- Native harness modes are enabled by default. Pass `AgentMessage.Args["_plan-mode"] = "false"`
  for execution runs, or set `_HarnessMode=false` to retain the previous mode-independent todo loop.

- Migrate Surface blueprint documents from:

  ```json
  { "swarm": { "squads": [/* ... */] } }
  ```

  to:

  ```json
  { "squads": [/* ... */] }
  ```

  Replace `squadType: "swarm"` with either `orchestrator` for one-hop routing or `task` for
  harness-driven multi-step coordination.
- The `FabrCore.Surface.Ai.Swarm` API surface, `SurfaceSwarm*` types, Swarm services and options,
  and the former `SurfaceTaskRunnerAgent` were removed. Update integrations to
  `FabrCore.Surface.Ai.Squads`, `FabrCore.Surface.Ai.Tasks`, `ISurfaceSquadService`, and
  `SurfaceTaskHarnessAgent` as applicable.
- `MidTurnCompactionEnabled` and `_MidTurnCompactionEnabled` are retired. The model property remains
  deserializable but is obsolete and ignored; use `ContextCompactionEnabled` or
  `_ContextCompactionEnabled` instead.
- `FabrCoreRunStoppedException.CheckpointCount`, the constructor checkpoint-count parameter, and
  the `_fabrcore_checkpoint_count` response diagnostic were removed because run safety no longer
  performs compaction checkpoints.
- Set both `ContextWindowTokens` and `MaxOutputTokens` to enable layer 1. If either value is missing,
  FabrCore continues without in-run context compaction and reports `context:unconfigured` in the
  resolved ladder diagnostic.
- Harness APIs inherit the Microsoft Agent Framework experimental designation. Always execute via
  `FabrCoreHarnessResult.RunAsync`; calling the inner agent directly bypasses durable snapshotting.
- Harness snapshots preserve todos and delegation records, but work already running in another
  agent cannot be resumed after grain deactivation and is reported as lost on restore.

[Compare v1.6.3...develop](https://github.com/vulcan365/FabrCore/compare/v1.6.3...develop)

## v1.0.0-v1.6.3 highlights

- Added durable agent-to-principal delivery and proactive Microsoft 365 Copilot messaging.
- Split Orleans hosting into provider packages and added provider-neutral Orleans client
  gateway discovery.
- Added model-level inference defaults, custom OpenAI endpoints, cloud-managed configuration,
  and opt-in LLM gateway attribution.
- Expanded the standalone OSS platform with Contracts, Memory, GraphRAG, Surface, supervised
  Swarm orchestration, canonical blueprints, and a runnable Blazor sample application.
- Added an outbound-only Cloud Server administration channel, capability discovery, and a
  dedicated administration authentication policy.
- Returned the project to the Apache License 2.0 in `v1.5.0` after the GPLv3 license used by
  `v1.0.0` through `v1.4.1`.

## Upgrade notes for v1.6.3

- The final remote-administration configuration is `FabrCore:RemoteAdministration:Enabled`.
  Earlier `CloudServer:Connect`, `CloudServer:RemoteAdministration`, and singular `Enable`
  shapes should be migrated.
- Remote administration requires `FabrCore:CloudServer:Enabled=true`, a Cloud Server API key,
  and an absolute `FabrCore:HostUrl`. The Cloud Server API key is reused for the authenticated
  local administration hop while remote administration is enabled.
- SQL Server and Azure Storage hosting require their separate provider packages beginning with
  `v1.1.0`; Localhost mode remains built into `FabrCore.Host`.
- The `v1.3.0`, `v1.3.2`, and `v1.6.0` tags are version aliases: each points to the same commit
  as its immediately preceding tag and contains no additional tree changes.

## v1.6.3 — 2026-08-02

### Fixed

- Renamed `FabrCore:RemoteAdministration:Enable` to the consistent
  `FabrCore:RemoteAdministration:Enabled` setting.
- Updated validation, runtime enablement, capability reporting, authentication, tests, and
  documentation to honor the corrected setting.

[Compare v1.6.2...v1.6.3](https://github.com/vulcan365/FabrCore/compare/v1.6.2...v1.6.3)

## v1.6.2 — 2026-08-02

### Changed

- Moved remote administration out of the Cloud Server subtree into the top-level
  `FabrCore:RemoteAdministration` configuration section.
- Reused `FabrCore:CloudServer:ApiKey` for both Cloud Server communication and the local
  administration hop, removing the need to duplicate a separate local administration key.
- Advertised host administration capabilities only when remote administration is enabled.
- Added compatibility warnings for the obsolete nested configuration section.

### Security

- Extended the `FabrCoreAdmin` authentication handler to accept the Cloud Server key only when
  both Cloud Server and remote administration are enabled.
- Added coverage for standalone admin keys, Cloud Server keys, disabled modes, invalid keys,
  and constant-time credential comparison.

[Compare v1.6.1...v1.6.2](https://github.com/vulcan365/FabrCore/compare/v1.6.1...v1.6.2)

## v1.6.1 — 2026-08-01

### Changed

- Renamed the Cloud Server `Connect` settings and diagnostics to `RemoteAdministration`.
- Removed the alternate local admin target and made the required `FabrCore:HostUrl` the single
  execution target for proxied administration commands.
- Added validation and startup warnings for non-loopback host URLs carrying administration
  credentials.
- Updated the documented `appsettings` hierarchy and OSS/Forge boundary guidance.

[Compare v1.6.0...v1.6.1](https://github.com/vulcan365/FabrCore/compare/v1.6.0...v1.6.1)

## v1.6.0 — 2026-08-01

This tag points to the same commit as `v1.5.0`; there are no additional repository changes.

[Compare v1.5.0...v1.6.0](https://github.com/vulcan365/FabrCore/compare/v1.5.0...v1.6.0)

## v1.5.0 — 2026-08-01

This was the largest release in the range and established FabrCore as a broader standalone OSS
agent platform.

### Added

- Added `FabrCore.Services.Contracts`, the open Memory and GraphRAG administration contracts.
- Added `FabrCore.Services.Memory` with scoped hot/warm/cold memory, retrieval, compaction,
  taxonomy, synthetic imagining, auditing, SQL storage, administration APIs, and tests.
- Added `FabrCore.Services.GraphRag` with scoped ingestion, graph-backed retrieval, search,
  migrations, auditing, administration APIs, and tests.
- Added `FabrCore.Surface`, a standalone Blazor command center with agent chat, Adaptive Cards,
  actions, attachments, monitoring, and supervised Swarm orchestration.
- Added canonical `FabrCoreBlueprint` documents, extension expanders, host-side blueprint CRUD
  and apply APIs, and the top-level `swarm.squads` extension.
- Added `FabrCore.SampleApp`, its Aspire AppHost and ServiceDefaults, and sample/test coverage
  for the standalone development experience.
- Added cluster capability discovery, the `FabrCoreAdmin` bearer policy, and remote
  administration endpoints.
- Extended the Cloud Server protocol with the outbound-only v2 connect channel for proxied
  administration commands and responses.
- Added opt-in LLM attribution headers for agent handle, trace ID, and origin so compatible
  gateways can meter and govern usage per agent.

### Changed

- Relicensed the repository from GPLv3 to Apache License 2.0 and documented the relicensing.
- Consolidated public runtime, protocol, developer tooling, Memory, GraphRAG, Surface, and Swarm
  into the OSS repository while keeping Forge administration and commercial adapters separate.
- Updated the NuGet workflow, local packing scripts, solution, documentation, and skills for
  the expanded OSS package set.

### Fixed

- Corrected Orleans SQL Server membership and reminder schema compatibility and added provider
  regression coverage.

[Compare v1.4.1...v1.5.0](https://github.com/vulcan365/FabrCore/compare/v1.4.1...v1.5.0)

## v1.4.1 — 2026-07-25

### Dependencies

- Updated Microsoft Orleans packages from `10.2.1` to `10.2.2`.
- Updated Microsoft Agent Framework packages from `1.14.0` to `1.15.0`.
- Updated MSTest to `4.3.2` and Microsoft.NET.Test.Sdk to `18.8.1`.

[Compare v1.4.0...v1.4.1](https://github.com/vulcan365/FabrCore/compare/v1.4.0...v1.4.1)

## v1.4.0 — 2026-07-25

### Added

- Added the vendor-neutral Cloud Server configuration protocol under
  `/fabrcore-cloud/v1`, including shared contracts in `FabrCore.Core`.
- Added remote `fabrcore.json` retrieval with ETag-based refresh, retry/backoff behavior, and a
  last-known-good disk cache.
- Added per-silo heartbeat reporting with applied configuration versions and immediate refresh
  requests.
- Added configuration validation, startup failure policies, local cache tests, API client tests,
  and sync-service tests.

[Compare v1.3.2...v1.4.0](https://github.com/vulcan365/FabrCore/compare/v1.3.2...v1.4.0)

## v1.3.2 — 2026-07-22

This tag points to the same commit as `v1.3.1`; there are no additional repository changes.

[Compare v1.3.1...v1.3.2](https://github.com/vulcan365/FabrCore/compare/v1.3.1...v1.3.2)

## v1.3.1 — 2026-07-22

### Added

- Added `ReasoningEffort` to model configuration with support for `none`, `low`, `medium`,
  `high`, and extra-high values.
- Added a model-default chat client wrapper that applies configured reasoning effort and
  `MaxOutputTokens` without overriding explicit per-call options.
- Added model configuration API propagation, examples, validation, and test coverage.

### Fixed

- Ensured inference defaults from `fabrcore.json` reach both streaming and non-streaming chat
  requests across supported providers.

[Compare v1.3.0...v1.3.1](https://github.com/vulcan365/FabrCore/compare/v1.3.0...v1.3.1)

## v1.3.0 — 2026-07-22

This tag points to the same commit as `v1.2.1`; there are no additional repository changes.

[Compare v1.2.1...v1.3.0](https://github.com/vulcan365/FabrCore/compare/v1.2.1...v1.3.0)

## v1.2.1 — 2026-07-22

### Changed

- Removed the separate enable flag and ASP.NET Core authorization-policy requirement from the
  Orleans gateway discovery endpoint.
- Gateway discovery is now mapped with the normal FabrCore endpoints by `UseFabrCoreServer()`;
  clients only need to provide an `HttpClient`.
- Simplified host/client configuration, exception handling, tests, and documentation for the
  unauthenticated discovery request. Orleans transport security remains a separate production
  responsibility.

[Compare v1.2.0...v1.2.1](https://github.com/vulcan365/FabrCore/compare/v1.2.0...v1.2.1)

## v1.2.0 — 2026-07-21

### Added

- Added `FabrCore.Client.Orleans`, allowing trusted backend applications to discover active
  Orleans gateways from a FabrCore Host without referencing its SQL Server or Azure Storage
  provider package.
- Added the cluster gateway discovery document, endpoint, validation, dynamic refresh, cached
  fallback behavior, TLS requirements, and client/host test projects.
- Added the repository-local `.agents/skills` catalog and synchronized FabrCore development
  guidance and templates.

### Fixed

- Corrected provider isolation so Azure Storage dependencies no longer interfere with SQL
  Server hosting mode.
- Refreshed package and release-script references for the new client and provider layout.

[Compare v1.1.0...v1.2.0](https://github.com/vulcan365/FabrCore/compare/v1.1.0...v1.2.0)

## v1.1.0 — 2026-07-20

### Added

- Added the `FabrCore.Host.SqlServer` package for Orleans clustering, persistence, reminders,
  automatic schema deployment, and SQL provider configuration.
- Added the `FabrCore.Host.AzureStorage` package for table-based clustering and reminders,
  blob/table persistence, queue streams, and automatic resource provisioning.
- Added `IFabrCoreOrleansProvider`, built-in Localhost mode, provider auto-discovery, explicit
  provider registration, and provider-focused tests.

### Changed

- Moved SQL Server implementation and schema assets out of `FabrCore.Host` into the SQL Server
  provider package.
- Updated packaging, release scripts, solution structure, README, and Orleans/server guidance
  for independently installable provider packages.

[Compare v1.0.2...v1.1.0](https://github.com/vulcan365/FabrCore/compare/v1.0.2...v1.1.0)

## v1.0.2 — 2026-07-20

### Fixed

- OpenAI model configurations now honor a custom `Uri` from `fabrcore.json` by applying it to
  `OpenAIClientOptions.Endpoint`.

### Documentation

- Added an Agentic Resource Discovery research report and proposed implementation plan.

[Compare v1.0.1...v1.0.2](https://github.com/vulcan365/FabrCore/compare/v1.0.1...v1.0.2)

## v1.0.1 — 2026-07-14

### Added

- Added durable agent-to-principal delivery for messages sent while no live user observer is
  connected, including a persisted outbox, retries, backoff, expiry, endpoint selection, and
  pluggable `IPrincipalMessageRelay` providers.
- Added `SendToUserAsync` delivery overloads and targeted `PrincipalDeliveryTarget` routing.
- Added the Microsoft 365 Copilot relay for proactive delivery to previously established
  conversations.
- Added Copilot activity, conversation-context, UI-action, app-package, and proactive-delivery
  mapping with expanded automated tests.
- Added relay configuration, operational documentation, a webhook sample, and a dedicated
  principal-delivery skill.

[Compare v1.0.0...v1.0.1](https://github.com/vulcan365/FabrCore/compare/v1.0.0...v1.0.1)

## v1.0.0 — 2026-07-12

### Changed

- Marked the 1.x release line and changed the repository license from Apache License 2.0 to the
  GNU General Public License v3.0. The project returned to Apache License 2.0 in `v1.5.0`.
- Updated the SDK's Model Context Protocol dependency from `1.4.0` to `1.4.1`.

[Compare v0.10.2...v1.0.0](https://github.com/vulcan365/FabrCore/compare/v0.10.2...v1.0.0)
