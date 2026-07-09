# FabrCore Platform Security Review

**Date:** 2026-07-07
**Reviewer:** Security surface analysis of `FabrCore.Host`
**Scope:** Externally reachable attack surface of the FabrCore platform — HTTP API, WebSocket, SSE, Orleans cluster ports, backing stores, secrets, and the agent content/tool layer.
**Status:** Findings and recommendations only. No code changes made.

---

## 1. Executive summary

FabrCore recently gained a capable **authorization** layer (the ACL system controlling agent-to-agent
message flow). However, the platform currently has essentially **no authentication**. Caller identity
is whatever string is supplied in the `x-user-handle` HTTP header (or the `x-fabrcore-userhandle`
header / `userhandle` query parameter on the WebSocket). Nothing verifies that a caller is who they
claim to be.

The consequence: the ACL controls *what a claimed identity may do*, but nothing controls *who may
claim an identity*. Any party that can reach the host can assert any principal — including the
`system` principal, which bypasses ACL enforcement entirely.

In addition to the identity gap, the surface inventory found several endpoints with **no identity
check at all**, one of which returns **LLM API keys in plaintext to anonymous callers**.

The single most important architectural decision is **how callers prove identity**, and where that
check is enforced. Everything else composes cleanly once that is settled.

### Highest-priority items

| # | Finding | Risk |
|---|---------|------|
| F-01 | `GET /fabrcoreapi/ModelConfig/apikey/{alias}` returns LLM API keys to anonymous callers | **Critical** |
| F-02 | No authentication anywhere; `x-user-handle` identity is caller-supplied and unverified | **Critical** |
| F-03 | `system` principal (full ACL bypass) is claimable via an unverified header | **Critical** |
| F-04 | Orleans silo/gateway ports have no authentication; grains callable directly | **Critical** |
| F-05 | Multiple endpoints fully open (File, ChatCompletion, Embeddings, Diagnostics) | **High** |
| F-06 | Clustering / grain-persistence stores are unguarded cluster-admission and ACL back doors | **High** |

---

## 2. Current security posture

### What exists and works

- **ACL authorization** (`src/FabrCore.Core/Acl/*`, `src/FabrCore.Host/Services/AclEvaluator.cs`,
  `AclEnforcer.cs`) — enforced at the Orleans grain boundary for agent-to-agent flow: message send,
  event dispatch, agent create/reconfigure/destroy. Deny-by-default with explicit allow/deny grants,
  same-principal implicit allow, and a `system` bypass. Three enforcement modes: `Disabled`,
  `AuditOnly`, `Enforce`.
- **Auditing** (`src/FabrCore.Core/Auditing/*`, `AuditController`) — records ACL decisions,
  management changes, agent creation, and cross-principal boundary crossings. Default provider is an
  in-memory bounded FIFO (lost on restart).
- **Verifiable execution / SPIFFE** (`src/FabrCore.Core/VerifiableExecution/*`) — optional signing of
  execution evidence chains; SPIFFE/SVID is one signer option. Currently used for *signing evidence*,
  not for *authenticating callers*. Off by default.
- **Pluggable WebSocket authentication seam** (`IWebSocketAuthenticator`) — the default implementation
  trusts the header; the seam is the correct place to add real verification.

### What is missing

- No `AddAuthentication()` / `UseAuthentication()` / `UseAuthorization()` anywhere in
  `FabrCoreHostExtensions.cs`.
- No `[Authorize]` / `[AllowAnonymous]` attributes on any controller.
- No API-key middleware, JWT validation, or mTLS on the HTTP surface.
- No CORS policy (ASP.NET default), no enforced HTTPS/HSTS, no rate limiting.
- Orleans cluster ports and backing stores rely entirely on network topology for protection.

---

## 3. Attack surface inventory

### 3.1 Network listeners

| Listener | Path / Port | Auth today | Notes |
|----------|-------------|-----------|-------|
| HTTP API | `/fabrcoreapi/*` | Header only (unverified) | ~15 controllers; see §3.3 |
| WebSocket | `/ws` (configurable) | Header/query (unverified) | `IWebSocketAuthenticator` seam exists |
| SSE stream | `/fabrcoreapi/monitor/stream` | Header (unverified) | Long-lived; ACL-filters *data* correctly but trusts handle |
| Health | `/health`, `/health/live`, `/health/ready` | None | Detailed `/health` leaks agent/session/token counters |
| Orleans silo/gateway | 11111 / 30000 (defaults) | **None** | Binary protocol; grains callable directly, bypassing all HTTP controls |

