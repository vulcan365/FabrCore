---
name: fabrcore-acl
description: "Configure and manage FabrCore 2.0 relational ACL and security audit: principals, roles, groups, permission grants, enforcement modes, cross-principal access, administration APIs and custom audit providers. Use for ACL migration and SQL versus standalone authorization behavior; use fabrcore-spiffe for signed evidence."
allowed-tools: "Bash(dotnet:*) Bash(mkdir:*) Bash(ls:*) Bash(pwsh:*) Bash(powershell:*) Bash(git:*) Bash(dir:*)"
metadata:
  author: FabrCore
  version: 2.0.0
  documentation: https://fabrcore.ai/docs
---

# FabrCore Access Control (ACL) & Security Audit

## FabrCore 2.0 baseline

FabrCore 2.0 GA enables relational ACL with ConnectionStrings:FabrCore. Without the feature database, standalone trusts cross-principal messaging and ACL administration is unavailable; authentication and storage/session ownership checks still apply. SQL mode persists ACL in the acl schema and security audit in fabrOps. JSON seeds/rules are migration input only and are rejected at normal startup.

Full access-control platform for FabrCore: **principals**, **roles**, **groups**, and
**permission grants**, enforced at every principal-initiated and agent-to-agent messaging
boundary in SQL mode, persisted in relational ACL tables, managed via REST/SDK, and
observable through a pluggable security audit provider.

## Overview

- **In SQL mode, deny by default across principals.** A principal (and its agents) always has full access to
  its own agents; anything cross-principal requires an explicit grant.
- **Permissions use 3-dot notation:** `entity.behavior.effect` — e.g. `agent.message.allow`,
  `agent.create.deny`. Effects are only `allow` and `deny`; **deny overrides allow**.
- **Agent-to-agent (a2a) traffic is enforced.** Throughout this skill "a2a" is FabrCore's
  shorthand for *agent-to-agent messaging inside the cluster* — not the Agent2Agent (A2A)
  protocol, which is a separate ingress addon (**fabrcore-a2a**) whose callers land on a principal
  and are then subject to exactly these rules. The first cross-principal hop is authorized
  sender-side; once a message lands at another principal's agent, further hops run as that
  principal (checked again if they cross another boundary) and are audited via a breadcrumb —
  warned, never blocked.
- **The System principal is unrestricted** and bypasses all checks. Its handle is set in
  appsettings (`FabrCore:Acl:SystemPrincipal`, default `"system"`).
- **Database mode controls enforcement.** SQL initializes built-in System entities and optional
  default System-agent grants. Without the feature database, standalone bypasses ACL and
  does not expose ACL administration.

## Architecture

| Component | Type | Purpose |
|---|---|---|
| `IAclEvaluator` / `AclEvaluator` | `FabrCore.Core.Acl` / Host service | Synchronous, snapshot-backed decision engine (safe inside grain turns; never I/O) |
| `AclEnforcer` | Host service | Wraps evaluator + audit; throws `AclDeniedException` in Enforce mode; stamps breadcrumbs |
| `IAclSnapshotProvider` / `AclSnapshot` | Core contract | Immutable indexed view of all entities, swapped atomically per version |
| `IAclEntityStore` / `GrainBackedAclEntityStore` | Core contract / Host service | Async CRUD; per-silo snapshot cache with change-stream + TTL refresh |
| `AclRegistryGrain` | Orleans grain (single activation, key `"acl"`) | Serialized writer; transactionally persists relational entities/version; publishes `AclChanged` notifications; bootstraps |
| `IAuditProvider` / `InMemoryAuditProvider` | `FabrCore.Core.Auditing` / Host service | Security audit sink (SQL default in SQL mode, bounded FIFO in standalone; `NullAuditProvider` to disable) |
| `AclController` / `AuditController` | REST | `fabrcoreapi/acl/*`, `fabrcoreapi/audit/*` |
| `IFabrCoreHostApiClient` (ACL surface) | SDK | Typed client methods for everything above |

## Core Concepts

### Principals
`AclPrincipal { Handle, DisplayName, Roles[], IsSystem }`. The handle is the same string that
keys `PrincipalGrain`. The System principal is created at bootstrap, flagged `IsSystem`, cannot
be deleted (409), and bypasses evaluation entirely — treat its handle like a credential name.

