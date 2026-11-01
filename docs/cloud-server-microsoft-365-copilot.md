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

**Revised 13 October 2026 for several agents per host.** The Copilot add-on now exposes a list of
agents, `Microsoft365Copilot:Agents`, in place of a single agent, and its status document is
`apiVersion` `2`. The Microsoft 365 examples in sections 1 to 6 were revised to the new shape from
the status model, the add-on code, and the assertions in `CopilotIntegrationEndpointTests`, whose
configured host now exposes two agents, `assistant` and `sales`. They were not captured again, so
treat them as a description of the shape, not as byte-exact output. The A2A, audit, and manifest
captures are unchanged.

Why a list: Microsoft allows at most one bot, and one custom engine agent, per app manifest, and
an Azure Bot resource has one messaging endpoint and one app id. Each agent a customer wants
listed in Microsoft 365 Copilot is therefore its own Entra app registration, its own Azure Bot,
and its own app package. One host serves them all, each on its own messaging route.

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
| Give the administrator the package to upload, one per agent | `GET …/integrations/microsoft365/agents/{name}/app-package` | 6 |

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
| `m365copilot.enabled: "true" \| "false"` | Whether the channel is on |
| `m365copilot.agents: "2"` | Number of agents the channel exposes. Present only on hosts that take the `Agents` list; `"0"` while the channel is off or has no agents |
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
      "apiVersion": "2",
      "features": [ "status", "app-package", "diagnostics", "multi-agent" ],
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
  "apiVersion": "2",
  "features": [ "status", "app-package", "diagnostics", "multi-agent", "activity-protocol", "streaming", "adaptive-cards", "principal-canonicalentra" ],
  "available": true,
  "unavailableReason": null
}
```

Rules:

* **Check `available`, not presence.** `a2a` is listed on every host, on or off. A check for
  "is there a service named `a2a`" is true everywhere and means nothing.
* `status`, `app-package`, `diagnostics`, and `multi-agent` are listed even while the channel is
  off, because they describe the add-on, not its configuration.
* **`multi-agent` tells you which keys a host reads.** A host that lists it, or reports the
  `m365copilot.agents` flag or a capability `apiVersion` of `2`, reads `Microsoft365Copilot:Agents`
  and not the single-agent keys. A host without it reads only the single-agent keys of earlier
  releases. Publishing only the shape a host does not read stops it from starting; section 3,
  rule 4, has how to publish for both during an upgrade. The `agent-binding` feature of earlier
  releases is gone.
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
  "apiVersion": "2",
  "enabled": false,
  "configured": false,
  "disabledReason": "No Microsoft365Copilot configuration was found and no options were supplied in code.",
  "tenantId": null,
  "tokenValidationEnabled": true,
  "principalStrategy": "EntraObjectId",
  "singleSignOn": false,
  "forwardsUserCredential": false,
  "proactiveEnabled": false,
  "proactiveConversationTypes": [ "personal" ],
  "streamingEnabled": true,
  "publicHostName": null,
  "turnStateStorage": null,
  "status": "skipped",
  "agents": [],
  "findings": [
    { "id": "token-validation", "status": "skipped", "message": "The Microsoft 365 Copilot channel is off on this host." }
  ]
}
```

`configured: false` is the signal that nobody has chosen these values. The non-null fields are
option defaults, not decisions. `agents` is empty whenever the channel is off, and also when it is
on with no agents configured, which is a valid state: the host then maps no messaging endpoint.

The same route on a configured host, complete:

```json
{
  "apiVersion": "2",
  "enabled": true,
  "configured": true,
  "disabledReason": null,
  "tenantId": "11111111-1111-1111-1111-111111111111",
  "tokenValidationEnabled": true,
  "principalStrategy": "CanonicalEntra",
  "singleSignOn": false,
  "forwardsUserCredential": false,
  "proactiveEnabled": false,
  "proactiveConversationTypes": [ "personal" ],
  "streamingEnabled": true,
  "publicHostName": "agents.contoso.com",
  "turnStateStorage": "MemoryStorage",
  "status": "warn",
  "agents": [
    {
      "name": "assistant",
      "messagesEndpoint": "/api/messages",
      "clientId": "22222222-2222-2222-2222-222222222222",
      "tenantId": "11111111-1111-1111-1111-111111111111",
      "authType": "ClientSecret",
      "agentType": "assistant-agent",
      "agentHandle": "assistant",
      "manifestId": "22222222-2222-2222-2222-222222222222",
      "manifestName": "Contoso Assistant",
      "manifestVersion": "1.0.0"
    },
    {
      "name": "sales",
      "messagesEndpoint": "/api/messages/sales",
      "clientId": "33333333-3333-3333-3333-333333333333",
      "tenantId": "11111111-1111-1111-1111-111111111111",
      "authType": "ClientSecret",
      "agentType": "sales-agent",
      "agentHandle": "sales",
      "manifestId": "33333333-3333-3333-3333-333333333333",
      "manifestName": "Contoso Sales",
      "manifestVersion": "1.0.0"
    }
  ],
  "findings": [
    { "id": "token-validation", "status": "pass", "message": "Inbound activities must carry a valid Azure Bot Service token issued for the bot that owns the route." },
    { "id": "principal-strategy", "status": "pass", "message": "Users map to the Entra tenant and object identity shared with the A2A endpoint." },
    { "id": "credential-type", "status": "warn", "message": "These agents authenticate with a client secret held in host configuration: assistant, sales. Prefer WorkloadIdentity, FederatedCredentials, a managed identity, or a certificate." },
    { "id": "turn-state-storage", "status": "warn", "message": "Turn state is held in process memory. It is lost on restart and is not shared between host instances." },
    { "id": "user-credential-forwarding", "status": "pass", "message": "User access tokens are not placed on agent messages." },
    { "id": "public-host", "status": "pass", "message": "The app package lists this host's public name as a valid domain." },
    { "id": "proactive-scopes", "status": "skipped", "message": "Proactive delivery is off." }
  ]
}
```

That host was started with client secrets in its own configuration. They do not appear
anywhere in the response, and the test behind this example asserts that.

Reading `agents`:

* **`name` is the key.** It is the `Name` of the configuration entry, it is unique on the host,
  and it addresses the per-agent package routes in section 6. Key your rows on it.
* **Every value has its defaults applied.** `messagesEndpoint` is `/api/messages/{name}` unless the
  entry set a `Route`; `agentHandle` is the entry name unless it set a `Handle`; `tenantId` and
  `authType` fall back to the top-level settings; `manifestId` is the agent's client id unless a
  manifest id was set.
* **`messagesEndpoint` is what the Azure Bot must point at.** Show it joined to the public host
  name, because the operator has to paste that URL into the Azure Bot resource of that agent.
* **`authType` is `null`** when the host configures the Agents SDK `Connections` section itself.
* The fields a version `1` document carried at the top level (`clientId`, `authType`,
  `messagesEndpoint`, `agentBinding`, `agentType`, `agentHandle`, `sharedAgentHandle`,
  `manifestId`, `manifestName`, `manifestVersion`) are gone. A server that must also manage hosts
  on earlier releases branches on `apiVersion`.
* **Each route accepts only its own bot.** The host validates a token issued for any configured
  bot, then requires the token audience to be the client id of the agent that owns the route (or
  one of the host-wide `TokenValidation:Audiences`), and answers `403` otherwise.

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
  host has no notion of accepted risk. `credential-type` is the obvious case: the host reports a
  client secret as `warn` on every deployment, so where publishing a client secret is the chosen
  approach, treat that finding as accepted by default instead of showing every host as needing
  review.

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
  "Microsoft365Copilot:AuthType": "WorkloadIdentity",
  "Microsoft365Copilot:Principal:Strategy": "CanonicalEntra",
  "Microsoft365Copilot:Manifest:PublicHostName": "agents.contoso.com",
  "Microsoft365Copilot:Agents:0:Name": "assistant",
  "Microsoft365Copilot:Agents:0:Route": "/api/messages",
  "Microsoft365Copilot:Agents:0:ClientId": "22222222-2222-2222-2222-222222222222",
  "Microsoft365Copilot:Agents:0:AgentType": "assistant-agent",
  "Microsoft365Copilot:Agents:0:Handle": "assistant",
  "Microsoft365Copilot:Agents:0:Models": "default",
  "Microsoft365Copilot:Agents:0:Manifest:Name": "Contoso Assistant",
  "Microsoft365Copilot:Agents:0:Manifest:Description": "Answers questions for Contoso staff.",

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

