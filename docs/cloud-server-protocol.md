# FabrCore Cloud Server Protocol (v1)

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
    "CloudServer": {
      "Enabled": true,
      "Url": "https://forge.vulcan365.ai",
      "ApiKey": "<per-cluster API key>",
      "ClusterId": null,
      "Environment": null
    }
  }
}
```

- `Url` defaults to the hosted Forge endpoint and is overridable for self-hosted servers.
- `ClusterId` defaults to the Orleans `ClusterOptions.ClusterId`.
- `Environment` defaults to `IHostEnvironment.EnvironmentName` (`ASPNETCORE_ENVIRONMENT`).
- Securing `ApiKey` (user secrets, environment variables, vault-backed configuration
  providers) is the operator's responsibility.

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
  "settings": null
}
```

- `configuration` is exactly the `FabrCore.Core.FabrCoreConfiguration` shape (all
  `ModelConfiguration` tuning fields are supported; the example above is abbreviated).
- `settings` is a **reserved** optional map of flat IConfiguration keys (for example
  `"FabrCore:Host:WebSocketPath": "/ws"`). Current hosts ignore it; servers may omit it or
  populate it without breaking compatibility.
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
  "timestamp": "2026-07-23T18:00:00Z"
}
```

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

## Host behavior summary (normative for host implementations)

1. **Startup**: fetch configuration before serving traffic (a few quick attempts). On failure,
   fall back to the last-known-good disk cache; with no cache, fail startup (default) or start
   degraded per `StartupFailureBehavior`.
2. **Refresh**: poll with `If-None-Match` at `RefreshInterval` (default 5 minutes),
   exponential backoff on failure, never dropping the last-known-good snapshot.
3. **Cache**: after every successful fetch, persist the envelope to
   `fabrcore.cloud-cache.json` (opt out with `CacheLastKnownGood: false`). The cache stores
   API keys in plaintext — the same exposure profile as `fabrcore.json`.
4. **Key rotation**: servers rotate cluster API keys by allowing multiple active keys per
   cluster; servers rotate *provider* keys by publishing a new configuration version — hosts
   pick it up on the next refresh (or immediately via `refreshRequested`).

## Security notes for server implementers

- Store cluster API keys hashed (they are high-entropy random secrets; a fast hash such as
  SHA-256 with fixed-time comparison is appropriate).
- Configuration bodies contain LLM provider secrets — protect them at rest.
- Return identical error bodies for unknown-cluster vs. bad-key to avoid enumeration.
- Rate-limit heartbeats per key if exposure demands it; hosts default to one per minute per silo.