### Permission names
`PermissionName` validates `entity.behavior.effect`: three lowercase `[a-z0-9-]` segments, the
last being `allow` or `deny`. Entities `agent`, `principal`, `acl`, `system`, `fabrcore` are
reserved for built-ins; everything else is application space. Built-in catalog (`FabrPermissions`):

| Permission | Enforced at |
|---|---|
| `agent.message.allow/deny` | PrincipalGrain sends/events; AgentGrain a2a sends/events |
| `agent.create.allow/deny` | `PrincipalGrain.CreateAgent` — resource's principal segment = "for whom" |
| `agent.reconfigure.allow/deny` | `PrincipalGrain.ResetAgent` |
| `agent.destroy.allow/deny` | `PrincipalGrain.UntrackAgent` |
| `agent.read.allow/deny` | Monitor read filtering (`MonitorController`, SSE stream) |
| `acl.manage.allow/deny` | ACL management API mutations |
| `acl.read.allow/deny` | ACL/audit read endpoints, evaluate/check |

### Grants
`PermissionGrant { Id, Subject, Permission, Resource }`.

- **Subject** (`AclSubject { Kind, Selector }`): exact selector for a `Principal` (handle),
  `Agent` (full `"principal:agent"` handle), `Role` (name), or `Group` (name). No wildcards —
  "everyone" is the dynamic `all-principals` group.
- **Resource**: pattern matched against `"principal:agent"`: `p2:agent3` (exact), `p2:*`,
  `*:agent5`, `*:*`, `prefix*` per segment, `group:<name>:*` (group ref on the principal
  segment). App permissions that need no resource use `"*:*"`.

Cross-talk examples (the three scopes):

| Intent | Grant |
|---|---|
| P1.Agent1 → P2.Agent3 only | Subject `agent:p1:agent1`, `agent.message.allow`, Resource `p2:agent3` |
| P1 (any of its agents) → all of P2 | Subject `principal:p1`, `agent.message.allow`, Resource `p2:*` |
| P1 → any principal's `agent5` | Subject `principal:p1`, `agent.message.allow`, Resource `*:agent5` |
| Let P1 create agents for P2 | Subject `principal:p1`, `agent.create.allow`, Resource `p2:*` |
| Ban P1 from creating agents at all | Subject `principal:p1`, `agent.create.deny`, Resource `*:*` |

### Roles
`AclRole { Name, Description, Grants[], IsBuiltIn }` — named grant sets. Assign to principals
directly (`AclPrincipal.Roles`) or to all members of a group (`AclGroup.Roles`). Built-in:
`acl-admin` (grants `acl.manage.allow` + `acl.read.allow` on `*:*`), assigned to System,
protected from deletion.

### Groups
`AclGroup { Name, Description, Members[], Roles[], IsDynamic }` — members are principals or
agents; flat (no nesting). Two **dynamic built-in groups** have computed membership:
`all-principals` (every principal) and `all-agents` (every agent). Their names are configurable
(`AllPrincipalsGroupId` / `AllAgentsGroupId`); member edits return 409.

### Evaluation order
```
Disabled bypass → System bypass → explicit DENY → same-principal implicit allow
→ explicit ALLOW → default deny
```
Deny sits **before** the same-principal implicit allow so `agent.create.deny` on `*:*` can
disable an action entirely for a subject. Zero-config behavior is unchanged (no deny grants
exist by default). Subject identities resolved per evaluation: the principal, the acting agent
handle (a2a only), stored group memberships, dynamic groups, and effective roles (direct +
via groups).

## Grants for A2A protocol callers

The A2A endpoints in `FabrCore.Host` (**fabrcore-a2a**) authenticate external A2A clients and map each
to a FabrCore principal (`A2A:Principal:Strategy`). From that point on it is an ordinary principal,
so the SQL-mode rules above apply — and the ACL implications follow from which strategy and which
exposure style you chose:

| A2A configuration | ACL consequence |
|---|---|
| `AgentTypes` + `Principal:Strategy = Fixed` (the default) | The addon provisions agents **under the caller's own principal**, so no grant is needed. Every A2A caller shares that one principal and therefore each other's agents and history |
| `AgentTypes` + `Strategy = ContextId` / `ApiKey` / `Claim` | Each caller gets its own principal with its own agents. Still no grant needed, and callers are isolated from one another. Prefer this for multi-tenant exposure |
| `AgentHandles` (for example `system:assistant`) | The A2A principal messages **another principal's** agent, so it needs `agent.message.allow` on that resource — unless a configured grant permits it (built-in System-agent access is enabled by default) |