For a customer using a client secret, which is the common case, replace the `AuthType` line with:

```json
{
  "Microsoft365Copilot:AuthType": "ClientSecret",
  "Microsoft365Copilot:Agents:0:ClientSecret": "<the client secret>"
}
```

A second agent is a second entry with its own bot registration. `Route` is left out here, so the
host serves it on `/api/messages/sales`:

```json
{
  "Microsoft365Copilot:Agents:1:Name": "sales",
  "Microsoft365Copilot:Agents:1:ClientId": "33333333-3333-3333-3333-333333333333",
  "Microsoft365Copilot:Agents:1:ClientSecret": "<the second bot's client secret>",
  "Microsoft365Copilot:Agents:1:AgentType": "sales-agent",
  "Microsoft365Copilot:Agents:1:Manifest:Name": "Contoso Sales",
  "Microsoft365Copilot:Agents:1:Manifest:Description": "Answers sales questions."
}
```

The keys of one entry:

| Key under `Microsoft365Copilot:Agents:{i}:` | Required | Default |
| --- | --- | --- |
| `Name` | yes | Letters, digits, `-`, `_`; at most 63 characters. Unique on the host |
| `Route` | no | `/api/messages/{Name}`. One entry may claim `/api/messages`. A fixed path, unique on the host |
| `ClientId` | when token validation is on | The agent's bot registration. Unique on the host |
| `ClientSecret` | when the auth type is `ClientSecret` | *Secret* |
| `TenantId`, `AuthType`, `CertificateThumbprint`, `CertificateSubjectName`, `CertificateStoreName`, `FederatedClientId`, `FederatedTokenFile`, `AuthorityEndpoint` | no | The top-level `Microsoft365Copilot:` value |
| `AgentType` | yes | A registered `[AgentAlias]` |
| `Handle` | no | The entry `Name`. A bare handle, no `:` |
| `Models` | no | `default` |
| `WelcomeMessage` | no | The top-level `WelcomeMessage` |
| `Manifest:Name` | no | The top-level manifest name with one agent, the entry `Name` with several. Unique on the host |
| `Manifest:Id` | no | The agent's `ClientId` |
| `Manifest:Description`, `Version`, `FullName`, `FullDescription`, `DeveloperName`, `WebsiteUrl`, `PrivacyUrl`, `TermsOfUseUrl`, `AccentColor`, `ColorIconPath`, `OutlineIconPath`, `ConversationStarters:{n}:Title\|Description` | no | The top-level `Microsoft365Copilot:Manifest:` value |

This example is assembled from the keys the host binds, not captured. Its identity values are the
ones the status responses above use. The test host used client secrets, as in the variant, exposed
both agents, and used a fixed A2A agent handle instead of a bound A2A agent.

`AgentBindings:assistant:*` is in the map for A2A, which still resolves `A2A:Agents:0:Binding`
through it. The Copilot channel no longer reads `AgentBindings`. The same person reaches the same
agent on both channels because the Copilot entry names the same agent type and handle as the
binding, and both channels use `CanonicalEntra`.

Rules a composer must enforce:

1. **Values are strings.** Booleans are `"true"` and `"false"`. Lists are indexed keys
   (`RequiredScopes:0`).
2. **Secrets may be published, and you then own them.** `Microsoft365Copilot:Agents:{i}:ClientSecret`,
   `A2A:Authentication:ApiKey:Keys:{n}:Value`, and `FabrCore:ConnectionCredentials:{ref}:Secret` are
   ordinary settings to the host. Three things follow. The host writes the whole envelope to
   `fabrcore.cloud-cache.json` in plaintext on every silo, so that file is secret material. The
   value stays in every earlier revision you keep, so rotating means deleting the old secret in
   Entra as well as publishing the new one. And a client secret has an end date: record it and
   warn before it, because an expired secret takes the bot offline with no change on the host.
   With several agents there is one secret, and one end date, per agent.
   Never echo a secret back in a review, a diff, an audit entry, or a log; show that it is set,
   not what it is. A customer who wants you to hold nothing uses `WorkloadIdentity` or
   `FederatedCredentials` instead.
