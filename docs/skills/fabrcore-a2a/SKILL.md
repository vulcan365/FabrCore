---
name: fabrcore-a2a
description: >
  Publish FabrCore agents over the open Agent2Agent (A2A) protocol, built into FabrCore.Host, and
  connect them to Microsoft 365 Copilot Studio as A2A agents. Covers the A2A:Enabled feature flag,
  A2AOptions, FabrCoreServerOptions.ConfigureA2A, registry-driven exposure with A2A:Discovery
  (Described/All modes, include/exclude globs, [FabrCoreHidden], [FabrCoreNote] on cards, live
  agent handle globs, stored FabrCore harness skills advertised from _HarnessSkills) and
  A2A:Defaults, selecting which agent types or agent handles are exposed, agent cards on
  /.well-known/agent-card.json and /.well-known/agent.json, the JSON-RPC and HTTP+JSON bindings,
  message/send, message/stream, SSE streaming, tasks/get, tasks/cancel, task lifecycle, API key and
  OAuth 2.0 (JwtBearer) authentication, principal mapping, reverse-proxy exposure, and Copilot
  Studio's JSON-RPC-on-REST-route wire shape. Use for A2A,
  agent2agent, agent card, A2AOptions, "Add agent > A2A agent", connected agents, Copilot Studio
  Code/CoWork, message:stream, IA2APrincipalResolver, IA2ATaskStore, or A2A:Interop. Use
  fabrcore-microsoft365copilot for the Copilot/Teams user-chat channel, fabrcore-server for general
  hosting, fabrcore-agent for agent code, and fabrcore-acl for cross-principal grants.
allowed-tools: "Bash(dotnet:*) Bash(mkdir:*) Bash(ls:*) Bash(pwsh:*) Bash(powershell:*) Bash(git:*) Bash(dir:*) Bash(curl:*) Bash(devtunnel:*)"
metadata:
  author: FabrCore
  # The FabrCore.Host line this skill describes. A copy whose value is older than the package you
  # reference is stale: the skill is what an agent acts on, so check this before following it.
  appliesTo: FabrCore.Host 1.7.3+
---

# FabrCore ⇄ Agent2Agent (A2A)

