---
name: fabrcore-microsoft365copilot
description: "Integrate FabrCore 2.0 with Microsoft 365 Copilot and Teams using the separate Copilot package: channel hosting, agent provisioning, authentication, principal resolution, streaming, attachments, cards, Entra SSO and proactive delivery. Use fabrcore-a2a for Copilot Studio A2A connected agents."
allowed-tools: "Bash(dotnet:*) Bash(mkdir:*) Bash(ls:*) Bash(pwsh:*) Bash(powershell:*) Bash(git:*) Bash(dir:*) Bash(az:*) Bash(curl:*) Bash(devtunnel:*)"
metadata:
  version: 2.0.0
---

# FabrCore ⇄ Microsoft 365 Copilot (Custom Engine Agent)

For the inverse direction—FabrCore calling a Copilot Studio or Work IQ agent—use
[fabrcore-connections](../fabrcore-connections/SKILL.md). It covers remote handles,
shared authenticated connections, external consent, and optional Entra Agent ID.
This channel addon does not enable those features or provide their downstream
permissions. Existing channel authentication and outbound user consent are separate.

## FabrCore 2.0 baseline

Use FabrCore.Services.Microsoft365Copilot 2.0.0 with matching Host/SDK packages. Channel authentication and principal mapping still apply in standalone mode, while cross-principal ACL enforcement requires SQL mode. Proactive delivery state survives process restart only when the configured Orleans provider persists it; default standalone state is in memory.

`FabrCore.Services.Microsoft365Copilot` is a server addon that exposes a FabrCore.Host's agents
to Microsoft 365 Copilot, Teams, and other Azure Bot Service channels. It hosts one messaging
endpoint per exposed agent (`/api/messages` by convention) with the Microsoft 365 Agents SDK,
validates channel + Entra identity, maps each Microsoft 365 user to a FabrCore principal, ensures
their agent exists, and streams replies back. No changes to FabrCore.Host or the agents are
required.

```
Microsoft 365 Copilot / Teams
        │  Activity (JWT from Azure Bot Service)
        ▼
POST /api/messages  ──►  FabrCoreCopilotAgent (Agents SDK bridge, in the addon)
        │                    │ Entra user ──► FabrCore principal handle
        │                    │ ensure agent ──► IFabrCoreAgentService.EnsureAgentsAsync
        │                    ▼
        ◄── streamed reply ── your [AgentAlias] agent on the Orleans silo
```

## This addon or fabrcore-a2a?

Both connect FabrCore to Microsoft, and picking the wrong one costs a day. They are not
alternatives — many hosts run both.

| | This addon | A2A, built into `FabrCore.Host` (**fabrcore-a2a**) |
|---|---|---|
| Who calls | A **person**, chatting in Microsoft 365 Copilot or Teams | **Another agent** — Copilot Studio's orchestrator, or any A2A client |
| Protocol | Azure Bot Service Activity Protocol | Open Agent2Agent (A2A) |
| Endpoint | One route per agent: `/api/messages`, `/api/messages/{name}` | `/a2a/{agent}` plus agent cards on `/.well-known/*` |
| Identity | Each Microsoft 365 user, via Entra and Azure Bot Service tokens | The calling agent or tenant, via API key or OAuth 2.0 |
| Setup | Package reference, Azure Bot registration, Entra app, app package upload | Built into `FabrCore.Host`: an HTTPS route, a credential, and `A2A:Enabled` |
| Discovery | The app package manifest | The agent card's description and skills |

Rule of thumb: **"my users want to chat with this agent in Copilot"** → this addon.
**"Copilot Studio should be able to delegate work to this agent"** (Add agent → A2A agent, and the
Code / CoWork multi-agent experiences) → **fabrcore-a2a**.

Both mount on the same host and share the same agents, principals, and ACLs.

## Install and wire up

```powershell
dotnet add <server-project> package FabrCore.Services.Microsoft365Copilot
```

Two lines in the host's `Program.cs` (full template: `assets/server-program.cs`):

```csharp
builder.AddFabrCoreServer();
builder.AddMicrosoft365Copilot();     // after AddFabrCoreServer

var app = builder.Build();
app.UseFabrCoreServer();
app.UseMicrosoft365Copilot();         // maps one POST route per configured agent
```

`AddMicrosoft365Copilot(Action<Microsoft365CopilotOptions>? configure = null)` accepts a code
override applied after configuration binding; code wins over config.

## Configuration — the `Microsoft365Copilot` section

