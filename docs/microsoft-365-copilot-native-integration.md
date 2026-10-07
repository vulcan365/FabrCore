# FabrCore as a native extension of Microsoft 365 Copilot

This is the integration guide for making a FabrCore deployment behave like a first-class part of a
customer's Microsoft 365 tenant: reachable from Copilot and Teams, callable by Copilot Studio,
able to call Work IQ and Copilot Studio back, and governed by the same identity, audit, and data
controls the tenant already applies to Microsoft's own agents.

It describes what ships today, what is missing, and the order to close the gaps. Every statement
about current behavior was checked against the code at the time of writing; source locations are
given so a reader can re-check. Statements about Microsoft services describe their public contracts
and must be re-validated against current Microsoft documentation and a real tenant before any of
the proposed work is built.

Companion documents:

| Document | Purpose |
| --- | --- |
| [Cloud-managed Copilot integrations plan](cloud-managed-copilot-integrations-plan.md) | How a cloud server (Insights or any other) provisions and manages this per customer |
| [Cloud server how-to](cloud-server-microsoft-365-copilot.md) | Wire-level guide for a cloud server team, with captured responses |
| [Connections and Microsoft integration](connections-and-microsoft-integration.md) | Connections, Entra Agent ID exchanges, remote agents |
| [A2A](a2a.md), [shared channel identity](channel-agent-identity.md) | The A2A endpoint and the identity it shares with the Copilot channel |
| [Principal message relays](principal-message-relays.md) | Proactive (out-of-turn) delivery |
| [Addon README](../src/FabrCore.Services.Microsoft365Copilot/README.md) | Quickstart and configuration reference for the Copilot/Teams channel |

## 1. Goal

"Native" means five things. A deployment is native when all five hold end to end, not when a bot
answers in Teams.

| Property | Meaning | Where FabrCore stands today |
| --- | --- | --- |
| **Identity** | The agent has its own Entra identity. Every user is a verified Entra user mapped to one stable FabrCore principal on every channel. Nothing trusts an identity string a caller typed. | User identity is solid on the two channels (`CanonicalEntra`). The agent's own identity is an ordinary app registration; Entra Agent ID is implemented for outbound token exchange only. The general HTTP API still trusts `x-user-handle` (finding F-02). |
| **Reach in** | Users and Microsoft orchestrators can call FabrCore agents from where they already work: Copilot, Teams 1:1 and group chats, Copilot Studio, Agent 365. | Copilot and Teams personal chat, and Copilot Studio over A2A, work. Group chats, Agent 365 notifications, MCP exposure, and file attachments do not. |
| **Reach out** | FabrCore agents can call Microsoft's agents and data as the user: Work IQ, Copilot Studio agents, Microsoft Graph. | Work IQ and Copilot Studio work through `remote-agent`; Graph works through Connections. The user must grant consent in a separate client application, because the channel's sign-in is not connected to Connections (backlog B1). |
| **Governance** | A tenant administrator can see, configure, enable, and disable the integration without redeploying, and can tell what is applied on each host. | Configuration is file-based and frozen at startup. A cloud server can publish the settings but cannot see whether the channel is on, healthy, or misconfigured. Addressed by the plan's Phase 1. |
| **Compliance** | Every turn is attributable, auditable, and subject to the tenant's data policy (Purview), with no credential leaving its protected store. | Administrative changes are audited. Conversation turns are not. No Purview integration exists. One opt-in setting moves a user token into message telemetry. See the control matrix in section 9. |

The single most important missing piece is **B1**: carrying the Teams single sign-on token into
Connections as an on-behalf-of assertion. Until that exists, "reach out as the user" depends on a
second consent experience that Teams and Copilot users have no way to reach.

## 2. Vocabulary

| FabrCore | Microsoft | Notes |
| --- | --- | --- |
| Host / cluster | The service behind an Azure Bot messaging endpoint; an A2A server | One Orleans cluster of one or more silos |
| Principal (`entra-{tenant}-{object}`) | Entra user (`tid` + `oid`) | `EntraPrincipalHandle.Create` in `FabrCore.Core`; applications map to `app-{tenant}-{object}` |
| Agent type (`[AgentAlias]`) | — | Code; a class deriving from `FabrCoreAgentProxy` |
| Agent instance (`principal:handle`) | — | One per user by default; holds that user's threads and state |
| Agent binding (`AgentBindings:{name}`) | A published agent: one Teams app, one A2A card | Shared provisioning template for both channels |
| Copilot/Teams channel | Custom engine agent on the Activity Protocol | `FabrCore.Services.Microsoft365Copilot`, `POST /api/messages` |
| A2A endpoint | Copilot Studio "connected agent" over A2A 1.0 | Built into `FabrCore.Host`, `/a2a/{agent}` |
| Connection profile | App registration + delegated or application permission grant | `(ownerPrincipal, name)`; metadata only, never secrets |
| Credential reference | Client secret, certificate, or federated credential | Resolved on the host by `IConnectionCredentialProvider` |
| `AgentIdApplication` / `AgentIdOnBehalfOf` | Entra Agent ID: blueprint (parent) and agent identity (child) | Token exchange only; no directory provisioning |
| Remote agent (`remote-agent`) | Work IQ A2A agent; Copilot Studio agent | A FabrCore handle that proxies a remote conversation |
| Principal message relay | Proactive message | Durable outbox; Microsoft 365 is one relay provider |
| Cloud server | — | Insights is Vulcan365's implementation; the protocol is vendor-neutral |
| Audit event (`IAuditProvider`) | Purview Audit / unified audit log | Not connected today |