`FabrCore.Host` publishes a server's agents over the open
[A2A protocol](https://a2a-protocol.org). Microsoft 365 Copilot Studio uses A2A to add external
agents to its multi-agent orchestration, which is how a FabrCore agent joins Copilot's Code and
CoWork experiences. Every FabrCore server has it; the agents need no changes.

```
Copilot Studio / any A2A client
        │  GET  /a2a/support/.well-known/agent-card.json     ← discovery
        │  POST /a2a/support                                 ← JSON-RPC binding
        │  POST /a2a/support/v1/message:stream               ← HTTP+JSON binding (SSE)
        ▼
FabrCore.Host A2A ──── authenticate ──► map to a FabrCore principal ──► ensure agent
        │                                                                      │
   A2A task lifecycle  ◄──────────── reply ──────────── your [AgentAlias] agent on the silo
```

Full reference: `docs/a2a.md`.

## Turning it on

A2A is part of `FabrCore.Host`, so every FabrCore server already has it. There is no package to
add and no code to write — `AddFabrCoreServer` registers the services and `UseFabrCoreServer` maps
the routes, both gated on `A2A:Enabled`:

```csharp
using FabrCore.Host;

builder.AddFabrCoreServer();

var app = builder.Build();
app.UseFabrCoreServer();
app.Run();
```

With `A2A:Enabled` unset or false — the default — nothing is registered and no route is mapped, so
a server that does not want A2A carries no A2A surface at all. Publishing agents to external
orchestrators is a configuration change, not a dependency change.

For settings you would rather keep in code than in configuration, use `ConfigureA2A`. It runs after
the `A2A` section is bound, so configuration supplies the deployment-specific values and code
supplies the rest:

```csharp
using FabrCore.Host;
using FabrCore.Host.Configuration;   // A2AOptions, A2ADiscoveryMode

builder.AddFabrCoreServer(new FabrCoreServerOptions()
    .ConfigureA2A(a2a =>
    {
        a2a.Enabled = true;
        a2a.Discovery.AgentTypes = A2ADiscoveryMode.Described;
    }));
```

Settings bind from the `A2A` section of `fabrcore.json` (pulled into configuration automatically
when the host has not loaded it) or `appsettings.json`. **`A2A:Enabled` defaults to `false`**,
because A2A endpoints are meant to be publicly reachable.

Types live in `FabrCore.Host.A2A` (`IA2APrincipalResolver`, `IA2ATaskStore`, `A2ADefaults`) and
`FabrCore.Host.Configuration` (`A2AOptions` and friends).

## Minimum configuration

```json
{
  "A2A": {
    "Enabled": true,
    "PublicBaseUrl": "https://agents.contoso.com",
    "Discovery": { "AgentTypes": "Described" },
    "Authentication": {
      "Mode": "ApiKey",
      "ApiKey": { "Keys": [ { "Name": "copilot-studio", "Value": "a-long-random-secret" } ] }
    }
  }
}
```

That is the whole section. Every registered agent type with a `[Description]` is published at
`/a2a/{alias}` with its own card — no agent is named, and none needs adding as the fleet grows.
Publish exactly one agent and its card is also served from `/.well-known/agent-card.json`.

That suits a host whose agents are all meant to be callable. If yours are not, set
`"AgentTypes": "None"` and name the agents you mean to publish under `Agents` — see
**Publishing agents without listing them** for why that is a decision worth writing down.

Assets: `assets/fabrcore-json-a2a.json` (production — registry discovery, shared defaults, one
curated agent, API key auth), `assets/fabrcore-json-a2a-oauth.json` (OAuth 2.0),
`assets/server-program.cs` (host wiring).

**Always set `PublicBaseUrl` in production.** An agent card advertises the URL clients must call;
behind a reverse proxy that is not the URL the request arrived on. Leaving it unset derives the URL
from the request, which suits local development and dev tunnels only.

## Publishing agents without listing them

The host already knows its agents: the registry carries every `[AgentAlias]` type with its
`[Description]`, `[FabrCoreCapabilities]`, and `[FabrCoreNote]`, and the cluster knows which agents
are actually running. `A2A:Discovery` reads both, so the configuration says *which* agents to
publish rather than restating them. `"Discovery": { "AgentTypes": "Described" }` — the one line in
the minimum configuration above — publishes every registered agent type that has a `[Description]`,
each with its own agent card, route, and skills, and stays correct as agents are added and
removed.

### `Discovery:AgentTypes`

| Mode | Publishes |
|------|-----------|
| `None` (default) | Nothing from the registry; only explicit configuration applies |
| `Described` | Every registered agent type carrying a `[Description]` |
| `All` | Every registered agent type, described or not |

`Described` suits a host whose agents are all meant to be callable. **It makes `[Description]` a
publication opt-in**, so on a mixed fleet every described agent goes out behind the same
credential, and adding a `[Description]` to a new agent later publishes it silently. A host with
agents that must not be reachable — one holding a named person's mailbox or a customer's directory —
should choose deliberately: name agents in `Agents` and write `"AgentTypes": "None"` out rather than
leaving it at its default, so the decision is visible in the file and a reviewer can see it was made.

`All` publishes undescribed agents with a generated placeholder description, which orchestrators
route on poorly.

**`[FabrCoreHidden]` is honored automatically.** Discovery reads the same registry call that backs
`/fabrcoreapi/discovery`, and that call already drops hidden types — so hiding an agent from
discovery hides it from A2A, with no second switch to remember.

Narrow the set with globs. An exclude always beats an include:

```json
"Discovery": {
  "AgentTypes": "All",
  "IncludeAgentTypes": [ "*-agent" ],
  "ExcludeAgentTypes": [ "internal-*", "*-worker" ]
}
```

### What lands on the card

| Registry metadata | Agent card |
|---|---|
| `[Description]` | The card's `description` — what an orchestrator matches against |
| `[FabrCoreCapabilities]` | Comma-separated values become the skill's `tags` |
| `[FabrCoreNote]` | Become skill `examples` (set `Discovery:IncludeNotes` to `false` to leave them off) |
| `[AgentAlias]` | The route name and skill id |

Notes are worth carrying: they usually say *when not* to use an agent, which is exactly what an
orchestrator needs to avoid mis-routing.

### Stored harness skills on the card

Two different things share the word *skill*:

| | FabrCore **harness skill** | A2A **skill** |
|---|---|---|
| What it is | A versioned, principal-scoped package of instructions and resources an agent loads at runtime | A line of agent-card metadata |
| Where it lives | The FabrCore API, under `/fabrcoreapi/admin/v1/principals/{principal}/skills` | The agent card |
| What it does | Changes what the agent can actually do | Tells a remote orchestrator when to call the agent |

They line up well, so an agent that loads harness skills advertises them. Give the agent the
`_HarnessSkills` arg and each `name@version` is resolved against the principal's stored catalog and
added to the card as its own skill, tagged `harness-skill`, carrying the published description:

```json
"Defaults": {
  "Args": { "_HarnessSkills": "order-lookup@1.2.0,returns-policy@2.0.0" }
}
```

```json
"skills": [
  { "id": "support", "name": "Support", "description": "Answers Contoso questions.", "tags": [ "fabrcore" ] },
  { "id": "order-lookup", "name": "order-lookup",
    "description": "Looks up Contoso order status from an order number.",
    "tags": [ "fabrcore", "harness-skill", "order-lookup" ] }
]
```

A reference the principal has not published is left off the card and logged — the card never claims
a capability the agent does not have. Exact versions are honored, so a stored `order-lookup@9.9.9`
does not satisfy a declared `order-lookup@1.2.0`.

**Publish the skill to the principal the agent runs as.** Harness skills are principal-scoped, and
an A2A agent runs as the principal `A2A:Principal` resolves to — by default `a2a`, not the principal
you published from. Agents published through `AgentHandles` run as the principal in their handle.

Because a card is served to every caller alike, usually before anyone has authenticated, skills are
resolved only where the principal does not depend on the caller: agents published by handle, and
provisioned agents under `Principal:Strategy = Fixed`. Under `ContextId`, `ApiKey`, or `Claim` the
catalog differs per caller, so cards omit harness skills rather than claim ones they cannot
attribute. Set `Discovery:IncludeHarnessSkills` to `false` to leave them off everywhere.

Catalog reads are cached for `Discovery:RefreshInterval` and happen only for agents that declare
harness skills. If the skill store is unreachable the card still serves, without them.

### Publishing live agents by handle

`Discovery:IncludeAgentHandles` matches globs against the keys of agents actually running in the
cluster (`principal:handle`), and publishes each one as-is — no provisioning, no per-agent config:

```json
"Discovery": {
  "IncludeAgentHandles": [ "system:*" ],
  "ExcludeAgentHandles": [ "system:*-internal" ]
}
```

Unlike agent-type discovery this reads cluster state, so it is refreshed every
`Discovery:RefreshInterval` (default 30 seconds) and **agents created after startup become
reachable without a restart** — routes are parameterized, not mapped one per agent. A server with
no handle globs configured makes no cluster calls at all.

Cross-principal delivery still needs an `agent.message.allow` ACL grant for the mapped principals;
see **fabrcore-acl**.

### Shared settings

`A2A:Defaults` supplies the settings every published agent would otherwise repeat — model
configuration, system prompt, plugins, tools, args, streaming, input/output modes. Discovered
agents take them wholesale; a per-agent entry overrides only what it states.

```json
"Defaults": {
  "Models": "gpt-4o",
  "Plugins": [ "orders-plugin" ],
  "AgentPerContext": true
}
```

### Precedence

`A2A:Agents` → `A2A:AgentTypes` / `A2A:AgentHandles` → registry discovery → live-agent discovery.
The first source to claim a route name keeps it, so curating one agent's card is a matter of adding
an `Agents` entry for it while discovery handles the rest. A live agent whose bare handle is
already taken is republished under its fully-qualified name (`system-assistant`) rather than
dropped — it matched your glob, so it stays reachable.

Check the result at `GET /a2a`, which reports each agent's `source` (`Configured`, `Registry`, or
`LiveAgent`).