Lives in **fabrcore.json or appsettings.json** (copyable templates:
`assets/fabrcore-json-microsoft365copilot.json` for production,
`assets/fabrcore-json-microsoft365copilot-localdev.json` for local dev).

Minimal production section:

```json
"Microsoft365Copilot": {
  "TenantId": "<entra-tenant-id>",
  "Agents": [
    {
      "Name": "assistant",
      "Route": "/api/messages",
      "ClientId": "<bot-app-registration-client-id>",
      "ClientSecret": "<bot-app-registration-secret>",
      "AgentType": "chat-agent"
    }
  ]
}
```

`AgentType` must be an `[AgentAlias]` registered via
the automatically discovered application assembly or referenced FabrCore project/package dependencies.
Use `FabrCoreServerOptions.AdditionalAssemblies` only for assemblies outside the application
dependency graph.

### Several agents on one host

`Agents` is a list of zero or more entries, and each entry is one agent in the Copilot agent
list. Microsoft allows at most one bot, and one custom engine agent, per app manifest, and an
Azure Bot resource has one messaging endpoint and one app id. So each exposed agent needs its own
Entra app registration, Azure Bot, and app package. One host serves them all:

```json
"Microsoft365Copilot": {
  "TenantId": "<entra-tenant-id>",
  "AuthType": "ClientSecret",
  "Manifest": { "DeveloperName": "Contoso", "PublicHostName": "agents.contoso.com" },
  "Agents": [
    { "Name": "assistant", "Route": "/api/messages", "ClientId": "<bot-1>", "ClientSecret": "<secret>",
      "AgentType": "chat-agent", "Handle": "assistant", "Models": "default",
      "Manifest": { "Name": "Assistant", "Description": "General-purpose assistant." } },
    { "Name": "sales", "ClientId": "<bot-2>", "ClientSecret": "<secret>", "AgentType": "sales-agent" }
  ]
}
```

- `Route` defaults to `/api/messages/{Name}`; one entry may claim the bare `/api/messages`. It
  must be a fixed path. Each Azure Bot's messaging endpoint points at its own agent's route.
- Top-level `TenantId`, `AuthType`, certificate/federation settings, `AuthorityEndpoint`,
  `WelcomeMessage`, and `Manifest` are defaults; an entry overrides any of them.
- Names, routes, client ids, and manifest names must be unique. A violation fails startup with a
  message naming `Microsoft365Copilot:Agents:{n}:...`.
- The JWT scheme accepts a token for any configured bot. Each route then requires the token `aud`
  to equal that entry's `ClientId` (or be listed in the host-wide `TokenValidation:Audiences`);
  anything else gets `403`.
- Outbound, each bot has its own Agents SDK connection: `ServiceConnection` for the first entry,
  `ServiceConnection-{Name}` for the rest, selected through `ConnectionsMap` by audience.
- An enabled channel with no agents is valid: no messaging endpoint is mapped and a warning is
  logged.

**Plugins, tools, system prompt, and args are not Copilot configuration.** Define them on the
agent type or in the blueprint that provisions the agent. The Copilot channel does not read
`AgentBindings` (A2A still does).

**Removed keys are no longer read.** With the channel enabled and no `Agents` section, the
presence of `Microsoft365Copilot:Agent`, `:MessagesEndpoint`, `:ClientId`, or `:ClientSecret`
fails startup with a message pointing at `Microsoft365Copilot:Agents`. When an `Agents` list is
present they are ignored and a startup warning names them, so one configuration can serve old and
new hosts while a cluster is upgraded. `SharedAgentHandle`,
`AgentPerConversation`, and `Agent:Binding` are gone. When migrating, note that `Handle` now
defaults to the entry's `Name` rather than `copilot`; set `"Handle": "copilot"` to keep reaching
agents created under the old default.

**Known limits with several agents.** User authorization (SSO) handlers are configured once and
shared, so sign-in works only for the agent whose bot owns the Azure Bot OAuth connection.
Proactive conversation endpoints are stored per principal, not per agent.

### Full reference