No gRPC endpoint, no MCP listener at the host, and no Orleans Dashboard were found wired in.

### 3.2 Non-listener attack surfaces

- **Orleans clustering / membership store** (SQL Server or Azure Table) — write access = ability to
  register a rogue trusted silo. Cluster security equals connection-string security.
- **Grain persistence storage** (`fabrcore-acl-*` containers, etc.) — write access to ACL storage is
  an `acl.manage` back door.
- **`fabrcore.json` config / secrets at rest** — holds LLM API keys in plaintext; tampering with
  model endpoint URLs can redirect LLM traffic to an attacker.
- **Prompt injection / confused deputy** — the ACL governs *who may message an agent*, not *what the
  message makes a tool-holding agent do*. Untrusted content (chat, file, cross-principal message) can
  steer agents; a low-privilege agent may reach a high-privilege agent's tools through the mesh.
- **Tools / plugins / MCP servers** — outbound trust; a malicious MCP server can poison tool results
  or exfiltrate context. URL/file-touching tools are SSRF/file-access vectors with host credentials.
- **File upload content** — served back out; content-type handling matters (stored XSS), plus size
  limits and abuse as anonymous file hosting.
- **Deserialization** — HTTP/WS JSON is relatively safe; the Orleans binary protocol deserializes
  from anyone who connects to the cluster ports.
- **Captured LLM payloads** — with `CapturePayloads=true`, full prompts/responses are buffered in
  memory and served via the Monitor API; a concentrated store of potentially sensitive data.
- **Supply chain** — NuGet dependencies and, eventually, customer-installed agent/skill packages.

### 3.3 HTTP endpoint posture

| Endpoint | Identity | ACL | Notes |
|----------|----------|-----|-------|
| `/health/*` | None | No | Detailed view leaks counters |
| `/fabrcoreapi/Agent/*` | Header only | No (grain enforces later) | Create/delete/chat/event |
| `/fabrcoreapi/acl/*` | Header + permission | Yes | `system` bypasses |
| `/fabrcoreapi/audit/*` | Header + permission | Yes | Reads ACL-checked |
| `/fabrcoreapi/Monitor/*` | Header + permission | Yes (server filter) | |
| `/fabrcoreapi/monitor/stream` (SSE) | Header | Yes (filter) | Trusts unverified handle |
| `/fabrcoreapi/File/*` | **None** | No | Open upload/download/delete |
| `/fabrcoreapi/Storage/*` | Header only | No | User-partitioned, no ACL |
| `/fabrcoreapi/ChatCompletion` | **None** | No | Direct LLM access on your bill |
| `/fabrcoreapi/Embeddings*` | **None** | No | Free LLM access |
| `/fabrcoreapi/ModelConfig/apikey/{alias}` | **None** | No | **Returns API keys in plaintext** |
| `/fabrcoreapi/ModelConfig/model/{name}` | **None** | No | Model config disclosure |
| `/fabrcoreapi/Discovery` | **None** | No | Agent/plugin/tool metadata |
| `/fabrcoreapi/Diagnostics/*` | **None** | No | Lists all agents/principals; anon `POST /agents/purge` |
| `/fabrcoreapi/monitor/verifiable-execution/*` | **None** | No | Evidence-bundle reads |
| `/fabrcoreapi/Debug/replay` | Dev only | No | Message replay |
| `/ws` | Header/query | No (grain enforces) | WebSocket |

---

## 4. Findings with risk levels and remediation plans

Risk uses **Critical / High / Medium / Low**, reflecting impact × ease of exploitation given the
platform is reachable.

---

### F-01 — API keys served to anonymous callers · **Critical**

**Where:** `src/FabrCore.Host/Api/Controllers/ModelConfigController.cs` — `GET /apikey/{alias}`.

**Impact:** Any party that can reach the host retrieves provider LLM API keys in plaintext. Direct
theft of billable, potentially production credentials. No auth, no ACL.

**Plan of attack:**
1. **Immediate:** Remove the `apikey` endpoint from the HTTP surface, or gate it behind an admin
   permission as a stopgap.
2. **Design fix:** Keys should be resolved server-side at LLM-call time and never handed to clients.
   Review why any caller needs a key by alias; treat client-side key retrieval as a design smell.
