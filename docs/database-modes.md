# Standalone and SQL modes

FabrCore.Host includes the agent runtime, SQL Server provider, Memory, GraphRAG,
and ACL implementations. A database is optional. SQL client assemblies are shipped
with Host, but standalone startup does not register SQL services or connect to SQL.

## Standalone

```csharp
var builder = WebApplication.CreateBuilder(args);
builder.AddFabrCoreServer();
var app = builder.Build();
app.UseFabrCoreServer();
app.Run();
```

Configure your model access in `fabrcore.json`. Install `FabrCore.Surface` when you
want a Blazor chat UI; the sample app demonstrates Host and Surface in one process.
An automation server needs only Host. Agent libraries reference SDK, which brings
Core transitively and has no SQL implementation dependency.

Standalone is a trusted workspace: agents can interact across principal handles
without grants. Authentication, privileged admin API authentication, and existing
storage/session ownership checks remain in effect. ACL management endpoints and
SQL-only agents/plugins are unavailable. Requests for SQL-only agents or plugins
produce a feature-unavailable error.

Agent state, conversations, typed storage, delivery checkpoints, and reminders have
in-memory lifetime. A process restart loses them. Local configuration and files
retain their existing file-based behavior. Ordinary context compaction, harnesses,
tools, MCP, blueprints, skills, squads, and Surface chat do not require long-term
Memory or GraphRAG.

## SQL

Supply one connection through configuration or a secret manager:

```json
{
  "ConnectionStrings": {
    "FabrCore": "Server=localhost;Database=fabrcore;Integrated Security=true;TrustServerCertificate=true"
  }
}
```

This enables SQL Orleans clustering/persistence/reminders, enforced ACL, Memory,
GraphRAG, and their administration endpoints together. It does not automatically
attach memory to every agent: keep selecting memory plugins and scopes per agent.
The capability endpoints describe the active feature set consistently.

The combined feature set uses the existing SQL Server 2025/Azure SQL vector and
graph schemas. GraphRAG currently requires 1536-dimensional embeddings. Configure
an `embeddings` model and a `default` chat model, plus any explicit Memory model
overrides. GraphRAG resolves an explicit extraction model, then `graphrag`, then
`default`. Model configuration and credential aliases are checked at startup;
startup does not make paid model inference requests.

The database must already exist. FabrCore initializes tables and applies existing
GraphRAG migrations. For a pre-provisioned database, set:

```json
{
  "FabrCore": {
    "Database": {
      "ConnectionStringName": "FabrCore",
      "AutoInitialize": false
    }
  }
}
```

Validate-only startup checks required schemas and GraphRAG migration versions.
Database and feature selection requires a process restart. Missing or invalid
configured connections, schema failures, and missing required model configuration
fail startup; a failed SQL deployment never silently becomes a standalone host.
Readiness also requires an initialized ACL snapshot and reachable feature databases.

For existing split databases, the optional `MemoryConnectionStringName`,
`GraphRagConnectionStringName`, and `AclConnectionStringName` properties under
`FabrCore:Database` override individual connections. These require the main SQL
connection to be configured.

An explicitly configured Orleans clustering mode/provider takes precedence over
the SQL runtime default. Existing Orleans connection overrides remain supported.
This permits Azure/custom Orleans storage with SQL-backed knowledge and ACL data.
Selecting SQL Orleans storage alone without the main feature connection does not
enable the knowledge/ACL feature set.

SQL mode also selects durable security audit, verifiable execution, and A2A task
stores. These are Host features in the `fabrOps` schema, independent of Orleans
providers. General message/LLM monitoring keeps its existing providers.
Memory/GraphRAG feature audit logs retain their SQL storage.

## Durable operational features

The built-in defaults become `SqlAuditProvider`, `SqlVerifiableExecutionStore`,
and `SqlA2ATaskStore` when the main feature database is configured. Standalone
hosts keep their in-memory implementations. Custom audit/evidence providers and
an explicitly registered A2A task store remain supported. A SQL storage failure
never switches to in-memory storage.

`FabrCore:Database:OperationsConnectionStringName` optionally selects a separate
configured connection for all three operational stores. No Orleans configuration
changes are needed. `AutoInitialize` also controls the versioned `fabrOps` schema:
initialization is transactional and serialized across hosts; validate-only startup
rejects missing schemas. Provision with schema-creation privileges, then run with
the table read/write permissions needed by the enabled features and application
lock permissions. No private signing keys are stored in these tables.