| Key | Default | Purpose |
|-----|---------|---------|
| `Enabled` | `true` | Master switch; `false` makes Add/Use no-ops |
| `TenantId` | — | Entra tenant of the bot app registrations. Default for every agent |
| `AuthType` | `ClientSecret` | Outbound auth: `ClientSecret`, `Certificate`, `CertificateSubjectName`, `UserManagedIdentity`, `SystemManagedIdentity`, `FederatedCredentials`, `WorkloadIdentity`. Default for every agent |
| `CertificateThumbprint` / `CertificateSubjectName` / `CertificateStoreName` | — | Certificate auth inputs. Defaults for every agent |
| `FederatedClientId` / `FederatedTokenFile` | — | Managed identity / workload identity inputs. Defaults for every agent |
| `AuthorityEndpoint` | computed | Override token authority. Default for every agent |
| `WelcomeMessage` | greeting | Sent on conversation join; `""` disables. Default for every agent |
| `ErrorMessage` | generic | Shown when the FabrCore agent fails |
| `TokenValidation:Enabled` | `true` | Inbound channel JWT validation. `false` = anonymous, **local dev only** |
| `TokenValidation:Audiences` | — | Audiences accepted on every route, in addition to each agent's own `ClientId` |
| `TokenValidation:ValidIssuers` | built-ins | Appended to Bot Framework + Entra issuers |
| `Agents` | empty | Agents to expose, zero or more; one entry per Azure Bot registration |
| `Agents:{n}:Name` | — | **Required.** Letters, digits, `-`, `_`. Used in the default route, package routes, and admin API |
| `Agents:{n}:Route` | `/api/messages/{Name}` | Fixed path Azure Bot Service posts to; one entry may claim `/api/messages` |
| `Agents:{n}:ClientId` / `ClientSecret` | — | This agent's bot app registration. `ClientId` is required when token validation is on |
| `Agents:{n}:TenantId` / `AuthType` / certificate / federation / `AuthorityEndpoint` | top-level value | Per-agent overrides |
| `Agents:{n}:AgentType` | — | **Required.** `[AgentAlias]` that answers on this route |
| `Agents:{n}:Handle` | entry `Name` | Agent handle under each user's principal; bare, no `:` |
| `Agents:{n}:Models` | `default` | Model configuration used when the agent has to be created |
| `Agents:{n}:WelcomeMessage` | top-level value | Greeting for this agent; `""` disables it |
| `Agents:{n}:Manifest:*` | top-level `Manifest` | Per-agent manifest overrides. `Name` defaults to the top-level name with one agent and to the entry `Name` with several; `Id` defaults to the agent's `ClientId` |
| `Streaming:Enabled` | `true` | Use channel streaming (Teams/Copilot) when available |
| `Streaming:InformativeUpdate` | "Working on it..." | Status update while the agent thinks; `""` disables |
| `Streaming:EnableGeneratedByAILabel` | `true` | "AI generated" label on replies |
| `Streaming:EnableFeedbackLoop` | `false` | Thumbs up/down buttons |
| `Proactive:Enabled` | `false` | Opt in to durable out-of-turn delivery through stored conversation endpoints |
| `Proactive:AllowedConversationTypes` | `["personal"]` | Conversation scopes eligible for proactive endpoint capture |
| `Proactive:MaxStoredEndpoints` | `8` | Maximum retained conversation endpoints per principal |
| `Proactive:MaxDeliveryAttempts` / `RetryBaseDelay` / `SendTimeout` | `3` / `2s` / `30s` | Provider-local retry policy |
| `Proactive:WorkerShards` / `OutboundQueueCapacity` | `4` / `64` | Bounded proactive sender queues (capacity per shard) |
| `Principal:Strategy` | `EntraObjectId` | `EntraObjectId`, `TenantAndObjectId`, `UserPrincipalName`, `ChannelUserId` |
| `Principal:Prefix` | — | Prefix for mapped principal handles (no `:` allowed) |
| `Principal:AllowChannelIdFallback` | `false` | Allow `{channelId}-{userId}` fallback when no Entra identity (auto-on when token validation is off) |
| `UserAuthorization:Handlers:*` | — | Agents SDK SSO handlers, forwarded verbatim (see `references/entra-sso-setup.md`) |
| `UserAuthorization:PassUserTokenToAgent` | `false` | Stamp the user's Entra token onto `AgentMessage.Args` |
| `Manifest:*` | sensible defaults | App name, developer info, icons, conversation starters, `PublicHostName`. Defaults for every agent |
| `Manifest:EnableAppPackageEndpoint` | dev-only | Serve `/m365copilot/{agent}/manifest.json`, `/m365copilot/{agent}/appPackage.zip`, and `/manifests/{name}.json` |

### Configuration mechanics (important)