3. Move keys out of `fabrcore.json` into a secret store (Key Vault / managed identity / environment).
4. Rotate any keys that have been exposed by this endpoint.

---

### F-02 — No authentication; identity is caller-supplied · **Critical**

**Where:** All controllers via `[FromHeader(Name = "x-user-handle")]`;
`src/FabrCore.Host/WebSocket/DefaultWebSocketAuthenticator.cs` for `/ws`.

**Impact:** Any caller can impersonate any principal by setting a header. The ACL becomes decorative
because the subject it evaluates is attacker-controlled.

**Plan of attack:**
1. Introduce an `IPrincipalAuthenticator` seam (mirroring `IWebSocketAuthenticator`) and a single
   authentication middleware that resolves the verified principal handle into a scoped
   `ICallerContext`.
2. Refactor controllers and the WebSocket authenticator to read identity from `ICallerContext`
   instead of the raw header. Mechanical change across ~15 controllers.
3. Ship at least one real authenticator (see §5 for the profile model): gateway shared-key and/or
   per-principal API key and/or OIDC/JWT.
4. Keep the bare-header mode as an explicit **Development-only** path that **fails startup in
   Production** unless a deliberate `AllowInsecureInProduction` flag is set.

---

### F-03 — `system` principal claimable via unverified header · **Critical**

**Where:** `AclEvaluator.Evaluate()` system-bypass branch; any endpoint honoring `x-user-handle`.

**Impact:** Sending `x-user-handle: system` yields unrestricted ACL bypass — full compromise of every
agent and all ACL state. Amplifies F-02 to catastrophic.

**Plan of attack:**
1. Reject the `system` handle (and any configured `SystemPrincipal`) from *all* externally
   authenticated paths — HTTP and WebSocket. Reserve `system` for in-process calls only.
2. Add a defense-in-depth check so that even a valid credential cannot assert the system handle over
   the network.
3. Audit any attempt to claim `system` from an external path as a security event.

---

### F-04 — Orleans cluster ports unauthenticated · **Critical**

**Where:** Silo/gateway ports (defaults 11111/30000); Orleans configuration in
`FabrCoreHostExtensions.cs` / `FabrCoreSiloBuilderExtensions.cs`.

**Impact:** Anyone who can reach these ports invokes grains directly — sending agent messages,
rewriting ACL state — bypassing every HTTP-layer control. Also deserializes the binary protocol from
untrusted peers.

**Plan of attack:**
1. **Mandatory baseline:** bind silo/gateway to a private network/VNet; expose only the ASP.NET port
   publicly. Document loudly (FabrCore is a framework others deploy).
2. Enable TLS on silo-to-silo (`UseTls` on the silo builder) for shared-network deployments.
3. Enterprise: SPIFFE mTLS between silos, consistent with existing SPIFFE positioning.
4. Provide a reference network-architecture diagram for customer network teams.

---

### F-05 — Fully open endpoints · **High**

**Where:** `FileController`, `ChatCompletionController`, `EmbeddingsController`,
`DiagnosticsController`, `DiscoveryController`, `VerifiableExecutionController`; detailed `/health`.

**Impact:**
- **File** — anonymous upload/download/delete; free file hosting; stored-XSS risk on served content.
- **ChatCompletion / Embeddings** — free LLM access on your bill (cost-abuse / DoS).
- **Diagnostics** — enumerates all agents/principals; anonymous `POST /agents/purge` is destructive.
- **Discovery / verifiable-execution / detailed health** — information disclosure.

**Plan of attack:**
1. Apply the §5 "authenticated by default" policy: everything requires an authenticated principal
   unless explicitly marked public (`/health/live`, `/health/ready` only).
2. Add new surface permissions to `FabrPermissions`: `file.read`/`file.write`, `llm.invoke`,
   `system.diagnostics`, `storage.read`/`storage.write` — so the existing grants engine governs the
   HTTP surface, not just agent-to-agent flow.
3. Move `Diagnostics` and the detailed `/health` behind an admin permission.
4. Add per-principal rate limiting on LLM and upload endpoints (`AddRateLimiter`).

---

### F-06 — Backing stores are unguarded back doors · **High**

**Where:** Orleans clustering/membership store and grain-persistence storage (connection strings in
config).