## Selecting agents explicitly

Discovery covers the common case. Name agents directly to curate a card, use a different route
name, or give one agent settings the rest of the fleet does not get.

| Setting | Behavior |
| --- | --- |
| `AgentTypes: [ "chat-agent" ]` | Publish a registered `[AgentAlias]`; each caller gets an instance provisioned on first contact |
| `AgentHandles: [ "system:assistant" ]` | Publish an agent that already exists; routes straight to it, provisions nothing |
| `Agents: [ { … } ]` | Full control over route name, card metadata, skills, and provisioning |

These merge with each other and with discovery; the first source to claim a route name keeps it.
`AgentHandles` entries must be fully qualified (`principal:handle`) and cross-principal delivery
needs an `agent.message.allow` ACL grant — see `fabrcore-acl`.

```json
"Agents": [
  {
    "Name": "support",
    "DisplayName": "Contoso Support",
    "Description": "Answers questions about Contoso orders, returns, and shipping.",
    "AgentType": "chat-agent",
    "SystemPrompt": "You are a Contoso support specialist.",
    "Plugins": [ "orders-plugin" ],
    "AgentPerContext": true,
    "Skills": [
      {
        "Name": "Order status",
        "Description": "Looks up the status of a Contoso order from its number.",
        "Tags": [ "orders", "shipping" ],
        "Examples": [ "Where is order 41288?" ]
      }
    ]
  }
]
```

