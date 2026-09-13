---
name: fabrcore-connections
description: "Implement optional FabrCore connections for user OAuth, application credentials, Entra Agent ID, authenticated MCP, and handle-addressable Copilot Studio or Work IQ agents. Use for external client consent, SDK token access, connectedAgents blueprints, and encrypted cloud handoffs; inbound Copilot channels and A2A exposure have separate skills."
metadata:
  author: FabrCore
  version: 2.0.0
---

# FabrCore connections and outbound Microsoft agents

Use the bundled [implementation guide](references/integration.md) for host setup,
profile fields, SDK examples, client consent, blueprint JSON, and API routes.
This skill describes implemented source APIs; verify package availability in the
consumer's feed. Live Microsoft/Google tenant compatibility is not established
by local protocol tests.

## Choose the integration

| Request | Implementation |
| --- | --- |
| Call an API from trusted agent code | `Connections.GetHttpClientAsync(alias, resource, ct)`; use `GetAccessTokenAsync` only for SDKs requiring a raw token |
| Authenticate an HTTP MCP server | `McpServerConfig.Connection` and `Resource`; see [MCP](../fabrcore-mcp/SKILL.md) |
| Call Copilot Studio by FabrCore handle | `remote-agent`, provider `copilot-studio`, published direct-connect URL, Studio Client SDK |
| Call Work IQ reasoning by handle | `remote-agent`, provider `work-iq`, A2A 1.0; MCP/API tools also use generic connections |
| Let Copilot Studio call FabrCore | [Inbound A2A](../fabrcore-a2a/SKILL.md) |
| Let people chat with FabrCore in Copilot/Teams | [Microsoft 365 channel addon](../fabrcore-microsoft365copilot/SKILL.md) |

## Implementation rules

- `FabrCore.Connections` provides contracts and API clients;
  `FabrCore.Services.Connections` provides the host broker;
  `FabrCore.Services.RemoteAgents` provides the remote proxy. Connections, remote
  agents, Entra Agent ID, and client handoffs are separate opt-ins. Do not enable
  them on every cluster or require a cloud server for standalone operation.
- FabrCore adds APIs only. The external client owns interactive login, consent,
  and callbacks. Insights is Vulcan365's cloud server, not a universal client app.
- Host selects protection automatically: default in-memory Localhost uses one
  ephemeral provider; SQL persistence uses an encrypted database key ring and a
  host-configured PFX certificate. SQL takes precedence over explicit Localhost
  clustering. Custom persistence requires shared protection. Do not set the legacy
  `ProtectedKeyRingConfigured` flag as a substitute for real configuration. Read
  the guide's host setup for certificate rotation, manual schema, and overrides.
  Ephemeral state is lost on restart; protect internal silo traffic as well.
- Bind each connection to its FabrCore owner principal and grant exact full agent
  handles separately. Agent aliases reference those bindings. Delegated credentials
  cannot cross principal ownership; application connections can explicitly grant
  agents owned by other principals. Obtain actor identity from host context.
- Keep tokens, authorization codes, assertions, and secret values out of model
  tools, agent arguments, blueprint documents, logs, and ordinary cloud commands.
  Profiles contain credential references resolved by the host. Diagnostic turns
  cannot use production connections.
- Authorization code flow uses begin/complete with exact registered callbacks,
  PKCE, nonce and single-use state. OBO accepts validated user assertions. Client
  credentials reacquire access tokens without human login or refresh tokens;
  provider application permissions/admin consent remain required. Google service
  account delegation is not the standard client-credentials implementation.
- Entra Agent ID is optional even for Microsoft app registrations. Its profile
  binds parent blueprint credentials and a child identity. Delegated mode also
  needs a human assertion for the blueprint API. This implementation performs
  token exchanges; it does not provision identities, sponsors, consent, or Entra
  governance. Do not silently switch delegated requests to app-only permissions.
- `connectedAgents` is a top-level canonical blueprint extension. `$principal`
  resolves on deployment. Preview computes configuration only, never consent,
  credential provisioning, token acquisition, or external agent calls.
- Remote context belongs to a principal and remote handle, shared with that
  principal's authorized agents. Reauthorization/rebinding resets it. Persist
  task IDs as they arrive; use Work IQ status/resume/cancel operations after an
  uncertain request instead of repeating it. A request accepted without returning
  a task ID cannot be recovered automatically. Studio and Work IQ require their
  own delegated scopes; inbound A2A support proves nothing about outbound auth.

## Cloud and client integration

Read the guide's cloud section and
[cloud administration](../fabrcore-cloud-administration/SKILL.md) for broker work.
Discover `connections` and its optional features before showing controls. Profile
writes require the current revision (`*` for create), and invalidate authorization.
`FabrCoreConnectionAdministrationClient` manages profiles and encrypted handoffs;
`FabrCoreConnectionsClient` exposes authenticated user operations without returning
tokens. Disconnect clears local authorization; disable an application profile to
prevent it reacquiring a token.

For a cloud relay, encrypt in the client using the short-lived cluster challenge.
Only the encrypted envelope enters durable administration records. The cluster
consumes it once and validates user proof against the owner; an administrator's
identity is not user consent. Configure the proof authority/audience or implement
`IConnectionHandoffPrincipalValidator` for another validated identity system.

Validate ownership denial, stale revisions, protected persistence, renewal,
handoff replay/tampering, and provider protocol behavior. Explicitly distinguish
automated coverage from live tenant consent and Agent ID compatibility testing.