**Impact:** Write access to the membership table lets an attacker register a rogue trusted silo.
Write access to `fabrcore-acl-*` storage is an `acl.manage` back door. Store credentials = cluster
and ACL control.

**Plan of attack:**
1. Treat clustering and storage connection strings as top-tier secrets (Key Vault / managed
   identity); never in `appsettings.json`.
2. Use least-privilege store credentials; separate credentials for clustering vs. application data
   where possible.
3. Restrict store network access to the silo subnet.
4. Document the trust relationship so operators understand the store *is* cluster admission control.

---

### F-07 — WebSocket / SSE identity and origin handling · **High**

**Where:** `DefaultWebSocketAuthenticator.cs`; `FabrCoreHostOptions.AllowedWebSocketOrigins`
(empty = allow all); `MonitorStreamController`.

**Impact:** Browsers cannot send custom headers on WS upgrade, so identity often lands in the query
string (logged). Empty origin allowlist permits cross-site WebSocket hijacking. SSE inherits the
unverified-handle problem.

**Plan of attack:**
1. Add a short-lived **connection-ticket** endpoint: authenticated `POST /fabrcoreapi/ws/ticket`
   returns a one-time ~30s token used on `/ws` and `/monitor/stream`. Avoids long-lived tokens in
   URLs.
2. Validate the ticket/credential in a `GatewayKey`/`Ticket` `IWebSocketAuthenticator`, before
   reading the handle.
3. Change `AllowedWebSocketOrigins` semantics to **empty = deny all in Production**.
4. Verify the chosen proxy forwards WebSockets and does not buffer SSE (test the monitor stream
   specifically on APIM).

---

### F-08 — In-memory audit provider loses the security trail · **Medium**

**Where:** Default `InMemoryAuditProvider` (bounded FIFO, lost on restart).

**Impact:** No durable audit record; disqualifying for regulated environments (FFIEC/SOX). Auth
failures are not currently audited at all.

**Plan of attack:**
1. Ship a durable audit provider (SQL / Azure Table, matching Orleans storage options).
2. Add SIEM export (Splunk / Sentinel) for enterprise deployments.
3. Audit authentication failures in the same stream as ACL denials.

---

### F-09 — File upload content handling · **Medium**

**Where:** `FileController`.

**Impact:** Beyond missing auth: served-back content can execute in the browser (stored XSS if an
uploaded HTML file is rendered); no enforced size/type limits; abuse as anonymous hosting.

**Plan of attack:**
1. Enforce content-type allowlist and safe response headers (`Content-Disposition: attachment`,
   `X-Content-Type-Options: nosniff`).
2. Enforce upload size limits and per-principal quotas.
3. Scope file access to the owning principal via ACL.

---

### F-10 — Captured LLM payloads are a sensitive data store · **Medium**

**Where:** `LlmCaptureOptions.CapturePayloads = true`; Monitor API `tool-calls` and payload views.

**Impact:** Full prompts/responses (potentially customer PII) buffered in memory and served via the
Monitor API. Concentrated sensitive-data exposure if Monitor access is weak.

**Plan of attack:**
1. Gate payload views behind a dedicated permission distinct from general monitoring.
2. Add retention limits and optional redaction.
3. Document data-handling posture for privacy reviews.

---

### F-11 — Transport, CORS, and rate-limiting gaps · **Medium**

**Where:** `FabrCoreHostExtensions.cs` (no CORS policy, no HTTPS enforcement, no rate limiter).

**Plan of attack:**
1. Enforce HTTPS + HSTS in Production.
2. Add an explicit restrictive CORS policy (no wildcard with credentials).
3. Add global rate limiting on unauthenticated paths and per-principal limits on expensive
   operations.

---

### F-12 — Prompt injection / tool confused-deputy · **High (design-track)**

**Where:** Agent content path — chat messages, uploaded files, cross-principal messages reaching
tool-holding agents.

**Impact:** No amount of API authentication addresses this. An attacker with legitimate low-privilege
access can steer an agent into misusing tools, or reach a high-privilege agent's tools through the
agent-to-agent mesh. This is the attack surface unique to an agent platform.

**Plan of attack (research + design, not a middleware fix):**
1. Define content-trust levels tied to message origin (external / cross-principal / same-principal).
2. Scope tool permissions per message origin — extend the ACL with origin-aware tool grants. The
   existing cross-principal hop-stamping (`CrossPrincipalOrigin` / `CrossPrincipalHops`) is a good
   primitive to build on.
