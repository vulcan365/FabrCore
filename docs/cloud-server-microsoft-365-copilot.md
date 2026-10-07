# Managing Microsoft 365 Copilot integrations from a cloud server

This is the working guide for a cloud-server team that wants to manage a customer's Microsoft 365
Copilot integration on FabrCore hosts: discover what a host has, switch it on, show what is
applied, diagnose it, and hand the administrator the app package. It was written for the Insights
team; the contract it describes is the vendor-neutral one in the
[cloud server protocol](cloud-server-protocol.md#managing-microsoft-365-copilot-and-a2a-integrations),
and any conforming server can follow it.

**Every response below was captured from the shipped code**, not written by hand. They come from
the FabrCore `develop` branch (commit `d55c98d`) with the administration routes exercised over
HTTP against a real FabrCore server by `CopilotIntegrationEndpointTests`. Tenant and client ids are
placeholders. Bodies are shown unescaped; on the wire `System.Text.Json` writes `'` as `'` and
`+` as `+`. Where a body is shortened, the text says so.

Background reading: the [integration guide](microsoft-365-copilot-native-integration.md) for what
the integration is, and the [plan](cloud-managed-copilot-integrations-plan.md) for where this fits.

## What the host gives you

| You want to | Use | Section |
| --- | --- | --- |
| Know which hosts have the features, without calling them | Heartbeat `capabilities` flags | 1 |
| Know what a host supports and whether it is on | `GET /fabrcoreapi/capabilities` | 1 |
| Read identity, configuration, and posture | `GET /fabrcoreapi/admin/v1/integrations/microsoft365` and `/a2a` | 2 |
| Turn it on or change it | The `settings` map in the configuration envelope | 3 |
| Show what is applied and what awaits a restart | The configuration-state report | 4 |
| Prove the bot credential works | `POST …/integrations/microsoft365/diagnostics` | 5 |
| Give the administrator the package to upload | `GET …/integrations/microsoft365/app-package` | 6 |

All administration routes use the host administration credential and travel over the connect
channel. `/fabrcoreapi/admin/v1` is already on the host's connect-channel allowlist, so no host
change is needed to reach them.

## 1. Discover

### Heartbeat flags

Each host instance reports these in its heartbeat `capabilities` map. They are enough to decide
which clusters to offer the integration for.

| Flag | Meaning |
| --- | --- |
| `m365copilot: "1"` | The Copilot add-on is installed on this host |
| `m365copilot.enabled: "true" \| "false"` | Whether the channel is serving its messaging endpoint |
| `a2a: "1.0"` | A2A is enabled. Absent when it is off. |
| `remote-agents: "1"`, `remote-agents.enabled` | The remote-agents package is registered, and whether it is on |
| `connections.admin: "1"` | Connections is enabled. Absent when it is off. |

An absent `m365copilot` flag means the add-on is not installed, and no amount of publishing will
turn the channel on: the host application has to reference the package and call
`AddMicrosoft365Copilot()`. Say that to the operator rather than offering a form that cannot work.

The flags above are asserted by the host test suite; no heartbeat body was captured for this
guide.

### Capability document

`GET /fabrcoreapi/capabilities` on a host with the add-on installed and nothing configured. The
`host-admin` entry is omitted here; the rest is as captured:

```json
{
  "apiVersion": "2",
  "hostVersion": "2.0.0.0",
  "services": [
    {
      "name": "a2a",
      "version": "2.0.0.0",
      "apiVersion": "1.0",
      "features": [ "status", "jsonrpc", "http-json", "streaming", "tasks", "auth-apikey", "principal-fixed", "agent-bindings" ],
      "dataScope": "cluster",
      "maxRequestBodyBytes": null,
      "available": false,
      "unavailableReason": "A2A:Enabled is false."
    },
    {
      "name": "microsoft365-copilot",
      "version": "2.0.0.0",
      "apiVersion": "1",
      "features": [ "status", "app-package", "diagnostics" ],
      "dataScope": "cluster",
      "maxRequestBodyBytes": null,
      "available": false,
      "unavailableReason": "No Microsoft365Copilot configuration was found and no options were supplied in code."
    }
  ],
  "blueprintExtensions": [],
  "maxRequestBodyBytes": 4194304,
  "dataScope": "cluster"
}
```

The same two services once both are configured and on:

```json
{
  "name": "a2a",
  "apiVersion": "1.0",
  "features": [ "status", "jsonrpc", "http-json", "streaming", "tasks", "auth-jwtbearer", "principal-canonicalentra", "agent-bindings" ],
  "available": true,
  "unavailableReason": null
},
{
  "name": "microsoft365-copilot",
  "apiVersion": "1",
  "features": [ "status", "app-package", "diagnostics", "activity-protocol", "streaming", "adaptive-cards", "principal-canonicalentra" ],
  "available": true,
  "unavailableReason": null
}
```

Rules:

* **Check `available`, not presence.** `a2a` is listed on every host, on or off. A check for
  "is there a service named `a2a`" is true everywhere and means nothing.
* `status`, `app-package`, and `diagnostics` are listed even while the channel is off, because
  those routes answer in both states.
* `principal-{strategy}` appearing on both services with the same value is the quick check that a
  user reaches the same agent from Copilot and from Copilot Studio.
* `token-validation-off` in the channel's features is a warning to raise, not a feature.
* Ignore features you do not recognize.

## 2. Read status

### The Copilot channel

`GET /fabrcoreapi/admin/v1/integrations/microsoft365` without a credential returns `401`. With the
administration credential, on a host where nothing is configured (findings shortened to the first;
all seven are `skipped` with the same message):

```json
{
  "apiVersion": "1",
  "enabled": false,
  "configured": false,
  "disabledReason": "No Microsoft365Copilot configuration was found and no options were supplied in code.",
  "tenantId": null,
  "clientId": null,
  "authType": "ClientSecret",
  "messagesEndpoint": "/api/messages",
  "tokenValidationEnabled": true,
  "principalStrategy": "EntraObjectId",
  "agentBinding": null,
  "agentType": null,
  "agentHandle": "copilot",
  "sharedAgentHandle": null,
  "singleSignOn": false,
  "forwardsUserCredential": false,
  "proactiveEnabled": false,
  "proactiveConversationTypes": [ "personal" ],
  "streamingEnabled": true,
  "publicHostName": null,
  "manifestId": null,
  "manifestName": "FabrCore Agent",
  "manifestVersion": "1.0.0",
  "turnStateStorage": null,
  "status": "skipped",
  "findings": [
    { "id": "token-validation", "status": "skipped", "message": "The Microsoft 365 Copilot channel is off on this host." }
  ]
}
```

`configured: false` is the signal that nobody has chosen these values. The non-null fields are
option defaults, not decisions. Do not show `authType: "ClientSecret"` to an operator as if the
host were using a secret.

The same route on a configured host, complete:

```json
{
  "apiVersion": "1",
  "enabled": true,
  "configured": true,
  "disabledReason": null,
  "tenantId": "11111111-1111-1111-1111-111111111111",
  "clientId": "22222222-2222-2222-2222-222222222222",
  "authType": "ClientSecret",
  "messagesEndpoint": "/api/messages",
  "tokenValidationEnabled": true,
  "principalStrategy": "CanonicalEntra",
  "agentBinding": null,
  "agentType": "assistant-agent",
  "agentHandle": "copilot",
  "sharedAgentHandle": null,
  "singleSignOn": false,
  "forwardsUserCredential": false,
  "proactiveEnabled": false,
  "proactiveConversationTypes": [ "personal" ],
  "streamingEnabled": true,
  "publicHostName": "agents.contoso.com",
  "manifestId": "22222222-2222-2222-2222-222222222222",
  "manifestName": "Contoso Assistant",
  "manifestVersion": "1.0.0",
  "turnStateStorage": "MemoryStorage",
  "status": "warn",
  "findings": [
    { "id": "token-validation", "status": "pass", "message": "Inbound activities must carry a valid Azure Bot Service token." },
    { "id": "principal-strategy", "status": "pass", "message": "Users map to the Entra tenant and object identity shared with the A2A endpoint." },
    { "id": "credential-type", "status": "warn", "message": "The bot authenticates with a client secret held in host configuration. Prefer WorkloadIdentity, FederatedCredentials, a managed identity, or a certificate." },
    { "id": "turn-state-storage", "status": "warn", "message": "Turn state is held in process memory. It is lost on restart and is not shared between host instances." },
    { "id": "user-credential-forwarding", "status": "pass", "message": "User access tokens are not placed on agent messages." },
    { "id": "public-host", "status": "pass", "message": "The app package lists this host's public name as a valid domain." },
    { "id": "proactive-scopes", "status": "skipped", "message": "Proactive delivery is off." }
  ]
}
```

That host was started with a client secret in its own configuration. The secret does not appear
anywhere in the response, and the test that produced this capture asserts that.

### The A2A endpoint

`GET /fabrcoreapi/admin/v1/integrations/a2a` on the same host, complete:

```json
{
  "apiVersion": "1",
  "enabled": true,
  "routePrefix": "/a2a",
  "publicBaseUrl": "https://agents.contoso.com",
  "authenticationMode": "JwtBearer",
  "apiKeyNames": [],
  "authority": "https://login.microsoftonline.com/11111111-1111-1111-1111-111111111111/v2.0",
  "audiences": [ "22222222-2222-2222-2222-222222222222" ],
  "requiredScopes": [ "agent.invoke" ],
  "requiredRoles": [],
  "principalStrategy": "CanonicalEntra",
  "discoveryMode": "None",
  "taskStore": "InMemoryA2ATaskStore",
  "status": "warn",
  "agents": [
    {
      "name": "assistant",
      "displayName": "Assistant",
      "basePath": "/a2a/assistant",
      "source": "Configured",
      "agentType": null,
      "agentHandle": "system:assistant",
      "binding": null
    }
  ],
  "findings": [
    { "id": "authentication", "status": "pass", "message": "Callers present a validated OAuth 2.0 bearer token." },
    { "id": "api-key-query", "status": "skipped", "message": "API key authentication is not in use." },
    { "id": "required-scopes", "status": "pass", "message": "Tokens must carry the configured scopes or roles." },
    { "id": "principal-strategy", "status": "pass", "message": "Callers map to the Entra tenant and object identity shared with the Microsoft 365 Copilot channel." },
    { "id": "publication", "status": "pass", "message": "Only agents named in configuration are published." },
    { "id": "public-base-url", "status": "pass", "message": "Agent cards advertise the configured public https origin." },
    { "id": "task-store", "status": "warn", "message": "Tasks are held in process memory. They are lost on restart and are not visible to other host instances." }
  ]
}
```

With A2A off the route still answers `200`, with `enabled: false`, empty `agents`, and every
finding `skipped`.

### Using the findings

| Status | Show it as | Examples |
| --- | --- | --- |
| `fail` | Blocking. The integration is unsafe or cannot work. | No A2A authentication; anonymous messaging endpoint; a channel-id principal strategy; a user token copied onto messages |
| `warn` | Review. It works, and someone should have chosen this on purpose. | A client secret; in-memory state; a shared principal; registry-wide publication |
| `pass` | Fine | |
| `skipped` | Not applicable, or the feature is off | |

* Key your text and severity on `id`. Messages are English and may be reworded between releases.
* The document `status` is the worst finding. Use it for a row badge.
* A finding id you do not recognize is still a finding. Show its message with its status.
* An operator who has decided to accept a `warn` should be able to say so in your product. The
  host has no notion of accepted risk.

## 3. Enable and configure

There is no enable call. You publish settings in the envelope's `settings` map and the host applies
them at its next start. For a customer who has already created their app registration and Azure
Bot resource, the map is:

```json
{
  "AgentBindings:assistant:AgentType": "assistant-agent",
  "AgentBindings:assistant:Handle": "assistant",
  "AgentBindings:assistant:Models": "default",

  "Microsoft365Copilot:Enabled": "true",
  "Microsoft365Copilot:TenantId": "11111111-1111-1111-1111-111111111111",
  "Microsoft365Copilot:ClientId": "22222222-2222-2222-2222-222222222222",
  "Microsoft365Copilot:AuthType": "WorkloadIdentity",
  "Microsoft365Copilot:Principal:Strategy": "CanonicalEntra",
  "Microsoft365Copilot:Agent:Binding": "assistant",
  "Microsoft365Copilot:Manifest:Name": "Contoso Assistant",
  "Microsoft365Copilot:Manifest:PublicHostName": "agents.contoso.com",

  "A2A:Enabled": "true",
  "A2A:PublicBaseUrl": "https://agents.contoso.com",
  "A2A:Principal:Strategy": "CanonicalEntra",
  "A2A:Agents:0:Name": "assistant",
  "A2A:Agents:0:Binding": "assistant",
  "A2A:Authentication:Mode": "JwtBearer",
  "A2A:Authentication:JwtBearer:Authority": "https://login.microsoftonline.com/11111111-1111-1111-1111-111111111111/v2.0",
  "A2A:Authentication:JwtBearer:Audience": "22222222-2222-2222-2222-222222222222",
  "A2A:Authentication:JwtBearer:RequiredScopes:0": "agent.invoke"
}
```

This example is assembled from the keys the host binds, not captured. Its identity values are the
ones the captured status responses above were produced from. The capture host differed in three
ways: it used a client secret instead of `WorkloadIdentity`, an explicit agent type instead of a
binding, and a fixed A2A agent handle instead of a bound A2A agent.

Rules a composer must enforce:

1. **Values are strings.** Booleans are `"true"` and `"false"`. Lists are indexed keys
   (`RequiredScopes:0`).
2. **No secrets, ever.** The host writes the envelope to `fabrcore.cloud-cache.json` in plaintext on
   every silo. Refuse to compose `Microsoft365Copilot:ClientSecret`,
   `A2A:Authentication:ApiKey:Keys:*:Value`, and anything under `FabrCore:ConnectionCredentials`.
   If the customer's only option is a secret, it goes into the host's own configuration and never
   through you.
3. **Use the same principal strategy on both channels.** `CanonicalEntra` on the channel and on
   A2A, with no prefix, or the same person reaches two different agents.
4. **A binding replaces the per-channel agent keys.** With `Agent:Binding` set, the binding
   overwrites `Microsoft365Copilot:Agent:AgentType`, `Handle`, `Models`, and the rest, so do not
   publish them as well. Combining a binding with `SharedAgentHandle` or `AgentPerConversation`
   fails startup.
5. **Do not publish top-level `Connections:*`.** That section belongs to the Microsoft 365 Agents
   SDK. The add-on synthesizes it from `Microsoft365Copilot:*` unless it already exists, so
   publishing it replaces the bot's credential configuration. The Connections *feature* is
   `FabrCore:Connections:*`.
6. **`FabrCore:Connections:*` and `FabrCore:RemoteAgents:*` need host cooperation.** They are read
   only by hosts that register those packages with the configuration-bound overloads. On other
   hosts the keys are stored and ignored. After a restart, confirm with the `connections.admin` and
   `remote-agents.enabled` heartbeat flags.
7. **An incomplete configuration stops the host from starting.** A `Microsoft365Copilot` section
   that enables the channel without an agent, or with token validation on and no client id, throws
   at startup. Validate before publishing: require an agent binding or agent type, a client id, a
   tenant id, and a secret-free auth type. The host's `POST /fabrcoreapi/admin/v1/settings/preview`
   evaluates configuration rules; it does not run the add-on's startup validation, so a clean
   preview is not proof the host will start. A host that fails to start fetches settings again on
   its next attempt, so publishing a corrected configuration recovers it without access to the
   machine.

All of these keys are restart-required. After a publish the host keeps running on its startup
values and lists the changed keys in the heartbeat's `pendingRestartSettings`. Nothing restarts the
host for you.

## 4. Show what is applied

`GET /fabrcoreapi/admin/v1/settings/state` returns one row per known setting; the same report rides
on the heartbeat as `configurationState`. Four rows from the configured host, as captured:

```json
{
  "key": "Microsoft365Copilot:ClientId",
  "hasDesiredValue": false,
  "desiredValue": null,
  "resolvedValue": "22222222-2222-2222-2222-222222222222",
  "appliedValue": "22222222-2222-2222-2222-222222222222",
  "appliedKnown": true,
  "source": "file-or-provider",
  "sourceId": "MemoryConfigurationProvider",
  "reason": null,
  "applyMode": "RestartRequired",
  "secret": false,
  "overridden": false,
  "pendingRestart": false,
  "canAdopt": true,
  "appliedRevision": null
}
```

```json
{
  "key": "Microsoft365Copilot:MessagesEndpoint",
  "hasDesiredValue": false,
  "desiredValue": null,
  "resolvedValue": "/api/messages",
  "appliedValue": "/api/messages",
  "appliedKnown": true,
  "source": "code-default",
  "sourceId": "Microsoft 365 Copilot add-on",
  "reason": "Built-in default; the key is not set in configuration.",
  "applyMode": "RestartRequired",
  "secret": false,
  "overridden": false,
  "pendingRestart": false,
  "canAdopt": true,
  "appliedRevision": null
}
```

```json
{
  "key": "Microsoft365Copilot:TokenValidation:Enabled",
  "hasDesiredValue": false,
  "desiredValue": null,
  "resolvedValue": null,
  "appliedValue": null,
  "appliedKnown": true,
  "source": "code-default",
  "sourceId": "Microsoft 365 Copilot add-on",
  "reason": "Built-in default; the key is not set in configuration.",
  "applyMode": "RestartRequired",
  "secret": true,
  "overridden": false,
  "pendingRestart": false,
  "canAdopt": false,
  "appliedRevision": null
}
```

```json
{
  "key": "Runtime:Microsoft365Copilot:ForwardsUserCredential",
  "hasDesiredValue": false,
  "desiredValue": null,
  "resolvedValue": "False",
  "appliedValue": "False",
  "appliedKnown": true,
  "source": "runtime",
  "sourceId": "Microsoft 365 Copilot add-on",
  "reason": null,
  "applyMode": "RestartRequired",
  "secret": false,
  "overridden": false,
  "pendingRestart": false,
  "canAdopt": false,
  "appliedRevision": null
}
```

The integration rows that host reported, summarized from the same capture:

| Key | Applied | Known | Source |
| --- | --- | --- | --- |
| `Microsoft365Copilot:Enabled` | `True` | yes | `code-default` |
| `Microsoft365Copilot:TenantId` | `11111111-…` | yes | `file-or-provider` |
| `Microsoft365Copilot:ClientId` | `22222222-…` | yes | `file-or-provider` |
| `Microsoft365Copilot:AuthType` | `ClientSecret` | yes | `code-default` |
| `Microsoft365Copilot:MessagesEndpoint` | `/api/messages` | yes | `code-default` |
| `Microsoft365Copilot:TokenValidation:Enabled` | redacted | yes | `code-default` |
| `Microsoft365Copilot:Principal:Strategy` | `CanonicalEntra` | yes | `file-or-provider` |
| `Microsoft365Copilot:Agent:Binding` | unset | yes | `unknown` |
| `Microsoft365Copilot:Proactive:Enabled` | `False` | yes | `code-default` |
| `Microsoft365Copilot:Streaming:Enabled` | `True` | yes | `code-default` |
| `Microsoft365Copilot:Manifest:PublicHostName` | `agents.contoso.com` | yes | `file-or-provider` |
| `Microsoft365Copilot:ClientSecret` | redacted | no | `file-or-provider` |
| `Microsoft365Copilot:Agent:AgentType`, `Manifest:Name` | not observed | no | `file-or-provider` |
| `Runtime:Microsoft365Copilot:SingleSignOn` | `False` | yes | `runtime` |
| `Runtime:Microsoft365Copilot:ForwardsUserCredential` | `False` | yes | `runtime` |
| `A2A:Enabled` | `True` | yes | `file-or-provider` |
| `A2A:Authentication:Mode` | `JwtBearer` | yes | `file-or-provider` |
| `A2A:Principal:Strategy` | `CanonicalEntra` | yes | `file-or-provider` |
| `A2A:PublicBaseUrl` | `https://agents.contoso.com` | yes | `file-or-provider` |
| `A2A:Authentication:JwtBearer:*`, `A2A:AgentHandles:0` | not observed | no | `file-or-provider` |

How to read it:

* **"Known: no" means the host makes no claim**, not that the setting is ignored. Only the settings
  listed in the protocol are observed by a consumer; other keys in the section are reported as
  configured (`resolvedValue`) with `appliedKnown: false`. Render them as "configured", not as
  "applied".
* **`code-default` is not an override.** Nobody set the key; the host is running on the built-in
  default and tells you what it is. `code-or-consumer` is the one that means startup code changed a
  value, and it is the row to show when a publish has no effect.
* **`pendingRestart: true`** is how you find changes that are published but not live. Filter the
  rows to the `Microsoft365Copilot:`, `A2A:`, `AgentBindings:`, `FabrCore:Connections:`, and
  `FabrCore:RemoteAgents:` prefixes for an integration-specific view.
* **`Microsoft365Copilot:TokenValidation:Enabled` arrives redacted.** The host treats any key
  containing `token` as a secret. The value is not a secret; read it from `tokenValidationEnabled`
  on the status route. Its `pendingRestart` flag is reliable. A server that applies the same
  name-based redaction to stored reports, as Insights does, will hide it a second time.
* **While the channel is off**, every channel row except `Enabled` has `appliedKnown: false`.
  On the unconfigured host, `Microsoft365Copilot:Enabled` was reported as applied `False` with
  source `code-default`, and `Microsoft365Copilot:AuthType` as not known.
* **`Runtime:` rows are facts.** They cannot be published or adopted. Treat
  `ForwardsUserCredential: True` as a finding in its own right.

## 5. Diagnose

`POST /fabrcoreapi/admin/v1/integrations/microsoft365/diagnostics` takes no body. On a host with
the channel off it returns `200` with `status: "skipped"` and nine skipped checks. On the
configured host, complete and as captured:

```json
{
  "apiVersion": "1",
  "integration": "microsoft365",
  "observedAt": "2026-10-07T16:39:04.7512186+00:00",
  "status": "fail",
  "checks": [
    { "id": "token-validation", "status": "pass", "message": "Inbound activities must carry a valid Azure Bot Service token." },
    { "id": "principal-strategy", "status": "pass", "message": "Users map to the Entra tenant and object identity shared with the A2A endpoint." },
    { "id": "credential-type", "status": "warn", "message": "The bot authenticates with a client secret held in host configuration. Prefer WorkloadIdentity, FederatedCredentials, a managed identity, or a certificate." },
    { "id": "turn-state-storage", "status": "warn", "message": "Turn state is held in process memory. It is lost on restart and is not shared between host instances." },
    { "id": "user-credential-forwarding", "status": "pass", "message": "User access tokens are not placed on agent messages." },
    { "id": "public-host", "status": "pass", "message": "The app package lists this host's public name as a valid domain." },
    { "id": "proactive-scopes", "status": "skipped", "message": "Proactive delivery is off." },
    { "id": "agent-type-registered", "status": "fail", "message": "Agent type 'assistant-agent' is not registered on this host. Check its [AgentAlias] and that its assembly is loaded." },
    { "id": "bot-service-credential", "status": "fail", "message": "MsalServiceException: AADSTS90002: Tenant '11111111-1111-1111-1111-111111111111' not found. Check to make sure you have the correct tenant ID and are signing into the correct cloud. Check with your subscription administrator, this may happen if there are no active subscriptions for the tenant. Trace ID: cc9bb61d-48d7-492" }
  ]
}
```

Both failures are real. The agent type named in configuration does not exist on that host, and the
tenant id is a placeholder, so Entra refused the token request. The second message is exactly what
the host reports: the exception type, then the first line of the identity provider's message, cut
at 300 characters.

The run was audited. `GET /fabrcoreapi/admin/v1/access/audit` afterwards, as captured:

```json
[
  {
    "id": "1b5d5a5bee4f47a49bad7c981c76095b",
    "timestamp": "2026-10-07T16:39:04.7513555+00:00",
    "category": 5,
    "outcome": 0,
    "subjectPrincipal": "cluster-admin",
    "subjectAgent": null,
    "resourcePrincipal": null,
    "resource": "integrations/microsoft365/diagnostics",
    "permission": "integrations.admin",
    "enforcementMode": null,
    "wasEnforced": false,
    "reason": null,
    "details": { "operation": "diagnostics", "status": "fail", "commandId": "" },
    "traceId": null,
    "verifiableExecutionId": null
  }
]
```

`category: 5` is `RemoteAdministration`. `outcome: 0` is success: the diagnostic ran. Its result is
in `details.status`. `subjectPrincipal` is `cluster-admin` because the capture called the host
directly; through the connect channel it is the operator you set in `X-FabrCore-Admin-Actor`, and
`commandId` is the connect command id.

What to tell operators:

* **A pass means the host can authenticate as the bot.** The host asked Entra for an Azure Bot
  Service token with the configured credential and got one. The token is discarded and never
  leaves the host.
* **A pass does not mean Microsoft can reach the host.** Nothing in this call travels inbound. A
  wrong messaging endpoint on the Azure Bot resource, a closed firewall, or a proxy that does not
  route `/api/messages` all pass diagnostics. If you want that assurance, send an unauthenticated
  `POST` to the public messaging URL from your own network and expect `401`, which shows the route
  exists and is protected.
* **It can take up to 20 seconds.** That is the token timeout. Keep your own command lifetime above
  it; a 45-second command lifetime is enough.
* **It is safe to run on demand, and it is not free.** Each run makes a real token request and
  writes an audit event. Run it when an operator asks, not on a timer.
* **Target one host instance.** Credentials such as workload identity are per pod. Run diagnostics
  against each instance you care about, not once per cluster.

## 6. Hand over the app package

`GET …/integrations/microsoft365/manifest` on the configured host, complete:

```json
{
  "$schema": "https://developer.microsoft.com/json-schemas/teams/v1.25/MicrosoftTeams.schema.json",
  "manifestVersion": "1.25",
  "version": "1.0.0",
  "id": "22222222-2222-2222-2222-222222222222",
  "developer": {
    "name": "FabrCore",
    "websiteUrl": "https://github.com/vulcan365/FabrCore",
    "privacyUrl": "https://github.com/vulcan365/FabrCore",
    "termsOfUseUrl": "https://github.com/vulcan365/FabrCore"
  },
  "name": { "short": "Contoso Assistant", "full": "Contoso Assistant" },
  "description": {
    "short": "A FabrCore agent available in Microsoft 365 Copilot.",
    "full": "A FabrCore agent available in Microsoft 365 Copilot."
  },
  "icons": { "color": "color.png", "outline": "outline.png" },
  "accentColor": "#4B53C5",
  "bots": [
    { "botId": "22222222-2222-2222-2222-222222222222", "scopes": [ "personal" ], "supportsFiles": false, "isNotificationOnly": false }
  ],
  "copilotAgents": { "customEngineAgents": [ { "id": "22222222-2222-2222-2222-222222222222", "type": "bot" } ] },
  "permissions": [ "identity", "messageTeamMembers" ],
  "validDomains": [ "agents.contoso.com" ]
}
```

`GET …/integrations/microsoft365/app-package` returned `200`, `application/zip`, 2,083 bytes: the
manifest and two placeholder icons. Both routes send `Cache-Control: no-store, no-cache`, because
the package is regenerated from live options on every request.

While the channel is off, both routes return `409`, as captured:

```json
{
  "error": "channel-disabled",
  "message": "No Microsoft365Copilot configuration was found and no options were supplied in code."
}
```

The same status with `error: "manifest-unavailable"` is returned when the channel is on but has no
client id to build a manifest from.

Handling:

* **Over the connect channel the zip is the base64 `body` of the command response.** Decode it to
  bytes. Do not pass it through anything that treats the body as text.
* **Name the file yourself.** The host sends `Content-Disposition`, but the connect channel returns
  only `Content-Type`, `ETag`, `Location`, and `Retry-After`. Use `appPackage.zip`.
* **Fetch it after the restart, not before.** The package reflects the options the host is running
  with. A package fetched while a manifest change is pending restart describes the old manifest.
  Check `pendingRestart` on the `Microsoft365Copilot:Manifest:*` rows first.
* **Replace the placeholder developer details before a customer uploads it.** The developer name
  and the three URLs in the capture are the defaults. Publish `Microsoft365Copilot:Manifest:*`
  values for the customer, including icon paths that exist on the host.
* **The manifest is personal scope only.** The app cannot be added to a group chat or a team.
* **Do not link operators to `/m365copilot/appPackage.zip`.** That developer endpoint is anonymous
  and off outside Development.

## 7. Status codes

| Code | From | Meaning | Do |
| --- | --- | --- | --- |
| `200` | Any route | Answered, including for a feature that is off | Read `enabled` and `status` |
| `400` | Integration routes | The request carried `X-FabrCore-Admin-Target`, `x-user`, or `x-user-handle` | Send none of them; these routes describe the host |
| `401` | Any route | Missing or wrong administration credential | Over the connect channel this should not happen; the host supplies its own key |
| `404` | `/integrations/microsoft365*` | The add-on is not installed on this host | Hide the feature for this host; check the `m365copilot` flag first |
| `404` | `/integrations/*` | The host predates the integrations API | Treat as "unknown"; show configured settings as unverified |
| `409` | `manifest`, `app-package` | The channel is off, or cannot describe itself | Show `message` |
| `408`, `413`, `502` | The connect channel | Deadline passed, body too large, or the host could not execute the request | See the protocol's connect channel section. None of the integration routes change host configuration, so a retry is safe. |

## 8. Notes for Insights

These map the steps above onto Insights as it is in `FabrCore-V365` at commit `7d851b0`. Each was
checked against that code. Section 15 of the [plan](cloud-managed-copilot-integrations-plan.md) has
the full findings.

| Step | Use | Gap to close |
| --- | --- | --- |
| Discover from heartbeats | `InsightsSiloStatusDto.CapabilitiesJson`, read at `$.capabilities` | The column holds the whole heartbeat payload as a string and has no typed accessor. Add one for the integration flags. |
| Capability document | `IInsightsEnvironmentAdminClient.SendAsync(target, HttpMethod.Get, "/fabrcoreapi/admin/v1/capabilities")` | The Connections page tests only for a service *named* `connections`. Do not copy that pattern for `a2a`; check `available`. |
| Status and diagnostics | The same client, with `InsightsEnvironmentTarget.HostInstanceId` set to target one host | None. Responses are JSON, which that client already returns as text. |
| Compose and publish | `IConfigDocumentService.PublishAsync` with `expectedVersion`, following the JSON-tree edit that the Foundry integration uses | A publish replaces the whole layer body, so read, edit `settings`, and republish. There is no key-ownership marker; an operator can edit composed keys in the runtime settings form. |
| Pending restart | `InsightsSiloStatusDto.SettingsStatus.PendingRestartSettings`; the existing columns on the status and overview pages | Only a filter to the integration's key prefixes is new. |
| Applied state | `InsightsSiloStatusDto.ConfigurationState`; live `GET /settings/state` | `ConfigurationReportPolicy` nulls values for keys matching `CloudConfigurationPolicy.IsSecret`, which includes `Microsoft365Copilot:TokenValidation:Enabled` and `UserAuthorization:PassUserTokenToAgent`. Read those from the status route. |
| App package | `IConnectCommandService.EnqueueAndWaitAsync` | `IInsightsEnvironmentAdminClient` decodes the response body as UTF-8 text, which corrupts a zip. A byte-returning call is needed. The 4 MiB body cap is far above the package size. |

Two things in Insights today work against the rules in section 3:

* **The runtime settings catalog offers `Microsoft365Copilot:ClientSecret` and A2A API key values
  as publishable fields.** A value entered there is delivered in the `settings` map and written to
  the host's plaintext cache. The composer must not emit these, and the form should stop offering
  them or warn that the value will be stored unencrypted on every host.
* **Connect command rows, including response bodies, are never deleted.** Every package download
  and status response stays in `insights.ConnectCommand`. That is tolerable for status documents,
  which contain no secrets, and worth a retention job before the feature is used routinely.

## Checklist

Before calling the integration healthy for a host:

- [ ] `m365copilot.enabled` is `true` in that host's heartbeat
- [ ] `microsoft365-copilot` is `available` in the capability document
- [ ] `GET integrations/microsoft365` has no `fail` finding
- [ ] `principalStrategy` is `CanonicalEntra` on the channel and, if A2A is on, on A2A
- [ ] `authType` is not `ClientSecret`
- [ ] No integration row in the configuration-state report has `pendingRestart: true`
- [ ] `Runtime:Microsoft365Copilot:ForwardsUserCredential` is `False`
- [ ] `POST diagnostics` passes `agent-type-registered` and `bot-service-credential` on every
      instance
- [ ] An unauthenticated `POST` to the public messaging URL answers `401` from outside
- [ ] The package was fetched after the last restart and its developer details are the customer's
