---
name: fabrcore-microsoft365copilot
description: >
  Surface FabrCore agents in Microsoft 365 Copilot and Teams as a custom engine agent using the
  FabrCore.Services.Microsoft365Copilot server addon: /api/messages hosting, AddMicrosoft365Copilot,
  UseMicrosoft365Copilot, the Microsoft365Copilot fabrcore.json section, Azure Bot Service setup,
  Entra user identity and SSO/OBO, principal mapping, app package/manifest generation, streaming,
  and local testing with the Agents Playground.
  Use for: "Microsoft 365 Copilot", "M365 Copilot", "custom engine agent", "Teams bot",
  "/api/messages", "AddMicrosoft365Copilot", "UseMicrosoft365Copilot", "Microsoft365CopilotOptions",
  "Azure Bot", "bot messaging endpoint", "app package", "appPackage.zip", "Teams manifest",
  "copilotAgents", "ICopilotPrincipalResolver", "SharedAgentHandle", "Agents Playground",
  "devtunnel bot", "Teams SSO", "OBO token", "PassUserTokenToAgent", "token validation",
  "Microsoft 365 Agents SDK", or making FabrCore agents chat-able from Copilot/Teams/Outlook.
  Do NOT use for: general server/host setup — use fabrcore-server; writing the agents themselves —
  use fabrcore-agent; ACL grants and enforcement — use fabrcore-acl.
allowed-tools: "Bash(dotnet:*) Bash(mkdir:*) Bash(ls:*) Bash(pwsh:*) Bash(powershell:*) Bash(git:*) Bash(dir:*) Bash(az:*) Bash(curl:*) Bash(devtunnel:*)"
---

# FabrCore ⇄ Microsoft 365 Copilot (Custom Engine Agent)

`FabrCore.Services.Microsoft365Copilot` is a server addon that exposes a FabrCore.Host's agents
to Microsoft 365 Copilot, Teams, and other Azure Bot Service channels. It hosts the
`/api/messages` endpoint with the Microsoft 365 Agents SDK, validates channel + Entra identity,
maps each Microsoft 365 user to a FabrCore principal, auto-provisions their agent, and streams
replies back. No changes to FabrCore.Host or the agents are required.

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

## Install and wire up

```powershell
dotnet add <server-project> package FabrCore.Services.Microsoft365Copilot
```

Two lines in the host's `Program.cs` (full template: `assets/server-program.cs`):

```csharp
builder.AddFabrCoreServer(new FabrCoreServerOptions
{
    AdditionalAssemblies = [typeof(MyAgent).Assembly]
});
builder.AddMicrosoft365Copilot();     // after AddFabrCoreServer

var app = builder.Build();
app.UseFabrCoreServer();
app.UseMicrosoft365Copilot();         // maps POST /api/messages
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
  "ClientId": "<bot-app-registration-client-id>",
  "ClientSecret": "<bot-app-registration-secret>",
  "Agent": { "AgentType": "chat-agent" }
}
```

`Agent:AgentType` must be an `[AgentAlias]` registered via
`FabrCoreServerOptions.AdditionalAssemblies`.

### Full reference

