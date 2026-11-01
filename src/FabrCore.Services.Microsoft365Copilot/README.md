# FabrCore.Services.Microsoft365Copilot

For the same user to reach the same agent through Teams and A2A, use the shared
[`CanonicalEntra` identity and the same agent handle on both channels](../../docs/channel-agent-identity.md).
The app package targets manifest 1.25. A2A targets specification release 1.0.1.

A FabrCore server addon that makes your FabrCore agents available in **Microsoft 365 Copilot**
and **Microsoft Teams** as a [custom engine agent](https://learn.microsoft.com/en-us/microsoft-365/copilot/extensibility/overview-custom-engine-agent).

It hosts one Azure Bot Service messaging endpoint per exposed agent (`/api/messages` by
convention) with the
[Microsoft 365 Agents SDK](https://learn.microsoft.com/en-us/microsoft-365/agents-sdk/), validates
channel and Entra user identity, and bridges every Copilot conversation to a FabrCore agent —
without any changes to `FabrCore.Host` or your agents. One host can expose any number of agents;
see [Exposing several agents](#exposing-several-agents).

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

- A Microsoft 365 work account with the tenant permissions and licensing required for the
  selected agent experience. Confirm current requirements with your tenant administrator.
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
builder.AddFabrCoreServer();
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
    "Agents": [
      {
        "Name": "assistant",
        "Route": "/api/messages",
        "ClientId": "<APP_ID>",
        "ClientSecret": "<SECRET>",
        "AgentType": "chat-agent"
      }
    ],
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
curl -o appPackage.zip http://localhost:5000/m365copilot/assistant/appPackage.zip
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
  tunnel URL + the agent's `Route` (`/api/messages` here), and `TenantId` and the agent's
  `ClientId`/`ClientSecret` must match step 2.
- **401s in the log** — token validation is on (good) but the `ClientId`/`TenantId` don't match
  the tokens Azure Bot Service is sending.
- **403s in the log** — the token is valid but was issued for a different bot than the one that
  owns the route. Check the Azure Bot's messaging endpoint points at this agent's `Route`.

## Adding the addon to an existing FabrCore server

**1. Reference the addon from your FabrCore server project** and add two lines to `Program.cs`:

```csharp
using FabrCore.Host;
using FabrCore.Services.Microsoft365Copilot;

var builder = WebApplication.CreateBuilder(args);

builder.AddFabrCoreServer();
builder.AddMicrosoft365Copilot();          // ← the addon

var app = builder.Build();
app.UseFabrCoreServer();
app.UseMicrosoft365Copilot();              // ← maps one messaging route per agent
app.Run();
```

**2. Add a `Microsoft365Copilot` section to `fabrcore.json`** (or `appsettings.json` — both work):

```json
{
  "Microsoft365Copilot": {
    "TenantId": "<entra-tenant-id>",
    "Agents": [
      {
        "Name": "assistant",
        "Route": "/api/messages",
        "ClientId": "<bot-app-registration-client-id>",
        "ClientSecret": "<bot-app-registration-secret>",
        "AgentType": "chat-agent",
        "Models": "default"
      }
    ],
    "Manifest": {
      "Name": "My FabrCore Agent",
      "Description": "Answers questions using my FabrCore agents.",
      "PublicHostName": "myagents.contoso.com"
    }
  }
}
```

Each entry of `Agents` is one agent in the Copilot agent list. `AgentType` is any discovered
`[AgentAlias]`. The application assembly and referenced
FabrCore project/package dependencies are discovered automatically; `AdditionalAssemblies` is
only needed for an assembly outside the application dependency graph. Each Microsoft 365 user automatically gets their own
agent instance (principal handle = their Entra object id), with isolated chat history and state.

The entry names the agent and nothing more. Plugins, tools, the system prompt, and args are not
set here: define them on the agent type or in the blueprint that provisions the agent. An agent
that already exists for the user under the entry's `Handle` is used as it is; a missing one is
created from `AgentType` and `Models` alone.

**3. Create the Azure resources** (once per agent — see [Azure setup](#azure-setup) below).

**4. Upload the app package.** Run the server in Development and download
`https://localhost:<port>/m365copilot/{agent}/appPackage.zip`, where `{agent}` is the entry's
`Name` (`assistant` above) — a ready-to-upload zip generated from your
configuration. The manifest alone is also served at `/m365copilot/{agent}/manifest.json` and,
addressed by app name, at `/manifests/{name}.json` — `{name}` is the URL slug of the agent's
`Manifest:Name` (for the config above: `/manifests/my-fabrcore-agent.json`; the app id works
too), searched across all agents. The startup log prints
the route templates. Upload it in Teams (*Apps → Manage your apps → Upload a custom app*) or the
[Microsoft 365 admin center](https://admin.microsoft.com) (*Settings → Integrated apps → Upload
custom apps*) for the whole org. The agent then appears in Teams chat and in the Microsoft 365
Copilot side panel.

## Exposing several agents

Microsoft allows at most one bot, and one custom engine agent, per app manifest, and an Azure Bot
resource has one messaging endpoint and one app id. So every agent you want listed in Microsoft 365
Copilot needs its own Entra app registration, its own Azure Bot, and its own app package. One
FabrCore host serves them all: add an entry to `Agents` for each.

```json
"Microsoft365Copilot": {
  "TenantId": "<entra-tenant-id>",
  "AuthType": "ClientSecret",
  "Manifest": { "DeveloperName": "Contoso", "PublicHostName": "agents.contoso.com" },
  "Agents": [
    {
      "Name": "assistant",
      "Route": "/api/messages",
      "ClientId": "<first-bot-client-id>",
      "ClientSecret": "<secret>",
      "AgentType": "chat-agent",
      "Handle": "assistant",
      "Models": "default",
      "Manifest": { "Name": "Assistant", "Description": "General-purpose assistant." }
    },
    {
      "Name": "sales",
      "ClientId": "<second-bot-client-id>",
      "ClientSecret": "<secret>",
      "AgentType": "sales-agent"
    }
  ]
}
```

- **Routes.** Each entry gets one `POST` route. `Route` defaults to `/api/messages/{Name}`
  (`/api/messages/sales` above); one entry may claim the bare `/api/messages`. A route is a fixed
  path, not a template. Point each Azure Bot's messaging endpoint at its own agent's route.
- **Defaults.** `TenantId`, `AuthType`, the certificate and federation settings,
  `AuthorityEndpoint`, `WelcomeMessage`, and `Manifest` at the top level are defaults for every
  entry; an entry overrides any of them. `Handle` defaults to the entry's `Name`. `Manifest:Name`
  defaults to the top-level name when there is one agent and to the entry's `Name` when there are
  several. `Manifest:Id` defaults to the agent's `ClientId`.
- **Uniqueness.** Names, routes, client ids, and manifest names must each be unique. A violation
  fails startup with a message naming the setting, for example
  `Microsoft365Copilot:Agents:1:Route`.
- **Separation.** The channel accepts a token issued for any configured bot, and each route then
  requires the token's audience to be that entry's `ClientId` (or one of the host-wide
  `TokenValidation:Audiences`). A token for one bot presented on another agent's route gets `403`.
  Outbound, each bot has its own Agents SDK connection (`ServiceConnection` for the first entry,
  `ServiceConnection-{Name}` for the others), selected by audience, so every agent replies under
  its own credential.
- **Identity.** The principal still comes from the inbound activity (see
  [How users map to FabrCore principals](#how-users-map-to-fabrcore-principals)); the agent handle
  is fixed per entry. Each bridged message carries the entry name in
  `Args["Microsoft365Copilot:Agent"]`.
- **No agents.** An enabled channel with an empty `Agents` list is valid. No messaging endpoint
  is mapped and a warning is logged.

Two limits apply when a host exposes more than one agent:

- **Sign-in is shared.** User authorization handlers are configured once for the host. An Azure
  Bot OAuth connection belongs to one bot registration, so sign-in works only for the agent whose
  bot owns that connection. The host logs a warning at startup when this applies.
- **Proactive endpoints are stored per principal, not per agent.** See
  [Proactive messages](#proactive-messages-opt-in).

### Migrating from the single-agent configuration

Earlier versions exposed one agent through `Microsoft365Copilot:Agent`, `MessagesEndpoint`, and a
top-level `ClientId` and `ClientSecret`. With the channel enabled and no `Agents` section, the
presence of any of those four keys now fails startup with a message pointing at
`Microsoft365Copilot:Agents`, so a host does not keep answering without the plugins and tools it
used to configure here.

When an `Agents` list is present, the four keys are ignored and a startup warning names them. This
is so one configuration can serve old and new hosts while a cluster is upgraded: an older host
reads the single-agent keys and ignores `Agents`, a newer one does the reverse. Remove the old
keys once no older host shares the configuration.

| Before | Now |
|---|---|
| `ClientId`, `ClientSecret` | `Agents:{n}:ClientId`, `Agents:{n}:ClientSecret` |
| `MessagesEndpoint` | `Agents:{n}:Route` |
| `Agent:AgentType`, `Agent:Handle`, `Agent:Models` | `Agents:{n}:AgentType`, `Handle`, `Models`. `Handle` now defaults to the entry's `Name`, not `copilot`; set `"Handle": "copilot"` to keep reaching existing per-user agents |
| `Agent:SystemPrompt`, `Plugins`, `Tools`, `Args` | Removed. Define them on the agent type or in a blueprint |
| `Agent:Binding` | Removed. The Copilot channel no longer reads `AgentBindings` (A2A still does) |
| `Agent:SharedAgentHandle`, `Agent:AgentPerConversation` | Removed |

## Local development — no Azure required

Test the full bridge locally with the
[Microsoft 365 Agents Playground](https://learn.microsoft.com/en-us/microsoft-365/agents-sdk/test-with-toolkit-project):

```json
{
  "Microsoft365Copilot": {
    "TokenValidation": { "Enabled": false },
    "Agents": [ { "Name": "assistant", "Route": "/api/messages", "AgentType": "chat-agent" } ]
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

One Entra app registration + one Azure Bot resource per agent, pointed at that agent's public
route (`/api/messages` for the entry that claims it, `/api/messages/{Name}` otherwise). Repeat
these steps for each entry in `Agents`:

```bash
# 1. App registration (SingleTenant shown; also supported: MultiTenant, managed identity, federated credentials)
az ad app create --display-name "MyFabrCoreAgent" --sign-in-audience AzureADMyOrg
APP_ID=<appId from output>
az ad app credential reset --id $APP_ID    # → the agent's ClientSecret for fabrcore.json

# 2. Azure Bot resource wired to your endpoint
az bot create --resource-group <rg> --name <bot-name> --app-type SingleTenant \
  --appid $APP_ID --tenant-id <tenant-id> \
  --endpoint "https://<your-public-host>/api/messages" --sku S1

# 3. Enable the Teams channel (this also carries Microsoft 365 Copilot traffic)
az bot msteams create --name <bot-name> --resource-group <rg>
```

Your FabrCore host can run **anywhere** — Azure, on-prem, another cloud — it only needs a public
HTTPS route to each agent's messaging route.

Supported outbound auth types (`Microsoft365Copilot:AuthType`, or `AuthType` on an `Agents`
entry): `ClientSecret`, `Certificate`,
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

Handlers are configured once and shared by every agent. With several agents, sign-in works only
for the agent whose bot owns the Azure Bot OAuth connection. `ServiceConnection` is the
connection of the first entry in `Agents`; the others are named `ServiceConnection-{Name}`.

## How users map to FabrCore principals

| `Principal:Strategy`  | Principal handle                            | Use when |
|-----------------------|---------------------------------------------|----------|
| `EntraObjectId` (default) | Entra object id (GUID)                  | Single-tenant bots |
| `TenantAndObjectId`   | `{tenantId}-{objectId}`                     | Multi-tenant bots |
| `UserPrincipalName`   | UPN from the SSO token (falls back to oid)  | Human-readable handles; needs user authorization |
| `ChannelUserId`       | `{channelId}-{channelUserId}`               | Dev/test channels only |

`Principal:Prefix` (e.g. `"m365-"`) namespaces the handles. Handles are sanitized (lowercased,
`:` and other separators stripped) before use. Every bridged message also carries the raw identity
in `Args` (`Microsoft365Copilot:AadObjectId`, `:TenantId`, `:UserName`, `:ConversationId`, ...),
along with `Microsoft365Copilot:Agent`, the name of the `Agents` entry the message arrived through.

To customize mapping entirely, register your own resolver **before** `AddMicrosoft365Copilot()`:

```csharp
builder.Services.AddSingleton<ICopilotPrincipalResolver, MyDirectoryBackedResolver>();
```

Agents are always per user: the principal comes from the inbound activity and the handle from the
`Agents` entry. Routing every user to one shared agent (`SharedAgentHandle`) has been removed.

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
deliberately. Endpoints are stored per principal, not per agent, so on a host that exposes several
agents a user's stored endpoints are not separated by agent.

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

## Cloud management and administration

The addon is built to be switched on, inspected, and diagnosed by a management console without a
shell on the host. A FabrCore cloud server uses the same routes over the outbound administration
channel; see [the protocol](../../docs/cloud-server-protocol.md#managing-microsoft-365-copilot-and-a2a-integrations).

**It starts off when it has nothing to go on.** Calling `AddMicrosoft365Copilot()` on a host with
no `Microsoft365Copilot` section and no code delegate no longer fails startup. The channel starts
off and reports why, so one host image can be enabled later by configuration alone. Supplying a
section or a delegate is a decision to run the channel, and an incomplete one still fails loudly.

**It advertises itself**, on or off, through `GET /fabrcoreapi/capabilities` as the service
`microsoft365-copilot` (with `available` and `unavailableReason`) and through the cloud heartbeat
flags `m365copilot`, `m365copilot.enabled`, and `m365copilot.agents` (the number of exposed
agents). The capability's `apiVersion` is `2` and its features include `multi-agent`.

**It reports what is applied.** The configuration-state report (`GET
/fabrcoreapi/admin/v1/settings/state`) carries the channel's effective `Enabled`, `TenantId`,
`AuthType`, `TokenValidation:Enabled`, `Principal:Strategy`, `Proactive:Enabled`,
`Streaming:Enabled`, and `Manifest:PublicHostName`; for each agent, `Agents:{i}:Name`, `Route`,
`ClientId`, `AgentType`, and `Handle` with defaults applied; plus two
facts: `Runtime:Microsoft365Copilot:SingleSignOn` and
`Runtime:Microsoft365Copilot:ForwardsUserCredential`. While the channel is off, only `Enabled` is
reported as applied, and there are no agent rows.

**It answers administration routes**, mapped by `UseMicrosoft365Copilot()` whether or not the
channel is on. All require the host administration credential.

| Route under `/fabrcoreapi/admin/v1/integrations/microsoft365` | Returns |
|---|---|
| `GET` (the root) | Configuration, the `agents` list, and posture findings. `apiVersion` is `2` |
| `GET /agents/{name}/manifest` | The generated manifest of one agent; `404` `agent-not-found` for an unknown name, `409` while the channel is off or the agent has no `ClientId` |
| `GET /agents/{name}/app-package` | The uploadable zip of one agent; same `404` and `409` |
| `POST /diagnostics` | The findings, whether every agent type is registered, and whether the host can obtain an Azure Bot Service token for every bot |

The status document lists each exposed agent under `agents` with `name`, `messagesEndpoint`,
`clientId`, `tenantId`, `authType`, `agentType`, `agentHandle`, `manifestId`, `manifestName`, and
`manifestVersion`. `{name}` in the package routes is the entry's `Name`.

```bash
curl -H "Authorization: Bearer $FABRCORE_ADMIN_KEY" \
  https://<host>/fabrcoreapi/admin/v1/integrations/microsoft365
curl -X POST -H "Authorization: Bearer $FABRCORE_ADMIN_KEY" \
  https://<host>/fabrcoreapi/admin/v1/integrations/microsoft365/diagnostics
curl -H "Authorization: Bearer $FABRCORE_ADMIN_KEY" -o appPackage.zip \
  https://<host>/fabrcoreapi/admin/v1/integrations/microsoft365/agents/assistant/app-package
```

Findings are `pass`, `warn`, `fail`, or `skipped`:

| Finding | `fail` or `warn` when |
|---|---|
| `token-validation` | The messaging endpoints accept anonymous requests |
| `principal-strategy` | Users are not mapped with `CanonicalEntra`, or channel-id fallback is allowed |
| `credential-type` | Any agent's bot authenticates with a client secret; the message names them |
| `turn-state-storage` | Sign-in and turn state are in process memory |
| `user-credential-forwarding` | `PassUserTokenToAgent` copies the user's token onto agent messages |
| `public-host` | `Manifest:PublicHostName` is not set |
| `proactive-scopes` | Proactive delivery is allowed outside personal conversations |

Diagnostics never returns the token it acquires. It waits at most 20 seconds, discards the token,
and on failure reports the exception type and the first line of its message. The
`agent-type-registered` and `bot-service-credential` checks cover every agent and name the one
that fails. A passing run shows
the host can authenticate to Azure Bot Service. It does not show that Azure Bot Service can reach
your messaging endpoints.

The administration `agents/{name}/app-package` route is the way to fetch a package in production.
The `/m365copilot` developer endpoints are anonymous and stay off outside Development.

## Production checklist

- **Durable Agents SDK storage** — sign-in and turn state default to `MemoryStorage`. When running
  multiple instances or using SSO, register a durable `IStorage`
  (e.g. `Microsoft.Agents.Storage.Blobs`) *before* `AddMicrosoft365Copilot()`:
  `builder.Services.AddSingleton<IStorage>(new BlobsStorage(...));`
- **Secrets** — keep each agent's `ClientSecret` out of source control (`fabrcore.json` is git-ignored in this
  repo; better: user-secrets, Key Vault, or a secret-free `AuthType`).
- **Token validation on** — `TokenValidation:Enabled` must be `true` (default) anywhere reachable
  from the internet.
- **Orleans persistence** — use SQL/Azure clustering so per-user agents survive restarts
  (see the fabrcore-server skill).
- **Icons** — replace the generated placeholder icons via `Manifest:ColorIconPath` /
  `Manifest:OutlineIconPath` before publishing. Both can be set per agent in
  `Agents:{n}:Manifest`. A management console may instead replace the icons in the zip it
  downloads.

## Configuration reference (`Microsoft365Copilot` section)

| Key | Default | Purpose |
|-----|---------|---------|
| `Enabled` | `true`; `false` when the host has no `Microsoft365Copilot` configuration at all | Master switch for the channel. Off still leaves the administration routes and capability reporting in place. |
| `TenantId` | — | Entra tenant of the bot app registrations. Default for every agent |
| `AuthType` | `ClientSecret` | Outbound auth: secret, certificate, MSI, federated. Default for every agent |
| `CertificateThumbprint` / `CertificateSubjectName` / `CertificateStoreName` / `FederatedClientId` / `FederatedTokenFile` / `AuthorityEndpoint` | — | Inputs for the chosen `AuthType`. Defaults for every agent |
| `WelcomeMessage` | greeting | Sent when a user joins; empty string disables. Default for every agent |
| `ErrorMessage` | generic | Shown when the FabrCore agent fails |
| `TokenValidation:Enabled` | `true` | Inbound channel JWT validation (off = local dev only) |
| `TokenValidation:Audiences` / `ValidIssuers` | — / built-ins | Audiences accepted on every route in addition to each agent's own `ClientId`; extra issuers |
| `Agents` | empty | The agents to expose, zero or more. One entry per Azure Bot registration |
| `Agents:{n}:Name` | — | **Required.** Letters, digits, `-`, `_`. Used in the default route, the package routes, and the administration API |
| `Agents:{n}:Route` | `/api/messages/{Name}` | Fixed path Azure Bot Service posts to. One entry may claim `/api/messages` |
| `Agents:{n}:ClientId` / `ClientSecret` | — | This agent's bot app registration. `ClientId` is required when token validation is on |
| `Agents:{n}:TenantId` / `AuthType` / certificate and federation settings / `AuthorityEndpoint` | top-level value | Per-agent overrides |
| `Agents:{n}:AgentType` | — | **Required.** FabrCore `[AgentAlias]` that answers on this route |
| `Agents:{n}:Handle` | the entry's `Name` | Handle of the agent under each user's principal. Bare, no `:` |
| `Agents:{n}:Models` | `default` | Model configuration used when the agent has to be created |
| `Agents:{n}:WelcomeMessage` | top-level value | Greeting for this agent; empty string disables it |
| `Agents:{n}:Manifest:*` | top-level `Manifest` | Per-agent manifest overrides. `Name` defaults to the top-level name with one agent and to the entry's `Name` with several; `Id` defaults to the agent's `ClientId` |
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
| `Manifest:*` | sensible defaults | App name, developer info, icons, conversation starters. Defaults for every agent |
| `Manifest:EnableAppPackageEndpoint` | dev-only | Serve `/m365copilot/{agent}/manifest.json`, `/m365copilot/{agent}/appPackage.zip`, and `/manifests/{name}.json` |

The single-agent keys `Agent`, `MessagesEndpoint`, `ClientId`, and `ClientSecret` directly under
`Microsoft365Copilot` are no longer read. Without an `Agents` section they fail startup; beside
one they are ignored with a warning. See
[Migrating from the single-agent configuration](#migrating-from-the-single-agent-configuration).

**Escape hatch:** if you define the Agents SDK's native `Connections`, `ConnectionsMap`, or
`AgentApplication` sections yourself, the addon leaves them untouched and only supplies what's
missing — you can drop down to raw SDK configuration at any time.