Security audit records survive restarts, retain event IDs, and reject conflicting
replacements. `/fabrcoreapi/audit/events` supports resource and trace filters plus
stable pagination: pass the last event's timestamp and ID as `before` and
`beforeId`. The SDK exposes `QueryAuditEventsAsync` with `AuditQuery`. Audit's
existing non-throwing write contract remains: failed writes are logged and counted
in the protected audit configuration endpoint (`FailedWrites`, `LastFailureUtc`).
These counters cover the current process lifetime; failed events are not queued
or replayed. Reads propagate storage failures. SQL audit does not use
`MaxBufferedEvents`; an administrator can explicitly prune old events in bounded
batches with `SqlAuditProvider.DeleteBeforeAsync`. There is no automatic audit
deletion policy.

Execution records, signatures, and public certificate chains commit atomically.
The recorder coordinates sequence allocation and signing across hosts for each
trace segment. Conflicting replacements and stale chain predecessors are rejected;
identical retries are accepted. Attestations are immutable too. Evidence has no
automatic retention deletion. Existing signing and verification settings still
apply: SQL persistence alone does not turn unsigned records into signed evidence,
and SPIFFE remains optional. The SQL store is append-only through its API, not an
immutable archive against database administrators; export and verify signed bundles
when independent assurance is required.

A2A saves acceptance before returning a task ID, then working state before agent
execution. A persisted owner fingerprint retains the existing caller/principal/
agent access checks. Reads work across hosts; cancellation can be requested on a
different host. The execution host renews a 60-second lease every 10 seconds and
checks remote cancellation. A lost lease fences subsequent writes. Expired work
is marked failed when task storage is next read or maintained, with an interruption
explanation. Work is never automatically replayed because external effects may
already have occurred. Cancellation cannot undo those effects.

Terminal notifications and blocking responses wait for the terminal persistence
attempt; a failed attempt reports failure rather than successful completion.
Durable task reads show committed state. Terminal snapshots cannot regress and
expire according to `A2A:Tasks:Retention` (default one hour); reads perform bounded
cleanup. Active tasks are not evicted by `MaxRetainedTasks`. Event streams remain
local to their execution host; SQL stores task snapshots, not a replayable event
stream. Task listing currently reads retained snapshots before applying protocol
ownership filters and pagination, so size retention for expected task volume.

## ACL administration and migration

ACL entities live in relational `acl` tables, independent of the Orleans storage
provider. The registry serializes changes; SQL transactions update entities and the
version atomically. Hosts evaluate immutable snapshots without SQL calls on the
message path, refreshing after local writes, stream notifications, or TTL checks.

Built-in system entities and default access to system agents are initialized by
code. Use the protected admin API to create your first administrator and maintain
principals, roles, groups, memberships, and grants. Configure
`FabrCore:AdminAuthentication:ApiKey` using a secret source; no Forge account is
required. JSON ACL seeds/rules are rejected with migration guidance.

Before upgrading an existing installation:

1. Back up databases and export ACL data from the old running host:
   `./scripts/Migrate-Acl.ps1 -Mode Export -HostUrl https://old-host -Path acl-export.json`
2. Configure the new SQL database and remove ACL seed/rule sections from startup
   configuration. Keep existing Memory/GraphRAG connections if they are separate.
3. Start the new host, then import into its built-in-only ACL installation:
   `./scripts/Migrate-Acl.ps1 -Mode Import -HostUrl https://new-host -Path acl-export.json`

Set `FABRCORE_ADMIN_API_KEY` for each host before invoking the script. An old JSON
configuration containing `FabrCore:Acl:Seed` can also be used as the import input.
The import validates references, preserves exported grant IDs, and commits the
whole import transactionally. It refuses to overwrite an installation containing
user-defined ACL data. Source files and the old backing store are not modified.
Empty exported grants remain empty instead of reintroducing demo grants.

## Package upgrade

This is a breaking release; rebuild every consuming project and package.

| Retired package | New owner |
| --- | --- |
| FabrCore.Services.Contracts | FabrCore.Core |
| FabrCore.Services.Memory | FabrCore.Host; shared interfaces/models in Core, remote clients in SDK |
| FabrCore.Services.GraphRag | FabrCore.Host; shared administration contracts in Core |
| FabrCore.Host.SqlServer | FabrCore.Host |

Existing namespaces, agent/plugin aliases, HTTP routes, and Orleans grain identities
are retained where applicable. Disabled feature routes return 404. No forwarding
packages are published. Remove manual Memory/GraphRAG registration from application
startup: `AddFabrCoreServer()` now performs integrated registration.

Azure Storage, Surface, transport clients, testing helpers, and Microsoft 365
integration remain separate packages. No additional feature packages are introduced.

Run `scripts/Test-SqlMode.ps1` against a local SQL Server 2025 container to exercise
the SQL integration tests in a uniquely named temporary database.
