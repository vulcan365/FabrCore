# FabrCore 2.0.0

These consolidated release notes cover changes after `v1.7.0`, including the intervening
1.7.x and 1.8.x development and the current 2.0 changes. They replace the previous cumulative
release log; earlier history remains available in Git and [historical release documentation](docs/releases).

FabrCore 2.0 simplifies hosting around two modes: a standalone, trusted workspace with no
database, and a SQL-enabled host with persistent runtime state, enforced ACL, Memory,
GraphRAG, and durable operational stores. This is a breaking package and configuration release.

## Highlights

- Added Orleans RPC contract build checks, persisted-JSON compatibility tests, Orleans telemetry,
  and opt-in SQL streams using the Orleans 10.3.1-alpha.1 provider. See the
  [Orleans adoption guide](docs/orleans-10.3-adoption.md) for migration and client configuration.
- Consolidated SQL Server, Memory, GraphRAG, and service contracts into Host, Core, and SDK.
- Added explicit standalone/SQL behavior, relational ACL storage, and ACL migration tooling.
- Added built-in A2A hosting and durable SQL task snapshots, audit records, and execution evidence.
- Updated Microsoft Agent Framework from 1.16.0 at `v1.7.0` to 1.20.0 and corrected both
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

### Choose the runtime mode explicitly when upgrading

- **No FabrCore database connection:** the default host is a trusted standalone workspace.
  Cross-principal agent communication does not require ACL grants. Authentication, privileged
  administration authentication, and storage/session ownership checks still apply. Runtime state,
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

## Database startup and durable operations

- The feature database must already exist and support the SQL Server 2025 / Azure SQL vector
  and graph schemas. Startup initializes required tables and applies GraphRAG migrations.
  `FabrCore:Database:AutoInitialize=false` validates pre-provisioned schemas instead.
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

- Added built-in A2A endpoints with agent cards, JSON-RPC/HTTP+JSON handling, streaming,
  cancellation, task lookup, and resubscription support.
- Added configurable discovery descriptions/capabilities, hidden-agent filtering, handle patterns,
  and per-agent overrides, with API-key/JWT authentication and principal-resolution strategies.
- Added asynchronous `IA2APrincipalResolver.ResolvePrincipalHandleAsync` and shared channel-agent
  binding behavior, including canonical Entra principal handling across channels.
- Improved Copilot Studio interoperability for JSON-RPC and streaming response shapes.
- Added `FabrCore.Host.Testing` with in-memory Host/A2A test helpers.
- Fixed Microsoft 365 streaming and Teams proactive-message delivery issues, and corrected
  Surface chat header foreground/background styling.

See [A2A hosting](docs/a2a.md) for configuration and protocol behavior.

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

## Builds, packaging, and validation

- `builds/Projects.psd1` is the shared inventory for nine supported packages and test projects.
  Build, pack, release, and CI workflows use that inventory, including both VSTest and
  Microsoft.Testing.Platform projects.
- Deterministic tests are separated from SQL integration tests and live evaluations. SQL/evaluation
  scripts resolve paths independently of the caller's working directory and restore environment settings.
- Release previews avoid repository mutations. Real releases require the expected branch and a clean
  tree, use fast-forward-only updates, and fail before publishing when validation fails.
- NuGet publishing reads `NUGET_API_KEY`; release workflows validate stable tags and run tests before
  publishing. Local package creation and push previews use the same supported package list.
- Current validation includes a Release build, 971 passing deterministic tests, eight passing SQL
  integration tests, and inspection of all nine generated packages and internal dependencies.
  Live model evaluations are separate from those deterministic checks.

The source targets .NET 10, Orleans 10.3.1, and Microsoft Agent Framework 1.20.0.
See [build and release instructions](builds/README.md) for reproducible commands.