## 3. What already ships

All four features are optional and independent. Source paths are relative to `src/`.

### 3.1 Copilot/Teams channel

`FabrCore.Services.Microsoft365Copilot` hosts the Azure Bot Service messaging endpoint using the
Microsoft 365 Agents SDK (1.8.77) and bridges each conversation to a FabrCore agent.

| Capability | Detail | Source |
| --- | --- | --- |
| Inbound token validation | Dedicated JWT scheme `Microsoft365CopilotBearer`; audience is the bot's client id; issuers are Bot Framework plus the configured tenant | `Authentication/CopilotChannelAuthenticationExtensions.cs` |
| Principal mapping | `EntraObjectId` (default), `TenantAndObjectId`, `UserPrincipalName`, `ChannelUserId`, `CanonicalEntra` | `Bridge/DefaultCopilotPrincipalResolver.cs` |
| Outbound auth types | `ClientSecret`, `Certificate`, `CertificateSubjectName`, `UserManagedIdentity`, `SystemManagedIdentity`, `FederatedCredentials`, `WorkloadIdentity` | `Configuration/AgentsSdkConfigurationBridge.cs` |
| Per-user or shared agent | Per-user provisioning from `Agent:*` or a named binding; or one `SharedAgentHandle` | `Bridge/CopilotAgentProvisioner.cs` |
| Streaming | Informative update plus the completed reply; AI-generated label; optional feedback buttons | `Bridge/FabrCoreCopilotAgent.cs` |
| Adaptive Cards | `ui.render` out, `Action.Submit` in as `ui.action` | `Bridge/CopilotActivityMapper.cs` |
| Proactive delivery | Opt-in relay through stored conversation endpoints; personal scope by default | `Bridge/CopilotPrincipalMessageRelay.cs` |
| Single sign-on | Agents SDK user-authorization handlers, forwarded verbatim; manifest gains `webApplicationInfo` | `Configuration/AgentsSdkConfigurationBridge.cs`, `Packaging/CopilotAppPackageBuilder.cs` |
| App package | Manifest 1.25 with `copilotAgents.customEngineAgents`, generated from configuration | `Packaging/CopilotAppPackageBuilder.cs` |

### 3.2 A2A for Copilot Studio

Built into `FabrCore.Host` and off until `A2A:Enabled` is true. It implements A2A 1.0 (JSON-RPC and
HTTP+JSON bindings, streaming, tasks) and accepts Copilot Studio's JSON-RPC-on-REST-route shape.

| Capability | Detail | Source |
| --- | --- | --- |
| Authentication | `None`, `ApiKey` (default), `JwtBearer` with `RequiredScopes` and `RequiredRoles` | `FabrCore.Host/A2A/A2AHostIntegration.cs` |
| Principal mapping | `Fixed` (default), `ContextId`, `ApiKey`, `Claim`, `CanonicalEntra` | `FabrCore.Host/A2A/Security/A2APrincipalResolver.cs` |
| Publication | Explicit agents, agent types, handles, registry discovery, live-agent discovery | `FabrCore.Host/A2A/Agents/A2AAgentCatalog.cs` |
| Task ownership | Every read, cancel, subscribe, and list checks the creating principal and agent | `FabrCore.Host/A2A/Execution/A2ATaskOwnership.cs` |
| Task store | In memory by default; a SQL store is registered when the integrated database is enabled | `FabrCore.Host/Database/FabrCoreDatabaseRegistration.cs` |

`CanonicalEntra` is validated at startup to require `JwtBearer` and an audience, so a
misconfiguration fails fast rather than mapping every caller to one principal.

### 3.3 Connections

`FabrCore.Services.Connections` brokers access tokens for agents. Tokens and grants are encrypted
with Data Protection in principal-scoped storage; profiles carry metadata and a credential
reference, never a secret value.

| Mode | Grant used | User involvement |
| --- | --- | --- |
| `ClientCredentials` | `client_credentials` | None; application permissions |
| `AuthorizationCode` | Authorization code with PKCE, then `refresh_token` | Consent in an external client application |
| `OnBehalfOf` | `jwt-bearer` with `requested_token_use=on_behalf_of` | A current user assertion submitted by a client |
| `AgentIdApplication` | Blueprint token with `fmi_path`, used as the agent identity's client assertion, then `client_credentials` | None |
| `AgentIdOnBehalfOf` | The same parent/child exchange, then on-behalf-of with the user assertion | A current user assertion targeting the blueprint API |