| Key | Default | Purpose |
|-----|---------|---------|
| `Enabled` | `true` | Master switch; `false` makes Add/Use no-ops |
| `TenantId` / `ClientId` / `ClientSecret` | — | Bot app registration identity |
| `AuthType` | `ClientSecret` | Outbound auth: `ClientSecret`, `Certificate`, `CertificateSubjectName`, `UserManagedIdentity`, `SystemManagedIdentity`, `FederatedCredentials`, `WorkloadIdentity` |
| `CertificateThumbprint` / `CertificateSubjectName` / `CertificateStoreName` | — | Certificate auth inputs |
| `FederatedClientId` / `FederatedTokenFile` | — | Managed identity / workload identity inputs |
| `AuthorityEndpoint` | computed | Override token authority |
| `MessagesEndpoint` | `/api/messages` | Azure Bot Service messaging route |
| `WelcomeMessage` | greeting | Sent on conversation join; `""` disables |
| `ErrorMessage` | generic | Shown when the FabrCore agent fails |
| `TokenValidation:Enabled` | `true` | Inbound channel JWT validation. `false` = anonymous, **local dev only** |
| `TokenValidation:Audiences` | `[ClientId]` | Extra accepted audiences |
| `TokenValidation:ValidIssuers` | built-ins | Appended to Bot Framework + Entra issuers |
| `Agent:AgentType` | — | **Required** (unless `SharedAgentHandle`): agent provisioned per user |
| `Agent:Handle` | `copilot` | Per-user agent instance handle |
| `Agent:SharedAgentHandle` | — | Route all users to one agent (`"system:assistant"`); see ACL note |
| `Agent:Models` / `SystemPrompt` / `Plugins` / `Tools` / `Args` | — | Provisioning template for per-user agents |
| `Agent:AgentPerConversation` | `false` | New agent instance per Copilot conversation (`{Handle}-{convId}`) |
| `Streaming:Enabled` | `true` | Use channel streaming (Teams/Copilot) when available |
| `Streaming:InformativeUpdate` | "Working on it..." | Status update while the agent thinks; `""` disables |
| `Streaming:EnableGeneratedByAILabel` | `true` | "AI generated" label on replies |
| `Streaming:EnableFeedbackLoop` | `false` | Thumbs up/down buttons |
| `Principal:Strategy` | `EntraObjectId` | `EntraObjectId`, `TenantAndObjectId`, `UserPrincipalName`, `ChannelUserId` |
| `Principal:Prefix` | — | Prefix for mapped principal handles (no `:` allowed) |
| `Principal:AllowChannelIdFallback` | `false` | Allow `{channelId}-{userId}` fallback when no Entra identity (auto-on when token validation is off) |
| `UserAuthorization:Handlers:*` | — | Agents SDK SSO handlers, forwarded verbatim (see `references/entra-sso-setup.md`) |
| `UserAuthorization:PassUserTokenToAgent` | `false` | Stamp the user's Entra token onto `AgentMessage.Args` |
| `Manifest:*` | sensible defaults | App name, developer info, icons, conversation starters, `PublicHostName` |
| `Manifest:EnableAppPackageEndpoint` | dev-only | Serve `/m365copilot/manifest.json` + `/m365copilot/appPackage.zip` |

### Configuration mechanics (important)

- The addon **adds `fabrcore.json` to `IConfiguration`** (optional, reload-on-change) when the
  `Microsoft365Copilot` section is not already present. FabrCore.Host does not do this itself.
  Side effect: other sections in fabrcore.json (for example `Acl:Seed`) become visible to the
  host configuration too.
- The addon synthesizes the Microsoft 365 Agents SDK configuration
  (`Connections`, `ConnectionsMap`, `AgentApplication`) from the one `Microsoft365Copilot`
  section. **Escape hatch:** any of those sections you define natively are left untouched, so raw
  Agents SDK configuration always remains possible.
- Inbound channel JWT validation runs under a dedicated scheme
  (`Microsoft365CopilotBearer`) and policy (`Microsoft365Copilot`) so it never interferes with
  host authentication. Valid issuers = Bot Framework + well-known Microsoft tenants + your
  `TenantId` + `TokenValidation:ValidIssuers`.

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
`:Locale`, `:ActivityId` (+ `:UserToken` when `PassUserTokenToAgent` is on). `AgentMessage.Channel`
is `"m365copilot"` so agents can branch on ingress source.

Custom mapping: implement `ICopilotPrincipalResolver` and register it **before**
`AddMicrosoft365Copilot()` (template: `assets/custom-principal-resolver.cs`).

### Per-user vs shared agent

- **Per-user (default):** each user gets `{oid}:copilot`, provisioned on first contact from the
  `Agent` template via `EnsureAgentsAsync`. Same-principal traffic — no ACL grants needed.
- **Shared:** `Agent:SharedAgentHandle: "system:helpdesk"` routes everyone to one agent. Under the
  default ACL `Enforce` mode, cross-principal sends require `agent.message.allow` grants for the
  mapped principals (see fabrcore-acl). If the handle starts with `system:` and `AgentType` is
  set, the addon provisions the system agent itself; otherwise provision it in host startup code.

## Azure + Entra setup

