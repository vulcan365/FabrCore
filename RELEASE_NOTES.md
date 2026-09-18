# FabrCore 2.0.0

These release notes describe the 2.0 release relative to `v1.8.1`, including the final
2.0 package, protocol, and configuration changes. Features already available in 1.8, such as
the harness, private specialists, Surface squads, WebSocket v2, and built-in A2A hosting,
remain part of the platform. Earlier history is available in Git and
[historical release documentation](docs/releases).

FabrCore 2.0 simplifies hosting around two modes: a standalone, trusted workspace with no
database, and a SQL-enabled host with persistent runtime state, enforced ACL, Memory,
GraphRAG, and durable operational stores. It also adds optional connections, remote agents,
cloud administration, runtime configuration reporting, and C# scripting. This is a breaking
package, configuration, and A2A protocol release.

## Highlights

- Added Orleans RPC contract build checks, persisted-JSON compatibility tests, Orleans telemetry,
  and opt-in SQL streams using the Orleans 10.3.1-alpha.1 provider. See the
  [Orleans adoption guide](docs/orleans-10.3-adoption.md) for migration and client configuration.
- Consolidated SQL Server, Memory, GraphRAG, and service contracts into Host, Core, and SDK.
- Added explicit standalone/SQL behavior, relational ACL storage, and ACL migration tooling.
- Upgraded inbound A2A to 1.0 (specification release 1.0.1), with shared channel-agent bindings
  and optional canonical Entra identity for Teams, Microsoft 365 Copilot, and A2A.
- Added durable SQL task snapshots, audit records, and execution evidence.
- Added optional principal-owned connections, authenticated MCP, encrypted authorization
  handoffs, and handle-addressable Work IQ and Copilot Studio agents.
- Added agent/blueprint administration, diagnostic conversations, paged monitoring, optional
  SQL monitoring, and evidence exports through vendor-neutral administration APIs.
- Added desired/resolved/applied runtime configuration reports, named code rules, previews,
  and a reference cloud server for independent implementations.
- Added `FabrCore.Scripting` for reusable C# plugins with developer-selected NuGet packages
  and a fresh worker process per execution.
- Updated Microsoft Agent Framework from 1.19.0 at `v1.8.1` to 1.20.0 and corrected both
  per-model-call context compaction and durable conversation compaction.
- Expanded Memory and GraphRAG ingestion, retrieval controls, and evaluation workflows.
- Improved model configuration, assembly discovery, Microsoft 365 delivery, and release automation.

## Breaking changes and upgrade

### Replace retired package references

| Retired package | 2.0 location |
| --- | --- |
| `FabrCore.Services.Contracts` | Shared contracts in `FabrCore.Core` |
| `FabrCore.Services.Memory` | Implementation in `FabrCore.Host`; contracts in Core; remote clients in SDK |
| `FabrCore.Services.GraphRag` | Implementation in `FabrCore.Host`; shared contracts in Core |
| `FabrCore.Host.SqlServer` | SQL Server provider in `FabrCore.Host` |

There are no forwarding packages. Update project references and rebuild consumers whose
types moved assemblies. Remove the former manual Memory/GraphRAG service registrations;
`AddFabrCoreServer` registers the integrated features according to the selected database mode.
Agent class libraries can reference SDK without taking a SQL implementation dependency.

