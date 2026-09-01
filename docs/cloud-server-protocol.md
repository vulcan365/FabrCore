# FabrCore Cloud Server Protocol (configuration v1 + remote administration v2)

The **Cloud Server** feature lets a FabrCore host pull its model/API-key configuration (the
`fabrcore.json` payload) from a remote server instead of the local file, and report periodic
heartbeats. This document is the vendor-neutral wire specification: anyone can implement the
server side. FabrCore Forge is the first-party implementation; a host configured with a
different conforming server behaves identically.

Protocol constants and DTOs ship in the `FabrCore.Core` NuGet package under
`FabrCore.Core.CloudServer` (`CloudServerProtocol`, `CloudConfigurationEnvelope`,
`CloudHeartbeatRequest`, `CloudHeartbeatResponse`). Server implementations may reference the
package or implement the JSON contract directly.

## Host configuration

The host enables the feature purely through `appsettings.json` — no `fabrcore.json` needed:

```json
{
  "FabrCore": {
    "HostUrl": "https://agents.example.com",
    "CloudServer": {
      "Enabled": true,
      "Url": "https://forge.vulcan365.ai",
      "ApiKey": "<per-cluster API key>",
      "ClusterId": null,
      "Environment": null,
      "Settings": { "Enabled": true }
    },
    "RemoteAdministration": {
      "Enabled": true,
      "PollWait": "00:00:20"
    }
  }
}
```

- `Url` defaults to the hosted Forge endpoint and is overridable for self-hosted servers.
- `ClusterId` defaults to the Orleans `ClusterOptions.ClusterId`.
- `Environment` defaults to `IHostEnvironment.EnvironmentName` (`ASPNETCORE_ENVIRONMENT`).
- Securing `ApiKey` (user secrets, environment variables, vault-backed configuration
  providers) is the operator's responsibility.
- Remote administration is disabled by default;
  `"RemoteAdministration": { "Enabled": true }` enables it. The dispatcher uses the existing
  `FabrCore:CloudServer:ApiKey`; there is no separate remote-administration credential.
  `PollWait` and `MaxBodyBytes` are optional tuning values.
- Remote administration requires `FabrCore:CloudServer:Enabled` to be true. A host cannot enable
  remote administration without also enabling its Cloud Server connection.
- `FabrCore:HostUrl` is the only remote-administration target. It must be an absolute http(s)
  URL reachable from the host process, and requests outside `/fabrcoreapi/` are rejected. The
  host logs a startup warning for a non-loopback URL because the Cloud Server API key then
  traverses the network.

See the `fabrcore-server` skill for the full option list (refresh interval, disk cache,
startup failure behavior, heartbeat settings).

## Conventions

- All endpoints are relative to the configured base URL.
- All JSON — requests and responses, including the nested configuration payload — is
  **camelCase** (`System.Text.Json` web defaults). Hosts parse case-insensitively.
- Paths are versioned (`/fabrcore-cloud/v1/...`). Breaking changes get a new path version;
  additive changes bump the envelope `schemaVersion`.
- A host rejects envelopes whose `schemaVersion` is greater than the version it supports and
  keeps its last-known-good configuration.

### Authentication headers (both endpoints)

| Header | Value |
|---|---|
| `Authorization` | `Bearer {apiKey}` — per-cluster API key issued by the server |
| `X-FabrCore-Cluster-Id` | The cluster identifier known to the server |
| `X-FabrCore-Environment` | The host's environment name, e.g. `Production` |

Servers should return identical `401` bodies for unknown clusters and invalid keys (no
enumeration oracle), and `403` for suspended tenants/clusters.

## GET /fabrcore-cloud/v1/configuration

Returns the **effective configuration** for the requesting cluster and environment. Servers
supporting appsettings-style layering merge a base document with an environment overlay; how
(and whether) a server layers configuration is a server-side concern — hosts always receive
the final result. An unknown environment is not an error: serve the base configuration (the
same semantics as a missing `appsettings.{Env}.json`).

Request headers: the auth headers above, plus optional `If-None-Match: "{configurationVersion}"`.

| Status | Meaning |
|---|---|
| `200` | Envelope below; `ETag` response header set to `"{configurationVersion}"` |
| `304` | Configuration unchanged — host keeps its current snapshot |
| `401` / `403` | Bad/revoked key or suspended tenant — host keeps last-known-good and backs off |
| `404` | No configuration published for this cluster |

