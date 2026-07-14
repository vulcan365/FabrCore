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

## Quickstart — chat with your agent in Microsoft 365 Copilot

From an empty folder to chatting with a FabrCore agent inside Copilot, using a dev tunnel so
nothing has to be deployed yet.

**What you need**

- A Microsoft 365 work account with a **Microsoft 365 Copilot license** (the $30/user/month
  add-on) — that license is what gives you the *Agents* list in the Copilot app.
- Permission to upload custom apps in Teams. If *Upload a custom app* is missing in step 7, ask a
  tenant admin to allow custom app uploads (Teams admin center → *Teams apps → Setup policies*) or
  to deploy the package org-wide instead.
- An Azure subscription (the Azure Bot resource below uses the free F0 tier) and the
  [Azure CLI](https://learn.microsoft.com/cli/azure/install-azure-cli).
- .NET 10 SDK, the [devtunnel CLI](https://learn.microsoft.com/azure/developer/dev-tunnels/get-started),
  and an LLM API key (OpenAI shown; Azure OpenAI, OpenRouter, Grok, and Gemini also work).

**1. Create the server project with a chat agent**

```powershell
dotnet new web -n CopilotQuickstart
cd CopilotQuickstart
dotnet add package FabrCore.Host
dotnet add package FabrCore.Services.Microsoft365Copilot
```

`MyChatAgent.cs`:

```csharp
using System.ComponentModel;
using FabrCore.Core;
using FabrCore.Sdk;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;

[AgentAlias("chat-agent")]
[Description("General-purpose chat agent")]
public class MyChatAgent : FabrCoreAgentProxy
{
    private AIAgent? _agent;
    private AgentSession? _session;

    public MyChatAgent(AgentConfiguration config, IServiceProvider serviceProvider, IFabrCoreAgentHost fabrcoreAgentHost)
        : base(config, serviceProvider, fabrcoreAgentHost) { }

    public override async Task OnInitialize()
    {
        var tools = await ResolveConfiguredToolsAsync();
        var result = await CreateChatClientAgent("default",
            threadId: config.Handle ?? fabrcoreAgentHost.GetHandle(), tools: tools);
        _agent = result.Agent;
        _session = result.Session;
    }

    public override async Task<AgentMessage> OnMessage(AgentMessage message)
    {
        var response = message.Response();
        await foreach (var update in _agent!.RunStreamingAsync(
            new ChatMessage(ChatRole.User, message.Message), _session!))
        {
            response.Message += update.Text;
        }
        return response;
    }
}
```

`Program.cs`:

```csharp
using FabrCore.Host;
using FabrCore.Services.Microsoft365Copilot;

var builder = WebApplication.CreateBuilder(args);
builder.AddFabrCoreServer(new FabrCoreServerOptions
{
    AdditionalAssemblies = [typeof(MyChatAgent).Assembly]
});
builder.AddMicrosoft365Copilot();

var app = builder.Build();
app.UseFabrCoreServer();
app.UseMicrosoft365Copilot();
app.Run();
```

**2. Register the bot identity in Entra**

```bash
az login
APP_ID=$(az ad app create --display-name "My FabrCore Agent" --sign-in-audience AzureADMyOrg --query appId -o tsv)
SECRET=$(az ad app credential reset --id $APP_ID --query password -o tsv)
TENANT_ID=$(az account show --query tenantId -o tsv)
```

**3. Start a dev tunnel** (so Azure Bot Service can reach your machine):

```bash
devtunnel user login
devtunnel host -p 5000 --allow-anonymous
```

Note the `https://<tunnel-host>` URL it prints.

**4. Create the Azure Bot and enable the Teams channel** (Teams also carries Copilot traffic):

```bash
az group create -n my-agents-rg -l eastus
az bot create -g my-agents-rg -n my-fabrcore-agent --app-type SingleTenant \
  --appid $APP_ID --tenant-id $TENANT_ID \
  --endpoint "https://<tunnel-host>/api/messages" --sku F0
az bot msteams create -g my-agents-rg -n my-fabrcore-agent
```

**5. Create `fabrcore.json`** in the project root (LLM + Copilot config in one file):

```json
{
  "ModelConfigurations": [
    { "Name": "default", "Provider": "OpenAI", "Model": "gpt-4o-mini", "ApiKeyAlias": "openai" }
  ],
  "ApiKeys": [
    { "Alias": "openai", "Value": "sk-..." }
  ],
  "Microsoft365Copilot": {
    "TenantId": "<TENANT_ID>",
    "ClientId": "<APP_ID>",
    "ClientSecret": "<SECRET>",
    "Agent": { "AgentType": "chat-agent", "SystemPrompt": "You are a helpful assistant." },
    "Manifest": {
      "Name": "My FabrCore Agent",
      "Description": "A FabrCore agent in Microsoft 365 Copilot.",
      "PublicHostName": "<tunnel-host>"
    }
  }
}
```

**6. Run, and download the generated app package:**

```bash
dotnet run --urls http://localhost:5000
curl -o appPackage.zip http://localhost:5000/m365copilot/appPackage.zip
```

**7. Upload the app:** Teams → *Apps → Manage your apps → Upload an app → Upload a custom app* →
pick `appPackage.zip`. (Org-wide instead: Microsoft 365 admin center → *Settings → Integrated
apps → Upload custom apps*.)

**8. Chat.** Open Microsoft 365 Copilot — the Copilot app in Teams, or
<https://m365.cloud.microsoft/chat> — and pick **My FabrCore Agent** under *Agents* in the side
rail. You're now chatting with your FabrCore agent: each user gets their own agent instance,
replies stream in with the AI-generated label, and the same agent also answers 1:1 chat in Teams.

If something doesn't click:

- **Agent not in the list** — allow a few minutes after upload; confirm your user has the Copilot
  license and the app shows under *Manage your apps* in Teams.
- **No reply** — check the server console: the bot's messaging endpoint must exactly match the
  tunnel URL + `/api/messages`, and `TenantId`/`ClientId`/`ClientSecret` must match step 2.
- **401s in the log** — token validation is on (good) but the `ClientId`/`TenantId` don't match
  the tokens Azure Bot Service is sending.

## Adding the addon to an existing FabrCore server

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
configuration. The manifest alone is also served at `/m365copilot/manifest.json` and, addressed by
app name, at `/manifests/{name}.json` — `{name}` is the URL slug of `Manifest:Name` (for the
config above: `/manifests/my-fabrcore-agent.json`; the app id works too). The startup log prints
the exact URL. Upload it in Teams (*Apps → Manage your apps → Upload a custom app*) or the
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

## Proactive messages (opt in)

Agents can send messages between user turns through FabrCore's generic durable principal-delivery
pipeline. Microsoft 365 is one relay provider; agents remain channel-neutral:

```csharp
await SendToUserAsync("Your report is ready.");
```

Enable endpoint capture and proactive delivery explicitly:

```json
"Microsoft365Copilot": {
  "Proactive": {
    "Enabled": true,
    "AllowedConversationTypes": [ "personal" ]
  }
}
```

On every eligible inbound turn, the addon stores a versioned conversation endpoint under
`m365copilot:proactive:endpoints:v1` and stamps its stable id into
`AgentMessage.Args["Microsoft365Copilot:DeliveryEndpointId"]`. It retains up to eight endpoints per
principal. Personal scope is the safe default; group or channel conversation types must be added
deliberately.

Plain text and valid Adaptive Cards are supported. System messages, blank content, `ui.action`,
other `ui.*` payloads, and targets for another channel are not sent. The provider uses four bounded
worker shards (64 queued entries each), three attempts with two-second exponential backoff, and a
30-second send timeout. `429`, `5xx`, network, and timeout failures are retryable; permanent `4xx`
and mapping errors are dead-lettered. Proactive delivery is durable at-least-once, so a timeout or
crash after Microsoft accepted a request can result in a duplicate.

See [the generic relay authoring guide](../../docs/principal-message-relays.md) for the platform
contracts and a provider-SDK-free webhook sample.

## Adaptive Cards

FabrCore surface renders are delivered to Copilot/Teams as Adaptive Cards. A message is treated
as a card when:

- `MessageType` is `ui.render`, and
- `DataType` is `application/vnd.fabrcore.surface.adaptive-card+json`.

Both delivery styles agents use are supported:

- **Returned from `OnMessage`** — the reply itself is the render.
- **Sent to the principal** — the common surface pattern. Agents deliver `ui.render` messages to
  their principal for surface clients to observe; since no surface client exists on this channel,
  the addon subscribes a turn-scoped observer on the principal grain, captures renders emitted
  during the turn, and relays them. When proactive delivery is enabled, eligible messages queued
  between turns are promoted to the durable M365 outbox when an endpoint is refreshed. Renders of
  other data types and `_thinking`/`_status` updates are discarded.

The addon parses the UTF-8 JSON payload in `Data`, finds the `"type": "AdaptiveCard"` object
(at the root or nested inside a surface envelope), and attaches it — as a parsed JSON object,
never a re-encoded string — with the `application/vnd.microsoft.card.adaptive` content type. All
cards from a turn are combined onto one reply activity, with the agent's reply text (when set) as
the accompanying message. On streaming channels that activity is delivered as the final streamed
message; elsewhere as a regular reply. If a render's payload contains no parseable card, it is
skipped with a warning, and a turn with no cards at all falls back to plain text.

For Copilot compatibility, author cards against schema **1.5** and use `Action.Submit` for
interaction.

### Card submits (inbound)

Pressing `Action.Submit` sends a message activity whose `Value` holds the submit payload — the
action's `data` merged with the card's input values — and whose text is empty. The addon routes
it to the same FabrCore agent as a `ui.action` message, mirroring what surface clients send:

- `MessageType` is `ui.action`, `DataType` is
  `application/vnd.fabrcore.surface.adaptive-card+json`.
- `Data` carries a UTF-8 JSON action event shaped like the surface `AdaptiveCardActionEvent`:

  ```json
  {
    "version": "2.0",
    "kind": "ui.action",
    "actionType": "Action.Submit",
    "actionId": "approve",
    "verb": "approve",
    "routeTo": "agent",
    "envelopeId": null,
    "message": null,
    "payload": { "actionType": "Action.Submit", "verb": "approve", "comment": "..." }
  }
  ```

  `actionId` resolves from the payload's `actionId`, `id`, `verb`, or `title` (first non-empty,
  case-insensitive), falling back to `Action.Submit`; `verb` and `envelopeId` are lifted from the
  payload when present, and the full submit payload is flattened into `payload`.
- If the payload contains a `messageTemplate` string, `{token}` placeholders (dotted paths
  supported) are expanded against the payload and the result becomes `AgentMessage.Message`;
  otherwise `Message` is null — the receiving agent owns verb handling from `Data` and should
  respond gracefully to unknown verbs.
- Surface clients dispatch `ui.action` one-way; this channel needs something to show the user, so
  the addon sends the action as a **request** and relays the agent's reply — including `ui.render`
  card replies, so an agent can answer a submit with a fresh card.

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
| `Proactive:Enabled` | `false` | Enable stored-endpoint, out-of-turn delivery |
| `Proactive:AllowedConversationTypes` | `["personal"]` | Conversation scopes eligible for endpoint capture |
| `Proactive:MaxDeliveryAttempts` / `RetryBaseDelay` / `SendTimeout` | `3` / `2s` / `30s` | Provider retry policy |
| `Proactive:WorkerShards` / `OutboundQueueCapacity` | `4` / `64` | Bounded sender queues (capacity is per shard) |
| `Proactive:MaxStoredEndpoints` | `8` | Conversation endpoints retained per principal |
| `Principal:Strategy` / `Prefix` / `AllowChannelIdFallback` | `EntraObjectId` | User → principal mapping |
| `UserAuthorization:Handlers:*` | — | Agents SDK SSO handlers (verbatim passthrough) |
| `UserAuthorization:PassUserTokenToAgent` | `false` | Stamp the user's token on agent messages |
| `Manifest:*` | sensible defaults | App name, developer info, icons, conversation starters |
| `Manifest:EnableAppPackageEndpoint` | dev-only | Serve `/m365copilot/appPackage.zip` and `/manifests/{name}.json` |

**Escape hatch:** if you define the Agents SDK's native `Connections`, `ConnectionsMap`, or
`AgentApplication` sections yourself, the addon leaves them untouched and only supplies what's
missing — you can drop down to raw SDK configuration at any time.
