# Building an ACL Management UI for FabrCore

This guide is for developer teams building an administration UI on top of FabrCore's access
control (ACL) and security audit platform. It covers the full API surface, the data model and
how to present it, the screens/features a complete management UI needs, and the best practices
and pitfalls that should shape your UX decisions.

For the underlying concepts and server-side architecture, see the
[fabrcore-acl skill](skills/fabrcore-acl/SKILL.md).

---

## 1. The mental model your UI must convey

FabrCore authorizes **subjects** performing **actions** on **resources**:

- **Subjects** — a principal (`"alice"`), a specific agent (`"alice:agent1"`), a role, or a group.
- **Actions** — effect-less permission stems in dot notation: `agent.message`, `agent.create`,
  `surface.adminview` (apps can define their own).
- **Resources** — `"principal:agent"` handle patterns: `p2:agent3`, `p2:*`, `*:agent5`, `*:*`,
  `prefix*`, `group:<name>:*`.
- **Grants** tie the three together with an effect: `agent.message.allow` or `agent.message.deny`.

Evaluation order (show this in your UI's help/decision-explainer):

```
Disabled mode → allow everything
System principal → allow everything
Explicit DENY grant → deny            (deny always wins)
Same principal as target → allow      (implicit; principals own their agents)
Explicit ALLOW grant → allow
Otherwise → deny                      (deny by default)
```

Key consequences to design around:

- **Cross-principal traffic is denied until granted.** The default state of the system is safe;
  your UI's job is mostly *adding* access, rarely restricting it.
- **Deny beats the implicit same-principal allow** — this is how you disable a capability
  entirely (e.g. ban a principal from creating agents with `agent.create.deny` on `*:*`).
- **The System principal bypasses everything.** Never show it as "editable like the others" —
  see §4.1.

---

## 2. Authentication, identity, and who can use your UI

Every API call carries the caller's identity in the **`x-user-handle` header**. FabrCore does
**not** authenticate this header — authentication is the hosting layer's job (your gateway,
reverse proxy, or app login). Your UI must:

1. Authenticate its users by its own means (SSO, etc.).
2. Map each authenticated user to a FabrCore principal handle.
3. Send that handle as `x-user-handle` on every request.

Authorization applied by the server per call:

| Caller | Read endpoints (GET, `evaluate`, `check`, audit) | Mutations (PUT/POST/DELETE) |
|---|---|---|
| System principal (`Acl:SystemPrincipal`, default `system`) | ✅ bypass | ✅ bypass |
| Holder of `acl.manage.allow` (e.g. via the built-in `acl-admin` role) | ✅ | ✅ |
| Holder of `acl.read.allow` | ✅ | ❌ 403 |
| Anyone else | ❌ 403 | ❌ 403 |

**UI recommendations**

- Bootstrap problem: on a fresh system only System holds `acl-admin`. Your setup flow should
  run *as* System once, to create an admin role/group for real humans, then stop using the
  System handle day-to-day. Treat the System handle like a root credential.
- Probe capability at login: call `GET fabrcoreapi/acl/config` as the user. A 403 means
  read-denied → show "no access"; success without `acl.manage` (test with a cheap dry-run
  `POST evaluate` for action `acl.manage`) → render the UI read-only.
- Every management call is audited server-side with the caller's handle — display a banner in
  your admin UI saying so; it deters casual misuse.

---

## 3. API reference

Base URL: `{host}/fabrcoreapi`. All requests/responses are JSON. The SDK
(`FabrCore.Sdk.IFabrCoreHostApiClient`) wraps every endpoint below 1:1 if your UI backend is
.NET — method names are noted inline.

### 3.1 Status codes (uniform across endpoints)

| Code | Meaning | UI handling |
|---|---|---|
| 200 / 204 | Success | — |
| 400 | Validation failure (bad permission name, missing subject, malformed body) | Show the `Error` message inline on the form field |
| 403 | Caller lacks `acl.read`/`acl.manage` | Downgrade to read-only / show access-denied |
| 404 | Entity not found | Refresh list; another admin may have deleted it |
| 409 | Built-in protection (System principal, `acl-admin` role, dynamic groups) | Show as "built-in, protected" — don't offer the action at all (see §4) |
| 500 | Server error | Generic retry affordance |