Response envelope:

```json
{
  "schemaVersion": 1,
  "configurationVersion": "5b3e…9c (opaque server-defined string; doubles as the ETag)",
  "issuedAt": "2026-07-23T18:00:00Z",
  "configuration": {
    "modelConfigurations": [
      {
        "name": "default",
        "provider": "OpenAI",
        "uri": "https://api.openai.com/v1",
        "model": "gpt-5",
        "apiKeyAlias": "openai",
        "timeoutSeconds": 60,
        "maxOutputTokens": null,
        "reasoningEffort": null
      }
    ],
    "apiKeys": [
      { "alias": "openai", "value": "sk-…" }
    ]
  },
  "settings": null,
  "blueprints": [
    {
      "principalId": "operations",
      "applyOnRefresh": true,
      "blueprint": {
        "name": "ops-agents",
        "version": "7",
        "agents": [],
        "squads": []
      }
    }
  ]
}
```

- `configuration` is exactly the `FabrCore.Core.FabrCoreConfiguration` shape (all
  `ModelConfiguration` tuning fields are supported; the example above is abbreviated).
- `settings` is an optional map of flat IConfiguration keys (for example
  `"FabrCore:Host:WebSocketPath": "/ws"`) that the host layers into its own configuration. It is
  optional in both directions: a server may omit it, and a host may decline to consume it
  (`FabrCore:CloudServer:Settings:Enabled: false`). See
  [Cloud-delivered settings](#cloud-delivered-settings) below.
- `blueprints` is an optional list of principal-scoped canonical `FabrCoreBlueprint`
  deployments. On a new configuration version the host stores each blueprint and, when
  `applyOnRefresh` is true, applies it through the same host-side expander pipeline used by
  `/fabrcoreapi/Blueprint`. This is the fleet rollout path; omitting the field is backward
  compatible.
- `configurationVersion` must change whenever the effective configuration changes — including
  when only an environment overlay changed. A content hash of the merged document is a good
  implementation.

## Cloud-delivered settings

A host that consumes `settings` layers the map into its own `IConfiguration`, which lets a server
manage far more than model configuration — Orleans clustering, connection strings, access control,
timeouts, add-on channel configuration. This is what makes provisioning a new host a matter of
supplying one API key.

Three rules govern it. All are host-side obligations; a server needs only to publish the map.

### Precedence

The cloud layer sits **above `appsettings*.json` and below environment variables and command-line
arguments**:

```
appsettings.json  <  appsettings.{Environment}.json  <  cloud settings  <  environment variables  <  command line
```

Central configuration therefore beats a stale file on the machine, while an operator keeps a local
override that does not depend on reaching the server — which matters precisely when the thing being
corrected is a bad publish.

### Keys a server may never set

A host **must** refuse these, whatever a server sends, and should log each refusal:

| Key or section | Reason |
|---|---|
| `FabrCore:CloudServer` | Owns enrollment. A server that could rewrite its own URL, key, or `Enabled` flag could orphan or redirect an entire fleet with no local way back. |
| `FabrCore:RemoteAdministration` | Owns the outbound recovery channel. |
| `FabrCore:HostUrl` | The connect channel dispatches admin requests to this address, so a remotely settable value is an SSRF pivot. |

A host should also bound the payload (key count and total size) and reject malformed keys, rather
than trusting a server to be well-behaved.

### Timing, and settings that need a restart

Settings are fetched **during host construction**, before clustering and connection strings are
read, so a provisioning-critical value arrives in time to be used. That fetch is best-effort: if
the server is unreachable the host falls back to its last-known-good cache, and failing that
continues with local configuration — the ordinary startup path then applies
`StartupFailureBehavior` as usual.

Most host options are captured once at startup and cannot observe a later change. When a refresh
delivers a value that differs from the one the process started with, and its consumers cannot pick
the change up, the host does **not** pretend the change took effect: it keeps serving the startup
value and reports the key in `pendingRestartSettings` on the next heartbeat. Hosts never restart
themselves; when to restart is the operator's decision.

A host that consumes settings advertises `"config.settings": "1"` in its heartbeat capabilities, so
a server can tell whether publishing them will have any effect.

## POST /fabrcore-cloud/v1/heartbeat

Sent periodically by **each host instance** (one heartbeat per silo — `hostInstanceId`
disambiguates; servers see N heartbeats for an N-silo cluster). Heartbeat failures are never
fatal on the host: log-and-continue.

Request body:

```json
{
  "schemaVersion": 1,
  "clusterId": "my-cluster",
  "environment": "Production",
  "serviceId": "fabrcore-service",
  "hostInstanceId": "HOSTNAME:3f2a…",
  "hostVersion": "1.3.0",
  "appliedConfigurationVersion": "5b3e…9c",
  "appliedSettingsVersion": "5b3e…9c",
  "pendingRestartSettings": ["FabrCore:Orleans:ClusterId"],
  "activeGatewayCount": 2,
  "capabilities": {
    "host": "1.5.0",
    "config.settings": "1",
    "memory.admin": "1",
    "graphrag.admin": "1",
    "blueprint.squads": "1"
  },
  "timestamp": "2026-07-23T18:00:00Z"
}
```

`appliedSettingsVersion` and `pendingRestartSettings` are additive and present only when the host
consumes cloud settings. `pendingRestartSettings` lists keys whose published value is stored but not
yet in effect, so a server can show an operator that a change is waiting on a restart rather than
silently failing. Both are omitted when empty.

`capabilities` is an additive service/API-version map. Forge uses it to avoid rendering
features a cluster does not have. Authenticated operators can obtain the richer feature
document directly from `GET /fabrcoreapi/capabilities`.

Response — `200` with an optional body; an empty object (or empty body) is valid:

```json
{
  "refreshRequested": true,
  "latestConfigurationVersion": "7a1d…4e"
}
```

- `refreshRequested: true` makes the host fetch configuration immediately instead of waiting
  for its next scheduled refresh. This is the deliberate seam for near-real-time config push
  without a persistent connection.
- `latestConfigurationVersion` lets hosts (and operators reading logs) detect staleness from
  the heartbeat alone.
- Future protocol versions may add response members (for example a command list) additively.
  Hosts ignore unknown members.

## Outbound remote administration channel (v2)

The optional connect channel lets a hosted console administer a cluster without exposing
inbound network ports. Every network connection originates at the FabrCore host:

1. Forge (or another server implementation) durably queues an admin request.
2. A cluster silo receives it through a long poll.
3. The silo validates the command, sends it to the `/fabrcoreapi/` endpoint at the required
   `FabrCore:HostUrl` using `FabrCore:CloudServer:ApiKey`, and captures the response.
4. The silo posts that response back to the server. The server completes the waiting console
   request.

The cluster API key and standard cluster/environment headers authenticate both v2 endpoints.

### GET /fabrcore-cloud/v2/connect

Query parameters:

- `waitSeconds`: requested long-poll duration, from 1 to 25 seconds.
- `hostInstanceId`: the polling silo identifier used for command leasing and diagnostics.

Returns `204` when no command arrives during the poll, or `200` with:

```json
{
  "commandId": "3f84f4fc-9cb8-4f68-a6ab-c8e41f840a6e",
  "method": "GET",
  "pathAndQuery": "/fabrcoreapi/capabilities",
  "headers": {
    "accept": ["application/json"],
    "x-user-handle": ["operator@example.com"]
  },
  "body": null,
  "expiresAt": "2026-07-29T18:00:45Z"
}
```

`204 No Content` after the requested wait is a normal empty-queue result. It is not a failed
connection and must not be logged or retried as an exception.

### Connect timeout and retry requirements

The connect request is an intentional long poll. A host's effective per-attempt timeout must
therefore be **strictly greater** than the server hold duration. The FabrCore host uses the
effective `PollWait` (default 20 seconds, constrained by the protocol to 1–25 seconds) plus a
10-second response/transport buffer. With defaults, the server holds for up to 20 seconds and
the host attempt timeout is 30 seconds.

The connect transport is isolated from application-wide `HttpClient` policies. In particular,
Aspire's standard HTTP resilience handler has a default 10-second attempt timeout and must not
wrap this long poll. The host performs at most three sequential attempts for DNS, TLS,
transport, HTTP 408/429, and 5xx failures. It never starts the next attempt until the preceding
request has completed or cancellation has unwound. One `CloudServerApiClient` permits only one
active connect poll; a multi-silo cluster can intentionally have one poll per host instance,
with Forge's durable lease preventing duplicate command execution.

Caller or host-shutdown cancellation is terminal and is not retried. An empty poll is logged at
Debug with outcome `empty`; delivery uses outcome `delivered`; cancellation uses `cancelled`;
and retries identify the endpoint, configured and effective poll duration, effective attempt
timeout, next attempt, HTTP status or exception category, and elapsed time. Genuine terminal
failures remain Warning-level and retain their exception details.

#### Workaround for affected hosts

Connect-channel hosts built from the initial v2 implementation (FabrCore 1.5.0 through the
1.7.1 local builds) can inherit a 10-second app-wide attempt timeout. Until upgrading to a
package containing the dedicated connect transport, use one of these mitigations:

1. Set `FabrCore:RemoteAdministration:PollWait` to `00:00:05`. This lets Forge return an empty
   `204` comfortably before a 10-second attempt timeout. It increases empty-poll traffic but
   does not affect heartbeat or agent execution.
2. If the application owns its global resilience configuration, set its attempt timeout above
   `PollWait` plus transport margin (30 seconds or more for the default 20-second poll). This
   changes the policy for every client using that default, so the shorter `PollWait` is the
   narrower operational workaround.
3. Disable `FabrCore:RemoteAdministration:Enabled` when the outbound administration channel is
   not required. Cloud configuration refresh and heartbeat remain separate features.

Normative host safety rules:

- Only `GET`, `POST`, `PUT`, `PATCH`, and `DELETE` are accepted.
- `pathAndQuery` must begin with `/fabrcoreapi/`; absolute URLs, scheme-relative URLs, and
  backslashes are rejected.
- `Authorization`, `Host`, and `Content-Length` from the command are discarded. The host sets
  its own `FabrCore:CloudServer:ApiKey` bearer token.
- Request and response bodies are bounded by `RemoteAdministration:MaxBodyBytes`.
- Expired commands are not executed.

### POST /fabrcore-cloud/v2/connect/{commandId}/response

The cluster returns:

```json
{
  "commandId": "3f84f4fc-9cb8-4f68-a6ab-c8e41f840a6e",
  "statusCode": 200,
  "headers": {
    "content-type": ["application/json"]
  },
  "body": "eyJzZXJ2aWNlcyI6W119",
  "error": null
}
```

`body` is a JSON base64 string because the channel also supports multipart uploads and other
binary admin payloads. Servers must bind a response to the authenticated cluster and command
id, accept at most one completion, expire abandoned commands, and use a durable/distributed
lease when more than one server replica can answer long polls.

## Host behavior summary (normative for host implementations)

1. **Startup**: fetch configuration before serving traffic (a few quick attempts). On failure,
   fall back to the last-known-good disk cache; with no cache, fail startup (default) or start
   degraded per `StartupFailureBehavior`. A host consuming `settings` performs one earlier,
   best-effort fetch during construction so provisioning-critical keys are in place before they
   are read; that fetch never decides startup failure on its own.
2. **Refresh**: poll with `If-None-Match` at `RefreshInterval` (default 5 minutes),
   exponential backoff on failure, never dropping the last-known-good snapshot.
3. **Cache**: after every successful fetch, persist the envelope to
   `fabrcore.cloud-cache.json` (opt out with `CacheLastKnownGood: false`). The cache stores
   API keys in plaintext — the same exposure profile as `fabrcore.json`.
4. **Blueprint rollout**: on a changed configuration version, store each delivered canonical
   blueprint under its declared principal and apply entries marked `applyOnRefresh`. A failed
   blueprint must be logged without discarding an otherwise valid model configuration.
5. **Settings**: apply the `settings` map subject to the precedence, blocklist and bounds above;
   report applied version and pending-restart keys on the heartbeat; never self-restart.
6. **Key rotation**: servers rotate cluster API keys by allowing multiple active keys per
   cluster; servers rotate *provider* keys by publishing a new configuration version — hosts
   pick it up on the next refresh (or immediately via `refreshRequested`).

## Security notes for server implementers

- Store cluster API keys hashed (they are high-entropy random secrets; a fast hash such as
  SHA-256 with fixed-time comparison is appropriate).
- Configuration bodies contain LLM provider secrets — protect them at rest.
- Return identical error bodies for unknown-cluster vs. bad-key to avoid enumeration.
- Rate-limit heartbeats per key if exposure demands it; hosts default to one per minute per silo.