The exchanges are in `FabrCore.Services.Connections/OAuthProvider.cs` (`Acquire`). Delegated
profiles can only be used by agents owned by the connection's owner; application profiles list the
exact agent handles allowed (`ConnectionGrain.Authorize`).

The default credential provider resolves a reference to a client secret or a PKCS#12 signing
certificate from `FabrCore:ConnectionCredentials:{reference}`. It has no managed-identity or
federated-credential support; that requires a custom `IConnectionCredentialProvider` today.

### 3.4 Remote agents

`FabrCore.Services.RemoteAgents` registers one agent type, `remote-agent`, with two providers
(`RemoteAgent.cs`):

* `work-iq` speaks A2A 1.0 to Work IQ, with streaming, task status, resume, and cancel.
* `copilot-studio` uses `Microsoft.Agents.CopilotStudio.Client` against a published agent's
  direct-connect URL.

Both obtain their transport from `Connections.GetHttpClientAsync`, so every remote call carries a
token the connection profile authorized. A caller in a different principal is refused.

## 4. Target architecture

Four planes. A request crosses them in order on the way in and in reverse on the way out. The
compliance plane is the only place content and identity are inspected, so a new channel or a new
outbound provider inherits the controls instead of reimplementing them.

```text
        Microsoft 365 Copilot / Teams      Copilot Studio      Agent 365        MCP clients
                    |                            |                 |                 |
   CHANNEL PLANE    v                            v                 v                 v
              /api/messages                 /a2a/{agent}     notifications        /mcp         (planned ->)
              Activity Protocol              A2A 1.0           (B7)               (B8)
                    |                            |                 |                 |
                    +----------------+-----------+-----------------+-----------------+
                                     | authenticated caller, resolved principal, named binding
   COMPLIANCE PLANE                  v
              identity assertion capture (B1)   turn audit (B2)   Purview gate (B9)   redaction (B4)
                                     |
   AGENT PLANE                       v
              principal grain -> agent grain (per user, per binding) -> ACL, state, monitor
                                     |
   OUTBOUND PLANE                    v
              Connections token broker (OBO, Agent ID)  ->  remote-agent, MCP, plugin HTTP
                    |                         |                        |
                    v                         v                        v
                 Work IQ               Copilot Studio            Microsoft Graph
```

| Plane | Owns | Status |
| --- | --- | --- |
| **Channel** | Protocol termination, caller authentication, principal resolution, binding selection, wire translation | Two adapters ship; two are planned |
| **Compliance** | Capturing the user's assertion, recording the turn, applying data policy to prompts and responses, keeping credentials out of telemetry | Does not exist as a plane; its pieces are scattered or absent |
| **Agent** | Durable per-user agents, ACL at the grain boundary, message monitor, verifiable execution | Ships |
| **Outbound** | Token acquisition bound to the calling principal and agent, remote conversations, authenticated MCP | Ships, minus a generic A2A provider |

Two rules keep the planes honest:

1. A channel adapter never hands a token to an agent. It hands the assertion to the compliance
   plane, which stores it with Connections. Agents ask Connections for a resource by alias.
2. The compliance plane runs on outbound calls as well. A response from Work IQ is content entering
   the tenant's agent, the same as a prompt from Teams.

## 5. Entra Agent ID mapping

Entra Agent ID gives agents their own directory objects: an **agent identity blueprint** (the
parent, which holds the credential), **agent identities** created from it (the children, which hold
permissions), and optionally an **agent user** for agents that need a user-shaped account.

| Entra object | FabrCore concept | Cardinality | Reason |
| --- | --- | --- | --- |
| Agent identity blueprint | The deployment's integration identity | One per deployment | It is the unit that holds a credential and that an administrator approves. One cluster, one trust decision. |
| Agent identity | A published agent binding (`AgentBindings:{name}`) | One per published binding | A binding is what users and administrators see as "an agent". Permissions, access reviews, and conditional access attach here. |
| Agent user | A digital-worker binding only | Zero or one per digital-worker binding | Needed only when the agent itself must own a mailbox, hold a license, or appear as a participant. An assistant acting for a person does not need one. |
| Entra user | FabrCore principal `entra-{tenant}-{object}` | One per person | Already how the channels map users. |

**No per-user agent identities.** FabrCore creates an agent *instance* per user, and the number of
instances grows with the user count. Creating a directory object for each would multiply the
objects an administrator must govern without adding any control: the user is already represented in
every delegated token through on-behalf-of. All of one binding's instances share that binding's
agent identity.

How this maps to the code today:

* `ConnectionProfile.ClientId` and `CredentialReference` identify the blueprint and its credential.
* `ConnectionProfile.AgentIdentityClientId` identifies the agent identity.
* `ConnectionProfile.BlueprintAudience` is the audience a user assertion must target in
  `AgentIdOnBehalfOf`.
* `ConnectionsOptions.EntraAgentIdEnabled` gates the feature. It is host bootstrap configuration;
  submitting a profile cannot turn it on.

What is not done:

* The Copilot channel and the A2A audience still use an ordinary app registration. Moving the
  channel's own identity to an agent identity is backlog B10.
* Nothing creates blueprints, agent identities, or agent users. That is directory provisioning and
  belongs to a cloud server (plan item I9) or to the customer's administrator.
* Whether a given Microsoft API accepts an agent identity token must be validated per API in the
  target tenant. Supporting the exchange does not imply it.

## 6. Inbound gaps

### 6.1 Teams group chats and channels

The generated manifest declares personal scope only: `CopilotAppPackageBuilder.BuildBotEntry` writes
`"scopes": ["personal"]` for the bot and for its command list. The app cannot be added to a group
chat or a team.

Changing the scope list is the small part. The design questions are:

* **Whose agent answers?** Today every turn resolves the sender's principal and that principal's
  agent instance. In a group chat, five people would address five different agents with five
  different memories under one bot name. A group conversation needs an explicit choice: a shared
  agent for the conversation, or per-sender agents with replies that make that visible.
* **What may be said aloud?** A per-user agent can read that user's mail. Its answer in a group
  chat is visible to people who cannot. Delegated tools must be disabled, or their results
  withheld, outside personal scope.
* **Proactive delivery** already restricts endpoint capture to `Proactive:AllowedConversationTypes`
  (default `personal`), which is the right default to keep.

### 6.2 Agent 365 notifications

Agent 365 delivers events that are not chat turns: an agent is mentioned in a document comment,
receives mail, or is assigned work. Nothing in FabrCore subscribes to or handles them. The natural
mapping is a notification to an agent **event** (`OnEvent`) on the binding's agent, carrying the
resource reference rather than the content, with the content fetched through Connections under the
compliance plane.

### 6.3 MCP exposure

FabrCore agents consume MCP servers (`McpServerConfig`, with Connections-backed authentication) but
the host exposes no MCP endpoint. Copilot Studio, declarative agents, and Agent 365 tooling can all
consume MCP servers, so exposing selected plugin tools, or an "ask this agent" tool per binding,
would reach clients that do not speak A2A. It must reuse the A2A authentication and principal
strategy rather than introduce a third.

### 6.4 File attachments

`supportsFiles` is `false` in the manifest, and an attachment-only message receives a fixed reply
saying attachments cannot be read (`FabrCoreCopilotAgent.OnMessageActivityAsync`). A2A file parts
are passed to the agent as references and never fetched.

Supporting files means fetching content the user can access, as the user, into FabrCore file
storage with a short lifetime, after the compliance plane has seen it. That depends on B1 for the
delegated token and on B9 for the policy check, so it follows them.

## 7. Outbound

Outbound calls to Microsoft agents go through one agent type, `remote-agent`, selected by its
`provider` argument:

| Provider | Protocol | Resource scope |
| --- | --- | --- |
| `work-iq` | A2A 1.0 over JSON-RPC, streaming by default | `api://workiq.svc.cloud.microsoft/WorkIQAgent.Ask` (delegated) |
| `copilot-studio` | Copilot Studio client SDK, direct-connect URL | `https://api.powerplatform.com/CopilotStudio.Copilots.Invoke` (delegated) |

**There is no generic A2A provider.** Any other `provider` value is rejected with "Unknown remote
agent provider". The Work IQ client is an A2A 1.0 client, but it is written to Work IQ's envelope
(a `Location` metadata block, its task states) and is not offered as a general one. A FabrCore
agent cannot call an arbitrary third-party A2A agent, or another FabrCore cluster's A2A endpoint,
without new code (backlog B12).

Properties worth relying on:

* The remote conversation lives behind an ordinary handle, so local agents address it like any
  other agent, and its context survives failover when storage is durable.
* The persisted context is bound to provider, endpoint, connection, resource, and the connection's
  session version. Reauthorizing or changing any of them starts a fresh remote conversation.
* A timeout is never retried automatically. A known task can be inspected, resumed, or cancelled.

## 8. Bi-directional flows

Each flow lists the identity on every hop, because that is where these integrations fail.

**Flow A: a Copilot user asks, and the agent consults Work IQ.**

```text
user in Copilot -> /api/messages -> principal entra-T-U -> agent entra-T-U:assistant
    -> remote agent entra-T-U:workiq -> Connections (OBO as U) -> Work IQ -> back
```

Works today only after user U has connected the `workiq` profile through some other client
application. With B1, the channel's own sign-in supplies the assertion and the user sees no second
prompt.

**Flow B: Copilot Studio delegates to a FabrCore agent.**