3. Constrain outbound tool actions (SSRF allowlists, filesystem sandboxing) for tools invoked on
   behalf of untrusted content.
4. Treat as its own workstream; do not bundle into surface-auth work.

---

## 5. Recommended architecture — graduated security profiles

Adopt **graduated security profiles with fail-closed defaults**, consistent with FabrCore's existing
pattern (Orleans clustering, ACL mode, verifiable-execution levels). One seam, three implementations,
selected by config:

```
"FabrCore": {
  "Security": {
    "Mode": "Development" | "Gateway" | "Enterprise"
  }
}
```

All modes implement the same `IPrincipalAuthenticator`; everything downstream (`ICallerContext`, ACL
evaluation, auditing, WebSocket auth) is identical. Moving from demo to production is a config change,
not a code change.

### Development — zero setup

- Bare `x-user-handle` accepted (today's behavior); permissive ACL seed; localhost binding.
- Loud startup warning that identities are unverified.
- **Fails startup in Production** unless `AllowInsecureInProduction: true` is deliberately set.
- Purpose: `scaffold-system` → `dotnet run` → working demo in minutes, no Azure tenant or certs.

### Gateway — trusted reverse proxy + shared key

- Host behind YARP / APIM / App Gateway; validates a gateway shared key; trusts proxy-set identity
  headers.
- **Non-negotiable within this mode:**
  - Proxy **strips and overwrites** any client-supplied `x-user-handle` and the key header (YARP
    forwards unknown headers by default — this is the classic bypass).
  - Host **rejects `system`** from the proxy path (F-03).
  - Key validated on HTTP **and** the WebSocket upgrade / SSE requests.
  - Host **fails startup** if no key is configured in Production.
  - Support two keys simultaneously (current + next) for zero-downtime rotation; key in Key Vault.
- Trade-off: the key authenticates the *channel*, not the *end user*; the key holder is fully
  trusted. Good tier-one for internal / early-adopter deployments.

### Enterprise — satisfies a bank / large-enterprise review

- **OIDC/JWT against the customer IdP** (Entra ID / Ping / Okta) with their conditional access + MFA +
  joiner-leaver lifecycle. Group claims map to ACL roles. A vendor-managed credential store as the
  *primary* identity is typically a vendor-assessment finding — delegate identity instead.
- **Service-to-service** via client-credentials or mTLS/SPIFFE (differentiator; SPIFFE work already
  exists).
- **Durable, exportable audit** to their SIEM; auth failures audited.
- **Secrets** from Key Vault / managed identity; TLS enforced; documented private-network Orleans
  posture with a reference diagram.

### What satisfies whom

- **Developers** care about time-to-first-agent — Development mode delivers it, provided it is the
  default for new scaffolds.
- **Enterprise security teams** evaluate *answers* as much as code: SSO, audit export, secrets
  handling, data-flow diagrams, least-privilege model. Half the work is documentation — a security
  architecture doc, a hardening checklist, and a reference deployment diagram. Fail-closed startup is
  itself a strong answer to "how do you prevent misconfiguration?"

---

## 6. Enforcement model — where identity is checked

1. **Single authentication middleware + `ICallerContext`** (recommended): one choke point resolves the
   verified principal; controllers and the WS authenticator read from context, not the raw header.
2. **ASP.NET authorization policies mapped to ACL permissions**: `[Authorize(Policy = "acl.manage")]`
   with a handler that calls `IAclEvaluator`, replacing hand-rolled checks in `AclController`. Pairs
   with (1).
3. **Authenticated-by-default**: set a fallback authorization policy; `[AllowAnonymous]` becomes the
   explicit, reviewable exception. Public tier = `/health/live`, `/health/ready` only.

Endpoint tiers:

- **Public:** `/health/live`, `/health/ready`.
- **Principal-scoped:** Agent, Storage, File, Monitor, ChatCompletion, Embeddings, WebSocket, SSE.
- **Admin:** ACL, Audit, Diagnostics, ModelConfig, Debug (require `acl.manage` or a new
  `system.admin`).

---

## 7. Suggested sequencing

**Tier 0 — stop the bleeding (days):**
- F-01 remove/gate the apikey endpoint and rotate keys.
- F-05 temporary "require any authenticated principal" gate on File, ChatCompletion, Embeddings,
  Diagnostics.
- F-04 confirm Orleans ports are on a private network in every deployment.

**Tier 1 — authentication foundation (weeks):**
- F-02 `IPrincipalAuthenticator` + middleware + `ICallerContext`; Gateway shared-key mode first
  (no external dependency); dev-mode header fallback behind an explicit flag.
- F-03 reject `system` from external paths.
- F-06 move store connection strings to a secret store.

**Tier 2 — authorization unification + realtime (weeks):**
- Fallback authorize-by-default policy; endpoint tiering; new surface permissions in
  `FabrPermissions`.
- F-07 connection-ticket endpoint for WS/SSE; origin allowlist deny-by-default.
- F-08 durable audit provider.

**Tier 3 — enterprise-grade (as demand arrives):**
- OIDC/JWT authenticator; SIEM export; Orleans mTLS/SPIFFE; reference architecture and hardening docs.

**Design track (parallel, own workstream):**
- F-12 prompt-injection / origin-aware tool scoping.

---

## 8. Ideas and future enhancements

- **Origin-aware tool permissions** — extend the ACL so tool grants depend on message origin/trust
  level, building on `CrossPrincipalOrigin`/`CrossPrincipalHops`. Potential real differentiator for an
  agent platform.
- **Per-principal API-key lifecycle API** — mint/rotate/revoke keys through the existing principal/ACL
  management surface, with hashed storage and one-time display.
- **Claims-to-roles mapping** — map IdP group claims directly to FabrCore ACL roles so customer AD/Entra
  groups drive permissions without manual grant management.
- **Signed gateway headers (HMAC)** — upgrade the Gateway mode's static key to a timestamped,
  HMAC-signed header to defeat replay and reduce the impact of key leakage; mTLS between proxy and host
  as the next step.
- **Data-at-rest encryption for grain state** — encrypt sensitive grain persistence (agent state,
  captured payloads) for regulated deployments.
- **Prompt/response retention & redaction policies** — configurable retention and PII redaction for
  Monitor-captured LLM payloads.
- **Multi-tenancy isolation guarantees** — formalize principal/tenant boundaries (storage partitioning,
  cross-tenant deny-by-default) as a documented guarantee for enterprise tenancy reviews.
- **Security posture endpoint** — expose (to admins) the active security mode, ACL enforcement mode,
  audit provider durability, and snapshot version, so operators can confirm the platform is configured
  as intended.
- **Startup security self-check** — on boot, log a security posture summary and fail closed on unsafe
  Production combinations (insecure mode, no key, in-memory audit, public Orleans ports).
- **Supply-chain controls** — dependency scanning and, eventually, signing/verification for
  customer-installed agent/skill packages.
- **Verifiable-execution as authentication** — extend the existing SPIFFE signer path so SVID identity
  can double as caller authentication for service-to-service calls, unifying the identity and evidence
  stories.

---

## 9. Appendix — key files

| Area | Files |
|------|-------|
| Host pipeline / wiring | `src/FabrCore.Host/FabrCoreHostExtensions.cs` |
| Controllers | `src/FabrCore.Host/Api/Controllers/*.cs` |
| WebSocket | `src/FabrCore.Host/WebSocket/WebSocketMiddleware.cs`, `DefaultWebSocketAuthenticator.cs`, `IWebSocketAuthenticator.cs` |
| ACL core | `src/FabrCore.Core/Acl/*` |
| ACL enforcement | `src/FabrCore.Host/Services/AclEvaluator.cs`, `AclEnforcer.cs` |
| Principal identity | `src/FabrCore.Host/Grains/PrincipalGrain.cs`, `src/FabrCore.Core/Interfaces/IPrincipalGrain.cs` |
| Auditing | `src/FabrCore.Core/Auditing/*`, `src/FabrCore.Host/Api/Controllers/AuditController.cs` |
| Verifiable execution / SPIFFE | `src/FabrCore.Core/VerifiableExecution/*`, `docs/skills/fabrcore-spiffe/` |
| Config / options | `FabrCoreAclOptions.cs`, `AuditOptions.cs`, `FabrCoreHostOptions.cs`, `PrincipalGrainOptions.cs` |
| SDK client | `src/FabrCore.Sdk/FabrCoreHostApiClient.cs` |

*This document is an analysis deliverable. No code was modified.*