### 3.2 Principals

| Method & route | SDK method | Notes |
|---|---|---|
| `GET acl/principals` | `GetAclPrincipalsAsync` | Full list |
| `GET acl/principals/{handle}` | `GetAclPrincipalAsync` | 404 if absent |
| `PUT acl/principals/{handle}` | `UpsertAclPrincipalAsync` | Route handle wins over body handle |
| `DELETE acl/principals/{handle}` | `DeleteAclPrincipalAsync` | 409 for System |
| `GET acl/principals/{handle}/roles` | `GetPrincipalRolesAsync` | **Effective** roles (direct + via groups + dynamic-group roles) |
| `GET acl/principals/{handle}/groups` | `GetPrincipalGroupsAsync` | Stored + dynamic memberships |

```json
// AclPrincipal
{ "handle": "alice", "displayName": "Alice", "roles": ["ops-reader"], "isSystem": false }
```

> **Important:** the principals list contains only principals that have been *explicitly
> registered in the ACL store* (bootstrap, seeds, or your UI). Principals exist and work in
> FabrCore without an `AclPrincipal` record — they only need one to carry direct role
> assignments. Your UI should not treat this list as "all principals in the system"; for that,
> use the agent-management API's principals listing and cross-reference. Offer "Add to ACL"
> for unregistered principals rather than implying they don't exist.

### 3.3 Roles

| Method & route | SDK method | Notes |
|---|---|---|
| `GET acl/roles` / `GET acl/roles/{name}` | `GetAclRolesAsync` / `GetAclRoleAsync` | |
| `PUT acl/roles/{name}` | `UpsertAclRoleAsync` | Grants inside are validated (400 on bad permission name) |
| `DELETE acl/roles/{name}` | `DeleteAclRoleAsync` | 409 for built-in `acl-admin` |

```json
// AclRole — grants carried by a role have the role as implicit subject (subject omitted)
{
  "name": "surface:admin",
  "description": "Surface addon administrators",
  "grants": [ { "permission": "surface.adminview.allow", "resource": "*:*" } ],
  "isBuiltIn": false
}
```

### 3.4 Groups

| Method & route | SDK method | Notes |
|---|---|---|
| `GET acl/groups` / `GET acl/groups/{name}` | `GetAclGroupsAsync` / `GetAclGroupAsync` | |
| `PUT acl/groups/{name}` | `UpsertAclGroupAsync` | 409 if you try to create a dynamic group or put members on one |
| `DELETE acl/groups/{name}` | `DeleteAclGroupAsync` | 409 for dynamic groups |
| `POST acl/groups/{name}/members` | `AddAclGroupMemberAsync` | Body: `{ "kind": "Principal" \| "Agent", "handle": "..." }` |
| `DELETE acl/groups/{name}/members?kind=&handle=` | `RemoveAclGroupMemberAsync` | Member identity via query params |

```json
// AclGroup
{
  "name": "ops-team",
  "description": "Operations staff",
  "members": [
    { "kind": "Principal", "handle": "ops-alice" },
    { "kind": "Agent", "handle": "ops-alice:dashboard" }
  ],
  "roles": [ "ops-reader" ],
  "isDynamic": false
}
```

**Dynamic groups** (`all-principals`, `all-agents` by default — names come from config):
`isDynamic: true`, empty member list, membership computed. Render them with a distinct badge,
show "membership: every principal / every agent (computed)", and disable member editing
entirely. Their `roles` and `description` remain editable — a role attached to `all-principals`
applies to *everyone*; make that consequence loud in the UI before saving.

### 3.5 Grants

| Method & route | SDK method | Notes |
|---|---|---|
| `GET acl/grants` / `GET acl/grants/{id}` | `GetAclGrantsAsync` / `GetAclGrantAsync` | |
| `PUT acl/grants/{id}` | `UpsertAclGrantAsync` | Generate a GUID id client-side for new grants |
| `DELETE acl/grants/{id}` | `DeleteAclGrantAsync` | |