3. **Use the same principal strategy on both channels.** `CanonicalEntra` on the channel and on
   A2A, with no prefix, or the same person reaches two different agents.
4. **Publish the agent list, and the single-agent keys only while older hosts remain.**
   `Microsoft365Copilot:Agents` is an indexed list: publish every entry in full, with indexes that
   start at 0 and have no gaps. The host no longer reads `Microsoft365Copilot:Agent:*`,
   `Microsoft365Copilot:MessagesEndpoint`, or a top-level `Microsoft365Copilot:ClientId` or
   `ClientSecret`. With the channel enabled and no `Agents` section, their presence anywhere in
   the host's effective configuration fails startup with a message pointing at
   `Microsoft365Copilot:Agents`. When an `Agents` list is present they are ignored, and a startup
   warning names them. `SharedAgentHandle` and `AgentPerConversation` no longer exist.
   **This is what makes an upgrade safe.** A host older than the list has no `m365copilot.agents`
   heartbeat flag, reads only the single-agent keys, and ignores `Agents`. Publish the list and,
   beside it, the first agent in the single-agent form: `Microsoft365Copilot:ClientId`,
   `ClientSecret`, `AuthType`, `Agent:Binding`, the `AgentBindings:{name}:*` it names, and
   `Manifest:Name` and `Description`. Older hosts then serve that one agent and newer hosts serve
   the whole list. The order is: upgrade the cloud server and republish, upgrade the hosts, then
   stop publishing the single-agent keys. Insights does this with its "Also publish for older
   hosts" option, which is on by default for integrations created before the change.
   **An entry names an agent and does not configure it.** Plugins, tools, the system prompt, and
   args are not settings of this channel. An agent that already exists for the user under the
   entry's handle, for example one a blueprint provisioned, is used untouched; a missing one is
   created from `AgentType` and `Models` only. Define the rest on the agent type or in a
   blueprint.
5. **Do not publish top-level `Connections:*`.** That section belongs to the Microsoft 365 Agents
   SDK. The add-on synthesizes it from `Microsoft365Copilot:*` unless it already exists, so
   publishing it replaces the bot's credential configuration. The Connections *feature* is
   `FabrCore:Connections:*`.
6. **`FabrCore:Connections:*` and `FabrCore:RemoteAgents:*` need host cooperation.** They are read
   only by hosts that register those packages with the configuration-bound overloads. On other
   hosts the keys are stored and ignored. After a restart, confirm with the `connections.admin` and
   `remote-agents.enabled` heartbeat flags.
7. **An incomplete configuration stops the host from starting.** An entry without a `Name` or an
   `AgentType`, a name, route, client id, or manifest name used by two entries, token validation
   on and an entry with no client id, or `AuthType` `ClientSecret` and an entry with no secret,
   throws at startup with a message naming the setting, for example
   `Microsoft365Copilot:Agents:1:Route`. Validate before publishing: require a tenant id, and for
   every entry a name, an agent type, a client id, and a secret whenever the auth type needs one,
   and check the four uniqueness rules across entries. An enabled channel with no entries is
   valid; the host starts, maps no messaging endpoint, and logs a warning. The host's `POST /fabrcoreapi/admin/v1/settings/preview`
   evaluates configuration rules; it does not run the add-on's startup validation, so a clean
   preview is not proof the host will start. A host that fails to start fetches settings again on
   its next attempt, so publishing a corrected configuration recovers it without access to the
   machine.

All of these keys are restart-required. After a publish the host keeps running on its startup
values and lists the changed keys in the heartbeat's `pendingRestartSettings`. Nothing restarts the
host for you.

## 4. Show what is applied

`GET /fabrcoreapi/admin/v1/settings/state` returns one row per known setting; the same report rides
on the heartbeat as `configurationState`. Three rows from the configured host:

```json
{
  "key": "Microsoft365Copilot:Agents:0:ClientId",
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

The integration rows that host reported. The `Agents:{i}` rows are from the test's assertions on
applied values; the source of a row whose key the configuration does not set was not captured:

| Key | Applied | Known | Source |
| --- | --- | --- | --- |
| `Microsoft365Copilot:Enabled` | `True` | yes | `code-default` |
| `Microsoft365Copilot:TenantId` | `11111111-…` | yes | `file-or-provider` |
| `Microsoft365Copilot:AuthType` | `ClientSecret` | yes | `code-default` |
| `Microsoft365Copilot:TokenValidation:Enabled` | redacted | yes | `code-default` |
| `Microsoft365Copilot:Principal:Strategy` | `CanonicalEntra` | yes | `file-or-provider` |
| `Microsoft365Copilot:Proactive:Enabled` | `False` | yes | `code-default` |
| `Microsoft365Copilot:Streaming:Enabled` | `True` | yes | `code-default` |
| `Microsoft365Copilot:Manifest:PublicHostName` | `agents.contoso.com` | yes | `file-or-provider` |
| `Microsoft365Copilot:Agents:0:Name` | `assistant` | yes | `file-or-provider` |
| `Microsoft365Copilot:Agents:0:Route` | `/api/messages` | yes | `file-or-provider` |
| `Microsoft365Copilot:Agents:0:ClientId` | `22222222-…` | yes | `file-or-provider` |
| `Microsoft365Copilot:Agents:0:AgentType` | `assistant-agent` | yes | `file-or-provider` |
| `Microsoft365Copilot:Agents:0:Handle` | `assistant` | yes | not captured |
| `Microsoft365Copilot:Agents:1:Name` | `sales` | yes | `file-or-provider` |
| `Microsoft365Copilot:Agents:1:Route` | `/api/messages/sales` | yes | not captured |
| `Microsoft365Copilot:Agents:1:ClientId` | `33333333-…` | yes | `file-or-provider` |
| `Microsoft365Copilot:Agents:1:AgentType` | `sales-agent` | yes | `file-or-provider` |
| `Microsoft365Copilot:Agents:1:Handle` | `sales` | yes | not captured |
| `Microsoft365Copilot:Agents:{i}:ClientSecret` | redacted | no | `file-or-provider` |
| `Microsoft365Copilot:Agents:{i}:Models`, `Manifest:Name` | not observed | no | `file-or-provider` |
| `Runtime:Microsoft365Copilot:SingleSignOn` | `False` | yes | `runtime` |
| `Runtime:Microsoft365Copilot:ForwardsUserCredential` | `False` | yes | `runtime` |
| `A2A:Enabled` | `True` | yes | `file-or-provider` |
| `A2A:Authentication:Mode` | `JwtBearer` | yes | `file-or-provider` |
| `A2A:Principal:Strategy` | `CanonicalEntra` | yes | `file-or-provider` |
| `A2A:PublicBaseUrl` | `https://agents.contoso.com` | yes | `file-or-provider` |
| `A2A:Authentication:JwtBearer:*`, `A2A:AgentHandles:0` | not observed | no | `file-or-provider` |

How to read it:

* **The add-on reports five rows per agent**: `Agents:{i}:Name`, `Route`, `ClientId`, `AgentType`,
  and `Handle`, each with its default applied, so a `Route` or `Handle` the configuration never
  set still shows the value the agent runs with. The `ClientId`, `MessagesEndpoint`, and
  `Agent:Binding` rows of earlier releases are gone. `{i}` is the position in the list, not a
  stable identity; match rows to agents through the `Name` row or the status document.
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
* **While the channel is off**, every channel row except `Enabled` has `appliedKnown: false`, and
  the add-on reports no `Agents:{i}` rows.
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
    { "id": "token-validation", "status": "pass", "message": "Inbound activities must carry a valid Azure Bot Service token issued for the bot that owns the route." },
    { "id": "principal-strategy", "status": "pass", "message": "Users map to the Entra tenant and object identity shared with the A2A endpoint." },
    { "id": "credential-type", "status": "warn", "message": "These agents authenticate with a client secret held in host configuration: assistant, sales. Prefer WorkloadIdentity, FederatedCredentials, a managed identity, or a certificate." },
    { "id": "turn-state-storage", "status": "warn", "message": "Turn state is held in process memory. It is lost on restart and is not shared between host instances." },
    { "id": "user-credential-forwarding", "status": "pass", "message": "User access tokens are not placed on agent messages." },
    { "id": "public-host", "status": "pass", "message": "The app package lists this host's public name as a valid domain." },
    { "id": "proactive-scopes", "status": "skipped", "message": "Proactive delivery is off." },
    { "id": "agent-type-registered", "status": "fail", "message": "Not registered on this host: 'assistant-agent' (assistant), 'sales-agent' (sales). Check each [AgentAlias] and that its assembly is loaded." },
    { "id": "bot-service-credential", "status": "fail", "message": "Agent 'assistant': MsalServiceException: AADSTS90002: Tenant '11111111-1111-1111-1111-111111111111' not found. Check to make sure you have the correct tenant ID and are signing into the correct cloud. Check with your subscription administrator, this may happen if there are no active subscriptions for the tenant. Trace ID: cc9bb61d-48d7-492" }
  ]
}
```

Both failures are real. The agent types named in configuration do not exist on that host, and the
tenant id is a placeholder, so Entra refused the token request. The second message is what
the host reports: the agent's name, the exception type, then the first line of the identity
provider's message, cut at 300 characters. The diagnostics report keeps `apiVersion` `1`; only the
status document moved to `2`.

The check ids do not multiply with the agents. `agent-type-registered` lists every agent whose
type is missing, as `'type' (name)`. `bot-service-credential` requests a token for each bot in
turn and stops at the first that fails, naming its agent, so a pass covers every agent and a
failure names one: fix it and run again to find the next. Key on the ids as before and show the
message, which carries the agent name.

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

* **A pass means the host can authenticate as every bot.** The host asked Entra for an Azure Bot
  Service token with each agent's configured credential and got one. The tokens are discarded and
  never leave the host.
* **A pass does not mean Microsoft can reach the host.** Nothing in this call travels inbound. A
  wrong messaging endpoint on the Azure Bot resource, a closed firewall, or a proxy that does not
  route an agent's messaging route all pass diagnostics. If you want that assurance, send an
  unauthenticated `POST` to each agent's public messaging URL (the host name plus the agent's
  `messagesEndpoint`) from your own network and expect `401`, which shows the route exists and is
  protected.
* **It can take up to 20 seconds per agent.** That is the token timeout, and the bots are tested
  one after another. Keep your own command lifetime above
  it; a 45-second command lifetime is enough for a host with one or two agents.
* **It is safe to run on demand, and it is not free.** Each run makes a real token request and
  writes an audit event. Run it when an operator asks, not on a timer.
* **Target one host instance.** Credentials such as workload identity are per pod. Run diagnostics
  against each instance you care about, not once per cluster.

## 6. Hand over the app package

There is one package per agent, because an app manifest holds one bot. Take the names from
`agents` in the status document and fetch each agent's package from
`…/integrations/microsoft365/agents/{name}/manifest` and `…/agents/{name}/app-package`. The
host-wide `…/microsoft365/manifest` and `…/microsoft365/app-package` routes of earlier releases
are gone.

`GET …/integrations/microsoft365/agents/assistant/manifest` on the configured host, complete:

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

`GET …/integrations/microsoft365/agents/assistant/app-package` returned `200`, `application/zip`,
2,083 bytes: the manifest and two placeholder icons. Both routes send `Cache-Control: no-store, no-cache`, because
the package is regenerated from live options on every request.

While the channel is off, both routes return `409`, as captured:

```json
{
  "error": "channel-disabled",
  "message": "No Microsoft365Copilot configuration was found and no options were supplied in code."
}
```

The same status with `error: "manifest-unavailable"` is returned when the channel is on but the
agent has no client id to build a manifest from.

With the channel on, a name the host does not expose returns `404`:

```json
{
  "error": "agent-not-found",
  "message": "This host exposes no Microsoft 365 Copilot agent named 'nobody'."
}
```

Handling:

* **Over the connect channel the zip is the base64 `body` of the command response.** Decode it to
  bytes. Do not pass it through anything that treats the body as text.
* **Name the file yourself.** The host sends `Content-Disposition` with `appPackage.zip` for every
  agent, and the connect channel returns only `Content-Type`, `ETag`, `Location`, and
  `Retry-After`. Put the agent name in the file name so two downloads do not collide.
* **Fetch it after the restart, not before.** The package reflects the options the host is running
  with. A package fetched while a manifest change is pending restart describes the old manifest.
  Check `pendingRestart` on the `Microsoft365Copilot:Manifest:*` rows first.
* **Replace the placeholder developer details before a customer uploads it.** The developer name
  and the three URLs in the capture are the defaults. Publish `Microsoft365Copilot:Manifest:*`
  values for the customer; they apply to every agent, and
  `Microsoft365Copilot:Agents:{i}:Manifest:*` overrides them for one.
* **Icons.** The host embeds default icons in every package. `Manifest:ColorIconPath` and
  `OutlineIconPath`, at the top level or per agent, name files that must exist on the host. A
  console may instead replace `color.png` (192x192) and `outline.png` (32x32, transparent) in the
  zip it downloads before handing it over; the manifest refers to them by those names. Insights
  does this: it stores each agent's custom icons in its own database and swaps them into the zip
  when the package is downloaded through it.
* **The manifest is personal scope only.** The app cannot be added to a group chat or a team.
* **Do not link operators to `/m365copilot/{agent}/appPackage.zip`.** That developer endpoint is
  anonymous and off outside Development.

## 7. Status codes

| Code | From | Meaning | Do |
| --- | --- | --- | --- |
| `200` | Any route | Answered, including for a feature that is off | Read `enabled` and `status` |
| `400` | Integration routes | The request carried `X-FabrCore-Admin-Target`, `x-user`, or `x-user-handle` | Send none of them; these routes describe the host |
| `401` | Any route | Missing or wrong administration credential | Over the connect channel this should not happen; the host supplies its own key |
| `404` | `/integrations/microsoft365*` | The add-on is not installed on this host | Hide the feature for this host; check the `m365copilot` flag first |
| `404` | `/integrations/*` | The host predates the integrations API | Treat as "unknown"; show configured settings as unverified |
| `404` | `agents/{name}/manifest`, `agents/{name}/app-package` | `error: "agent-not-found"`: the channel is on and exposes no agent with that name | Refresh the status document; the list changed at the last restart |
| `409` | `agents/{name}/manifest`, `agents/{name}/app-package` | The channel is off, or the agent cannot describe itself | Show `message` |
| `408`, `413`, `502` | The connect channel | Deadline passed, body too large, or the host could not execute the request | See the protocol's connect channel section. None of the integration routes change host configuration, so a retry is safe. |

## 8. Notes for Insights

*13 October 2026: the table below predates the agent list. Insights now holds a list of agents per
integration, publishes `Microsoft365Copilot:Agents:{i}:*` with each secret at
`Agents:{i}:ClientSecret`, downloads packages per agent from `agents/{name}/app-package`, and
detects older hosts by the missing `m365copilot.agents` flag. Section 17 of the
[plan](cloud-managed-copilot-integrations-plan.md) records it.*

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

Two more things about Insights as it is:

* **The runtime settings catalog already publishes secrets.** It offers
  `Microsoft365Copilot:ClientSecret`, A2A API key values, and `FabrCore:ConnectionCredentials` as
  secret fields, delivered in the `settings` map inside the encrypted configuration document. A
  composer can write the client secret the same way and needs no new storage.
* **Connect command rows, including response bodies, are never deleted.** Every package download
  and status response stays in `insights.ConnectCommand`. The integration routes return no
  secrets, so this is a growth problem rather than an exposure, and worth a retention job before
  the feature is used routinely.

## Checklist

Before calling the integration healthy for a host:

- [ ] `m365copilot.enabled` is `true` in that host's heartbeat, and `m365copilot.agents` is the
      number of agents you published
- [ ] `microsoft365-copilot` is `available` in the capability document
- [ ] `GET integrations/microsoft365` has no `fail` finding, and `agents` lists every agent you
      published with the route its Azure Bot points at
- [ ] `principalStrategy` is `CanonicalEntra` on the channel and, if A2A is on, on A2A
- [ ] Each agent's `authType` is the one chosen for this customer, and no client secret is within
      30 days of expiry
- [ ] No integration row in the configuration-state report has `pendingRestart: true`
- [ ] `Runtime:Microsoft365Copilot:ForwardsUserCredential` is `False`
- [ ] `POST diagnostics` passes `agent-type-registered` and `bot-service-credential` on every
      instance
- [ ] An unauthenticated `POST` to each agent's public messaging URL answers `401` from outside
- [ ] Each agent's package was fetched after the last restart and its developer details are the
      customer's