Use the same 2.0.0 version for all FabrCore package references and rebuild applications and
direct Orleans clients together. The [README package table](README.md#packages) lists the
thirteen published packages, including the optional connection, remote-agent, and scripting packages.

### Update A2A clients and review channel identity

The host now supports **A2A 1.0 only**; A2A 0.3 clients must be updated. Calls require
`A2A-Version: 1.0` (discovery cards do not). JSON-RPC uses `SendMessage`,
`SendStreamingMessage`, `GetTask`, `ListTasks`, `CancelTask`, and `SubscribeToTask`.
REST paths are directly under `/a2a/{agent}`, without the previous `/v1` prefix. Cards
advertise `supportedInterfaces`; parts, role/state names, and response/stream wrappers use
the 1.0 format. Missing or unsupported call versions are rejected.

`CanonicalEntra` principal resolution and named `AgentBindings` let Teams/Microsoft 365
and A2A address the same user's agent. This is opt-in: existing strategy defaults remain,
and changing strategy does not migrate existing principal or agent state. Cross-channel user
continuity requires validated delegated user identity; application credentials alone do not
identify the person chatting. See [channel identity and wire migration](docs/channel-agent-identity.md).

### Verify existing persisted state

Keep Orleans type-manifest checks enabled and explicitly register application-defined
persisted types when necessary. The `JsonElement` storage converter preserves custom JSON;
it cannot reconstruct data already lost by an earlier serialization. Test an upgrade using
a copy of your deployment's SQL/Azure state and custom payloads. Contract baselines and
synthetic compatibility fixtures do not establish compatibility for every existing client
or stored application type. See [Orleans storage compatibility](docs/orleans-10.3-adoption.md).

### Choose the runtime mode explicitly when upgrading

- **No FabrCore database connection:** the default host is a trusted standalone workspace.
  Cross-principal agent communication does not require ACL grants. Authentication, privileged
  administration authentication, and storage/session ownership checks still apply. APIs using
  forwarded identity headers require the hosting application to authenticate callers and supply
  trusted user handles; ACL enforcement is not authentication. Runtime state,
  conversations, typed storage, delivery checkpoints, and reminders are in memory by default
  and are lost on process restart. SQL-only plugins, agents, and ACL administration are unavailable.
- **`ConnectionStrings:FabrCore` configured:** the host enables SQL Orleans defaults, enforced
  ACL, Memory, GraphRAG, and durable operational stores. Memory remains an explicit per-agent
  plugin/scope choice. A database connection does not attach memory to every agent.

Existing Azure/custom Orleans providers can still be selected explicitly. Orleans storage
selection is separate from enabling the integrated SQL feature set. Host ships SQL assemblies;
standalone startup does not activate SQL services or connect to a database.

### Migrate ACL configuration and data

ACL principals, groups, roles, grants, and their version now live in relational `acl` tables,
independent of the Orleans storage provider. Normal startup rejects JSON ACL seeds/rules.
Use application configuration for database selection and administration authentication;
use the ACL administration API for user ACL data.

For an existing installation:

1. Export ACL data from the old running host with `scripts/Migrate-Acl.ps1 -Mode Export`.
2. Update package references and configure the new host's database and required models.
3. Configure `FabrCore:AdminAuthentication:ApiKey` and initialize the new ACL schema.
4. Import the export with `scripts/Migrate-Acl.ps1 -Mode Import` into a built-in-only target.
5. Validate principal/group membership, grants, readiness, and application access before cutover.

The migration tool preserves identifiers, validates references, imports transactionally, and
refuses to overwrite user-defined ACL data. Legacy `FabrCore:Acl:Seed` configuration is accepted
as migration input, not as ongoing startup configuration. Supply the tool's administration
credential through `FABRCORE_ADMIN_API_KEY`.

See [database modes and migration](docs/database-modes.md) for complete commands, connection
overrides, prerequisites, and deployment behavior.

### Configure protection before enabling connections

Host automatically supplies FabrCore credential protection when the optional connections
service is enabled. Default in-memory standalone hosts use ephemeral keys. SQL persistence
uses an encrypted shared key ring in `fabrOps.DataProtectionKey` and requires a deployment
certificate with its private key, or an explicitly configured shared encrypted provider.
Azure/custom persistence without SQL requires a custom shared provider; there is no silent
ephemeral fallback for durable storage.

Set `FabrCore:DataProtection:CertificatePath` and, when needed, `CertificatePassword` through
protected configuration. Keep the application identity stable across restarts and silos;
retain previous certificates during rotation. Manual-schema installations must apply the
[key-ring migration](docs/migrations/data-protection.sql) before enabling the service.
This protection is separate from application authentication cookies and does not require
adding a login UI. See [connection setup and protection](docs/connections-and-microsoft-integration.md#host-setup).

## Database startup and durable operations

- The feature database must already exist and support the SQL Server 2025 / Azure SQL vector
  and graph schemas. Startup initializes required tables and applies GraphRAG migrations.
  `FabrCore:Database:AutoInitialize=false` validates pre-provisioned schemas instead. Both paths
  validate required tables and columns, graph node/edge kinds, actual 1536-dimensional vector
  columns, and applicable migration versions. Incompatible schemas fail startup with the affected
  object identified; existing data is never silently rebuilt.
- Memory and GraphRAG initialization use a 90-second command timeout for their 60-second schema
  lock waits. Startup cancellation reaches built-in migrations and SQL commands; migration
  transactions roll back on failure and lock cleanup runs independently of cancellation.
  Existing initializer and migration entry points remain available through compatible overloads.
- Memory hot-index writes share the scope mutation lock. Entity updates, deletes, and hot-index
  updates use the existing scope/name/type index to avoid cross-scope heap-scan deadlocks.
  Pre-provisioned databases must retain the enabled `IX_MemoryEntity_Scope_Name_Type` index.
- SQL mode validates required model configuration and credential aliases without paid inference.
  Configure `default` chat and `embeddings`; GraphRAG requires 1536-dimensional embeddings.
  Extraction model resolution uses an explicit override, then `graphrag`, then `default`.
- Optional per-feature connection overrides support existing split databases. Database selection
  requires a restart. Invalid configuration, missing schemas, or failed initialization never
  silently downgrade a SQL host to standalone mode.
- Readiness checks include ACL initialization and feature database connectivity. ACL evaluation
  uses immutable cached snapshots; serialized, transactional writes update the version and
  invalidate snapshots through write notifications and refresh behavior.

SQL mode now selects durable defaults for three operational features, separate from ordinary
message/LLM monitoring. Explicit custom providers remain supported.

| Feature | 2.0 SQL behavior |
| --- | --- |
| Security audit | Stable record IDs, conflict detection, filtered cursor pagination, and explicit pruning |
| Verifiable execution | Atomic evidence/signature/public certificate-chain storage, coordinated per-trace sequencing, and immutable retry validation |
| A2A tasks | Durable accepted/working/terminal snapshots, ownership checks, cross-host reads/cancellation, and execution leases that fence stale writers |

The stores use the `fabrOps` schema and do not fall back to in-memory storage on SQL failure.
Audit writes retain their nonthrowing contract and report failures through logging/counters;
there is no automatic retry spool or retention job. Evidence signing must still be enabled
explicitly; SQL persistence alone does not sign records, and private keys are not stored there.

A2A persistence stores task snapshots, not replayable SSE event streams. Interrupted execution
is failed rather than automatically replayed. Terminal task retention defaults to one hour;
active tasks are not evicted by terminal retention.

## Harness, context compaction, and token management

The two compaction layers now have distinct responsibilities: Microsoft Agent Framework
compaction reduces the working context for each model call, while FabrCore compaction creates
and saves a validated handover for older conversation history. Neither requires long-term Memory.

### Per-model-call working context

- Compaction runs inside the function-invocation loop for both harnesses and standard SDK-created
  agents, so tool-heavy turns are checked before subsequent model calls. Recall and other
  invocation-level providers retain their once-per-invocation behavior.
- Older tool results retain a bounded head and tail: the default limit is 2,048 characters at
  50% of the input working set, tightening to 512 at 80%. Full original tool output remains in
  persisted history.
- User text, instructions, assistant prose, handovers, and the latest two interaction groups
  are protected. Tool calls and results preserve their pairing and identifiers.
- `ContextWorkingSetTokens` / `_ContextWorkingSetTokens` can set a smaller working context,
  capped by the physical context window minus output reservation. Protected oversized content
  stops explicitly when it cannot fit; compaction targets are not guaranteed hard caps.
- Transient framework compaction indices reset for each history invocation and are excluded
  from snapshots, preventing stale state after same-length history rewrites.

### Durable conversation history

- Automatic durable compaction uses 70% of a configured input working set, with a 75% fallback
  when that setting is absent. Physical context checks reserve model output space.
- Summarization requires model context metadata, reserves summary output plus headroom, and
  processes complete interaction groups within conservative UTF-8-aware budgets.
- Summary reduction is bounded to eight passes and 64 model calls, with no-progress detection.
  `_CompactionModelConfigName` or `CompactionConfig.SummaryModelConfigurationName` can select a
  separate summarization model.
- A single validated history write preserves original instructions, the latest user message,
  and the latest interaction group. Empty, incomplete, length-limited, canceled, oversized,
  nonreducing, malformed-tool, or concurrently stale results leave the original history intact.
- Historical handovers use assistant context rather than system authority. Legacy compaction
  messages are demoted when read.

Token estimation now accounts for UTF-8 content, instructions, tool definitions, and framing.
Harness narration/delegation instructions are leaner. Background-session cleanup cancels local
work and releases resources without recreating evicted state; background waits have a configurable
timeout through `_HarnessBackgroundWaitTimeoutSeconds` (300 seconds by default).

These changes improve budgeting and correctness; they do not establish a universal token-saving
percentage or guarantee semantic summary fidelity. Custom memory-aware compaction callbacks
remain separate opt-in implementations. See [compaction correctness](docs/compaction-correctness.md)
and [harness efficiency](docs/harness-efficiency.md) for safeguards, tests, and tuning limits.

## A2A and channel integration

- Updated the existing built-in endpoints, agent cards, JSON-RPC/HTTP+JSON handling, and SSE
  streams to A2A 1.0, including task listing, version negotiation, and 1.0 response shapes.
- Task access checks include the creating principal, exposed agent, and caller identity,
  including persisted snapshots. Task IDs are server-generated; a new turn uses a new task.
- Shared named bindings align agent configuration across channels. Invocation uses the caller's
  principal grain, stamps the trusted sender, and applies existing cross-principal ACL checks.
- Concurrent provisioning is deduplicated and failed provisioning can be retried.
- Microsoft 365 app packages use manifest 1.25. Attachment-only messages receive an explicit
  explanation; streaming delivers progress and the completed reply, not individual model tokens.
- `FabrCore.Host.Testing` continues to provide in-memory Host/A2A helpers for testing exposure,
  authentication, and routing without a running Orleans silo.

Push notifications and extended cards remain unsupported. Cancellation ends the A2A wait
and updates task state; it does not interrupt work already running inside an agent. See
[A2A hosting](docs/a2a.md) and [channel identity](docs/channel-agent-identity.md) for details.

## Connections and remote agents

- `FabrCore.Connections` supplies provider-neutral contracts, user/admin API clients, and
  client-side handoff encryption. `FabrCore.Services.Connections` adds opt-in profile storage,
  protected authorization, resource token acquisition, endpoints, and blueprint expansion.
- Connections belong to principals. An agent needs both an alias binding and an explicit
  full-handle grant on the profile. Delegated connections require matching ownership;
  application connections can explicitly grant agents owned by another principal.
- Supported flows include user authorization code with PKCE, application client credentials,
  on-behalf-of exchange, and separately enabled Entra Agent ID exchanges. Profiles contain
  credential references; client applications own login, consent, and callback handling.
- SDK HTTP clients renew authorization per request and restrict destinations to the configured
  resource base. HTTP MCP can use the same connection/resource binding. Disconnecting or
  replacing authorization invalidates clients and remote conversation bindings.
- Optional encrypted client handoffs use single-use challenges and validated user proof to
  carry authorization operations through a cloud broker without plaintext codes or assertions.
- `FabrCore.Services.RemoteAgents` exposes Work IQ A2A and Copilot Studio conversations as
  ordinary FabrCore handles, restricted to the owning principal. Conversation/task state uses
  agent storage. Work IQ supports streaming progress, structured artifacts, and task status,
  resume, and cancel operations; Copilot Studio uses its client SDK.
- The `connectedAgents` blueprint extension reuses connection bindings without embedding
  credentials or performing consent/provisioning during preview.

Connections, remote agents, Entra Agent ID, and encrypted handoffs require their respective
opt-ins. Entra directory provisioning and provider consent remain deployment responsibilities.
The Copilot Studio adapter uses the pinned `1.3.171-beta` client. Protocol and isolation tests
do not replace live tenant validation of login, renewal, consent, and selected remote providers.
See [connections and Microsoft integration](docs/connections-and-microsoft-integration.md).

## Cloud administration, diagnostics, and monitoring

- Added vendor-neutral administration APIs and `FabrCoreAdministrationClient`, with capability
  discovery and an authenticated OpenAPI document. They work directly where enabled or through
  the existing outbound cloud command transport; Forge/Insights is not required.
- Agent management supports configuration, restart/reset, state/thread maintenance, and
  operation receipts. Blueprint management supports conditional writes, validation, expansion
  previews, deployment receipts, selected-agent retries, and definition drift reporting.
- Conditional revisions prevent stale writes. Stable operation IDs expose running, completed,
  failed, or incomplete outcomes so clients can inspect uncertain mutations before retrying.
- Operator-owned diagnostic sessions use a separate transcript and read-only, target-bound
  tools. Production tools, MCP execution, connection access, and state mutations are excluded.
  Explicit test-message/test-event operations still invoke normal agent behavior.
- Added paged principal/ACL administration, filtered monitor queries, cursor/gap reporting,
  bounded payload reads, and immutable chunked evidence exports with verification metadata.
- Optional `FabrCore:Monitoring:Provider=sql` adds queued SQL monitoring, retention, and health
  counters. This is separate from SQL-default audit/evidence stores: ordinary monitoring is
  not automatically made durable by enabling SQL mode. Durability begins after a successful
  flush; saturated queues can drop records and report that loss.

Manual-schema deployments must apply the [monitoring migration](docs/migrations/monitoring.sql)
before selecting SQL monitoring. Observability reports retain provider/source scope and cannot
prove completeness across offline hosts. See [cloud administration](docs/cloud-administration.md)
and the [reference cloud server](samples/FabrCore.ReferenceCloud/README.md).

## C# scripting plugins

The optional `FabrCore.Scripting` package lets developers derive from `CSharpScriptingPluginBase`
and configure exact NuGet versions, imports, instructions, and execution limits. Agents discover
the inherited `GetScriptingEnvironment` and `ExecuteCSharp` tools through the plugin registry.
Each call runs in a fresh .NET process with JSON input, a structured result, bounded console
output, compiler/runtime diagnostics, and optional file artifacts.

Prepared environments cache dependencies and worker assets, without sharing execution state.
Preparation requires the .NET 10 SDK and package access; deployments can prewarm the cache and
disable runtime preparation. Execution requires the .NET 10 runtime on the matching platform.
Timeout/cancellation, output/artifact bounds, and best-effort memory supervision are configurable.
SDK tool resolution now retains disposable configured plugins until proxy teardown.

**The local worker is not a security sandbox.** It runs with the host OS user's permissions;
use external OS/container isolation for untrusted workloads. Package selection is trusted
developer configuration, and timeouts do not undo external side effects. See the
[scripting guide](docs/scripting.md) and [console sample](samples/FabrCore.Scripting.Sample).
The [SampleApp](samples/FabrCore.SampleApp/README.md) also includes a Surface scripting agent
and a deterministic integration test covering its real worker, calculations, and report artifact.

## Memory and GraphRAG

Memory now has explicit lifecycle/context-provider integration through `WithMemory` and
`WithMemoryLifecycle`, stronger scope policies for internal agents, and scope-constrained writes.
Private specialists can use their own memory or read core memory while writing their own scope;
core writes require explicit selection. Candidate handling, correction workflows, and retry
behavior have additional coverage.

Default recall selects headers and loads primary chunks; the hot index uses bounded pointers.
Warm selection defaults to five memories from a scan of up to 200 headers; the hot index defaults
to 20 entries and a 3,000-token budget. Automatic consolidation and summary trees remain off. Semantic/hybrid
retrieval, diversity, chunk/evidence expansion, previews, and planning experiments remain opt-in.
See [Memory release defaults](docs/memory-release-defaults.md) and the
[readiness review](docs/memory-readiness-review.md) before enabling experimental paths.

GraphRAG extraction now separates lossless source sections from overlapping vector chunks,
with configurable batching, input budgets, concurrency, and bounded malformed/truncated-response
retries. Small documents can combine graph extraction and taxonomy classification; larger
documents use graph batches and a separate sampled-source classification pass.

Additional improvements include optional structured JSON-schema output, relationship guidance,
evidence quotes and source-span provenance, endpoint repair, instruction-hash tracking, and
ingestion/extraction performance metrics. Schema migrations accompany the new persisted metadata.
Memory and GraphRAG evaluation workflows now support more detailed baseline, cost, fidelity,
relationship-direction, conditional-policy, and provenance checks. Live quality results still
depend on the selected model, corpus, and enabled options.

## Configuration and discovery

- Application/reference assembly discovery avoids missing agents and tools in deployed builds.
  `RegistryAssemblies` provides an explicit registry override; `AdditionalAssemblies` extends discovery.
- Local model configuration resolves through `IFabrCoreModelConfigurationResolver` and the
  configuration store, avoiding authenticated loopback HTTP. Remote model configuration endpoints
  remain protected, with redirects disabled on the resolver client.
- Cloud Server configuration includes bootstrap/provider/policy support, a settings catalog,
  live-versus-restart-required behavior, and last-known-good/readiness handling. Long polling has
  isolated HTTP resilience behavior. Cloud Server and Forge remain optional.
- Runtime reports distinguish cloud-desired values, resolved configuration, and values observed
  in supported providers/options consumers. Reports include source/rule information, process
  identity, sequence, rejected revisions, verification status, and pending restart.
- `FabrCoreServerOptions.ConfigureRuntime` registers named pure rules with defaults, overrides,
  and final-value constraints. Standard environment/command-line inputs retain final precedence;
  competing code owners are rejected. Rules run during registration, cloud refresh, and preview.
- The `configuration-state: 1` capability, heartbeat reports, and settings state/preview endpoints
  are available to independent cloud servers. Preview resolves a candidate without applying it
  or replaying arbitrary startup callbacks. Reports are bounded and redact secrets.
- The reference server demonstrates report ingestion, inspection, and eligible applied-value
  adoption into a draft. Reports never rewrite desired settings automatically; preview does not
  promise universal application, rollback, or storage migration. The fixture is single-process
  and is not a durable production broker.

See [configuration reconciliation](docs/cloud-configuration-reconciliation.md) and the
[open wire contract](docs/cloud-configuration-state-protocol.md) for implementation boundaries.

## Builds, packaging, and validation

- `builds/Projects.psd1` is the shared inventory for thirteen supported packages and test projects.
  Build, pack, and tag publishing use that inventory, including both VSTest and
  Microsoft.Testing.Platform projects.
- Deterministic tests are separated from SQL integration tests and live evaluations. SQL/evaluation
  scripts resolve paths independently of the caller's working directory and restore environment settings.
- Release previews avoid repository mutations. Real releases require the expected branch and a clean
  tree, use fast-forward-only updates, and fail before publishing when validation fails.
- NuGet publishing reads `NUGET_API_KEY`; release workflows validate stable tags and run tests before
  publishing. Local package creation and push previews use the same supported package list.
- Explicit `-Version` selects the exact build/package set. Automatic local versions advance beyond
  stable tags and the local feed; push rejects incomplete sets. `Pack-Local.ps1` skips tests, and
  `Push-NuGet.ps1` pushes then immediately unlists packages. Public stable releases use the tag workflow.
- Local validation commands cover isolated SQL-mode/Orleans streaming tests, deterministic
  Memory/GraphRAG SQL integration suites, real scripting workers and documentation links.
  Scripting integration requires SDK/package access and is excluded from the offline filter.
  Paid model evaluations remain separate.
- Package smoke tests restore the produced packages with a fresh cache, verify package identities
  and internal dependencies, compile README examples, and exercise standalone/SQL startup,
  readiness, agent discovery, and storage across SQL host restart.
- Run Release-Develop, then Major/Minor/Patch. One tag-triggered GitHub job builds, runs offline
  tests, packs and publishes to NuGet.org. There are no separate branch/PR validation workflows
  or prerequisite validation jobs.
- The SDK is pinned by `global.json` with patch roll-forward. MinVer is centralized at 8.0.0;
  evaluation dependencies are pinned and MSTest SDK/runner versions are aligned. The Aspire sample
  uses matching AppHost SDK/package versions and its CLI bundle.
- Shipped/unshipped public API baselines cover the published packages alongside Orleans RPC contract
  checks. Public API changes require an explicit baseline update and compatibility review.

Validation commands, prerequisites, and report locations are documented in
[build instructions](builds/README.md). Passing counts belong to individual runs; these notes do
not claim that live provider evaluations ran as part of release validation.

The source targets .NET 10, Orleans 10.3.1, and Microsoft Agent Framework 1.20.0.
See [build and release instructions](builds/README.md) for reproducible commands.