```json
// PermissionGrant
{
  "id": "3f2a...",
  "subject": { "kind": "Principal", "selector": "p1" },
  "permission": "agent.message.allow",
  "resource": "p2:*"
}
```

- `subject.kind` ∈ `Principal | Agent | Role | Group`; `selector` is exact (agent selectors are
  full `"principal:agent"` handles). No subject wildcards — "everyone" is the `all-principals`
  group.
- `permission` must be a valid 3-dot name (`entity.behavior.allow|deny`, lowercase `[a-z0-9-]`
  segments) — server returns 400 otherwise. Validate client-side with the same regex:
  `^[a-z0-9][a-z0-9-]*\.[a-z0-9][a-z0-9-]*\.(allow|deny)$`.
- `resource` defaults to `"*:*"` when omitted.

### 3.6 Evaluate & check (your decision-explainer and app query surface)

| Method & route | SDK method | Purpose |
|---|---|---|
| `POST acl/evaluate` | `EvaluateAclAsync` | Full dry-run: outcome, mode, reason, **deciding grant** |
| `POST acl/check` | `CheckPermissionAsync` | Simplified boolean for app permissions |

```json
// POST acl/evaluate — request
{ "subjectPrincipal": "p1", "subjectAgent": "p1:agent1", "action": "agent.message", "resource": "p2:agent3" }

// response
{
  "allowed": true,
  "outcome": "Allow",            // Allow | Deny | NoMatchDeny | ImplicitSamePrincipal | SystemBypass | DisabledBypass
  "mode": "Enforce",
  "reason": "Allowed by grant 'agent.message.allow' for subject 'principal:p1' on 'p2:*'",
  "decidingGrant": { "id": "...", "subject": {...}, "permission": "...", "resource": "..." }
}
```

Dry-runs are **not enforced** and are audited with `wasEnforced: false` — safe to call freely
from the UI.

### 3.7 Configuration & enforcement mode

| Method & route | SDK method | Notes |
|---|---|---|
| `GET acl/config` | `GetAclConfigAsync` | System principal, configured/override/effective mode, dynamic group names, snapshot version |
| `PUT acl/config/enforcement-mode` | `SetAclEnforcementModeAsync` | Body `{ "mode": "Enforce" \| "AuditOnly" \| "Disabled" \| null }` — null clears the runtime override |

The runtime override persists across restarts and wins over fabrcore.json. Show all three
values (`configuredMode`, `modeOverride`, `effectiveMode`) — admins get confused when the
running mode differs from the config file and the UI is where they should learn why.

### 3.8 Audit

| Method & route | SDK method | Notes |
|---|---|---|
| `GET audit/events?category=&outcome=&subject=&since=&limit=` | `GetAuditEventsAsync` | Newest first; limit clamped to 1000 (default 100) |
| `GET audit/config` | `GetAuditConfigAsync` | Provider type, `recordingAvailable`, levels, buffer size |
| `POST audit/clear` | — | **Development environments only** (403 otherwise) |

```json
// AuditEvent
{
  "id": "…", "timestamp": "2026-07-07T18:12:03Z",
  "category": "AclDecision",       // AclDecision | AclManagement | AgentCreation | BoundaryCrossing | Bootstrap
  "outcome": "Denied",             // Success | Denied | Error
  "subjectPrincipal": "p1", "subjectAgent": "p1:agent1",
  "resourcePrincipal": "p2", "resource": "p2:agent3",
  "permission": "agent.message",
  "enforcementMode": "Enforce", "wasEnforced": true,
  "reason": "No grant permits 'p1:agent1' to agent.message on 'p2:agent3'",
  "details": { "messageId": "…" },
  "traceId": "4bf92f3577b34da6a3ce929d0e0e4736",
  "verifiableExecutionId": null
}
```

---

## 4. Screens & features a complete UI needs