`Description` and `Skills` are what an orchestrator reads to decide **when** to route work to the
agent. Copilot Studio takes them straight off the card. A vague description is the most common
reason a connected agent is never called. When no skills are configured one is synthesized from the
name, description, and the agent type's declared capabilities — routable, but worth replacing.

## Endpoints

For an agent named `support`:

| Method | Route | Purpose |
| --- | --- | --- |
| `GET` | `/a2a/support/.well-known/*` | Agent card, under all spellings clients probe: `agent-card.json`, `agent.json`, `agentcard.json`, `agent_card.json` |
| `GET` | `/a2a/support/v1/card` | Agent card (HTTP+JSON binding) |
| `GET` | `/a2a/support/v1/.well-known/*` | Same card, for a client configured with the `/v1` URL |
| `GET` | `/a2a/support/v1/message:stream/.well-known/*` | Same card, for a client configured with the *message* endpoint |
| `GET` | `/a2a/support/v1/message:send/.well-known/*` | Same card |
| `GET` | `/a2a/.well-known/*` | Primary agent's card, under the route prefix |
| `GET` | `/a2a/support`, `…/v1/message:stream`, `…/v1/message:send` | Agent card — last-resort bare `GET` on an endpoint |
| `POST` | `/a2a/support` | JSON-RPC binding — all methods |
| `POST` | `/a2a/support/v1/message:send` | HTTP+JSON, buffered |
| `POST` | `/a2a/support/v1/message:stream` | HTTP+JSON, SSE |
| `GET` | `/a2a/support/v1/tasks/{taskId}` | Read a task back |
| `POST` | `/a2a/support/v1/tasks/{taskId}:cancel` | Cancel a running task |
| `POST` | `/a2a/support/v1/tasks/{taskId}:subscribe` | Resubscribe to a running task's stream |
| `GET` | `/.well-known/agent-card.json` | Primary agent's card from the server root; with several agents and no `PrimaryAgent`, returns a 404 naming each agent's card URL |
| `GET` | `/a2a` | Catalog of exposed agents and their endpoints (always anonymous) |

