# Cloud administration for FabrCore 2.0

This administration surface is vendor-neutral. Vulcan365 Insights is one client;
the contracts and `FabrCoreAdministrationClient` have no Insights dependencies.
The existing [outbound connection protocol](transport.md) carries
bounded HTTP requests. WebSockets and diagnostic token streaming are deferred.
Existing audit APIs remain unchanged.

## Authentication and addressing

All paths below are relative to `/fabrcoreapi/admin/v1`. Authenticate with the
configured administration credential. The trusted cloud server establishes
`X-FabrCore-Admin-Actor` from its authenticated operator identity; never copy it
from an untrusted request. It must authorize the operator for the cluster before
queuing a command. The cluster credential is an administrative trust boundary.
Route parameters identify the target; agent handles in target routes are local
handles. Encode each path segment once.

Preserve status codes, `If-Match`, `ETag`, command IDs, lease tokens, response
headers, expiration, and binary bodies through the broker. An expired or lost
response to a mutation is uncertain execution. Read its operation receipt;
never automatically resubmit side effects with a new ID.

## Discovery and compatibility

Read `GET /capabilities` before rendering controls. `host-admin` advertises
`agent-management`, `blueprint-management`, `admin-conversations`, `operations`,
and provider-dependent `monitor-query`, `evidence-query`, and `acl-conditional`.
Older hosts retain their existing endpoints. Clients must hide unsupported
controls and explicitly label legacy observability as a bounded snapshot.
The authenticated `GET /openapi.json` endpoint describes installed administration
routes and request schemas. The typed SDK supplies concrete response contracts.

## Agents and blueprints

Use the prefix `/principals/{principal}`:

| Method and suffix | Purpose |
| --- | --- |
| GET `/agents/catalog` | Installed types, models, plugins, tools, skills, extension schemas |
| GET `/agents/{agent}/configuration` | Configuration, state, threads, busy flags and revision |
| POST `/agents/{agent}/actions/{action}` | Conditional configure/restart/reset/state/thread maintenance |
| PUT `/operations/{clientId}` | Submit create, ensure, configure, restart, reset, test-message, test-event or deploy |
| GET `/operations/{clientId}` | Poll running/completed/failed/incomplete receipt |
| GET `/blueprint-management/summaries` | Typed summary page, default 100, maximum 1,000 |
| GET `/blueprints/{name}` | Export a definition, including extension JSON |
| PUT/DELETE `/blueprint-management/{name}` | Save/delete using `If-Match` revision |
| POST `/blueprint-management/validate` | Validate and expand an unsaved definition |
| GET `/blueprint-management/{name}/preview` | Review saved revision and expansion digest |

For summary pagination pass `offset`, `limit`, and the first page's `revision`.
A 412 means restart pagination. Clone/import by saving the complete definition
under its destination name. For a new definition use `If-Match: "*"`; updates
use the existing summary revision. Management actions carry their configuration
snapshot `revision` in the request body. A stale revision returns 412; busy
agents return 409.

Deploy through an operation with `kind: "deploy"`, `blueprintName`, and
`deployment: { operationId, mode, revision, expansionDigest }`. Obtain both
digests from preview. `ensure` creates missing agents; `update` reconfigures
existing agents. Neither deletes agents omitted from the definition. Deployment
receipts preserve per-agent results. An interrupted receipt must be inspected
before an explicit retry using a new ID. Extension packages must implement
`IBlueprintPreviewExpander` to assert side-effect-free expansion. Ordinary
expanders remain available to legacy apply workflows.
For an explicit partial retry, include `agentHandles` containing only selected
handles from the reviewed expansion. Summary pages expose the last deployment
status/revision and definition drift. A partial retry status does not assert
that all other agents applied successfully. Definition drift compares stored
blueprint revisions; inspect runtime configurations to detect independent edits.

Restart reconstructs the proxy with configuration and stored state. Reset
clears threads/custom state. Eviction deletes runtime/persisted agent state;
it is not a temporary deactivation. Test messages/events deliberately invoke
normal behavior; diagnostic chat does not.

## Diagnostic sessions

Under `/principals/{principal}/agents/{agent}/admin-sessions`:

* POST `{ sourceThreadId: null | "thread-id" }` creates a server-generated session.
* GET lists the current operator's sessions; GET/DELETE `/{sessionId}` reads/deletes.
* POST `/{sessionId}/turns` accepts `{ turnId, message }` and returns a receipt.
* GET `/{sessionId}/turns/{turnId}` reads the persisted result and diagnostic transcript.