One Entra app registration + one Azure Bot with the Teams channel enabled (Teams also carries
Microsoft 365 Copilot traffic). Script: `assets/provision-azure-bot.ps1`; full walkthrough with
auth-type variants and dev tunnels: `references/azure-bot-provisioning.md`.

The FabrCore host can run anywhere — it only needs a public HTTPS route to `/api/messages`.

## App package (making the agent visible in Copilot)

The addon generates the Microsoft 365 app package from configuration — no hand-authored
manifest. In Development (or with `Manifest:EnableAppPackageEndpoint: true`):

- `GET /m365copilot/manifest.json` — manifest schema **v1.22** with
  `copilotAgents.customEngineAgents` bound to the bot, `personal` scope, conversation starters
  from `Manifest:ConversationStarters`, and `webApplicationInfo` added automatically when SSO
  handlers are configured.
- `GET /m365copilot/appPackage.zip` — manifest + icons, ready to upload.

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
monitors, so enable deliberately.

## Streaming

On streaming channels the addon sends the informative update, keeps a typing indicator running,
and delivers the reply through the channel streaming protocol with the AI-generated label. Other
channels get one buffered reply. The reply is delivered when the FabrCore agent's turn completes;
interim `_thinking` updates are not relayed (would require a Host-side observer hook not yet
exposed to addons).

## Local development

No Azure needed: set `TokenValidation:Enabled: false`, run the host, and use the Microsoft 365
Agents Playground (`winget install agentsplayground`, point it at
`http://localhost:<port>/api/messages`). Users map via channel-id fallback. A startup warning
logs when the endpoint is anonymous.

For real channels from a dev box: `devtunnel host -p <port> --allow-anonymous` and set the Azure
Bot messaging endpoint to `https://<tunnel>/api/messages`.

## Production checklist

- `TokenValidation:Enabled` must stay `true` on anything internet-reachable.
- Register a durable Agents SDK `IStorage` (e.g. `Microsoft.Agents.Storage.Blobs`) **before**
  `AddMicrosoft365Copilot()` when scaling out or using SSO — sign-in/turn state defaults to
  `MemoryStorage`.
- Prefer certificate or managed-identity `AuthType` over `ClientSecret`; keep secrets out of
  source control (`fabrcore.json` should be git-ignored).
- Use SQL/Azure Orleans clustering so per-user agents survive restarts (fabrcore-server).
- Replace placeholder icons via `Manifest:ColorIconPath` / `Manifest:OutlineIconPath`.

## Troubleshooting

| Symptom | Cause / fix |
|---------|-------------|
| Upload rejected: schema validation | Regenerate the package; don't hand-add properties from older manifest schemas (`packageName` etc.) |
| Agent not in Copilot *Agents* list | Wait a few minutes; user needs the Copilot license; app must show under *Manage your apps* |
| 401 in host logs on /api/messages | `ClientId`/`TenantId` don't match the tokens Azure Bot Service sends |
| No reply in Teams/Copilot | Bot messaging endpoint must exactly match your public host + `MessagesEndpoint`; check host logs |
| "Failed to provision Copilot agent" | `Agent:AgentType` doesn't match a registered `[AgentAlias]`, or the agent assembly is missing from `AdditionalAssemblies` |
| NU1605 package downgrade at restore | Don't mix a `FabrCore.Host` PackageReference with a ProjectReference to this addon's source — reference both as packages or both as projects |
| Startup throws "ClientId is required" | Token validation is on without a ClientId; set it, or disable validation for local dev |

## Key types

| Type | Purpose |
|------|---------|
| `Microsoft365CopilotExtensions` | `AddMicrosoft365Copilot` / `UseMicrosoft365Copilot` |
| `Microsoft365CopilotOptions` | Options bound from the `Microsoft365Copilot` section |
| `Microsoft365CopilotDefaults` | Section/scheme/route/arg-key constants |
| `FabrCoreCopilotAgent` | The Agents SDK bridge application |
| `ICopilotPrincipalResolver` | Replaceable user → principal mapping |
| `ICopilotAgentProvisioner` | Ensures/caches the target FabrCore agent |
| `CopilotAppPackageBuilder` | Manifest + app package generator |