Methods: `message/send`, `message/stream`, `tasks/get`, `tasks/cancel`, `tasks/resubscribe`. Push
notification configuration returns `PushNotificationNotSupportedError` (`-32003`).

A client appends `/.well-known/…` to whatever URL it was configured with rather than resolving it
against the agent base — Copilot Studio asks for `{endpoint}/.well-known/agent-card.json`, method
segment included — so the card answers under every base segment and spelling. Adding a spelling?
ASP.NET route matching is case-insensitive, so `agentcard.json` and `agentCard.json` are one route
and mapping both throws `AmbiguousMatchException` (500 on either).

`GET /a2a` is the fastest wiring check — it lists routes, not secrets.

## Connect from Copilot Studio

1. Expose the host publicly (reverse proxy, direct route, or a **public** dev tunnel port).
2. Copilot Studio → your agent → **Agents** → **Add agent** → **A2A agent**.
3. **Endpoint URL** is the *message* endpoint, not the card:
   `https://agents.contoso.com/a2a/support/v1/message:stream`
4. Copilot Studio fetches the card and fills in name and description. It fetches with a
   cross-origin `fetch()` **from the browser**, so card routes must send
   `Access-Control-Allow-Origin` - FabrCore does by default (`A2A:AgentCardCorsOrigins`).
5. **Authentication** must match `A2A:Authentication:Mode`: **None** → `None`; **API key** →
   `ApiKey` (give it the header name, `x-api-key` by default, and the value); **OAuth 2.0** →
   `JwtBearer`.
6. **Save** → pick or create the connection → **Add and configure**. The connection step is not
   optional: the key in the *Add agent* dialog and the key on the *connection* are different fields,
   and a missing connection fails as an internal `SystemError` with no request reaching your server.
7. **Settings → Generative AI → Orchestration = Yes.** Connected agents are never invoked under
   classic orchestration.
8. Test with a prompt only your agent can answer, and open the **activity map** to see whether it
   delegated.

### Copilot Studio's wire shape (why no shim is needed)

Copilot Studio is configured with the REST-style `/v1/message:stream` URL but posts **JSON-RPC**
bodies and reads a **single JSON response** — a mix of the two bindings that a strict A2A server
rejects. Microsoft's own sample bolts on a middleware shim. FabrCore handles it natively: a
JSON-RPC envelope on an HTTP+JSON route is answered with a matching JSON-RPC envelope, buffered
rather than streamed, with the reply as a flat agent `Message`.

| `A2A:Interop` setting | Default | Effect |
| --- | --- | --- |
| `AcceptJsonRpcOnHttpRoutes` | `true` | Accept the envelope on `/v1/...` routes |
| `CollapseStreamForJsonRpcOnHttpRoutes` | `true` | Answer with one buffered response, not SSE |
| `CompatibilityResultShape` | `Message` | Flat `Message` result; `Task` for the full object |
| `ResultShape` | `Task` | Result shape on the standard A2A routes |
| `PassMessageMetadataToAgent` | `true` | Copy inbound `metadata` to `Args["A2A:Metadata"]` |

Standard A2A clients are unaffected — strict JSON-RPC on the base path, strict HTTP+JSON under
`/v1`, real SSE, `Task` results.

Copilot Studio also sends conversation history in
`metadata["copilotstudio.microsoft.com/a2a/chathistory"]`, which reaches the agent in
`Args["A2A:Metadata"]`.

## What the agent receives

Every A2A turn is an ordinary `AgentMessage` with `Channel = "a2a"`:

| Arg | Contents |
| --- | --- |
| `A2A:ContextId` | Conversation id grouping related turns |
| `A2A:TaskId` | Id of the task this turn belongs to |
| `A2A:MessageId` | Id of the inbound message |
| `A2A:AgentName` | Route name of the exposed agent |
| `A2A:Caller` | API key name or token subject |
| `A2A:Metadata` | Raw JSON of the caller's `metadata` object |
| `A2A:NonTextParts` | JSON array of `data` and `file` parts |

Text parts are concatenated into the message body, one per line. **File parts stay as references —
the host never fetches a caller-supplied URI**, since that would turn every A2A client into a way
to make the host issue outbound requests.

The reply text becomes a text part on the task's artifact and on its terminal status message. A
reply carrying `Data` with a JSON `DataType` also gets a `data` part.

## Authentication and principal mapping

`A2A:Authentication:Mode`:

- **`None`** — anyone who can reach the endpoint can call it. Only when the endpoint is not public,
  or a proxy authenticates callers.
- **`ApiKey`** (default) — shared secret in `x-api-key` (configurable), as `Authorization: Bearer`,
  or in a query parameter when `QueryParameterName` is set (off by default: query keys land in
  access logs). Constant-time comparison.
- **`JwtBearer`** — OAuth 2.0 / OIDC token validated against `Authority` and `Audience`.

Agent cards stay anonymous by default (`Authentication:AllowAnonymousAgentCard`) because clients
fetch the card before they hold a credential. Cards carry route and capability metadata only.

Keys can be scoped and mapped:

```json
"Keys": [
  { "Name": "contoso-copilot", "Value": "…", "PrincipalHandle": "contoso-tenant", "Agents": [ "support" ] }
]
```

`A2A:Principal:Strategy` picks the FabrCore principal the agents run as:

| Strategy | Handle | Use when |
| --- | --- | --- |
| `Fixed` (default) | `Principal:Handle`, default `a2a` | One trusted caller, or callers may share state |
| `ContextId` | The A2A `contextId` | Conversations must not see each other's history |
| `ApiKey` | The matched key's `PrincipalHandle` | One principal per tenant or client |
| `Claim` | Token claim (`JwtBearer:PrincipalClaimType`, default `oid`) | Per-user isolation under OAuth |

`Principal:Prefix` is prepended to derived handles. Startup fails fast if `Strategy` and `Mode`
disagree (`ApiKey` needs `ApiKey`, `Claim` needs `JwtBearer`).

## Tasks and streaming

Each turn creates a task: `submitted` → `working` → `completed` / `failed` / `canceled`. A streaming
client sees the task, each status transition, and the artifact. FabrCore agents answer as a unit
rather than token by token, so the artifact arrives in one event; the status events still signal
progress.

| `A2A:Tasks` setting | Default | Purpose |
| --- | --- | --- |
| `ExecutionTimeout` | 5 min | Time an agent gets before the task fails |
| `Retention` | 1 hour | How long a finished task stays readable via `tasks/get` |
| `MaxRetainedTasks` | 1000 | In-memory store cap; running tasks are never evicted |
| `DefaultHistoryLength` | 10 | History turns returned when the client asks for none |
| `StreamHeartbeatInterval` | 15 s | SSE keep-alive comments |

The store is in-memory and per-process. For scale-out, register your own `IA2ATaskStore`.

## Reverse proxy checklist

- Forward `/a2a/*`, and `/.well-known/agent-card.json` + `/.well-known/agent.json` if you rely on
  root discovery.
- Set `A2A:PublicBaseUrl`, or add forwarded-headers middleware.
- Disable response buffering for `/a2a/*` (the host sets `X-Accel-Buffering: no`, but nginx-style
  proxies may need explicit config).
- Read timeout above `A2A:Tasks:ExecutionTimeout`.
- Preserve `x-api-key` / `Authorization`.

## Extension points