```text
user in a Copilot Studio agent -> A2A SendMessage (delegated token for U, scope agent.invoke)
    -> principal entra-T-U -> the same agent instance as flow A
```

Works today when the Copilot Studio connection sends a delegated token. With an API key or client
credentials the caller is an application, the principal is `app-…` or a fixed handle, and the user's
agent and memory are not reached. That is correct behavior, not a defect: an application credential
does not prove which person is typing.

**Flow C: a FabrCore agent delegates to a Copilot Studio agent.**

```text
any FabrCore client -> agent -> remote agent (copilot-studio) -> Connections -> Copilot Studio
```

Works today, with the same consent dependency as flow A.

**Flow D: an agent starts the conversation.**

```text
background work -> SendToUserAsync -> durable outbox -> Microsoft 365 relay -> Teams personal chat
user replies -> /api/messages -> the same agent
```

Works today when `Proactive:Enabled` is set and the user has spoken to the app at least once.

**Flow E: round trip.** Copilot Studio calls a FabrCore agent (B), which calls Work IQ (A), which
may itself call other agents. Two hazards are specific to round trips:

* **Loops.** Nothing bounds a chain that leaves FabrCore and returns through A2A. Cross-principal
  hops are counted inside a cluster (`CrossPrincipalHops`); the count does not survive leaving and
  re-entering. A hop counter must travel in A2A message metadata and be enforced on entry.
* **Identity continuity.** The chain is only as strong as its weakest hop. One application-only
  hop turns the remainder into application access. Each hop must either carry a delegated token
  for the same user or be explicitly an application call.

## 9. Compliance control matrix

Status values: **Met**, **Partial**, **Gap**. Findings marked ⚠ are defects in what ships today, not
missing features.

| Control | Copilot channel | A2A | Connections | Remote agents | Status and reference |
| --- | --- | --- | --- | --- | --- |
| Caller authentication | Bot Service JWT, validated | API key or JWT; `None` is permitted | Host authentication for user routes; admin key for admin routes | Inherits Connections | **Met** when token validation is on and A2A is not `None` |
| User to principal | `From.AadObjectId` on a validated activity | `tid` + `oid` from a validated token | Entra claims, or a custom resolver | Caller must equal owner | **Met** with `CanonicalEntra`; the default strategies differ per channel |
| Agent identity | App registration | App registration as audience | Agent ID exchange implemented | Inherits Connections | **Partial** (B10) |
| Authorization | ACL at the grain boundary | ACL, plus task ownership | Owner match and explicit agent grants | Owner-only | **Met** |
| ⚠ Audit of conversation turns | None | None | Non-GET admin calls are audited; user calls are recorded as ACL decisions, failures only by default | None | **Gap.** No channel, A2A, or remote-agent code calls `IAuditProvider`. A same-principal turn is an implicit ACL allow, and ACL decisions are recorded for failures only by default, so an ordinary turn leaves no audit record at all (B2). |
| Audit of administration | — | — | `RemoteAdministration` category | — | **Met** for admin routes; in memory unless the integrated database is on |
| ⚠ HTTP API caller identity | — | — | — | — | **Gap.** Security review finding F-02 is still open: `AgentController`, `BlueprintController`, `StorageController`, `MonitorController`, and others read the caller from `x-user-handle` with no authentication. Anything that can reach `/fabrcoreapi/agent/chat/{handle}` can speak to a user's agent as that user, bypassing both channels' authentication (B3). |
| ⚠ Credential containment | User token can enter messaging | — | Encrypted at rest; never serialized | Token stays in the handler | **Gap** when `UserAuthorization:PassUserTokenToAgent` is true: the token is written to `AgentMessage.Args`, and `AgentGrain` copies `Args` unredacted into every monitor record (`MonitoredMessage.Args`), which the monitor API serves and the SQL monitor, when enabled, persists (B4). |
| Secrets in configuration | Client secret supported | API key values in configuration | References only | — | **Partial.** Secret-free auth types exist for the channel; Connections needs a certificate or custom provider (B5). Cloud-delivered settings are cached in plaintext in `fabrcore.cloud-cache.json`. |
| Data policy (DLP, sensitivity) | None | None | — | None | **Gap** (B9) |
| Durable evidence | Monitor and verifiable execution | Same | — | Same | **Partial.** Telemetry, not an audit trail; in memory by default |
| Tenant isolation | Tenant is part of the principal | Same | Authority is per profile | — | **Met** with `CanonicalEntra`. `EntraObjectId` alone omits the tenant and is unsafe for a multi-tenant bot. |
| Turn-state durability | Agents SDK `MemoryStorage` by default | SQL store when the database is on | Orleans storage | Agent state | **Partial** (B13) |

Three findings need action before a compliance claim can be made:

1. **Channel turns produce no audit events.** A reviewer asking "who asked this agent what, and
   when" has only monitor telemetry, which is in memory by default and was never designed as an
   audit trail.