### 4.1 Entity browsers (principals, roles, groups, grants)

- Standard list/detail/edit for each entity type. All list endpoints return the full set —
  there is **no server-side paging**; page/filter/sort client-side. This is fine at the
  intended policy scale (see §6); if your grant list has 50k rows, the *policy* is wrong, not
  the UI (link admins to the guidance in §6).
- **Built-in protection surfaced as UX, not errors**: `isSystem`, `isBuiltIn`, `isDynamic`
  flags mean "no delete button, badge shown", not "let them click and show a 409". Still
  handle 409 gracefully — a second admin may race you.
- Names/handles are case-insensitive unique ids. Normalize display, prevent duplicate-by-case
  creation client-side.
- **Upsert semantics**: PUT replaces the entity. For member-level changes on groups, prefer the
  dedicated member endpoints over PUT-with-full-body — they're safe under concurrent edits of
  other fields; whole-entity PUT is last-writer-wins.

### 4.2 The grant builder (get this one right)

The grant form is the heart of the UI. Free-text fields here generate the most support load;
build structured pickers:

1. **Subject picker** — tabs or segmented control for Principal / Agent / Role / Group.
   Populate from the corresponding lists (agents from the agent-management API). Show dynamic
   groups prominently — "Everyone (all-principals)" is the correct answer far more often than
   a specific principal.
2. **Action picker** — dropdown of well-known actions with human labels:
   `agent.message` ("send messages/events to"), `agent.create` ("create agents for"),
   `agent.reconfigure`, `agent.destroy`, `agent.read` ("view monitor data of"), `acl.manage`,
   `acl.read` — plus a free-text option for application-defined actions (validated by regex).
3. **Effect toggle** — Allow / Deny, with Deny visually alarming and a persistent hint:
   *"Deny overrides every allow, including a principal's implicit access to its own agents."*
4. **Resource builder** — structured: `[principal segment] : [agent segment]` with per-segment
   choices of *any* (`*`), *exact* (picker), *prefix*, or *group* (principal segment only).
   Show the composed pattern live, and preview matches ("this currently matches 37 agents").
5. **Pre-save dry-run** — before saving, call `POST evaluate` with a representative
   subject/resource and show the *current* outcome, so the admin sees "this is currently
   Denied; saving this grant will allow it." After save, re-evaluate and show the delta.

Provide **intent templates** that pre-fill the form for the common cross-talk cases:

| Template | Pre-filled grant |
|---|---|
| "Let agent X talk to agent Y" | Subject `Agent x`, `agent.message.allow`, Resource `y` |
| "Let principal P reach all of principal Q's agents" | Subject `Principal p`, `agent.message.allow`, Resource `q:*` |
| "Let principal P reach any principal's agent named N" | Subject `Principal p`, `agent.message.allow`, Resource `*:N` |
| "Let group G create agents for principal Q" | Subject `Group g`, `agent.create.allow`, Resource `q:*` |
| "Ban principal P from creating agents" | Subject `Principal p`, `agent.create.deny`, Resource `*:*` |

### 4.3 Effective-access views (the killer feature)

Raw entity lists don't answer the questions admins actually have. Build both directions:

- **"What can this principal/agent do?"** — on a principal's detail page, combine:
  effective roles (`GET principals/{h}/roles`), group memberships
  (`GET principals/{h}/groups`), and all grants whose subject resolves to it (client-side
  join over the grants list: direct subject match + its groups + its roles + dynamic groups).
  Then a "test access" widget that runs `evaluate` against a chosen target.