Register as a singleton **before** `AddFabrCoreServer`; the host only fills in what you have not
supplied. Registering afterwards is silently ignored — the host uses `TryAdd`, so a late
`IA2APrincipalResolver` leaves every caller on the default principal with no error to notice.

| Interface | Replace to |
| --- | --- |
| `IA2APrincipalResolver` | Map callers to principals your own way — `ValueTask<string?> ResolvePrincipalHandleAsync(HttpContext, A2AExposedAgent, string contextId, CancellationToken)`, so a directory lookup is a plain `await` |
| `IA2ATaskStore` | Persist tasks durably or share them across instances |
| `IA2AAgentCardFactory` | Take full control of card contents |
| `IA2AAgentProvisioner` | Change how agent instances are resolved and provisioned |

## Verify locally

```bash
curl -s https://localhost:5001/a2a | jq
```

```bash
curl -s https://localhost:5001/a2a/support/.well-known/agent-card.json | jq
```

```bash
curl -s -X POST https://localhost:5001/a2a/support -H "content-type: application/json" -H "x-api-key: $KEY" -d '{"jsonrpc":"2.0","id":1,"method":"message/send","params":{"message":{"kind":"message","role":"user","messageId":"m-1","parts":[{"kind":"text","text":"hello"}]}}}'
```

```bash
curl -N -s -X POST https://localhost:5001/a2a/support/v1/message:stream -H "content-type: application/json" -H "accept: text/event-stream" -H "x-api-key: $KEY" -d '{"message":{"kind":"message","role":"user","messageId":"m-1","parts":[{"kind":"text","text":"hello"}]}}'
```

## Testing your own exposure

```powershell
dotnet add <test-project> package FabrCore.Host.Testing
```

`FabrCoreA2ATestHost` stands the A2A endpoints up over an in-memory server with fake agent and
registry services — no Orleans silo. Everything except those two is the shipped code, so the test
exercises the real routes, credential checks, and wire format rather than the handler in isolation,
which is where A2A interop actually breaks.

```csharp
using FabrCore.Host.Testing;

await using var host = await FabrCoreA2ATestHost.StartAsync(new Dictionary<string, string?>
{
    ["A2A:Enabled"] = "true",
    ["A2A:Authentication:Mode"] = "None",
    ["A2A:AgentTypes:0"] = "support-agent",
});

using var card = await host.GetJsonAsync("/a2a/support-agent/.well-known/agent-card.json");
```

| Test | Catches |
| --- | --- |
| Principal and handle at the agent boundary | Which grain a turn lands in — `AgentService.Sends` carries both |
| JSON-RPC on `/v1/message:stream` returns one buffered JSON body | The Copilot Studio interop path |
| Agent card contents | An empty or wrong `description` — the usual cause of "never called" |
| 401 without a credential, 200 for the card | The anonymous-card / authenticated-call split |
| `metadata` reaching the agent | `chathistory` arriving as `Args["A2A:Metadata"]` |

The first is the one only this seam can answer: matching configuration fields between two channels
does not tell you the callers reach the same agent. Pass `configureServices` to register a custom
`IA2APrincipalResolver` **before** the host's own `TryAdd`, then assert on
`host.AgentService.Sends.Single().Principal` and `.Handle`.

`FakeFabrCoreAgentService` records `Sends` and `Ensured`, answers with `Reply` or `ReplyFactory`,
and lists `LiveAgents` for handle discovery. `FakeFabrCoreRegistry.WithAgentType` supplies agent
types with description, capabilities, and notes. `ReadServerSentEventsAsync` returns parsed SSE
frames. Full reference: `docs/a2a.md` → **Testing your exposure**. See **fabrcore-testing** for
general harness conventions.

## Troubleshooting