- The addon **adds `fabrcore.json` to `IConfiguration`** (optional, reload-on-change) when the
  `Microsoft365Copilot` section is not already present. FabrCore.Host does not do this itself.
  This also makes any other sections in that file visible to host configuration, but keep
  FabrCore Host runtime branches such as `FabrCore:Acl` in `appsettings.json` so their behavior
  does not depend on whether this addon loaded the model file.
- The addon synthesizes the Microsoft 365 Agents SDK configuration
  (`Connections`, `ConnectionsMap`, `AgentApplication`) from the one `Microsoft365Copilot`
  section: one connection per agent with a `ClientId`, mapped by audience. **Escape hatch:** any of those sections you define natively are left untouched, so raw
  Agents SDK configuration always remains possible.
- Inbound channel JWT validation runs under a dedicated scheme
  (`Microsoft365CopilotBearer`) and policy (`Microsoft365Copilot`) so it never interferes with
  host authentication. Valid issuers = Bot Framework + well-known Microsoft tenants + your
  `TenantId` (and any per-agent `TenantId`) + `TokenValidation:ValidIssuers`. Valid audiences =
  every agent's `ClientId` + `TokenValidation:Audiences`; each route then checks the audience
  against its own agent and answers `403` on a mismatch.

## How users map to FabrCore principals

Teams/Copilot stamp the user's Entra identity on every activity; the addon turns it into a
FabrCore principal handle (sanitized: lowercase, `:` stripped, ≤96 chars):

| Strategy | Handle | Use when |
|----------|--------|----------|
| `EntraObjectId` (default) | Entra object id | Single-tenant bots |
| `TenantAndObjectId` | `{tid}-{oid}` | Multi-tenant bots |
| `UserPrincipalName` | UPN from SSO token, falls back to oid | Readable handles; needs user authorization |
| `ChannelUserId` | `{channelId}-{userId}` | Dev/test channels only |

Every bridged message carries identity/context in `AgentMessage.Args`:
`Microsoft365Copilot:AadObjectId`, `:TenantId`, `:UserName`, `:ConversationId`, `:ChannelId`,
`:Locale`, `:ActivityId`, and `:Agent` (the name of the `Agents` entry the message arrived
through) (+ `:UserToken` when `PassUserTokenToAgent` is on, and
`:DeliveryEndpointId` when proactive endpoint capture succeeds). `AgentMessage.Channel` is
`"m365copilot"` so agents can branch on ingress source.

Custom mapping: implement `ICopilotPrincipalResolver` and register it **before**
`AddMicrosoft365Copilot()` (template: `assets/custom-principal-resolver.cs`).

### Which agent answers

Agents are per user. The principal comes from the inbound activity and the handle from the
`Agents` entry, so each user reaches `{principal}:{Handle}` (`Handle` defaults to the entry's
`Name`). Same-principal traffic — no ACL grants needed.

- An agent that already exists for the principal under that handle, for example one provisioned
  by a blueprint, is used untouched.
- A missing one is created through `EnsureAgentsAsync` from `AgentType` and `Models` only.
- Routing every user to one shared agent (`SharedAgentHandle`) and one agent per conversation
  (`AgentPerConversation`) have been removed.

## Azure + Entra setup

Per agent: one Entra app registration + one Azure Bot with the Teams channel enabled (Teams also
carries Microsoft 365 Copilot traffic). Script: `assets/provision-azure-bot.ps1`, run once per
`Agents` entry; full walkthrough with auth-type variants and dev tunnels:
`references/azure-bot-provisioning.md`.

The FabrCore host can run anywhere — it only needs a public HTTPS route to each agent's messaging
route.

## App package (making the agent visible in Copilot)

The addon generates one Microsoft 365 app package per agent from configuration — no hand-authored
manifest. In Development (or with `Manifest:EnableAppPackageEndpoint: true`), where `{agent}` is
the entry's `Name`:

- `GET /m365copilot/{agent}/manifest.json` — manifest schema **v1.22** with
  `copilotAgents.customEngineAgents` bound to the bot, `personal` scope, conversation starters
  from `Manifest:ConversationStarters`, and `webApplicationInfo` added automatically when SSO
  handlers are configured.
- `GET /m365copilot/{agent}/appPackage.zip` — manifest + icons, ready to upload.
- `GET /manifests/{name}.json` — the manifest addressed by the slug of an agent's manifest name
  (or its app id), searched across all agents.

