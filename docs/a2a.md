# Agent2Agent (A2A) for FabrCore

FabrCore hosts publish their agents over the open
[Agent2Agent (A2A) protocol](https://a2a-protocol.org). Any A2A client can discover and call them —
including **Microsoft 365 Copilot Studio**, which uses A2A to delegate work to agents hosted
outside it, as part of its multi-agent orchestration.

It is built into `FabrCore.Host` and off until you enable it. Your agents need no changes: they
keep their handles, their ACLs, their monitors, and their durable state. A2A is another way in.

```
Copilot Studio / any A2A client
        │  GET  /a2a/support/.well-known/agent-card.json     ← discovery
        │  POST /a2a/support                                 ← JSON-RPC binding
        │  POST /a2a/support/v1/message:stream               ← HTTP+JSON binding (SSE)
        ▼
FabrCore.Host A2A ──── authenticate ──► map to a FabrCore principal
        │                                          │
        │                                          ▼
        │                          ensure the agent (IFabrCoreAgentService)
        ▼                                          │
   A2A task lifecycle  ◄── reply ──  your [AgentAlias] agent on the Orleans silo
```

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

Settings come from the `A2A` section of `fabrcore.json` or `appsettings.json`. A2A endpoints are
meant to be publicly reachable, so `A2A:Enabled` defaults to false and publishing them stays an
explicit decision.

## Minimum configuration

```json
{
  "A2A": {
    "Enabled": true,
    "PublicBaseUrl": "https://agents.contoso.com",
    "Discovery": { "AgentTypes": "Described" },
    "Authentication": {
      "Mode": "ApiKey",
      "ApiKey": {
        "Keys": [ { "Name": "copilot-studio", "Value": "generate-a-long-random-secret" } ]
      }
    }
  }
}
```

That is the whole thing. Every registered agent type that carries a `[Description]` is published,
each at `/a2a/{alias}` with its own agent card — no agent is named here, and none needs to be added
when the fleet grows. Publish exactly one agent and its card is also served from the server root at
`/.well-known/agent-card.json`.

That suits a host whose agents are all meant to be callable. If yours are not, set
`"AgentTypes": "None"` and name the agents you mean to publish under **Choosing what to expose
explicitly** — see **Publishing agents without listing them** for why that is a decision worth
writing down.

`PublicBaseUrl` matters: an agent card advertises the URL clients must call, which behind a reverse
proxy is not the URL the request arrived on. Set it to the public origin. If you leave it unset the
host derives the URL from the request, which is right for local development and dev tunnels, and
correct behind a proxy only if you have already added forwarded-headers middleware.

## Publishing agents without listing them

The host already knows its agents: the registry carries every `[AgentAlias]` type with its
`[Description]`, `[FabrCoreCapabilities]`, and `[FabrCoreNote]`, and the cluster knows which agents
are actually running. `A2A:Discovery` reads both, so the configuration says *which* agents to
publish rather than restating them. `"Discovery": { "AgentTypes": "Described" }` — the one line in
the minimum configuration above — publishes every registered agent type that has a `[Description]`,
each with its own agent card, route, and skills, and stays correct as agents are added and removed.
Nothing else to maintain.

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
see [acl-management.md](acl-management.md).

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

## Choosing what to expose explicitly

Discovery covers the common case. Name agents directly when you want to curate what a card says,
publish an agent under a different route name, or give one agent settings the rest of the fleet
does not get. These compose with discovery and with each other; the first source to claim a route
name keeps it.

**By agent type** — publish a registered `[AgentAlias]`. Each caller gets their own agent instance,
provisioned on first contact.

```json
"AgentTypes": [ "chat-agent", "research-agent" ]
```

**By handle** — publish an agent that already exists, by its fully-qualified handle. Nothing is
provisioned; the host routes straight to it. Cross-principal delivery needs an
`agent.message.allow` ACL grant for the mapped principals (see [acl-management.md](acl-management.md)).

```json
"AgentHandles": [ "system:assistant" ]
```

**Explicitly** — for full control over the route, the card, and how instances are provisioned.

```json
"Agents": [
  {
    "Name": "support",
    "DisplayName": "Contoso Support",
    "Description": "Answers questions about Contoso orders, returns, and shipping.",
    "AgentType": "chat-agent",
    "Models": "default",
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

`Description` and `Skills` are the text an orchestrator reads to decide *when* to route work to
your agent. Copilot Studio pulls them straight off the card. Vague descriptions are the most common
reason a connected agent never gets called.

When no skills are configured, one is synthesized from the agent's name and description, plus any
capabilities the registered agent type declares — enough to be routable, but worth replacing with
something specific.

## Endpoints

For an agent named `support`, mounted at `/a2a/support`:

| Method | Route | Purpose |
| --- | --- | --- |
| `GET` | `/a2a/support/.well-known/agent-card.json` | Agent card (A2A 0.3 path) |
| `GET` | `/a2a/support/.well-known/agent.json` | Agent card (pre-0.3 path) |
| `GET` | `/a2a/support/.well-known/{agentcard,agent_card}.json` | Same card, under the remaining spellings clients probe |
| `GET` | `/a2a/support/v1/card` | Agent card (HTTP+JSON binding) |
| `GET` | `/a2a/support/v1/.well-known/*` | Same five spellings, for a client configured with the `/v1` URL |
| `GET` | `/a2a/support/v1/message:stream/.well-known/*` | Same again, for a client configured with the *message* endpoint |
| `GET` | `/a2a/support/v1/message:send/.well-known/*` | Same again |
| `GET` | `/a2a/support` | Agent card — last-resort bare `GET` on the endpoint |
| `GET` | `/a2a/support/v1/message:stream` | Agent card — same, on the streaming endpoint |
| `GET` | `/a2a/support/v1/message:send` | Agent card — same, on the send endpoint |
| `GET` | `/a2a/.well-known/*` | The primary agent's card, under the route prefix |
| `POST` | `/a2a/support` | JSON-RPC binding — every method |
| `POST` | `/a2a/support/v1/message:send` | HTTP+JSON — send, buffered |
| `POST` | `/a2a/support/v1/message:stream` | HTTP+JSON — send, streamed over SSE |
| `GET` | `/a2a/support/v1/tasks/{taskId}` | Read a task back |
| `POST` | `/a2a/support/v1/tasks/{taskId}:cancel` | Cancel a running task |
| `POST` | `/a2a/support/v1/tasks/{taskId}:subscribe` | Resubscribe to a running task's stream |
| `GET` | `/.well-known/agent-card.json` | The primary agent's card, from the server root; with several agents and no `PrimaryAgent`, returns a 404 naming each agent's card URL |
| `GET` | `/a2a` | Catalog of every exposed agent and its endpoints |

Supported JSON-RPC methods: `message/send`, `message/stream`, `tasks/get`, `tasks/cancel`,
`tasks/resubscribe`. Push notification configuration is answered with the protocol's
`PushNotificationNotSupportedError` (`-32003`) rather than silently ignored.

The catalog at `GET /a2a` is the fastest way to check your wiring — it lists routes, not secrets,
and is always anonymous.

### Why the card is served under so many paths

A2A clients do not resolve `/.well-known/agent-card.json` against the agent's base path. They
append it to whatever URL they were configured with. Copilot Studio, configured with
`…/a2a/support/v1/message:stream`, asks for
`…/a2a/support/v1/message:stream/.well-known/agent-card.json` — the method segment and all — then
tries four more spellings (`agent.json`, `agentcard.json`, `agentCard.json`, `agent_card.json`),
then the server root, then a bare `GET` on the endpoint itself. Serving only the two spec paths at
the agent base leaves the card sitting at a URL such clients never request.

So the card answers under every base segment a client may hold, under every spelling, plus the
route prefix, the server root, and bare `GET` on the endpoints. If you add a spelling, note that
ASP.NET route matching is case-insensitive: `agentcard.json` and `agentCard.json` are one route, and
mapping both throws `AmbiguousMatchException` and answers **500** to every request for either.

## Connecting from Microsoft 365 Copilot Studio

1. Expose the server publicly. In production that is a reverse proxy or a direct route to
   `https://agents.contoso.com`; for local work, a dev tunnel on the FabrCore host's port works, and
   the tunnel port must be **public**, not private.
2. In Copilot Studio open your agent → **Agents** → **Add agent** → **A2A agent**.
3. For **Endpoint URL** enter the *message* endpoint, not the card:
   `https://agents.contoso.com/a2a/support/v1/message:stream`
4. Copilot Studio fetches the card and fills in the name and description. It does this with a
   cross-origin `fetch()` **from the browser**, so the card routes must send
   `Access-Control-Allow-Origin`. FabrCore does by default (`A2A:AgentCardCorsOrigins`, default
   `*`). If you see "We couldn't find an agent card at this URL" while the server logs a 200 for
   every probe, that header is missing or has been stripped in front of the app.
5. Pick the authentication method matching your `A2A:Authentication:Mode`:
   - **None** → `"Mode": "None"`
   - **API key** → `"Mode": "ApiKey"`; give Copilot Studio the header name (`x-api-key` by default)
     and the key value
   - **OAuth 2.0** → `"Mode": "JwtBearer"`; Copilot Studio takes the client id, secret, and the
     authorization/token/refresh URLs
6. **Save**, then **choose or create the connection**, then **Add and configure**. Do not skip the
   connection step. The API key typed into the *Add agent* dialog and the key held by the
   *connection* are separate fields; a connected agent with no authorized connection behind it
   fails as an internal `SystemError` in the test canvas, with no request ever reaching your server.
7. Turn on **generative orchestration**: agent **Settings → Generative AI → Orchestration →
   "Use generative AI orchestration for your agent's responses?" = Yes**. Under classic
   orchestration connected agents are *never* invoked — Microsoft's own behavior table reads
   "Child and connected agents — Not applicable".
8. Test with a prompt only your agent can answer, so the orchestrator has a reason to delegate. Open
   the **activity map** in the test canvas to see whether it delegated, and to what.

### The Copilot Studio wire shape

Copilot Studio is configured with the REST-style `/v1/message:stream` URL but posts **JSON-RPC**
bodies and reads a **single JSON response**:

```json
{
  "jsonrpc": "2.0",
  "id": "…",
  "method": "message/send",
  "params": {
    "message": {
      "contextId": "ee1e68ee-75fc-42bb-83d7-25fd26e559c3",
      "metadata": {
        "copilotstudio.microsoft.com/a2a/chathistory": [
          { "From": "user", "Text": "Which plant needs more sunlight?", "Locale": "en-US" }
        ]
      },
      "parts": [ { "kind": "text", "text": "Which plant needs more sunlight?" } ]
    }
  }
}
```

That mixes the two bindings, so a strict A2A server rejects it — Microsoft's own sample bolts on a
middleware shim to bridge it. FabrCore handles it natively instead: a JSON-RPC envelope on an
HTTP+JSON route is answered with a matching JSON-RPC envelope, buffered rather than streamed, with
the reply as a flat agent `Message` so a connector expecting one answer finds it. `A2A:Interop`
controls all of it:

| Setting | Default | Effect |
| --- | --- | --- |
| `AcceptJsonRpcOnHttpRoutes` | `true` | Accept a JSON-RPC envelope on the `/v1/...` routes at all |
| `CollapseStreamForJsonRpcOnHttpRoutes` | `true` | Answer such a request with one buffered response instead of SSE |
| `CompatibilityResultShape` | `Message` | Result shape for that path — a flat `Message`; `Task` for the full task object |
| `ResultShape` | `Task` | Result shape on the standard A2A routes |
| `PassMessageMetadataToAgent` | `true` | Copy the caller's `metadata` to `AgentMessage.Args["A2A:Metadata"]` |

Standard A2A clients are unaffected: they get strict JSON-RPC on the base path and strict HTTP+JSON
under `/v1`, with real SSE streaming and `Task` results.

The inbound `metadata` — including that chat history — reaches your agent as
`AgentMessage.Args["A2A:Metadata"]`.

## What your agent sees

Every A2A turn arrives as an ordinary `AgentMessage` with `Channel = "a2a"` and these args:

| Arg | Contents |
| --- | --- |
| `A2A:ContextId` | Conversation id grouping related turns |
| `A2A:TaskId` | Id of the task this turn belongs to |
| `A2A:MessageId` | Id of the inbound message |
| `A2A:AgentName` | Route name of the exposed agent that was called |
| `A2A:Caller` | API key name or token subject |
| `A2A:Metadata` | Raw JSON of the caller's `metadata` object, when present |
| `A2A:NonTextParts` | JSON array of any `data` and `file` parts |

Text parts are concatenated into the message body, one per line. Data and file parts are carried
through in `A2A:NonTextParts` rather than flattened into the prompt. **File parts stay as
references — the host never fetches a caller-supplied URI**, because doing so would turn every A2A
client into a way to make your host issue outbound requests.

On the way back, the agent's reply text becomes a text part on the task's artifact and on its
terminal status message. A reply that also carries `Data` with a JSON `DataType` gets a `data` part
alongside it.

## Authentication and identity

`A2A:Authentication:Mode` is one of:

- **`None`** — anyone who can reach the endpoint can call it. Only appropriate when the endpoint
  is not publicly reachable, or a proxy in front of it authenticates callers.
- **`ApiKey`** (default) — a shared secret in `x-api-key` (configurable), as an
  `Authorization: Bearer` credential, or in a query parameter when you set `QueryParameterName`.
  Keys are compared in constant time. A query-parameter key ends up in access logs and proxy
  traces, so it is off unless you ask for it.
- **`JwtBearer`** — an OAuth 2.0 / OIDC access token validated against `Authority` and `Audience`.

Agent cards stay readable without credentials by default
(`Authentication:AllowAnonymousAgentCard`), because a client fetches the card before it holds a
credential. The card carries route and capability metadata only. Set it to `false` if you would
rather callers authenticate for discovery too.

Each key can be scoped to specific agents and mapped to a principal:

```json
"Keys": [
  {
    "Name": "contoso-copilot",
    "Value": "…",
    "PrincipalHandle": "contoso-tenant",
    "Agents": [ "support" ]
  }
]
```

`A2A:Principal:Strategy` decides which FabrCore principal the agents run as:

| Strategy | Principal handle | Use when |
| --- | --- | --- |
| `Fixed` (default) | `A2A:Principal:Handle`, default `a2a` | One trusted caller, or callers who may share state |
| `ContextId` | The A2A `contextId` | Conversations must not see each other's history |
| `ApiKey` | The matched key's `PrincipalHandle` | One principal per tenant or per client |
| `Claim` | A claim from the bearer token (`JwtBearer:PrincipalClaimType`, default `oid`) | Per-user isolation under OAuth |

`A2A:Principal:Prefix` is prepended to derived handles (for example `a2a-`). For anything beyond
these, register your own `IA2APrincipalResolver` singleton **before `AddFabrCoreServer`**:

```csharp
public ValueTask<string?> ResolvePrincipalHandleAsync(
    HttpContext context, A2AExposedAgent agent, string contextId, CancellationToken cancellationToken);
```

It is async because mapping a caller to a real user usually means a directory or store lookup, which
is the point of the per-caller strategies; a resolver needing no I/O returns a completed
`ValueTask` at no cost. `DescribeCaller` stays synchronous — it only reads claims already on the
request.

**Register before `AddFabrCoreServer`.** The host registers its default with `TryAdd`, so a
resolver registered afterwards is ignored without an error and every caller quietly runs as the
default principal instead of the one you configured.

## Tasks, streaming, and cancellation

A2A is task-based. Each turn creates a task that moves `submitted` → `working` → `completed`
(or `failed` / `canceled`), and a streaming client sees the task, each status transition, and the
artifact as they happen. FabrCore agents answer as a unit rather than token by token, so the
artifact arrives in one event — the status events still tell the client work is under way.

Tuning lives under `A2A:Tasks`:

| Setting | Default | Purpose |
| --- | --- | --- |
| `ExecutionTimeout` | 5 minutes | How long an agent gets before the task fails |
| `Retention` | 1 hour | How long a finished task stays readable through `tasks/get` |
| `MaxRetainedTasks` | 1000 | Cap on the in-memory store; running tasks are never evicted |
| `DefaultHistoryLength` | 10 | Turns of history returned when the client does not ask for a length |
| `StreamHeartbeatInterval` | 15 seconds | SSE keep-alive comments, so intermediaries do not time the stream out |

The task store is in-memory and per-process. For a scaled-out deployment where a client may read a
task back through a different instance, register your own `IA2ATaskStore` singleton before
`AddFabrCoreServer`.

Streaming responses set `X-Accel-Buffering: no`. Proxies that buffer by default (nginx among them)
otherwise hold the SSE body until the response ends, which defeats streaming entirely.

## Testing your exposure

`FabrCore.Host.Testing` stands the A2A endpoints up over an in-memory server with fake agent and
registry services, so an application can test its own exposure without an Orleans silo. Everything
except those two services is the shipped code: real routes, real authentication handlers, real wire
format.

```powershell
dotnet add <test-project> package FabrCore.Host.Testing
```

```csharp
using FabrCore.Host.Testing;

await using var host = await FabrCoreA2ATestHost.StartAsync(new Dictionary<string, string?>
{
    ["A2A:Enabled"] = "true",
    ["A2A:Authentication:Mode"] = "None",
    ["A2A:AgentTypes:0"] = "support-agent",
});

using var card = await host.GetJsonAsync("/a2a/support-agent/.well-known/agent-card.json");
Assert.AreEqual("Support Agent", card.RootElement.GetProperty("name").GetString());
```

The five things worth pinning, in rough order of how much they cost when wrong:

| Test | Catches |
| --- | --- |
| Principal and handle at the agent boundary | Which grain a turn actually lands in — `AgentService.Sends` carries both |
| A JSON-RPC envelope on `/v1/message:stream` returns one buffered JSON body | The Copilot Studio interop path, which has no second implementation to compare against |
| Agent card contents | An empty or wrong `description`, the usual cause of "the connected agent is never called" |
| 401 without a credential, 200 for the card | The anonymous-card / authenticated-call split |
| `metadata` reaching the agent | `copilotstudio.microsoft.com/a2a/chathistory` arriving as `Args["A2A:Metadata"]` |

The first is the one that matters most and the one only this seam can answer. Matching
configuration fields between two channels does not tell you the callers reach the same agent — the
principal and the handle both feed the grain key, and the principal is whatever your resolver
returns:

```csharp
await using var host = await FabrCoreA2ATestHost.StartAsync(
    configuration,
    registry: FabrCoreA2ATestHost.RegistryFor(typeof(MyAgent).Assembly),
    // Before the host registers its own, which uses TryAdd.
    configureServices: s => s.AddSingleton<IA2APrincipalResolver>(new MyResolver()));

await host.Client.SendAsync(request);

var send = host.AgentService.Sends.Single();
Assert.AreEqual("eric", send.Principal);
Assert.AreEqual("service-assistant", send.Handle);
```

`FakeFabrCoreAgentService` records `Sends` and `Ensured`, answers with `Reply` or a `ReplyFactory`
for error and delay cases, and lists `LiveAgents` for `Discovery:IncludeAgentHandles`.
`FakeFabrCoreRegistry.WithAgentType` supplies agent types with their description, capabilities, and
notes, so discovery and card generation can be exercised without loading real agent assemblies.

`FabrCoreA2ATestHost.ReadServerSentEventsAsync` returns each SSE frame parsed, for the streaming
paths.

## Reverse proxy checklist

- Forward `/a2a/*` and, if you rely on root discovery, `/.well-known/agent-card.json` and
  `/.well-known/agent.json`.
- Set `A2A:PublicBaseUrl` to the public origin, or add forwarded-headers middleware.
- Disable response buffering for `/a2a/*` so SSE flows.
- Give the proxy a read timeout above `A2A:Tasks:ExecutionTimeout`.
- Preserve the credential header (`x-api-key` or `Authorization`).

## Extension points

Register any of these as a singleton **before** `AddFabrCoreServer`; the host only fills in what you
have not supplied.

| Interface | Replace to |
| --- | --- |
| `IA2APrincipalResolver` | Map callers to principals your own way |
| `IA2ATaskStore` | Persist tasks durably or share them across instances |
| `IA2AAgentCardFactory` | Take full control of card contents |
| `IA2AAgentProvisioner` | Change how agent instances are resolved and provisioned |

## Troubleshooting

**Copilot Studio says "We couldn't find an agent card at this URL" while your server logs 200.**
CORS. Copilot Studio fetches the card with a cross-origin `fetch()` from its own page, not from
its service - the request carries `Origin: https://copilotstudio.microsoft.com` and
`Sec-Fetch-Mode: cors`. Without `Access-Control-Allow-Origin` on the response the browser reads
the 200 and discards it before the page sees it, so the message is **accurate**: it really could
not read the card. Nothing in your server log shows this, because on the wire the request
succeeded.

FabrCore sends the header on every card route by default. If the message persists, check that a
reverse proxy or CDN in front of the app is not stripping it, then confirm with:

```bash
curl -sD - -o /dev/null -H "Origin: https://copilotstudio.microsoft.com" https://your-host/a2a/your-agent/.well-known/agent-card.json | grep -i access-control
```

`A2A:AgentCardCorsOrigins` controls this (default `["*"]`); set an explicit origin list to narrow
it, or an empty list to send no header. It applies to card routes only - the call endpoints are
never opened cross-origin by it.

This is also why chasing card paths and card content is a dead end when the symptom appears: the
body is never parsed, so path spellings, `preferredTransport`, and `securitySchemes` are all
irrelevant. Microsoft's own `Simple-A2A-Sample` reproduces the message for the same reason - it
sets no CORS header either.

**The card returns 200 but a client still says it cannot find one.** Check what your app returns
for *unmatched* paths under `/a2a`, not what it returns for the correct one. An interactive web app
hosting FabrCore can turn a 404 into something a probing client misreads:

- A global authorization `FallbackPolicy` plus Blazor's `MapRazorComponents` catch-all: the
  catch-all matches every unmatched path and carries no authorization metadata, so the fallback
  policy applies and unmatched `/a2a/*` answers **302 to a sign-in page**. A client follows the
  redirect, parses an HTML login form, and concludes there is no card — while the correctly spelled
  path was answering 200 the whole time.
- `UseStatusCodePagesWithReExecute("/not-found")`: a 401 from an A2A route gets re-executed as a
  POST into a Razor component, where antiforgery rejects it and the caller receives **400 HTML**
  instead of 401 JSON.

Keep both off the machine-facing routes:

```csharp
app.UseWhen(
    context => !context.Request.Path.StartsWithSegments("/a2a")
        && !context.Request.Path.StartsWithSegments("/.well-known"),
    branch => branch.UseStatusCodePagesWithReExecute("/not-found", createScopeForStatusCodePages: true));
```

**The agent answers "I rendered the ..." and the caller sees no data.** An agent written for a
Surface canvas narrates the UI it drew, which is invisible over A2A: the reply reads as a success
while delivering nothing. A2A traffic is stamped `AgentMessage.Channel = "a2a"`
(`A2ADefaults.ChannelName`). Branch on it, skip any card-rendering shortcut, and steer the model
away from rendering for that turn. See `CrmDemoAgent.IsTextOnlyChannel` in
`samples/FabrCore.SampleApp`.

Watch for shortcuts that run *before* the model. The CRM sample rendered an add-contact form from
a regex match, so an A2A caller got "Which customer should I add the contact to?" with the answer
sitting in a form they could not see. Such a turn cannot recover; skip the shortcut entirely on
text-only channels.

**Copilot Studio reports `ContentFiltered` / `openAIndirectAttack` and blocks the step.** Its
Prompt Shields scan what your agent returns for injected instructions. The usual cause is
well-intentioned steering: appending a bracketed imperative to the user message, for example
`[Channel note: state the full answer as text in your reply.]`, so the model changes behavior.
That is exactly the shape an indirect-injection shield exists to catch, and the shield is right to
catch it - instruction text riding inside message content is indistinguishable from an attack.

Pass per-turn steering as a system turn instead. It never becomes part of the content returned to
the caller:

```csharp
var turn = new List<ChatMessage>();
if (IsTextOnlyChannel(message.Channel))
{
    turn.Add(new ChatMessage(
        ChatRole.System,
        "This caller renders no Surface UI and sees only your reply text. Do not render, and do "
        + "not describe UI. State the full answer as text."));
}

turn.Add(new ChatMessage(ChatRole.User, message.Message));
await foreach (var update in agent.RunStreamingAsync(turn, session)) { ... }
```

**An agent is missing from `GET /a2a`.** With `Discovery:AgentTypes` set to `Described` the agent
type needs a `[Description]`. Check too that it is not `[FabrCoreHidden]` — that hides it from
`/fabrcoreapi/discovery` and from A2A alike — and that no `ExcludeAgentTypes` glob catches it.

**A live agent is published as `system-assistant` rather than `assistant`.** Its bare handle was
already claimed by a configured agent or by another principal's agent of the same name, so it is
republished under its fully-qualified name rather than dropped. The startup log names it.

**The connected agent is never called, and no request reaches your server.** In order:

1. **Generative orchestration must be on** (Settings → Generative AI → Orchestration). Under
   classic orchestration connected agents are never invoked, whatever the card says.
2. **The description must not be blank.** Copilot Studio's dialog leaves it empty when card
   discovery "fails", and the orchestrator routes on that field alone.
3. **The primary agent's own knowledge sources compete for the same query.** If it keeps answering
   from a website source, remove that source to prove the routing, then put it back.
4. Only then tune the card: a discovered agent's card comes from its `[Description]`,
   `[FabrCoreCapabilities]`, and `[FabrCoreNote]` — improve those and every surface improves at
   once, or add an `Agents` entry to curate just that one card.

Confirm from your side rather than guessing: log requests and see whether a `POST` arrives at all.
No POST means the failure is upstream in Copilot Studio and nothing about your card or agent is
implicated.

**Copilot Studio's tooltip and its documentation disagree on the URL to enter.** The in-product
tooltip says the base URI (`https://your-domain.com/a2a`, card at `{base}/.well-known/agent-card.json`);
[the Learn article](https://learn.microsoft.com/en-us/microsoft-copilot-studio/add-agent-agent-to-agent)
and its quickstart say the message endpoint (`…/v1/message:stream`). FabrCore serves the card at
every location either reading implies, so discovery works with both — but **enter the message
endpoint**. Because the card is never actually read, Copilot Studio POSTs to whatever URL you typed,
and `/a2a` only answers `GET`.

**Calls return 401.** The card is anonymous but calls are not. Confirm Copilot Studio is sending
the header name your config expects, and that the proxy forwards it.

**Responses arrive empty.** If a custom client reads only the top-level result, set
`A2A:Interop:CompatibilityResultShape` to `Message` (the default) rather than `Task`.

**Streaming hangs.** A buffering proxy. Disable buffering for `/a2a/*` and confirm the read timeout
exceeds `ExecutionTimeout`.

**The task times out.** Raise `A2A:Tasks:ExecutionTimeout`, and check the agent's own health
through `/fabrcoreapi/agent/health/{handle}`.

## Relationship to the Microsoft 365 Copilot addon

The two solve different problems and can run side by side:

| | `FabrCore.Services.Microsoft365Copilot` | A2A (built into `FabrCore.Host`) |
| --- | --- | --- |
| Protocol | Azure Bot Service Activity Protocol | Agent2Agent |
| Endpoint | `/api/messages` | `/a2a/{agent}` |
| Identity | Each Microsoft 365 user, via Entra | The calling agent or tenant |
| Use it for | A user chatting with your agent in Copilot or Teams | Copilot Studio orchestrating your agent as one of several |