| Symptom | Cause and fix |
| --- | --- |
| "We couldn't find an agent card at this URL" while the server logs 200 | **CORS.** The card is fetched by a cross-origin `fetch()` from the Copilot Studio page (`Origin: https://copilotstudio.microsoft.com`, `Sec-Fetch-Mode: cors`); without `Access-Control-Allow-Origin` the browser discards the 200 before the page sees it, so the message is accurate. FabrCore sends it by default - check nothing in front of the app strips it. Chasing card paths, spellings, `preferredTransport`, or card content is a dead end here: the body is never parsed |
| Card returns 200 but the client says it cannot find one | Check what the app returns for **unmatched** `/a2a` paths. An auth `FallbackPolicy` plus Blazor's catch-all turns 404 into a 302 to sign-in; `UseStatusCodePagesWithReExecute` turns an A2A 401 into 400 HTML via antiforgery. Keep both off `/a2a` and `/.well-known` with `app.UseWhen(...)` |
| Agent replies "I rendered the ..." and the caller sees no data | The agent narrates a Surface canvas that does not exist over A2A. Branch on `AgentMessage.Channel == A2ADefaults.ChannelName`, skip any card-rendering shortcut, and tell the model not to render - see `CrmDemoAgent.IsTextOnlyChannel` |
| `ContentFiltered` / `openAIndirectAttack`, step blocked | Copilot Studio runs Prompt Shields over what your agent returns. Never steer the agent by appending bracketed imperatives ("[Note: answer as text]") to the user message - that is the exact shape an indirect-injection shield flags, and it blocks the whole step. Pass per-turn steering as a `ChatRole.System` message instead, which never reaches the caller |
| Agent asks a question whose answer is in a form | A form shortcut that runs before the model renders an Adaptive Card the A2A caller cannot see, so the turn dead-ends. Skip such shortcuts on text-only channels and let the model ask in chat |
| Tooltip and docs disagree on the URL to enter | The tooltip says the base URI, the Learn article says the message endpoint. FabrCore serves the card for both, but **enter the message endpoint** — the card is never read, so Copilot Studio POSTs to whatever you typed, and `/a2a` is `GET`-only |
| `401` on calls but the card loads | Expected — cards are anonymous, calls are not. Confirm the header name and that the proxy forwards it |
| Empty responses in a custom client | Set `A2A:Interop:CompatibilityResultShape` to `Message` (the default) rather than `Task` |
| Streaming hangs | A buffering proxy. Disable buffering for `/a2a/*`; check the read timeout against `ExecutionTimeout` |
| Task times out | Raise `A2A:Tasks:ExecutionTimeout`; check agent health at `/fabrcoreapi/agent/health/{handle}` |
| An agent is missing from `GET /a2a` | With `Discovery:AgentTypes = Described` it needs a `[Description]`; check it is not `[FabrCoreHidden]` and that no `ExcludeAgentTypes` glob catches it |
| A live agent is published as `system-assistant` not `assistant` | Its bare handle was already claimed; it is qualified rather than dropped. The startup log names it |
| The connected agent is never called, no request arrives | In order: generative orchestration on; description not blank; the primary agent's own knowledge sources competing for the query; only then the card wording (`[Description]`, `[FabrCoreCapabilities]`, `[FabrCoreNote]`, or an `Agents` entry). Log requests first — no `POST` means the failure is upstream in Copilot Studio, not in your card |
| `A2A publishes no agents` | `Discovery:AgentTypes` is `None` (the default) and nothing is named in `AgentTypes`, `AgentHandles`, or `Agents` |

## A2A vs. the Microsoft 365 Copilot addon

Different problems; they run side by side.

| | `fabrcore-microsoft365copilot` | `fabrcore-a2a` |
| --- | --- | --- |
| Protocol | Azure Bot Service Activity Protocol | Agent2Agent |
| Endpoint | `/api/messages` | `/a2a/{agent}` |
| Identity | Each Microsoft 365 user, via Entra | The calling agent or tenant |
| Use for | A user chatting with your agent in Copilot or Teams | Copilot Studio orchestrating your agent among several |