These developer routes are anonymous. In production fetch packages through the administration
API instead: `GET /fabrcoreapi/admin/v1/integrations/microsoft365/agents/{name}/manifest` and
`.../agents/{name}/app-package` (`404` `agent-not-found` for an unknown name, `409` while the
channel is off or the agent has no `ClientId`). The status document at
`/fabrcoreapi/admin/v1/integrations/microsoft365` has `apiVersion` `2` and lists the exposed
agents under `agents`.

The host embeds default icons. Set `Manifest:ColorIconPath` / `OutlineIconPath`, at the top level
or per agent, to use your own. A management console may also replace the icons in the zip it
downloads.

Upload via Teams (*Apps → Manage your apps → Upload a custom app*) or org-wide via the
Microsoft 365 admin center (*Settings → Integrated apps*). The agent appears in the Copilot
*Agents* rail and in Teams chat. Users need a Microsoft 365 Copilot license to see it in Copilot.

Pitfall: manifest schema v1.17+ **removed `packageName`** and v1.22 sets
`additionalProperties: false` — if you post-edit the generated manifest, do not add properties
from older-schema examples; upload validation rejects them.

## Entra user SSO / OBO

Default behavior needs no sign-in: user identity comes from the activity. Configure
`UserAuthorization:Handlers` when agents must call Graph or your APIs **as the user** — full
setup (Expose an API, `api://botid-{clientId}`, OAuth connection, OBO scopes, token security
notes) in `references/entra-sso-setup.md`. With `PassUserTokenToAgent: true`, agents receive the
token in `Args["Microsoft365Copilot:UserToken"]` — it then flows through FabrCore messaging and
monitors, so enable deliberately. Handlers are configured once and shared by all agents: with
several agents, sign-in works only for the agent whose bot owns the Azure Bot OAuth connection.

With several agents, set `Agents:{n}:SignInConnectionName` instead: that agent gets its own handler,
and only it declares single sign-on in its package. The person's token then goes to its on-behalf-of
connections through `ICopilotUserAssertionSink` (default: `IConnectionUserAssertions`), not into its
messages. Agents ask for it with `Connections.GetHttpClientAsync("name", "resource")`. See the
`fabrcore-connections` skill for templates.

## Streaming

On streaming channels the addon sends the informative update, keeps a typing indicator running,
and delivers the reply through the channel streaming protocol with the AI-generated label. Other
channels get one buffered reply. The reply is delivered when the FabrCore agent's turn completes;
interim `_thinking` updates are not relayed (would require a Host-side observer hook not yet
exposed to addons).

## Proactive / out-of-turn delivery (opt in)

Set `Microsoft365Copilot:Proactive:Enabled` to `true` only when agents need to send after the
inbound turn has ended. On every eligible inbound turn, the addon stores a versioned
conversation endpoint in the principal context before subscribing the turn observer, refreshes
its last-active time, and stamps its stable ID into
`Args["Microsoft365Copilot:DeliveryEndpointId"]`. The default allowlist captures only `personal`
conversations and retains at most eight endpoints per principal. Endpoints are stored per
principal, not per agent.

Agent code stays provider-neutral:

```csharp
await SendToUserAsync("Your report is ready");

await SendToUserAsync(
    "Your report is ready",
    target: new PrincipalDeliveryTarget("m365copilot"));

await SendToUserAsync(
    "Send this to a specific conversation",
    target: new PrincipalDeliveryTarget("m365copilot", endpointId));
```

An untargeted message uses the most recently active eligible endpoint across every installed
relay. A channel-only target selects this provider's most recently active eligible endpoint; an
endpoint target selects that eligible conversation. Either explicit form bypasses local observer
precedence, so Surface or another connected WebSocket/legacy observer does not consume the
proactive notification. The M365 relay accepts nonblank text and valid Adaptive Cards; it rejects
system/control messages, foreign targets, unsupported `ui.*` payloads, and invalid mappings. It
sends on bounded worker shards rather than blocking the principal grain.

The provider retries throttling, 5xx, network, and timeout failures (honoring `Retry-After` when
available). Permanent 4xx and mapping failures are dead-lettered; stale conversation references
are marked unavailable until refreshed by another eligible turn. Delivery is durable
at-least-once, so a provider accepting a request immediately before a crash or timeout can
produce a duplicate retry.

Use **fabrcore-principal-delivery** for the generic outbox, endpoint/context contracts, provider
authoring lifecycle, webhook template, ordering, leases, expiry, and completion semantics.

## Local development