Grant the A2A principal access to a shared agent owned by another principal (the default A2A
principal handle is `a2a`; `A2A:Principal:Prefix` and the non-`Fixed` strategies change it):

```csharp
await client.UpsertAclGrantAsync("system", new PermissionGrant
{
    Subject = new AclSubject(SubjectKind.Principal, "a2a"),
    Permission = FabrPermissions.AgentMessageAllow,
    Resource = "contoso:assistant"
});
```

With a per-caller strategy the subject is one principal per caller, so grant a **group** or **role**
instead of maintaining one grant per tenant.

Because an A2A principal is reachable from outside your cluster, treat its grants as an external
trust boundary: grant the specific resources it needs rather than `*:*`, prefer a per-caller
principal strategy over one shared principal, and test grant decisions before enabling external access.
The sending System principal bypasses ACL; targeting a System-owned agent uses the configured
System-agent grants. Broad shared exposure is permissive — do it deliberately.

## Enforcement Modes

| Mode | Behavior |
|---|---|
| `Enforce` (default) | Denials throw `AclDeniedException` (an `UnauthorizedAccessException`) and are audited |
| `AuditOnly` | Would-be denials log a warning + audit event, then proceed. Monitor **read filtering still filters** (data exposure ≠ message flow) |
| `Disabled` | No evaluation; everything allowed; per-call decisions are not audited |

Configured via `FabrCore:Acl:Mode`; changeable at runtime via `PUT fabrcoreapi/acl/config/enforcement-mode`
(persisted override; cleared by passing null).

## Cross-Principal Fan-out & the Breadcrumb

Concern: granting P1 access to P2.Agent3 means Agent3 may contact agents P1 was never granted.
That transitive hop runs as P2 and is **not blocked** — it is made visible instead:

- At the first cross-principal send, the host stamps `AgentMessage.CrossPrincipalOrigin` (the
  originating principal) and increments `CrossPrincipalHops`.
- The receiving agent's host stashes the breadcrumb; messages the agent composes while
  processing inherit it automatically (agent authors do nothing).
- If a tagged chain crosses a *second* principal boundary, the host logs a warning and emits a
  `BoundaryCrossing` audit event naming the origin, sender, target, and hop count.
- Same-principal hops of a tagged chain are logged at Debug.
- `Kind == Response` and system messages (`_status`/`_error`) are exempt from the a2a check so
  authorized request/reply round-trips can't be broken; the breadcrumb still flows on responses.

Enforcement identity always derives from the sending grain's key — `FromHandle` is spoofable
routing metadata and is never trusted for authorization.

## Configuration and relational storage

Enable SQL mode with `ConnectionStrings:FabrCore` from a secret provider. Configure host policy
in appsettings; principals, roles, groups and grants are administration data, not startup JSON:

```json
{
  "FabrCore": {
    "Acl": {
      "SystemPrincipal": "system",
      "Mode": "Enforce",
      "AllPrincipalsGroupId": "all-principals",
      "AllAgentsGroupId": "all-agents",
      "CacheTtlSeconds": 30,
      "SeedDefaultSystemAgentAccess": true
    },
    "Audit": { "DefaultLevel": "Failures", "Categories": { "AclDecision": "All" } }
  }
}
```

SQL startup initializes built-in System principal, dynamic groups, `acl-admin`, and configured
System-agent access. Entities and version live in relational `acl` tables, independent of the
Orleans provider. The registry serializes transactional writes and publishes changes; callers
read immutable snapshots. Do not write ACL tables or old Orleans containers directly.

`FabrCore:Database:AclConnectionStringName` optionally selects a split ACL connection.
`AutoInitialize=false` requires pre-provisioned schemas. Readiness includes ACL initialization
and database connectivity. `FabrCore:AdminAuthentication:ApiKey` protects the bootstrap/import
administration API; supply it through secrets.

### Upgrade existing ACL data

Normal startup rejects `FabrCore:Acl:Seed`, legacy `Acl:Seed`, and `FabrCore:Acl:Rules`.
Export from the old host before upgrading, retain a backup, then import into a built-in-only
2.0 SQL target using the migration utility bundled with
[fabrcore-releases](../fabrcore-releases/SKILL.md). Import preserves IDs, validates references,
and is transactional; it refuses to overwrite user-defined target data. An old seed JSON can
be used as import input, but is not ongoing configuration. Remove seeds/rules from startup
only after preserving migration input. Maintain ACL entities through the API thereafter.

