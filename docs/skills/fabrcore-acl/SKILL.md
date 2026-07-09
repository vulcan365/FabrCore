---
name: fabrcore-acl
description: >
  FabrCore access control and security audit — principals, roles, groups, permission grants
  in 3-dot notation (entity.behavior.allow/deny), cross-principal agent-to-agent enforcement,
  enforcement modes (Disabled/AuditOnly/Enforce), the unrestricted System principal, dynamic
  groups (all-principals, all-agents), the ACL management REST API and SDK client methods,
  application-defined permissions/roles for addons, and the pluggable IAuditProvider security
  audit with the in-memory default.
  Triggers on: "ACL", "access control", "permission grant", "PermissionGrant", "AclPrincipal",
  "AclRole", "AclGroup", "acl-admin", "acl.manage", "enforcement mode", "AuditOnly",
  "cross-principal", "cross talk", "boundary crossing", "System principal", "dynamic group",
  "IAuditProvider", "audit events", "security audit", "IAclEvaluator", "agent.message.allow".
  Do NOT use for: agent lifecycle (fabrcore-agent), message routing mechanics (fabrcore-messaging),
  signed evidence/SPIFFE (fabrcore-spiffe), message monitoring (fabrcore-agentmonitor).
allowed-tools: "Bash(dotnet:*) Bash(mkdir:*) Bash(ls:*) Bash(pwsh:*) Bash(powershell:*) Bash(git:*) Bash(dir:*)"
metadata:
  author: FabrCore
  version: 1.0.0
  documentation: https://fabrcore.ai/docs
---

# FabrCore Access Control (ACL) & Security Audit

Full access-control platform for FabrCore: **principals**, **roles**, **groups**, and
**permission grants**, enforced at every principal-initiated and agent-to-agent messaging
boundary, persisted through the standard storage abstraction, managed via REST/SDK, and
observable through a pluggable security audit provider.

## Overview

- **Deny by default across principals.** A principal (and its agents) always has full access to
  its own agents; anything cross-principal requires an explicit grant.
- **Permissions use 3-dot notation:** `entity.behavior.effect` — e.g. `agent.message.allow`,
  `agent.create.deny`. Effects are only `allow` and `deny`; **deny overrides allow**.
- **Agent-to-agent (a2a) traffic is enforced.** The first cross-principal hop is authorized
  sender-side; once a message lands at another principal's agent, further hops run as that
  principal (checked again if they cross another boundary) and are audited via a breadcrumb —
  warned, never blocked.
- **The System principal is unrestricted** and bypasses all checks. Its handle is set in
  fabrcore.json (`Acl:SystemPrincipal`, default `"system"`).
- **Zero-config works.** With an empty fabrcore.json you get: secure defaults, an auto-created
  System principal, dynamic groups, in-memory audit, and (by default) a seeded grant letting
  every principal message/read the System principal's agents so the shared-demo experience
  keeps working.

## Architecture

| Component | Type | Purpose |
|---|---|---|
| `IAclEvaluator` / `AclEvaluator` | `FabrCore.Core.Acl` / Host service | Synchronous, snapshot-backed decision engine (safe inside grain turns; never I/O) |
| `AclEnforcer` | Host service | Wraps evaluator + audit; throws `AclDeniedException` in Enforce mode; stamps breadcrumbs |
| `IAclSnapshotProvider` / `AclSnapshot` | Core contract | Immutable indexed view of all entities, swapped atomically per version |
| `IAclEntityStore` / `GrainBackedAclEntityStore` | Core contract / Host service | Async CRUD; per-silo snapshot cache with change-stream + TTL refresh |
| `AclRegistryGrain` | Orleans grain (single activation, key `"acl"`) | Sole writer; persists entities + indexes; publishes `AclChanged` notifications; bootstraps |
| `IAuditProvider` / `InMemoryAuditProvider` | `FabrCore.Core.Auditing` / Host service | Security audit sink (bounded FIFO default; `NullAuditProvider` to disable) |
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

## Enforcement Modes

| Mode | Behavior |
|---|---|
| `Enforce` (default) | Denials throw `AclDeniedException` (an `UnauthorizedAccessException`) and are audited |
| `AuditOnly` | Would-be denials log a warning + audit event, then proceed. Monitor **read filtering still filters** (data exposure ≠ message flow) |
| `Disabled` | No evaluation; everything allowed; per-call decisions are not audited |

Configured via `Acl:Mode`; changeable at runtime via `PUT fabrcoreapi/acl/config/enforcement-mode`
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

## Configuration (fabrcore.json)

```json
{
  "Acl": {
    "SystemPrincipal": "system",
    "Mode": "Enforce",
    "AllPrincipalsGroupId": "all-principals",
    "AllAgentsGroupId": "all-agents",
    "CacheTtlSeconds": 30,
    "SeedDefaultSystemAgentAccess": true,
    "Seed": {
      "Principals": [ { "Handle": "alice", "DisplayName": "Alice (dev)", "Roles": [ "ops-reader" ] } ],
      "Roles": [
        { "Name": "ops-reader", "Grants": [ { "Permission": "agent.read.allow", "Resource": "*:*" } ] }
      ],
      "Groups": [
        { "Name": "partners", "Members": [ "principal:p1", "agent:p1:agent1" ] }
      ],
      "Grants": [
        { "Subject": "principal:p1", "Permission": "agent.message.allow", "Resource": "p2:*" },
        { "Subject": "agent:p1:agent1", "Permission": "agent.message.allow", "Resource": "p2:agent3" }
      ]
    }
  },
  "FabrCore": {
    "Audit": {
      "DefaultLevel": "Failures",
      "Categories": { "AclDecision": "All" },
      "MaxBufferedEvents": 10000
    }
  }
}
```