No Azure needed: set `TokenValidation:Enabled: false`, run the host, and use the Microsoft 365
Agents Playground (`winget install agentsplayground`, point it at
`http://localhost:<port>` + the agent's `Route`, for example `/api/messages`). Users map via channel-id fallback. A startup warning
logs when the endpoint is anonymous.

For real channels from a dev box: `devtunnel host -p <port> --allow-anonymous` and set the Azure
Bot messaging endpoint to `https://<tunnel>` + the agent's `Route`.

## Production checklist

- `TokenValidation:Enabled` must stay `true` on anything internet-reachable.
- Register a durable Agents SDK `IStorage` (e.g. `Microsoft.Agents.Storage.Blobs`) **before**
  `AddMicrosoft365Copilot()` when scaling out or using SSO — sign-in/turn state defaults to
  `MemoryStorage`.
- Prefer certificate or managed-identity `AuthType` over `ClientSecret`; keep secrets out of
  source control (`fabrcore.json` should be git-ignored).
- Use SQL/Azure Orleans clustering so per-user agents survive restarts (fabrcore-server).
- When proactive delivery is enabled, use durable Orleans grain persistence and reminders so
  pending work, leases, and restart recovery are not process-local.
- Replace placeholder icons via `Manifest:ColorIconPath` / `Manifest:OutlineIconPath` (top level
  or per agent).

## Troubleshooting

| Symptom | Cause / fix |
|---------|-------------|
| Upload rejected: schema validation | Regenerate the package; don't hand-add properties from older manifest schemas (`packageName` etc.) |
| Agent not in Copilot *Agents* list | Wait a few minutes; user needs the Copilot license; app must show under *Manage your apps* |
| 401 in host logs on a messaging route | No agent's `ClientId`/`TenantId` matches the tokens Azure Bot Service sends |
| 403 on a messaging route | The token was issued for a different bot than the one that owns the route; point that Azure Bot's messaging endpoint at its own agent's `Route` |
| No reply in Teams/Copilot | Bot messaging endpoint must exactly match your public host + the agent's `Route`; check host logs |
| Startup fails naming `Agent`, `MessagesEndpoint`, `ClientId`, or `ClientSecret` | Single-agent keys are no longer read and there is no `Agents` section; move them into an `Agents` entry |
| Startup warning that single-agent settings are ignored | Those keys sit beside an `Agents` list. Harmless; remove them once no older host shares the configuration |
| Startup fails naming `Microsoft365Copilot:Agents:{n}:...` | A required value is missing or a name, route, client id, or manifest name is used twice |
| "Failed to provision Copilot agent" | The entry's `AgentType` doesn't match a discovered `[AgentAlias]`, or a dynamically selected agent assembly was not added to `AdditionalAssemblies` |
| NU1605 package downgrade at restore | Don't mix a `FabrCore.Host` PackageReference with a ProjectReference to this addon's source — reference both as packages or both as projects |
| Startup throws "agent '...' needs a ClientId" | Token validation is on and that entry has no `ClientId`; set it, or disable validation for local dev |
| Proactive message never sends | `Proactive:Enabled` is false, or the principal has not completed an eligible personal turn that captured a conversation endpoint |
| Proactive work remains pending | The endpoint is stale/unavailable, the payload is unsupported, the sender queue is saturated, or no relay supports the message; inspect relay/outbox logs and metrics |
| A proactive notification appears twice | Delivery is at-least-once; make the content or downstream operation idempotent using the delivery/message ID |

## Key types

| Type | Purpose |
|------|---------|
| `Microsoft365CopilotExtensions` | `AddMicrosoft365Copilot` / `UseMicrosoft365Copilot` |
| `Microsoft365CopilotOptions` | Options bound from the `Microsoft365Copilot` section |
| `Microsoft365CopilotDefaults` | Section/scheme/route/arg-key constants |
| `FabrCoreCopilotAgent` | The Agents SDK bridge application |
| `CopilotConversationContextWriter` | Captures and refreshes versioned proactive conversation endpoints |
| `CopilotPrincipalMessageRelay` | Resolves M365 endpoints and enqueues bounded proactive sends |
| `ICopilotPrincipalResolver` | Replaceable user → principal mapping |
| `CopilotAgentCatalog` / `CopilotAgent` | The exposed agents, resolved once at startup with defaults applied |
| `ICopilotAgentProvisioner` | Ensures/caches the target FabrCore agent |
| `CopilotAppPackageBuilder` | Manifest + app package generator |