Use stable client turn IDs for retries. Reusing an ID with another message is
a conflict. There is one running diagnostic turn per target agent across all
sessions. Ordinary user processing can proceed concurrently. Stop-waiting in
the UI cancels polling, not the accepted turn. The server applies a default
120-second deadline (`FabrCore:Administration:TimeoutSeconds`, 1–600 seconds).
`FabrCore:Administration:Model` overrides the target model alias.

`_admin` is privileged dispatch only. Ordinary messages/events with `_admin`
or `_debug` are rejected. Diagnostic tools are read-only, target-bound and
exclude production tools, MCP execution and state mutations. Explanations use
captured evidence, not hidden model reasoning. Missing capture is not proof
that no error occurred.

Source history is copied at creation (maximum 1 MiB), including a supported
live history snapshot when available. Other threads/newer records can be read
explicitly. Working-context compaction does not change the source. Sessions
use the user-scoped `fabrcore.admin-sessions` store, outside normal thread state.
Storage durability follows the configured provider; Localhost in-memory storage
does not survive process restart. Limits are 100 sessions per operator/agent,
100 turns/session, and 16,000 input characters/turn. Large diagnostic reads
report a limit instead of silently pretending to have complete evidence.

## ACLs

`GET /principals` returns a paged union of registered and runtime-discovered
principals, retaining inactive registrations and indicating each source. Use
`offset`, `limit` and `revision` for directory paging. Its revision is a directory
snapshot hash; obtain the numeric ACL version from `/access/metadata` for writes.

`GET /access/entities/{principals|roles|groups|grants}` supports `offset`, `limit`
and `version`. Individual GET, PUT and DELETE append `/{id}`. Conditional writes
require `If-Match` with the registry version; the serialized registry checks it
inside the mutation. Membership and role assignments are fields of principals
and groups. Built-in protection and existing enforcement/effective-access APIs
remain in force. Runtime discovery is available separately at `/runtime/principals`;
combine it with registered principals without discarding inactive registrations.
Conditional enforcement changes use PUT `/access/entities/enforcement/mode`
with `{ "mode": null }` to restore the host default or an explicit mode value.

## Monitoring and evidence

`GET /observability/monitor` accepts principal, agentHandle, traceId, kind,
channel, executionCategory, from/to timestamps, cursor, limit and includePayload.
Filters apply before paging. Cursors are opaque and scoped to a source; keep
filters unchanged while paging. `gap` requires an explicit refresh. The final
cursor can retrieve subsequent records. Fetch large payloads at
`/observability/monitor/payload/{sequence}?offset=0`, following `nextOffset`.
Pass the page's `sourceId` when reading payloads so a provider restart cannot
silently substitute another record at the same sequence number.
Offsets count UTF-16 characters. Health reports source identity and retention/
queue information; token summaries explicitly cover provider lifetime.

Query silo-local providers separately; query shared providers once per opaque
source identity. Report offline hosts and gaps. Do not infer cluster completeness
from a successful local query. Insights defaults to manual refresh and permits
ten-second visible-view polling with at most four concurrent host requests.

Opt into SQL monitoring with `FabrCore:Monitoring:Provider=sql` and a configured
FabrCore operational database. Defaults: seven-day retention, 10,000 queued
records, 64 MiB queued bytes, 250-record batches, one-second flush. Tune
`RetentionDays`, `QueueRecords`, `QueueBytes`, `BatchSize` under that section.
Recording enqueues without awaiting SQL; saturated queues drop records and
increment health counters. Durability starts after a successful flush. Legacy
clear affects the recent in-memory view; SQL retention controls durable deletion.
Provision the additive [SQL migration](../assets/monitoring.sql) before enabling
SQL in manual-schema mode. Auto-initialization provisions it with operational DDL.

Discover `/observability/evidence/traces`, then POST
`/observability/evidence/{traceId}/exports`. The immutable export manifest gives
revision SHA-256, chunk count, bytes and verification. GET
`/observability/evidence/exports/{id}/{chunk}` returns base64 bytes. Concatenate
in order and check the manifest hash. DELETE the export afterward. Verification
does not establish that all cluster records were available. Preserve original
records/signatures/certificates/attestations; never rewrite them when aggregating.

## Release validation

Run Host, SDK, client and Insights suites, then SQL integration against an
isolated database (`FABRCORE_SQL_TEST_PASSWORD` and optional
`FABRCORE_SQL_TEST_SERVER`). Exercise offline hosts, queue saturation, SQL outage,
retention, tampering, actor ownership, overlapping turns and restart recovery.
The less-than-5% throughput/p95 regression target is a release gate requiring a
controlled multi-silo load run; unit tests do not establish that result.