## Management REST API

Available in SQL mode; standalone returns 404 for ACL administration. All endpoints read the caller principal from `x-user-handle` (legacy header name). Mutations require `acl.manage.allow`;
reads/evaluate/check require `acl.read.allow` or `acl.manage.allow`; System bypasses. Every
call emits an `AclManagement` audit event. Base route: `fabrcoreapi/acl`.

| Method | Route | Notes |
|---|---|---|
| GET/PUT/DELETE | `principals[/{handle}]` | 409 deleting the System principal |
| GET | `principals/{handle}/roles` | Effective roles (direct + groups + dynamic) |
| GET | `principals/{handle}/groups` | Stored + dynamic memberships |
| GET/PUT/DELETE | `roles[/{name}]` | 409 deleting built-in `acl-admin` |
| GET/PUT/DELETE | `groups[/{name}]` | 409 deleting/editing-members-of dynamic groups |
| POST | `groups/{name}/members` | Body: `{ "kind": "Principal"\|"Agent", "handle": "..." }` |
| DELETE | `groups/{name}/members?kind=&handle=` | |
| GET/PUT/DELETE | `grants[/{id}]` | Permission validated (400 on bad 3-dot name) |
| POST | `evaluate` | Dry-run: full decision + deciding grant |
| POST | `check` | `{ principal, action, resource? }` → simplified boolean result |
| GET | `config` | Modes, System principal, dynamic group names, snapshot version |
| PUT | `config/enforcement-mode` | `{ "mode": "Enforce"\|"AuditOnly"\|"Disabled"\|null }` |

**Security note:** identity is the trusted `x-user-handle` header, matching the rest of the
FabrCore API surface. Despite the name, this is the caller principal handle. Authentication is the hosting layer's job (gateway/proxy). Anyone who can
reach the API claiming the System handle has full control — protect the endpoint accordingly.

## SDK Client Methods (`IFabrCoreHostApiClient`)

CRUD: `Get/Upsert/DeleteAclPrincipal(s)Async`, `...AclRole(s)...`, `...AclGroup(s)...`
(+ `AddAclGroupMemberAsync` / `RemoveAclGroupMemberAsync`), `...AclGrant(s)...`.
Queries: `GetPrincipalRolesAsync`, `GetPrincipalGroupsAsync`, `IsPrincipalInRoleAsync`,
`CheckPermissionAsync`, `EvaluateAclAsync`. Config: `GetAclConfigAsync`,
`SetAclEnforcementModeAsync`. Audit: `GetAuditEventsAsync`, `GetAuditConfigAsync`.
Every method takes `callerUserHandle` (legacy parameter name; pass the caller principal handle, sent as `x-user-handle`).

```csharp
// Grant P1's agent1 cross-talk to P2's agent3 (as System / an acl-admin):
await client.UpsertAclGrantAsync("system", new PermissionGrant
{
    Subject = new AclSubject(SubjectKind.Agent, "p1:agent1"),
    Permission = FabrPermissions.AgentMessageAllow,
    Resource = "p2:agent3"
});
```

## Application-Defined Permissions, Roles & Groups (Addons)

Consuming applications can run their own authorization on the same platform — nothing in the
vocabulary is FabrCore-specific except the reserved entities:

- **Permissions**: any non-reserved `entity.behavior.allow/deny` name, e.g.
  `surface.adminview.allow`, `chatapp.moderate.deny`. Use resource `"*:*"` when scoping isn't
  needed.
- **Roles/groups**: created via the same CRUD API. Convention: prefix names with your app
  (e.g. role `surface:admin`, group `surface:moderators`) to avoid collisions.
- **Grant the addon's service principal** `acl.read.allow` (queries) and, if it manages its own
  roles, `acl.manage.allow`, once at setup.

Worked example — an addon with user and admin views (SurfaceAdmin):

