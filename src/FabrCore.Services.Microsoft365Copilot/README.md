# FabrCore.Services.Microsoft365Copilot

A FabrCore server addon that makes your FabrCore agents available in **Microsoft 365 Copilot**
and **Microsoft Teams** as a [custom engine agent](https://learn.microsoft.com/en-us/microsoft-365/copilot/extensibility/overview-custom-engine-agent).

It hosts the Azure Bot Service messaging endpoint (`/api/messages`) with the
[Microsoft 365 Agents SDK](https://learn.microsoft.com/en-us/microsoft-365/agents-sdk/), validates
channel and Entra user identity, and bridges every Copilot conversation to a FabrCore agent —
without any changes to `FabrCore.Host` or your agents.

```
Microsoft 365 Copilot / Teams
        │  Activity (JWT from Azure Bot Service)
        ▼
POST /api/messages  ──►  FabrCoreCopilotAgent (Agents SDK bridge)
        │                    │ map Entra user ──► FabrCore principal handle
        │                    │ ensure agent   ──► IFabrCoreAgentService.EnsureAgentsAsync
        │                    ▼
        ◄── streamed reply ── your [AgentAlias] agent on the Orleans silo
```

## Quick start

**1. Reference the addon from your FabrCore server project** and add two lines to `Program.cs`:

```csharp
using FabrCore.Host;
using FabrCore.Services.Microsoft365Copilot;

var builder = WebApplication.CreateBuilder(args);

builder.AddFabrCoreServer(new FabrCoreServerOptions
{
    AdditionalAssemblies = [typeof(MyAgent).Assembly]
});
builder.AddMicrosoft365Copilot();          // ← the addon

var app = builder.Build();
app.UseFabrCoreServer();
app.UseMicrosoft365Copilot();              // ← maps /api/messages
app.Run();
```

**2. Add a `Microsoft365Copilot` section to `fabrcore.json`** (or `appsettings.json` — both work):

```json
{
  "Microsoft365Copilot": {
    "TenantId": "<entra-tenant-id>",
    "ClientId": "<bot-app-registration-client-id>",
    "ClientSecret": "<bot-app-registration-secret>",
    "Agent": {
      "AgentType": "chat-agent",
      "SystemPrompt": "You are a helpful assistant.",
      "Models": "default"
    },
    "Manifest": {
      "Name": "My FabrCore Agent",
      "Description": "Answers questions using my FabrCore agents.",
      "PublicHostName": "myagents.contoso.com"
    }
  }
}
```

`Agent:AgentType` is any `[AgentAlias]` registered through
`FabrCoreServerOptions.AdditionalAssemblies`. Each Microsoft 365 user automatically gets their own
agent instance (principal handle = their Entra object id), with isolated chat history and state.

**3. Create the Azure resources** (once — see [Azure setup](#azure-setup) below).

**4. Upload the app package.** Run the server in Development and download
`https://localhost:<port>/m365copilot/appPackage.zip` — a ready-to-upload zip generated from your
configuration. Upload it in Teams (*Apps → Manage your apps → Upload a custom app*) or the
[Microsoft 365 admin center](https://admin.microsoft.com) (*Settings → Integrated apps → Upload
custom apps*) for the whole org. The agent then appears in Teams chat and in the Microsoft 365
Copilot side panel.

## Local development — no Azure required

Test the full bridge locally with the
[Microsoft 365 Agents Playground](https://learn.microsoft.com/en-us/microsoft-365/agents-sdk/test-with-toolkit-project):

```json
{
  "Microsoft365Copilot": {
    "TokenValidation": { "Enabled": false },
    "Agent": { "AgentType": "chat-agent" }
  }
}
```

```bash
winget install agentsplayground
agentsplayground -e "http://localhost:5000/api/messages"
```

With token validation off, the endpoint accepts anonymous requests and users are mapped to
principals by channel user id. A warning is logged on startup — never run production this way.

To test against the real Teams/Copilot channels from your dev box, use a dev tunnel and point the
Azure Bot's messaging endpoint at it:

```bash
devtunnel host -p 5000 --allow-anonymous
az bot update -n <bot-name> -g <rg> --endpoint "https://<tunnel-host>/api/messages"
```

## Azure setup

One Entra app registration + one Azure Bot resource, pointed at your public `/api/messages`:

```bash
# 1. App registration (SingleTenant shown; also supported: MultiTenant, managed identity, federated credentials)
az ad app create --display-name "MyFabrCoreAgent" --sign-in-audience AzureADMyOrg
APP_ID=<appId from output>
az ad app credential reset --id $APP_ID    # → ClientSecret for fabrcore.json

# 2. Azure Bot resource wired to your endpoint
az bot create --resource-group <rg> --name <bot-name> --app-type SingleTenant \
  --appid $APP_ID --tenant-id <tenant-id> \
  --endpoint "https://<your-public-host>/api/messages" --sku S1

# 3. Enable the Teams channel (this also carries Microsoft 365 Copilot traffic)
az bot msteams create --name <bot-name> --resource-group <rg>
```

Your FabrCore host can run **anywhere** — Azure, on-prem, another cloud — it only needs a public
HTTPS route to `/api/messages`.

Supported outbound auth types (`Microsoft365Copilot:AuthType`): `ClientSecret`, `Certificate`,
`CertificateSubjectName`, `UserManagedIdentity`, `SystemManagedIdentity`, `FederatedCredentials`,
`WorkloadIdentity`. Prefer certificates or managed identity in production.

## Entra user single sign-on (SSO / OBO)

Out of the box the addon identifies users from the Entra identity Teams/Copilot stamp on every
activity (`From.AadObjectId`) — no sign-in prompt, no extra setup.

Configure **user authorization** when your agents need to call downstream APIs (Microsoft Graph,
your own APIs) *as the user*:

1. On the bot's app registration: *Expose an API* → Application ID URI `api://botid-<clientId>`,
   add a scope (e.g. `defaultScopes`), and authorize the Teams/M365/Outlook client apps
   ([documented ids](https://learn.microsoft.com/en-us/microsoft-365/agents-sdk/agent-oauth-configuration)).
2. On the Azure Bot: *Settings → Configuration → Add OAuth Connection Settings* — provider
   *Azure Active Directory v2*, Token Exchange URL `api://botid-<clientId>`.
3. Reference that connection in the addon config — the `Handlers` schema is the
   [Agents SDK handler schema](https://learn.microsoft.com/en-us/microsoft-365/agents-sdk/agent-oauth-configuration),
   forwarded verbatim:

```json
"Microsoft365Copilot": {
  "UserAuthorization": {
    "PassUserTokenToAgent": true,
    "Handlers": {
      "graph": {
        "Settings": {
          "AzureBotOAuthConnectionName": "<oauth-connection-name>",
          "OBOConnectionName": "ServiceConnection",
          "OBOScopes": [ "https://graph.microsoft.com/.default" ]
        }
      }
    }
  }
}
```

With `PassUserTokenToAgent: true`, agents and plugins receive the user's access token as
`message.Args["Microsoft365Copilot:UserToken"]`. **Enable deliberately** — the token then flows
through FabrCore messaging and any configured monitors. The generated manifest automatically gains
the `webApplicationInfo` SSO section when handlers are configured.

## How users map to FabrCore principals

| `Principal:Strategy`  | Principal handle                            | Use when |
|-----------------------|---------------------------------------------|----------|
| `EntraObjectId` (default) | Entra object id (GUID)                  | Single-tenant bots |
| `TenantAndObjectId`   | `{tenantId}-{objectId}`                     | Multi-tenant bots |
| `UserPrincipalName`   | UPN from the SSO token (falls back to oid)  | Human-readable handles; needs user authorization |
| `ChannelUserId`       | `{channelId}-{channelUserId}`               | Dev/test channels only |

`Principal:Prefix` (e.g. `"m365-"`) namespaces the handles. Handles are sanitized (lowercased,
`:` and other separators stripped) before use. Every bridged message also carries the raw identity
in `Args` (`Microsoft365Copilot:AadObjectId`, `:TenantId`, `:UserName`, `:ConversationId`, ...).

To customize mapping entirely, register your own resolver **before** `AddMicrosoft365Copilot()`:

```csharp
builder.Services.AddSingleton<ICopilotPrincipalResolver, MyDirectoryBackedResolver>();
```

**Shared agent instead of per-user agents:** set `Agent:SharedAgentHandle` (e.g.
`"system:helpdesk"`). All users then talk to one agent. Cross-principal messaging is subject to
FabrCore ACL — grant `agent.message.allow` on the shared agent to the mapped principals (see the
fabrcore-acl skill).

## Streaming

On streaming-capable channels (Teams, Microsoft 365 Copilot) the addon sends an informative
status update (`Streaming:InformativeUpdate`), a typing indicator while the FabrCore agent works,
and delivers the reply through the channel's streaming protocol with the *AI generated* label
(`Streaming:EnableGeneratedByAILabel`). Other channels get a single buffered reply.

> Note: the reply is delivered when the FabrCore agent completes its turn. Incremental
> token-by-token relay of the agent's internal `_thinking` updates would require a host-side
> observer hook that FabrCore does not currently expose to addons.

## Production checklist

- **Durable Agents SDK storage** — sign-in and turn state default to `MemoryStorage`. When running
  multiple instances or using SSO, register a durable `IStorage`
  (e.g. `Microsoft.Agents.Storage.Blobs`) *before* `AddMicrosoft365Copilot()`:
  `builder.Services.AddSingleton<IStorage>(new BlobsStorage(...));`
- **Secrets** — keep `ClientSecret` out of source control (`fabrcore.json` is git-ignored in this
  repo; better: user-secrets, Key Vault, or a secret-free `AuthType`).
- **Token validation on** — `TokenValidation:Enabled` must be `true` (default) anywhere reachable
  from the internet.
- **Orleans persistence** — use SQL/Azure clustering so per-user agents survive restarts
  (see the fabrcore-server skill).
- **Icons** — replace the generated placeholder icons via `Manifest:ColorIconPath` /
  `Manifest:OutlineIconPath` before publishing.

## Configuration reference (`Microsoft365Copilot` section)

| Key | Default | Purpose |
|-----|---------|---------|
| `Enabled` | `true` | Master switch for the addon |
| `TenantId` / `ClientId` / `ClientSecret` | — | Bot app registration identity |
| `AuthType` | `ClientSecret` | Outbound auth: secret, certificate, MSI, federated |
| `MessagesEndpoint` | `/api/messages` | Route for the Azure Bot Service endpoint |
| `WelcomeMessage` | greeting | Sent when a user joins; empty string disables |
| `ErrorMessage` | generic | Shown when the FabrCore agent fails |
| `TokenValidation:Enabled` | `true` | Inbound channel JWT validation (off = local dev only) |
| `TokenValidation:Audiences` / `ValidIssuers` | ClientId / built-ins | Extra validation inputs |
| `Agent:AgentType` | — | FabrCore `[AgentAlias]` provisioned per user (**required** unless shared) |
| `Agent:Handle` | `copilot` | Handle of the per-user agent instance |
| `Agent:SharedAgentHandle` | — | Route everyone to one existing agent (`principal:agent`) |
| `Agent:Models` / `SystemPrompt` / `Plugins` / `Tools` / `Args` | — | Provisioning template |
| `Agent:AgentPerConversation` | `false` | New agent instance per Copilot conversation |
| `Streaming:Enabled` | `true` | Use channel streaming when available |
| `Streaming:InformativeUpdate` | "Working on it..." | Status text while the agent thinks |
| `Streaming:EnableGeneratedByAILabel` | `true` | "AI generated" label on replies |
| `Streaming:EnableFeedbackLoop` | `false` | Thumbs up/down buttons |
| `Principal:Strategy` / `Prefix` / `AllowChannelIdFallback` | `EntraObjectId` | User → principal mapping |
| `UserAuthorization:Handlers:*` | — | Agents SDK SSO handlers (verbatim passthrough) |
| `UserAuthorization:PassUserTokenToAgent` | `false` | Stamp the user's token on agent messages |
| `Manifest:*` | sensible defaults | App name, developer info, icons, conversation starters |
| `Manifest:EnableAppPackageEndpoint` | dev-only | Serve `/m365copilot/appPackage.zip` |

**Escape hatch:** if you define the Agents SDK's native `Connections`, `ConnectionsMap`, or
`AgentApplication` sections yourself, the addon leaves them untouched and only supplies what's
missing — you can drop down to raw SDK configuration at any time.
