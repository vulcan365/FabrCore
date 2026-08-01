# FabrCore Cloud Server Protocol (configuration v1 + connect v2)

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
    "AdminAuthentication": {
      "ApiKey": "<separate local admin key>"
    },
    "CloudServer": {
      "Enabled": true,
      "Url": "https://forge.vulcan365.ai",
      "ApiKey": "<per-cluster API key>",
      "ClusterId": null,
      "Environment": null,
      "Connect": {
        "Enabled": true
      }
    }
  }
}
```

- `Url` defaults to the hosted Forge endpoint and is overridable for self-hosted servers.
- `ClusterId` defaults to the Orleans `ClusterOptions.ClusterId`.
- `Environment` defaults to `IHostEnvironment.EnvironmentName` (`ASPNETCORE_ENVIRONMENT`).
- Securing `ApiKey` (user secrets, environment variables, vault-backed configuration
  providers) is the operator's responsibility.
- Connect is disabled by default; `"Connect": { "Enabled": true }` is the minimal form.
  `LocalAdminUrl` is optional — it defaults to `FabrCore:HostUrl`, then
  `http://127.0.0.1:5000`. `LocalAdminApiKey` is optional — it defaults to
  `FabrCore:AdminAuthentication:ApiKey`. `PollWait` and `MaxBodyBytes` are also optional
  tuning values.
- The local admin target must be an absolute http(s) URL, and requests outside
  `/fabrcoreapi/` are rejected. Loopback remains the recommended default, but non-loopback
  targets (for example a container network alias) are allowed: the host logs a startup
  warning because the local admin key then traverses the network — use non-loopback targets
  only on trusted networks. Use a separate local admin key rather than reusing the Forge
  cluster key.

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
        "swarm": { "squads": [] }
      }
    }
  ]
}
```

- `configuration` is exactly the `FabrCore.Core.FabrCoreConfiguration` shape (all
  `ModelConfiguration` tuning fields are supported; the example above is abbreviated).
- `settings` is a **reserved** optional map of flat IConfiguration keys (for example
  `"FabrCore:Host:WebSocketPath": "/ws"`). Current hosts ignore it; servers may omit it or
  populate it without breaking compatibility.
- `blueprints` is an optional list of principal-scoped canonical `FabrCoreBlueprint`
  deployments. On a new configuration version the host stores each blueprint and, when
  `applyOnRefresh` is true, applies it through the same host-side expander pipeline used by
  `/fabrcoreapi/Blueprint`. This is the fleet rollout path; omitting the field is backward
  compatible.
- `configurationVersion` must change whenever the effective configuration changes — including
  when only an environment overlay changed. A content hash of the merged document is a good
  implementation.

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
  "activeGatewayCount": 2,
  "capabilities": {
    "host": "1.5.0",
    "memory.admin": "1",
    "graphrag.admin": "1",
    "blueprint.swarm": "1"
  },
  "timestamp": "2026-07-23T18:00:00Z"
}
```

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

## Outbound admin connect channel (v2)

The optional connect channel lets a hosted console administer a cluster without exposing
inbound network ports. Every network connection originates at the FabrCore host:

1. Forge (or another server implementation) durably queues an admin request.
2. A cluster silo receives it through a long poll.
3. The silo validates the command, sends it to its configured local admin `/fabrcoreapi/`
   endpoint (loopback by default; a non-loopback http(s) target such as a container network
   alias is allowed and logged with a startup warning) using the separately configured admin
   key, and captures the response.
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

Normative host safety rules:

- Only `GET`, `POST`, `PUT`, `PATCH`, and `DELETE` are accepted.
- `pathAndQuery` must begin with `/fabrcoreapi/`; absolute URLs, scheme-relative URLs, and
  backslashes are rejected.
- `Authorization`, `Host`, and `Content-Length` from the command are discarded. The host sets
  its own local admin bearer key.
- Request and response bodies are bounded by `Connect:MaxBodyBytes`.
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
   degraded per `StartupFailureBehavior`.
2. **Refresh**: poll with `If-None-Match` at `RefreshInterval` (default 5 minutes),
   exponential backoff on failure, never dropping the last-known-good snapshot.
3. **Cache**: after every successful fetch, persist the envelope to
   `fabrcore.cloud-cache.json` (opt out with `CacheLastKnownGood: false`). The cache stores
   API keys in plaintext — the same exposure profile as `fabrcore.json`.
4. **Blueprint rollout**: on a changed configuration version, store each delivered canonical
   blueprint under its declared principal and apply entries marked `applyOnRefresh`. A failed
   blueprint must be logged without discarding an otherwise valid model configuration.
5. **Key rotation**: servers rotate cluster API keys by allowing multiple active keys per
   cluster; servers rotate *provider* keys by publishing a new configuration version — hosts
   pick it up on the next refresh (or immediately via `refreshRequested`).

## Security notes for server implementers

- Store cluster API keys hashed (they are high-entropy random secrets; a fast hash such as
  SHA-256 with fixed-time comparison is appropriate).
- Configuration bodies contain LLM provider secrets — protect them at rest.
- Return identical error bodies for unknown-cluster vs. bad-key to avoid enumeration.
- Rate-limit heartbeats per key if exposure demands it; hosts default to one per minute per silo.