```csharp
// One-time setup (as System or an acl-admin):
await client.UpsertAclRoleAsync("system", new AclRole
{
    Name = "surface:admin",
    Grants = { new PermissionGrant { Permission = "surface.adminview.allow", Resource = "*:*" } }
});
var alice = await client.GetAclPrincipalAsync("system", "alice") ?? new AclPrincipal { Handle = "alice" };
alice.Roles.Add("surface:admin");
await client.UpsertAclPrincipalAsync("system", alice);

// At runtime, the addon gates its UI (surface-svc holds acl.read.allow):
var check = await client.CheckPermissionAsync("surface-svc", "alice", "surface.adminview");
if (check.Allowed) { /* render admin view */ }
// or role-based:
var isAdmin = await client.IsPrincipalInRoleAsync("surface-svc", "alice", "surface:admin");
```

Per-namespace delegated management (an app admin who can only manage `surface:*` entities) is
planned for a later phase — 2.0 has a single `acl.manage` permission.

## Security Audit

Pluggable provider model mirroring the AgentMonitor pattern.

- **`AuditEvent`**: category, outcome, subject principal/agent, resource (+ its principal),
  permission, enforcement mode, `WasEnforced`, reason, details, `TraceId` (joins OpenTelemetry
  traces), optional `VerifiableExecutionId` (populated from the message's verifiable-execution
  envelope when enabled — see fabrcore-spiffe for tamper-evident trails).
- **Categories**: `AclDecision`, `AclManagement`, `AgentCreation`, `BoundaryCrossing`, `Bootstrap`.
- **Levels** (`FabrCore:Audit`): `None` / `Failures` / `All` — `DefaultLevel: Failures` with
  per-category overrides (`AclManagement`/`BoundaryCrossing`/`Bootstrap` default to `All`).
  Providers apply `AuditOptions.ShouldRecord`; emit sites always record.
- **Default**: `SqlAuditProvider` in SQL mode; `InMemoryAuditProvider` in standalone.
  SQL records in `fabrOps` survive restart, preserve IDs, and reject conflicting replacements.
  `MaxBufferedEvents` applies only to memory storage. Custom `UseAuditProvider<T>()` and
  `UseNullAuditProvider()` remain supported. Failed SQL writes log/count failure without a retry
  spool or in-memory fallback. Reads propagate storage failures; retention is explicit.
- **Push**: subscribe to `IAuditProvider.OnAuditEventRecorded` for live viewers.

REST (`fabrcoreapi/audit`, gated like ACL reads): `GET events?category=&outcome=&subject=&since=&limit=`,
`GET config`, `POST clear` (Development environments only). SQL cursor pagination uses `before`
and `beforeId` from the last event, with resource/trace filters. The SDK exposes
`QueryAuditEventsAsync` with `AuditQuery`. Explicit pruning uses `SqlAuditProvider.DeleteBeforeAsync`;
there is no automatic retention job. `FailedWrites`/`LastFailureUtc` counters cover this process.

## Custom Providers

```csharp
builder.AddFabrCoreServer(options => options
    .UseAclEvaluator<MyEvaluator>()      // must be synchronous + snapshot-backed — never I/O
                                         // or grain calls inside Evaluate (grain-turn hot path)
    .UseAuditProvider<MySiemAuditProvider>());
```

## Multi-Silo Consistency

Each silo caches an immutable `AclSnapshot` keyed by a monotonic version. Mutations go through
the single-activation `AclRegistryGrain`, which publishes an `AclChanged` stream notification
(best-effort); silos also poll the version every `CacheTtlSeconds` (default 30) as fallback,
and local writes refresh immediately. **A revoked grant may therefore be honored on another
silo for up to the TTL** — size `CacheTtlSeconds` to your revocation-latency tolerance.

## Troubleshooting

- **Cross-principal send throws `AclDeniedException`** — expected in SQL Enforce mode. Add a grant
  (`agent.message.allow`) for the sender subject and target resource, or set `Mode: AuditOnly`
  while developing.
- **Legacy ACL seeds/rules fail startup** - preserve/export the data, import into SQL ACL, and remove startup seed/rule sections.
- **Grant added but still denied on another silo** — snapshot staleness; wait `CacheTtlSeconds`
  or lower it.
- **403 from `fabrcoreapi/acl/*`** — caller lacks `acl.manage`/`acl.read`; call as the System
  principal or grant the role/permission.
- **409 on delete** — built-in protection (System principal, `acl-admin` role, dynamic groups).
- **No audit events** — check the provider isn't `NullAuditProvider` (`GET fabrcoreapi/audit/config`,
  `RecordingAvailable`) and the category's level isn't filtering successes.