- **"Who can reach this target?"** — given a target agent/principal, list grants whose
  resource pattern matches it (reimplement the segment matcher client-side: `*`, `prefix*`,
  exact, `group:` — it's ~20 lines) and expand subjects to concrete principals where feasible.
- **Decision explainer** — a page wrapping `POST evaluate`: pick subject, action, resource →
  show outcome, the evaluation-order ladder with the deciding step highlighted, and a link to
  the `decidingGrant`. This turns "why is my agent blocked?" tickets into self-service.

### 4.4 Enforcement mode control

- Show effective mode as a global status chip in the header (Enforce = green shield,
  AuditOnly = yellow, Disabled = red).
- Mode changes are high-consequence: require confirmation with explicit copy —
  *Disabled*: "all access checks are off"; *AuditOnly*: "denials are logged but allowed".
- Every mode change lands in the audit log (`AclManagement`) — link the confirmation dialog to
  it ("this action is recorded").

### 4.5 Audit viewer

- **Filter bar** mapping 1:1 to the query params: category, outcome, subject, since, limit.
  Default view: last 100, all categories.
- **Category-aware rendering**:
  - `AclDecision` / `AgentCreation` — render as "subject → action → resource = outcome", with
    `wasEnforced` distinguishing *blocked* from *would-have-blocked* (AuditOnly). These two
    states must look different.
  - `BoundaryCrossing` — render as a chain: origin principal → sender → target, with hop count
    from `details.hops`. A **fan-out feed** filtered to this category is your surveillance
    view for the "agent contacted an agent it wasn't intended to" concern — consider a
    dedicated dashboard tile.
  - `AclManagement` — the admin change log: who changed what, when. Support filtering by
    subject to answer "what did this admin do?"
  - `Bootstrap` — rare; show in a system-events section.
- **Live tail**: poll `GET audit/events?since={lastSeen}` every few seconds. There is no
  websocket/SSE for audit (the `OnAuditEventRecorded` event is in-process, server-side only).
- **Show the buffer's nature**: call `GET audit/config` and display provider type,
  `recordingAvailable`, and `maxBufferedEvents`. The default in-memory provider is a bounded
  FIFO **lost on restart** — if `providerType == "InMemoryAuditProvider"`, show a persistent
  notice: "Audit history is in-memory (last {N} events, not durable). Configure a durable
  IAuditProvider for compliance." Do not build compliance features on the in-memory buffer.
- `traceId` correlates with OpenTelemetry traces and AgentMonitor messages — render it as a
  copy button or a deep link into your tracing tool.

### 4.6 System status / config panel

From `GET acl/config` + `GET audit/config`: system principal handle, configured vs override vs
effective mode, dynamic group names, **snapshot version**, audit provider/levels. The snapshot
version is useful operationally: it increments on every policy change, so displaying it (and
when it last changed) helps admins confirm "my change is live".

---

## 5. Data-consistency behaviors your UI must handle

- **Snapshot staleness (up to `CacheTtlSeconds`, default 30s).** Writes go through the single
  registry grain and refresh the *local* silo immediately, but other silos converge via
  change-notification or TTL. Consequences:
  - After a successful write, re-read via the API and trust the response — your write is
    durable and visible to subsequent API reads immediately.
  - But *runtime enforcement* on other silos may honor a revoked grant for up to the TTL.
    After a revocation, show: "Saved. Enforcement converges across the cluster within
    ~{CacheTtlSeconds}s." Don't file bugs when a denied-in-UI subject sends one more message.
- **No optimistic concurrency.** PUTs are last-writer-wins; there are no ETags. For
  multi-admin deployments, keep edit sessions short, re-fetch before edit, and prefer the
  granular member endpoints (§4.1).
- **List-then-detail races.** Between listing and opening detail, another admin may delete —
  handle 404 by returning to a refreshed list, not by erroring.
- **Never write `fabrcore-acl-*` storage containers directly** (via the Storage API). All ACL
  data flows through `fabrcoreapi/acl/*`. Direct storage writes bypass index maintenance and
  cache invalidation and will corrupt the registry's view.

---

## 6. Best practices to encode in the UI (not just document)

These are policy-shape rules; the UI is the right place to enforce them because bad shapes are
created one innocent form-submit at a time.

1. **Groups and roles first, individual grants last.** If an admin is creating the same grant
   with only the subject varying, they're building a bottleneck. Detect it: when saving a grant
   that is identical to ≥3 existing grants except for subject, suggest "create a group instead?"
2. **Prefer dynamic groups and wildcards over enumerations.** "Everyone can message system
   agents" is one grant on `all-principals` — not 20,000 grants. "Ops reads all tenants" is one
   grant on resource `tenant-*:*` if handles share a prefix.
3. **Entity counts should track policy concepts, not population.** Healthy: dozens of roles,
   dozens-to-hundreds of groups, hundreds-to-low-thousands of grants — regardless of whether
   the cluster has 100 or 20,000 principals. Consider a dashboard tile that shows entity counts
   with soft thresholds (e.g. warn past 5k grants) and links to this guidance. Very large
   *stored* groups (tens of thousands of members) work for reads but make each membership
   change rewrite a large document — prefer dynamic groups or prefix patterns when membership
   is derivable.
4. **Reserve deny for policy statements, not day-to-day management.** Deny is for "this subject
   must never do X" (it survives any future allow). Removing an allow is the normal way to
   revoke access. Surfacing every matching deny prominently in the effective-access view
   prevents "I granted it but it still doesn't work" confusion.
5. **App-defined (addon) vocabulary**: permissions in non-reserved entity space
   (`surface.adminview.allow`) and app-prefixed role/group names (`surface:admin`). Give addons
   their own filtered view (prefix filter on roles/groups/permission entity) so FabrCore
   built-ins and app policy don't visually blend. Reserved entities: `agent`, `principal`,
   `acl`, `system`, `fabrcore`.
6. **Seeds are first-boot only.** `Acl:Seed` in fabrcore.json applies once; after bootstrap,
   the API is the source of truth (seed drift only logs a warning). Your UI should state this
   wherever admins might expect config-file edits to take effect.

---

## 7. Pitfalls checklist

| Pitfall | What to do in the UI |
|---|---|
| Treating the ACL principals list as "all principals" | It only holds explicitly registered ones — cross-reference the agent-management API (§3.2 note) |
| Editing as `system` routinely | Bypasses all checks and normalizes root usage; push admins to `acl-admin`-granted identities |
| Expecting revocation to be instant cluster-wide | Show the convergence window (§5) |
| Building compliance reports on the default audit provider | In-memory FIFO, lost on restart — detect and warn (§4.5) |
| Letting users type raw permission strings | Validate with the 3-dot regex; use pickers (§4.2) |
| Deny grants created casually | Confirmation + explanation that deny overrides everything, including self-access (§4.2, §6.4) |
| Offering member editing on dynamic groups | 409 — disable in UI (§3.4) |
| Offering delete on System principal / `acl-admin` / dynamic groups | 409 — hide/disable with a "built-in" badge (§4.1) |
| Whole-entity PUT for group membership under concurrency | Use the member endpoints (§4.1, §5) |
| Per-subject grant sprawl | Detect duplication, steer to groups (§6.1) |
| `AuditOnly` mistaken for "off" | Distinct visual state; denials still logged with `wasEnforced: false` (§4.4, §4.5) |
| Assuming `evaluate` mutates anything | It's a safe dry-run; use it liberally for previews (§3.6) |

---

## 8. Feature checklist (summary)

A complete FabrCore ACL management UI ships:

- [ ] Login → principal-handle mapping; capability probe; read-only degradation
- [ ] Principal / Role / Group / Grant browsers with client-side search, built-in badges,
      and 400/403/404/409 handling per §3.1
- [ ] Group membership management via member endpoints; dynamic groups rendered as computed
- [ ] Grant builder with subject/action/effect/resource pickers, live pattern preview,
      pre-save dry-run, and intent templates
- [ ] Effective-access views (per-subject and per-target) + decision explainer on `evaluate`
- [ ] Enforcement-mode control with confirmations and a global status chip
- [ ] Audit viewer: filters, category-aware rendering, enforced-vs-would-be distinction,
      boundary-crossing/fan-out feed, live-tail polling, in-memory-provider warning
- [ ] System status panel: config, snapshot version, audit provider
- [ ] Guardrails from §6 (grant-sprawl detection, deny confirmations, entity-count tile)