2. **The HTTP APIs still trust `x-user-handle`.** The channels authenticate carefully and then
   share a process with an unauthenticated route to the same agents. Until F-02 is fixed, the host
   port must not be reachable from anywhere but a trusted proxy.
3. **`PassUserTokenToAgent` puts a bearer token in telemetry.** The README warns that the token
   "flows through FabrCore messaging and any configured monitors"; the consequence is that a user's
   Graph token is readable by anyone with monitor access and, with the SQL monitor enabled, is
   stored for the retention period. Leave it off. B1 removes the reason it exists.

## 10. Purview gate design

The gate is the part of the compliance plane that applies the tenant's Microsoft Purview policy to
content. It uses three Microsoft Graph operations under `dataSecurityAndGovernance`:

| Operation | Purpose | When |
| --- | --- | --- |
| `protectionScopes/compute` | Which activities, for this user and this application, are covered by policy, and whether evaluation must be inline | Once per user, cached |
| `processContent` | Submit a prompt or a response for evaluation; returns actions such as restricting access | When the scopes say the activity is covered |
| `activities/contentActivities` | Record that an interaction happened, for audit and analytics, without evaluation | When the scopes say no policy requires evaluation |

### 10.1 Placement

One interface in `FabrCore.Core`, called by the compliance plane and implemented by an optional
package so hosts without Purview carry no dependency:

```csharp
public interface IContentGovernanceGate
{
    ValueTask<ContentGovernanceDecision> EvaluateAsync(
        ContentGovernanceRequest request, CancellationToken cancellationToken);
}
```

It is called at four points: a prompt arriving on a channel, a response leaving on a channel, a
request leaving for a remote agent, and a response returning from one. The request carries the
principal, the binding, the direction, the conversation and message identifiers, and the text. It
never carries a token.

### 10.2 Flow

```text
1. scopes = cache[user] or protectionScopes/compute(user, app, activities)
2. activity not covered            -> contentActivities (asynchronous, best effort), allow
3. covered, evaluate offline       -> processContent (asynchronous), allow
4. covered, evaluate inline        -> processContent (awaited)
       action restricts access     -> block: replace the content with a policy message, audit
       no action                   -> allow
5. response says scopes changed    -> drop cache[user]; recompute on the next call
```

### 10.3 Decisions that must be made deliberately

* **Identity.** The calls are made for a user. Delegated permission through an on-behalf-of
  connection is preferable, which makes B1 a prerequisite. Application permission works without it
  but needs tenant-wide consent to process every user's content.
* **Application identity.** The policy location is the Entra application the content is attributed
  to: the integration app today, the binding's agent identity after B10.
* **Failure mode.** Configurable per deployment: fail closed (no answer when Purview is
  unreachable) or fail open with an audit event. Fail open must be an explicit choice.
* **Latency.** Inline evaluation is on the user's critical path twice per turn. Cache scopes; run
  the response check concurrently with stream preparation; never evaluate when the scopes say it is
  not required.
* **Streaming.** A response cannot be evaluated until it is complete. FabrCore already delivers the
  reply as one unit on both channels, so the gate fits without buffering changes. If incremental
  streaming is added later (B16), this constraint returns.
* **What is recorded.** The decision, the policy action, and the identifiers go to the audit trail
  (B2). The content does not.

Until the gate exists, a deployment can state only that content does not leave the tenant's
approved model endpoint. It cannot state that Purview policy is enforced on agent interactions.

## 11. Production how-to

This is the procedure for one customer tenant with what ships today. Steps that the plan automates
are marked.

**1. Create the integration app registration** in the customer's tenant, single tenant. Multi-tenant
Azure Bot registrations can no longer be created (retired after 31 July 2025), so there is one
registration per customer tenant.

* Expose an API with Application ID URI `api://botid-{appId}`.
* Add a delegated scope for Teams single sign-on and a scope named `agent.invoke` for A2A callers.
* Pre-authorize the Teams, Microsoft 365, and Outlook client applications for the sign-on scope.
* Add the delegated permissions the agents need (for example `WorkIQAgent.Ask`) and grant consent.

**2. Give the registration a credential that is not a secret.** Add a federated identity credential
that trusts the host's workload identity, and configure the channel with
`AuthType: "WorkloadIdentity"` or `"FederatedCredentials"`. Use a certificate where federation is
not available. Do not use a client secret in production.

**3. Create the Azure Bot resource**, single tenant, with the messaging endpoint
`https://{public-host}/api/messages`, and enable the Teams channel. For single sign-on, add an OAuth
connection setting with the token exchange URL `api://botid-{appId}`.

**4. Configure the host.** One identity strategy and one binding for both channels:

```json
{
  "AgentBindings": {
    "assistant": { "AgentType": "my-agent", "Handle": "assistant", "Models": "default" }
  },
  "Microsoft365Copilot": {
    "TenantId": "<tenant>",
    "ClientId": "<appId>",
    "AuthType": "WorkloadIdentity",
    "Principal": { "Strategy": "CanonicalEntra" },
    "Agent": { "Binding": "assistant" },
    "Manifest": { "Name": "Contoso Assistant", "PublicHostName": "agents.contoso.com" }
  },
  "A2A": {
    "Enabled": true,
    "PublicBaseUrl": "https://agents.contoso.com",
    "Principal": { "Strategy": "CanonicalEntra" },
    "Agents": [ { "Name": "assistant", "Binding": "assistant" } ],
    "Authentication": {
      "Mode": "JwtBearer",
      "JwtBearer": {
        "Authority": "https://login.microsoftonline.com/<tenant>/v2.0",
        "Audience": "<appId>",
        "RequiredScopes": [ "agent.invoke" ]
      }
    }
  }
}
```

**5. Make state durable.** Enable the integrated SQL database (durable audit, A2A task store,
monitor). Register a durable Agents SDK `IStorage` before `AddMicrosoft365Copilot()`; the default
is in memory and loses sign-in state on restart. Configure Data Protection with a certificate so
Connections can encrypt grants.

**6. Enable Connections and remote agents** in `Program.cs`, then create the connection profiles
through the administration API. For Work IQ, an `OnBehalfOf` or `AuthorizationCode` profile with the
resource scope from section 7. Deploy the remote agent with a `connectedAgents` blueprint.

**7. Publish the app.** Download the generated package and upload it in the Teams admin center or
the Microsoft 365 admin center. The package endpoints are served only in Development unless
`Manifest:EnableAppPackageEndpoint` is set, and they are anonymous; in production fetch the package
through the administration API once Phase 1 is in place.

**8. Connect Copilot Studio.** Add the agent as an A2A agent with the endpoint
`https://{public-host}/a2a/assistant/message:stream`, OAuth 2.0 authentication, and turn on
generative orchestration. See [a2a.md](a2a.md) for the full procedure.

**9. Close the open findings for this deployment.**

* Do not expose the host port. Route only `/api/messages`, `/a2a/*`, and the well-known card paths
  through the public proxy. This is the mitigation for F-02.
* Leave `UserAuthorization:PassUserTokenToAgent` off.
* Keep `TokenValidation:Enabled` true and never set `A2A:Authentication:Mode` to `None`.
* Treat `fabrcore.cloud-cache.json` as a secret file if a cloud server delivers settings.

**10. Verify.** Chat in Copilot; call the A2A card; confirm that both arrive at the same
`entra-…:assistant` agent. Read `/fabrcoreapi/admin/v1/access/audit` and confirm administrative
changes appear. Conversation turns will not appear until B2.

## 12. Backlog

Ordered by dependency, not by size. "Plan" names the matching item in the
[cloud-managed plan](cloud-managed-copilot-integrations-plan.md).

| Id | Item | Why | Depends on | Plan |
| --- | --- | --- | --- | --- |
| **B1** | **Bridge the Teams single sign-on token to the Connections on-behalf-of assertion** | The key missing link. Without it, reach-out needs a consent experience Teams users cannot reach. | — | — |
| B2 | Audit every channel, A2A, and remote-agent turn | Finding 1. A `ChannelInvocation` audit category with principal, binding, channel, conversation, and outcome; never content. | — | — |
| B3 | Authenticated caller context for the HTTP APIs | Finding 2 (F-02). | — | F9 |
| B4 | Remove credentials from messaging and redact credential-bearing args in the monitor | Finding 3. Deprecate `PassUserTokenToAgent` once B1 ships. | B1 | — |
| B5 | Workload-identity credential provider for Connections | Removes the last reason to store a certificate or secret for outbound calls. | — | F5 |
| B6 | Capability advertisement, configuration-bound enablement, applied-settings reporting, integrations admin API | Lets a cloud server see and manage the integration. | — | F1–F4 |
| B7 | Agent 365 notifications to agent events | Reach in. | B2 | — |
| B8 | MCP exposure of bindings and selected tools | Reach in for clients that do not speak A2A. | B3 | — |
| B9 | Purview gate | Compliance. | B1, B2 | — |
| B10 | Agent identity for the channel and the A2A audience | The agent becomes a governed directory object. | B5 | I9 |
| B11 | Connection profiles delivered in the cloud envelope | Removes per-host API calls from provisioning. | B5 | F6 |
| B12 | Generic A2A provider for `remote-agent` | Reach out to non-Microsoft agents and other FabrCore clusters, with a hop limit. | — | — |
| B13 | Durable Agents SDK turn-state storage shipped in the box | Sign-in state survives restart and scale-out without custom code. | — | — |
| B14 | Teams group chats and channels | Reach in. Needs the shared-versus-per-sender decision first. | B2, B9 | — |
| B15 | File attachments | Reach in. | B1, B9 | — |
| B16 | Incremental streaming relay | Experience. Reintroduces the response-evaluation constraint in the Purview gate. | B9 | — |
| B17 | Live reload of non-identity channel options | Change a welcome message or a starter without a restart. | B6 | F7 |
| B18 | Several bots per host | One cluster serving several published agents with separate Teams apps. | B10 | F8 |
| B19 | Audit export to the tenant: Purview Audit or a SIEM | The customer's reviewers read their own tools, not the FabrCore API. | B2 | — |