The `Acl` section binds at the root or under `FabrCore:Acl`. **Seeds apply on first bootstrap
only** — after that, manage entities via the API (seed drift logs a warning).

Zero-config defaults: System = `"system"`, `Enforce`, in-memory audit at `Failures` level,
own-principal traffic allowed, cross-principal denied, all-principals → message/read on
`system:*` seeded (disable with `SeedDefaultSystemAgentAccess: false`).

**Migration note:** the legacy rule-based shape (`Acl:Rules` / `Acl:Groups` with
`UserHandlePattern`/`CallerPattern`/`AclPermission` flags) no longer binds and logs a startup
warning. Mapping: `Message`→`agent.message.*`, `Configure`→`agent.create.*` /
`agent.reconfigure.*` / `agent.destroy.*`, `Read`→`agent.read.*`, `Admin`→`acl.manage.*`;
`CallerPattern` becomes the grant Subject (`"*"` → group `all-principals`), and
`UserHandlePattern:AgentPattern` becomes the Resource.

## Bootstrap & Secure Defaults

`AclRegistryGrain.EnsureBootstrappedAsync()` runs at startup (driven by the
`GrainBackedAclEntityStore` hosted service, with retry until the cluster is ready). Idempotent:

1. Read the bootstrap marker (`fabrcore-acl-meta/bootstrap`); done if current schema version.
2. Upsert the System principal (+ `acl-admin` role assignment).
3. Upsert the dynamic groups.
4. Upsert the `acl-admin` built-in role.
5. If the grant store is empty and `SeedDefaultSystemAgentAccess`: seed the system-agent grants.
6. Apply `Acl:Seed` entities.
7. Write the marker **last** (a crash mid-bootstrap re-runs cleanly), emit a `Bootstrap` audit event.

## Storage Layout

Entities persist through `IUserScopedFabrCoreStorageProvider` (the same backend as the Storage
API) under the `system` scope:

| Container | Keys |
|---|---|
| `fabrcore-acl-principals` | principal handle (lowercased) |
| `fabrcore-acl-roles` / `-groups` / `-grants` | role name / group name / grant id |
| `fabrcore-acl-meta` | `index/{container}`, `config` (version + mode override), `bootstrap` |

**Do not write these containers directly** (via StorageController or `IFabrCoreStorageProvider`)
— that bypasses index maintenance and cache invalidation. The registry self-heals dangling
index entries on activation, but direct writes are unsupported.

## Management REST API

All endpoints read the caller from `x-user-handle`. Mutations require `acl.manage.allow`;
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
FabrCore API surface. Authentication is the hosting layer's job (gateway/proxy). Anyone who can
reach the API claiming the System handle has full control — protect the endpoint accordingly.

## SDK Client Methods (`IFabrCoreHostApiClient`)

CRUD: `Get/Upsert/DeleteAclPrincipal(s)Async`, `...AclRole(s)...`, `...AclGroup(s)...`
(+ `AddAclGroupMemberAsync` / `RemoveAclGroupMemberAsync`), `...AclGrant(s)...`.
Queries: `GetPrincipalRolesAsync`, `GetPrincipalGroupsAsync`, `IsPrincipalInRoleAsync`,
`CheckPermissionAsync`, `EvaluateAclAsync`. Config: `GetAclConfigAsync`,
`SetAclEnforcementModeAsync`. Audit: `GetAuditEventsAsync`, `GetAuditConfigAsync`.
Every method takes `callerUserHandle` (sent as `x-user-handle`).

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
planned for a later phase — v1 has a single `acl.manage` permission.

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
- **Default**: `InMemoryAuditProvider` (bounded FIFO, `MaxBufferedEvents`, lost on restart) —
  chosen so denials are visible out of the box and `AuditOnly` mode is meaningful.
  `options.UseNullAuditProvider()` disables recording; production should use
  `options.UseAuditProvider<MyDurableSink>()` (database, SIEM, event hub).
- **Push**: subscribe to `IAuditProvider.OnAuditEventRecorded` for live viewers.

REST (`fabrcoreapi/audit`, gated like ACL reads): `GET events?category=&outcome=&subject=&since=&limit=`,
`GET config`, `POST clear` (Development environments only).

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

- **Cross-principal send throws `AclDeniedException`** — expected default. Add a grant
  (`agent.message.allow`) for the sender subject and target resource, or set `Mode: AuditOnly`
  while developing.
- **Legacy `Acl:Rules` warning at startup** — the old rule shape is ignored; migrate (see
  Configuration above).
- **Grant added but still denied on another silo** — snapshot staleness; wait `CacheTtlSeconds`
  or lower it.
- **403 from `fabrcoreapi/acl/*`** — caller lacks `acl.manage`/`acl.read`; call as the System
  principal or grant the role/permission.
- **409 on delete** — built-in protection (System principal, `acl-admin` role, dynamic groups).
- **No audit events** — check the provider isn't `NullAuditProvider` (`GET fabrcoreapi/audit/config`,
  `RecordingAvailable`) and the category's level isn't filtering successes.