### B1 in detail

What exists on each side:

* The channel obtains a user token with `UserAuthorization.GetTurnTokenAsync`
  (`FabrCoreCopilotAgent.TryGetUserTokenAsync`) when a handler is configured. Its only uses are
  reading claims for the `UserPrincipalName` strategy and, optionally, `PassUserTokenToAgent`.
* Connections accepts a user assertion through the `assertion` operation
  (`ConnectionGrain.ExecuteCore`). It requires an `OnBehalfOf` or `AgentIdOnBehalfOf` profile; a
  signed token whose audience is `BlueprintAudience` or, when that is unset, `ClientId`; `tid`,
  `oid`, and `scp` claims; and an owner principal equal to `entra-{tid}-{oid}`.

What connects them:

1. The integration app is both the bot and the connection's client. The Teams sign-on token's
   audience is that app, so it is a valid on-behalf-of assertion for a profile whose `ClientId` is
   the same app.
2. `CanonicalEntra` with an empty prefix is required, because the connection owner must be exactly
   `entra-{tid}-{oid}`.
3. The sign-on handler must be configured **without** `OBOConnectionName` and `OBOScopes`. With
   them, the Agents SDK performs its own exchange and returns the downstream token, which is not an
   assertion for the integration app.
4. The addon gains an extension point, for example `ICopilotUserAssertionSink`, called once per
   turn with the principal and the token. It must not reference the Connections package. A small
   bridge registered by the host forwards to the `assertion` operation for a configured list of
   connection names.
5. The token is never placed on the message. `PassUserTokenToAgent` becomes unnecessary.

Open questions to settle in a real tenant: the exact audience form on the sign-on token (application
id or the `api://botid-…` URI) and whether both must be accepted; token lifetime against
long-running turns; and whether Copilot, as opposed to Teams, always supplies the sign-on token
silently.

## 13. Developer-experience improvements

These do not change what is possible. They change how long it takes to find out why it is not
working.

* **Start without configuration.** Calling `AddMicrosoft365Copilot()` on a host with no
  `Microsoft365Copilot` section throws at startup today. It should start with the channel off and
  say why, so a single host image can be enabled later by configuration alone (plan F2).
* **Configuration-bound enablement for Connections and remote agents.** Both require a code
  delegate today, so they cannot be turned on by a cloud server or an environment variable
  (plan F2).
* **One diagnostic call.** `POST …/integrations/microsoft365/diagnostics` that checks the
  configuration, whether the agent type is registered, and whether the host can obtain a Bot
  Service token, and reports each as a named pass or fail (plan F4). This replaces reading the
  troubleshooting list in the README.
* **Say what is applied.** Report the channel's effective tenant, client id, auth type, endpoint,
  and identity strategy through the configuration-state report, so "is it on, and as whom" has an
  answer that does not involve a shell on the host (plan F3).
* **Authenticated package download.** The package endpoints are anonymous and development-only. An
  administration route makes the package available in production to the people who need it
  (plan F4).
* **A test host for the channel.** `FabrCore.Host.Testing` stands up A2A without a silo. The
  Copilot channel has no equivalent; tests build the adapter by hand.
* **One sample for both directions.** A sample that enables the channel, A2A, Connections, and a
  Work IQ remote agent with one binding and `CanonicalEntra`, with the Azure CLI script that creates
  the registration.
* **Align the defaults.** The channel defaults to `EntraObjectId` and A2A to `Fixed`. A host that
  enables both gets two different principals for the same person unless it reads
  [channel-agent-identity.md](channel-agent-identity.md) first. A startup warning when both
  channels are on and their strategies differ would cost nothing.
* **Name the handler mistake.** When B1 ships, detect a sign-on handler configured with
  `OBOConnectionName` and explain why the assertion bridge cannot use it.

## References

* [Custom engine agents](https://learn.microsoft.com/en-us/microsoft-365/copilot/extensibility/overview-custom-engine-agent)
* [Microsoft 365 Agents SDK](https://learn.microsoft.com/en-us/microsoft-365/agents-sdk/)
* [Copilot Studio A2A connections](https://learn.microsoft.com/en-us/microsoft-copilot-studio/add-agent-agent-to-agent)
* [Work IQ A2A](https://learn.microsoft.com/en-us/microsoft-365/copilot/extensibility/work-iq/a2a/overview)
* [Entra Agent ID](https://learn.microsoft.com/en-us/entra/agent-id/)
* [Security review, 2026-07-07](security-review-2026-07-07.md)
