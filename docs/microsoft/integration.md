# FabrCore and the Microsoft 365 Copilot ecosystem

How FabrCore and Insights fit into Microsoft 365 Copilot, what each Microsoft extension point offers,
where Entra Agent ID fits, and what to change in both products so that onboarding a customer tenant
is as close to "consent once, publish once" as Microsoft allows.

This document is the strategy and design layer. It does not replace the existing how-to and
reference documents; it links to them.

| Read this for | Document |
| --- | --- |
| What ships today, the native-integration backlog (B1–B19), production how-to | [microsoft-365-copilot-native-integration.md](../microsoft-365-copilot-native-integration.md) |
| How a cloud server provisions and manages the integration, phases 0–5 | [cloud-managed-copilot-integrations-plan.md](../cloud-managed-copilot-integrations-plan.md) |
| Wire-level guide for a cloud-server team | [cloud-server-microsoft-365-copilot.md](../cloud-server-microsoft-365-copilot.md) |
| Connections, Entra Agent ID exchanges, Work IQ and Copilot Studio remote agents | [connections-and-microsoft-integration.md](../connections-and-microsoft-integration.md) |
| The A2A endpoint and Copilot Studio | [a2a.md](../a2a.md), [channel-agent-identity.md](../channel-agent-identity.md) |
| Proactive delivery | [principal-message-relays.md](../principal-message-relays.md) |
| Copilot/Teams add-on quickstart and options | [add-on README](../../src/FabrCore.Services.Microsoft365Copilot/README.md) |

**Sources.** Two Microsoft Learn exports in this folder, read in full:
`microsoft-365-copilot-extensibility.pdf` (334 articles, pages dated up to October 2026) and
`entra-agent-id.pdf` (68 articles plus the Graph permission and resource references, pages dated up
to September 2026). Statements about FabrCore were checked against the `develop` working tree on
24 October 2026, which includes the uncommitted several-agents-per-host rework; statements about
Insights were checked against the `C:\repos\FabrCore-V365` working tree on the same day. Microsoft
statements describe documented contracts. Several conflict with each other; those are called out
where they matter and collected in [section 13](#13-open-questions-to-settle-in-a-real-tenant).

**Vendor neutrality.** Everything proposed for FabrCore is a protocol or host capability that any
cloud server can drive. Everything specific to Vulcan365's product goes in Insights. FabrCore never
learns that Insights exists.

---

## Contents

1. [Summary](#1-summary)
2. [The goal: Copilot first, FabrCore where Microsoft stops](#2-the-goal-copilot-first-fabrcore-where-microsoft-stops)
3. [Microsoft's extension points](#3-microsofts-extension-points)
4. [Every path between Microsoft 365 and FabrCore](#4-every-path-between-microsoft-365-and-fabrcore)
5. [What FabrCore and Insights do today](#5-what-fabrcore-and-insights-do-today)
6. [Why onboarding is hard today](#6-why-onboarding-is-hard-today)
7. [Target onboarding experience](#7-target-onboarding-experience)
8. [Changes to FabrCore](#8-changes-to-fabrcore)
9. [Changes to Insights](#9-changes-to-insights)
10. [Entra Agent ID: what it is, what we built, and whether to use it](#10-entra-agent-id-what-it-is-what-we-built-and-whether-to-use-it)
11. [How-to recipes](#11-how-to-recipes)
12. [Roadmap](#12-roadmap)
13. [Open questions to settle in a real tenant](#13-open-questions-to-settle-in-a-real-tenant)
14. [Reference](#14-reference)

---

## 1. Summary

**Where FabrCore fits.** Microsoft gives an outside platform four ways into Microsoft 365 Copilot,
and FabrCore can use all four:

1. **Be an agent** that users talk to in Copilot and Teams: a *custom engine agent* behind an Azure
   Bot. FabrCore does this today.
2. **Be a tool** that Microsoft's own agents call: a remote **MCP server**, referenced from a
   declarative agent, a Copilot Cowork plugin, Copilot Studio, or Agent 365's tool registry.
   FabrCore does not do this yet. It is the largest missing piece.
3. **Be a peer agent** over **A2A**: Copilot Studio calls FabrCore (works today); Agent 365 can
   hold a registration with FabrCore's agent card (preview API).
4. **Be a source of knowledge**: a Copilot connector that indexes FabrCore content into Microsoft
   Graph, or serves it in real time.

FabrCore reaches back into Microsoft 365 through **Work IQ** (A2A to Copilot or to any Copilot
agent by ID, MCP tools over Graph, REST chat), the **Microsoft 365 Copilot APIs** (Retrieval,
Search, Chat, meeting insights), Copilot Studio's direct-connect client, and plain Graph.

**What works today.** Copilot and Teams 1:1 chat with several FabrCore agents per host,
proactive messages to Teams, Copilot Studio calling FabrCore over A2A, and FabrCore calling Work IQ
and Copilot Studio as remote agents. Insights composes and publishes the host settings and hands
the operator an app package. Everything on the Microsoft side (app registrations, Azure Bots,
scopes, consent, package upload, Copilot Studio wiring) is manual, and the whole flow has not yet
been run against a real customer tenant.

**The changes that matter most**, in order:

| # | Change | Where | Why |
| --- | --- | --- | --- |
| 1 | Run the existing flow end to end in a real tenant (plan Phase 0) and record what Microsoft actually does | Both | Nothing below is safe to build until the bot, SSO, A2A, and package steps are proven |
| 2 | Managed provisioning: a Vulcan365 multi-tenant app the customer admin consents to once, which then creates everything Insights now asks the customer to create by hand | Insights | Removes the 8–10 manual steps per agent (section 6) |
| 3 | Bridge the Teams/Copilot sign-in token into Connections (B1), with per-agent sign-in handlers | FabrCore | Without it no FabrCore agent can call Work IQ, Graph, or Copilot Studio as the user from a Copilot conversation |
| 4 | Expose FabrCore agents as a remote MCP server (B8) | FabrCore | One endpoint unlocks declarative agents, MCP apps UI, Cowork, Copilot Studio tools, Agent 365's tool registry, and federated connectors, all without an Azure Bot |
| 5 | Meet Copilot's custom-engine-agent rules: 15-second first response, 45-second streaming heartbeat, `copilot` bot scope, feedback handler, citations, three prompt starters | FabrCore | Required for a good experience and for Agent Store certification |
| 6 | A generic A2A client with Work IQ agent targeting (B12) | FabrCore | Lets a FabrCore agent call any Copilot agent in the tenant, not just Work IQ's default agent |
| 7 | Tenant-level connection templates instead of per-user profiles (B11) | FabrCore + Insights | Today every user needs a hand-made connection profile before an agent can reach out |
| 8 | Bind each Insights tenant to an Entra tenant and let the customer's admin sign in | Insights | Required for consent-based onboarding and self-service |

**Entra Agent ID verdict.** It is useful, but not yet, and not on its own. What FabrCore built is a
correct implementation of Microsoft's token exchange for agent identities. It has never been used
because nothing creates the blueprint or agent identities, because delegated mode needs a user token
addressed to the blueprint (which the Teams sign-in does not produce), and because connection
profiles are per user. Agent ID becomes valuable when FabrCore agents need to appear as governed
agents in the customer's tenant: in the Agent 365 registry, under the customer's Conditional Access,
with per-agent audit, or as "digital workers" with their own mailbox. Recommended path: keep the
code, test it once in a dev tenant using the runbook in [section 10.8](#108-how-to-test-what-we-built),
and adopt Agent ID in Phase D of the roadmap, using the Agents SDK's own "agentic" token support
(present in the SDK version FabrCore already uses) at runtime and the cloud server for provisioning. Details in [section 10](#10-entra-agent-id-what-it-is-what-we-built-and-whether-to-use-it).

---

## 2. The goal: Copilot first, FabrCore where Microsoft stops

The business model is to onboard customers onto Microsoft 365 Copilot and use FabrCore where
Microsoft becomes too expensive, too restrictive, or lacks a feature. That only works if a FabrCore
agent feels like part of the customer's Copilot, and if Copilot agents and FabrCore agents can hand
work to each other.

### 2.1 When Microsoft's own agents are the right answer

| Need | Use Microsoft | Use FabrCore |
| --- | --- | --- |
| Answer questions over SharePoint, mail, Teams, meetings for licensed users | Declarative agent or Agent Builder agent | — |
| Simple knowledge agent built by a business user | Agent Builder, Copilot Studio | — |
| Own orchestration, multi-agent workflows, business rules | — | Custom engine agent |
| Own or fine-tuned models, model routing, cost control per call | — | Custom engine agent |
| Users without a Copilot license | Copilot Chat with pay-as-you-go credits | Custom engine agents need no Copilot license (see 2.2) |
| Agent-initiated messages, long-running work, scheduled jobs | Declarative agents cannot do this | Custom engine agent (Teams; see the Copilot limitation in 4.2) |
| Channels outside Microsoft 365 (web, Slack, SMS, API) | Copilot Studio partly | FabrCore natively |
| Durable memory, per-user state, evaluation harness, own telemetry | Limited | FabrCore |
| Data that must stay in a regulated store | Federated connector | FabrCore as an MCP server or federated connector |

Microsoft's own decision table recommends a custom engine agent for "an existing conversational
assistant built outside Copilot", for proactive messaging, for group and channel collaboration, for
custom models and orchestration, and for availability outside Microsoft 365. That describes FabrCore.

### 2.2 Who pays for what

From "Plan licensing and cost", the declarative-agent schema reference, the FAQ, and the Copilot API
articles:

| Item | Cost model |
| --- | --- |
| Microsoft 365 Copilot add-on license | Per user. No extra charge for connectors, agents, plugins. Unlocks Copilot in Word, Excel, Outlook, Teams |
| Microsoft 365 Copilot Chat | Included with eligible Microsoft 365 plans. Instruction-only and web-grounded agents are free; grounding on shared tenant data (SharePoint, connectors) is metered in Copilot Credits through Copilot Studio pay-as-you-go |
| Declarative agent capabilities other than web search | Need a Copilot license or metered usage (declarative agent schema 1.8). SharePoint and OneDrive knowledge needs an active Copilot license (known issues) |
| **Custom engine agents** | **No Copilot license needed** to use them in Copilot Chat; credits apply only if the agent uses Microsoft's grounding on shared tenant data. Hosting (bot service, compute, models) is ours |
| Work IQ (A2A, MCP, REST) | Copilot Credits, regardless of Copilot license, for custom and third-party agents (one article disagrees; see Q16). The calling user must be in a spending policy that includes Work IQ, or calls fail with 403 |
| Retrieval API | Free for Copilot-licensed users. Pay-as-you-go preview for unlicensed users: $0.10 per call, SharePoint and connectors only, needs at least one Copilot license in the tenant |
| Graph Copilot Chat API, Search API, meeting AI insights, interaction export | Copilot license per user; no pay-as-you-go |
| Agent 365 | Per-user license. Needed for Package Management API, and (with Entra P1/P2 or E5/E7) for Conditional Access, ID Protection, and governance of agents |
| Entra Agent ID itself | Available to all Entra customers |
| Government clouds | No usage-based billing for extensibility in GCC/GCCH; no tenant-data agents in GCC, GCCH, DoD; no authenticated custom actions in government tenants |

The economic argument for FabrCore is therefore concrete: a FabrCore custom engine agent can serve a
user who has no Copilot license, and FabrCore can buy Microsoft intelligence per call (Work IQ,
Retrieval pay-as-you-go) only when a task needs Microsoft 365 data.

### 2.3 Terms that constrain the positioning

The "Microsoft 365 Copilot APIs Terms of Use" (preview, December 2025) cover the Copilot REST APIs,
**the Microsoft 365 Agents SDK**, and the Copilot client libraries. Three clauses bear directly on
this business model and need a legal reading before anything is marketed:

* You may not use the APIs or data obtained through them "for the purpose of migrating Customers
  away from a Microsoft Offering unless approved by your customers", nor make an application
  available in a way that circumvents users' need for a valid Microsoft license.
* "ISVs are prohibited from using a customer as a proxy to set up a single tenant use and must
  register as multi-tenant." The current Insights design creates or asks for a **single-tenant**
  app registration in each customer tenant (section 4 of the plan). This
  clause may require the Copilot-API-calling identity to be a Vulcan365 multi-tenant app. See
  [section 13](#13-open-questions-to-settle-in-a-real-tenant).
* "You may not create an Application that is not human directed, such as bots, multiplexing or
  similar." Taken literally this conflicts with Microsoft's own custom-engine-agent guidance; in
  practice it argues for keeping every Copilot API and Work IQ call delegated, user-initiated, and
  never shared across users.

Position FabrCore as **complementary and customer-approved**: it extends the customer's Copilot,
respects licensing, and never resells Microsoft data access.

---

## 3. Microsoft's extension points

### 3.1 The map

"Plugin" changed meaning in 2026. A **Microsoft 365 Copilot plugin** is now an installable
Microsoft 365 app package that groups declarative agents, skills, and references to Copilot
connectors and remote MCP servers. It holds definitions and references; MCP servers, APIs, and
connector services run elsewhere. The older meaning, an "API plugin" as an action inside a
declarative agent, survives in the TypeSpec and Agents Toolkit articles. **Custom engine agents are
not plugin components**; they keep their own app-package path.

| Surface | What it is | Who orchestrates | Who hosts | FabrCore's role | FabrCore today |
| --- | --- | --- | --- | --- | --- |
| **Custom engine agent** | Your agent behind an Azure Bot, listed in the Agent Store, used in Copilot Chat and Teams | You | You | The agent | **Built** (`FabrCore.Services.Microsoft365Copilot`) |
| **Declarative agent** | Instructions, knowledge, and actions on Copilot's orchestrator and models | Microsoft | Microsoft | A tool it calls (MCP or OpenAPI action) | Not built |
| **MCP plugin action** | A remote MCP server as an action of a declarative agent (plugin manifest 2.4 `RemoteMCPServer`) | Microsoft | You | The MCP server | Not built (B8) |
| **API plugin action** | A REST API with an OpenAPI description as an action | Microsoft | You | The API | Not built |
| **MCP apps** | Interactive UI widgets returned by MCP tools, rendered in Copilot Chat | Microsoft | You | Widget provider | Not built |
| **Connected agents** (`worker_agents`) | A declarative agent delegating to other declarative agents | Microsoft | Microsoft | Only through a proxy declarative agent | Not applicable |
| **Skills** | A folder with `SKILL.md` (Agent Skills format, same as Claude) | Microsoft | Microsoft | Ship know-how about our tools | Not built |
| **Copilot Cowork plugin** | App package with skills (`agentSkills`) and MCP connectors (`agentConnectors`) | Microsoft | You (connectors) | MCP connector | Not built |
| **Synced Copilot connector** | Content indexed into Microsoft Graph's semantic index | Microsoft | You (crawler) | Knowledge source | Not built |
| **Federated Copilot connector** | Real-time retrieval over MCP, no indexing | Microsoft | You | MCP server | Not built |
| **Copilot Studio** | Low-code agents; connects to A2A agents and MCP servers | Microsoft | Microsoft | A2A agent; MCP server | **A2A built** |
| **Agent 365** | Microsoft's control plane: agent registry, tools registry, `agenticUserTemplate` agents with Entra identities | — | — | Registered agent, registered MCP server, governed identity | Not built |
| **Agent Registration API** (beta) | Graph API to register an agent with an A2A agent card | Microsoft (how it calls the agent is not yet documented) | You | Registered A2A agent | Not built |
| **Work IQ** | Copilot's reasoning and Microsoft 365 data as A2A, MCP, and REST services | Microsoft | Microsoft | Client | **A2A client built** (Work IQ only) |
| **Copilot APIs** | Retrieval, Search, Chat, meeting insights, interaction export, reports, package and cost management | Microsoft | Microsoft | Client | Through Connections (generic HTTP) |
| **Entra Agent ID** | Agent identities: blueprint, agent identity, agent user | — | — | Identity provider for our agents | **Token exchange built**, untested |

### 3.2 Custom engine agents (what FabrCore is today)

* Built with the Microsoft 365 Agents SDK (FabrCore uses 1.8.77), the Teams SDK, Copilot Studio, or
  Foundry. "Bring your agents into Microsoft 365 Copilot" describes **one** route for an existing
  external agent: wrap it with the Agents SDK, create an Entra app registration and an Azure Bot,
  and point an app manifest at the hosted endpoint. FabrCore follows exactly that route.
* Manifest: schema **1.21 or later**; `copilotAgents.customEngineAgents: [{ "type": "bot", "id": <botId> }]`
  plus a `bots` entry. Microsoft's conversion sample uses bot scopes `["copilot", "personal", "team"]`
  with command-list scopes `["copilot", "personal"]`; the Agent Store rules require that
  `bots.scopes` and `commandLists.scopes` **match** and **include `copilot`**, so copy the idea, not
  the sample. FabrCore emits `personal` only.
* A package can contain a declarative agent **or** a custom engine agent, not both. One bot per
  manifest, so one package per agent.
* Updates that change permissions make users re-consent; users who pinned the agent must re-pin.
* **Timing:** first response within **15 seconds**; no more than **45 seconds** between streaming
  updates. One stream per turn, `endStream` before anything else, one send at a time, `replyToId`
  for follow-ups.
* Expected UX: prompt starters, suggested prompts, streaming, citations, AI-generated label,
  feedback buttons. The Agent Store requires the AI label, feedback, citations, streaming, at least
  three prompt starters or a welcome message, two context-specific suggestions, p50 ≤ 2 s,
  p75 ≤ 5 s, p99 ≤ 9 s latency, and 99.9% availability.
* **Known limitations in the Microsoft 365 Copilot surface** (known issues, 30 September 2026):
  feedback is not shared with the developer; no access to Copilot conversation history before the
  @mention; messages cannot be updated; no file or image attachments to the agent; no file
  downloads; several rich card types unsupported; **proactive notifications not supported**;
  sensitivity labels unsupported; not available in Outlook, Word, Excel, PowerPoint, or Edge.
  Microsoft says Teams still works for proactive messages and file downloads; for the other items
  assume the limitation may also apply in Teams until tested.
* Privacy: custom engine agent prompts and responses in Copilot Chat and Teams are stored under the
  Microsoft 365 product terms and are discoverable in Purview Content Search.
* Microsoft says pro-code agents reach Microsoft 365 data "through Microsoft Graph APIs and the
  Retrieval API", and its architecture article lists "limited options for Microsoft tool calling":
  built-in tools such as the image generator are a poor fit for custom engine agents.

### 3.3 Declarative agents and their actions

* Schema 1.8 (July 2026): `instructions` up to 8,000 characters, up to 12 conversation starters,
  `actions` (1–10 plugin references), `capabilities` (WebSearch, OneDriveAndSharePoint,
  GraphConnectors, Email, **EmailActions**, TeamsMessages, People, Meetings, **MeetingActions**,
  Dataverse, CodeInterpreter, GraphicArt, EmbeddedKnowledge, ScenarioModels), `worker_agents`,
  `behavior_overrides`, `user_overrides`.
* **Connected agents** (`worker_agents`, schema 1.6+, still marked preview in the 1.8 reference): a declarative agent lists other declarative
  agents by title ID (`U_`, `T_`, or `P_` prefix). Copilot Chat brokers the exchange using the
  worker's name, description, and starter text. Text only. **Only declarative agents can be
  workers**; Microsoft explicitly says to use MCP or API plugins to reach other systems. A FabrCore
  custom engine agent cannot be a worker. A thin "proxy" declarative agent whose only action is
  FabrCore's MCP server can be one.
* **Actions** are MCP servers or OpenAPI APIs. Plugins exist only as actions inside a declarative
  agent.
  * MCP: plugin manifest 2.4 runtime `{ "type": "RemoteMCPServer", "spec": { "url": ... } }`,
    JSON-RPC `tools/list` and `tools/call`. Tools are discovered **at runtime** by default
    (`functions: []`, `run_for_functions: ["*"]`), which allows a per-user or per-tenant tool list
    without republishing. Microsoft screens new or changed tools at runtime and blocks failing ones.
  * Up to 5 plugins in an agent are always in the prompt; above 5, Copilot selects by plugin
    description. Every tool of a selected plugin is injected; quality drops beyond about 10 tools.
    Large inputs and outputs are truncated. Developer mode reports a 10-second plugin execution
    timeout; Cowork asks for under 30 seconds per tool call.
  * Results: return JSON inside the MCP text content block; Copilot parses it and builds clickable
    citations from common fields (`results[]`, `url`, `title`). Mark read-only tools with
    `annotations.readOnlyHint: true` to skip repeat confirmations after the first use. (In Cowork,
    tools without annotations are treated as destructive; annotate every tool.)
* **Authentication for MCP and API actions.** Credentials live in Microsoft's Enterprise token
  store; the manifest carries only `"auth": { "type": "OAuthPluginVault" | "ApiKeyPluginVault" | "None", "reference_id": ... }`.

  | Scheme | MCP | API |
  | --- | --- | --- |
  | Entra SSO | Yes | Yes |
  | OAuth 2.0 authorization code (PKCE on by default) | Yes | Yes |
  | Dynamic client registration (RFC 7591) | Yes, but **not for Entra-protected servers** | No |
  | API key | No | Yes |
  | None | Dev only | Dev only |

  Fixed values: OAuth redirect `https://teams.microsoft.com/api/platform/v1.0/oAuthRedirect`; SSO
  consent redirect `https://teams.microsoft.com/api/platform/v1.0/oAuthConsentRedirect`; token-store
  client ID `ab3be6b7-f5df-413d-ac2d-abf1e3fd9c0b` (pre-authorize it and accept it as `azp`).
  Traps: binding an OAuth registration to one Teams app makes every MCP tool call return 404 (use
  "Any Teams app"); "Any Microsoft 365 organization" is the multi-tenant setting; scope consent is
  not checked at provisioning; token endpoints that answer 307 are unsupported; the registration's
  base URL must match the server URL exactly.
* **MCP apps**: a tool sets `_meta.ui.resourceUri` to a `text/html;profile=mcp-app` resource (a
  single-file bundle). CORS must allow `{sha256(domain)}.widget-renderer.usercontent.microsoft.com`.
  Auth is OAuth 2.1 or Entra SSO. UX rules: inline widget first, at most two actions, explicit
  state, Fluent 2, optional side-by-side mode. OpenAI Apps SDK widgets are also accepted.

### 3.4 Copilot Cowork plugins

* App packages (manifest 1.28 or 1.29) with `agentSkills[]` (up to 20) and `agentConnectors[]` (up
  to 10 remote MCP servers). **No agent component**: FabrCore can be a toolset in Cowork, not a
  Cowork agent.
* Connector requirements: Streamable HTTP over TLS 1.2+, `tools/list` and `tools/call`, under 30 s
  per call, 99.9% uptime recommended for store apps. Auth `OAuthPluginVault` (set "Any Microsoft
  365 Organization") or DCR; DCR without an `authorization` node works in Cowork but **not** in
  Copilot Chat; API keys not yet supported. File inputs use `contentEncoding: base64` (8 files,
  150 MiB). Cowork identifies itself with `User-Agent: copilot-cowork/...`.
* Ship a tool-description file: one article says Cowork ignores it, another says connection
  verification fails without it.
* Tooling: `wiqd plugin create | add skill | add connector | validate --mode deep | package | share`
  (alpha), or `atk import openplugin`, which imports Claude, Cursor, and Open Plugin formats,
  including `.mcp.json` and `skills/`.

### 3.5 Copilot connectors

* **Synced**: index items into Microsoft Graph through the connectors API (`externalConnection`,
  schema, `externalItem` with ACLs, external groups). Semantic labels (`title`, `url`,
  `lastModifiedDateTime`, `iconUrl` and others) and the `content` property drive relevance. For
  results in Copilot Chat an admin must turn on "Include connector results" (not needed when the
  connector is only a declarative agent's knowledge source). Items count against the tenant item quota. Results feed
  Copilot, Microsoft Search, Researcher, and Copilot in Excel.
* **Federated**: real-time retrieval over MCP; data stays in the source.
* For FabrCore: a synced connector could publish FabrCore knowledge (GraphRAG corpora, agent
  outputs) into Copilot with ACLs mapped to Entra users and groups; a federated connector is the same
  MCP server as section 3.3.

### 3.6 Copilot Studio

* Inbound to FabrCore: "connected agent" over **A2A** (works today; see [a2a.md](../a2a.md)). A
  connected agent is invoked only under generative orchestration and is selected by its
  description. The card is fetched from the browser, so card routes need CORS.
* Copilot Studio can also use MCP servers (MCP Server Connector) and is a supported client of Agent
  365's "bring your own remote MCP server" registration.
* New Copilot Studio agents have had Entra agent identities since 18 March 2026, so tokens from
  Copilot Studio agents may carry agent claims (`xms_par_app_azp`, `xms_act_fct`).
* Custom engine agents built in Copilot Studio cannot send asynchronous messages, which is a
  differentiator for FabrCore.

### 3.7 Agent 365 and agent registration

Agent 365 is "the administrative control plane for observing, governing, and securing agents and
the tools they use", in the Microsoft 365 admin center:

| Admin surface | Holds |
| --- | --- |
| Agents > All agents > Registry | Agent Registry: every agent, packaged or not, with owners, availability, lifecycle, Entra Agent ID |
| Agents > All agents > Requests | Agent submissions, updates, activation requests |
| Agents > Tools > Registry / Plugins / Requests | Plugins, skills, MCP servers, connectors; standalone MCP server registration requests |
| Agents > Settings > Agent and plugin access | Allowed publisher categories |

Controls do not propagate: approving or blocking a package does not approve or block the MCP
servers, connections, or external services it references.

Three ways for a FabrCore agent to appear there:

1. **Packaged**: the custom engine agent's app package, uploaded by an admin or through Partner
   Center. The **Package Management API** (`/v1.0/copilot/admin/catalog/packages`, list and get with
   `CopilotPackages.Read.All`, delegated or application; block, unblock, reassign, and availability updates are beta
   and delegated only; needs an Agent 365 license) reports block state, availability, assignments,
   pending requests, and 30-day usage. There is no documented upload endpoint.
2. **Registered**: the **Agent Registration API** (beta, April 2026): `POST/GET/PATCH/DELETE
   /beta/copilot/agentRegistrations/{id}` with `displayName`, `createdBy`, source timestamps,
   `ownerIds` or `managedByAppId`, `sourceAgentId`, `originatingStore`, optional
   `agentIdentityId`/`agentIdentityBlueprintId`, and `agentCard` (an A2A card as JSON). A newer
   `a2aAuthorization` complex type (21 October 2026: `none`, `oAuthPluginVault`, `apiKeyPluginVault`,
   `dynamicClientRegistration`, `connectionVault`, plus a `referenceId`) is "authorization
   configuration used to invoke an Agent2Agent server", but it is **not yet a property of
   `agentRegistration`**, and nothing documents that Microsoft invokes registered agents today. There
   is no list operation, and Microsoft does not document where a registered agent surfaces for users.
   Treat registration as inventory and governance for now.
3. **As an Agent 365 agent** with an `agenticUserTemplate` manifest node: an agent with an Entra
   agent identity blueprint and agent users ("digital workers"). Store rules: emit observability
   traces (invoke agent, execute tool, inference call) into Sentinel, Defender, and Purview for both
   blueprint and agent IDs; unique blueprint ID; not bundled with other offer types. Entra's docs
   say a fully provisioned agent user (mailbox, Teams presence) must be created through Teams /
   Agent 365. The Entra docs disagree on who provisions: the blueprint-creation article says "the
   Microsoft 365 Agents SDK ... handles agent identity creation and registration in the Agent 365
   registry for you"; the Entra FAQ says the **Agent 365 CLI** (`a365 setup all`) provisions the
   blueprint, identities, and permissions, and the SDK handles **token acquisition at runtime**.

The Agents SDK assemblies FabrCore already references (1.8.77) contain the runtime side: types and
members such as `AgenticUserAuthorization`, `IAgenticTokenProvider`, `GetAgenticInstanceTokenAsync`,
`GetAgenticUserTokenAsync`, `IsAgenticRequest`, `RouteIsAgentic`, and activity fields
`AgenticAppId`/`AgenticUserId` (found by searching the assemblies; behavior not yet verified). No
provisioning or registry symbols were found, so plan on the `a365` CLI or Graph for provisioning.
FabrCore does not use any of them.

### 3.8 Work IQ (Copilot as a service FabrCore can call)

Work IQ became generally available on 16 June 2026 and is Microsoft's stated foundation for new
agentic applications over Microsoft 365 data.

| Interface | Endpoint | Use |
| --- | --- | --- |
| A2A | `POST https://workiq.svc.cloud.microsoft/a2a/` (JSON-RPC 2.0) | Delegate a task to Microsoft 365 Copilot or to **any Copilot agent in the tenant** at `/a2a/{agent-id}` |
| Remote MCP | `https://workiq.svc.cloud.microsoft/mcp` | About ten generic tools over Graph v1.0 paths: `fetch`, `fetch_blob`, `create_entity`, `update_entity`, `delete_entity`, `do_action`, `call_function`, `ask` (optional `agentId`), `retrieve` (preview), `list_agents`, `get_schema`, `search_paths` |
| REST | `POST /rest/conversations`, `/{id}/chat`, `/{id}/chatOverStream` | Copilot answers with citations; text only, no actions, no long-running tasks |

* **Auth: delegated only.** Scope `api://workiq.svc.cloud.microsoft/WorkIQAgent.Ask` (admin consent
  required). On-behalf-of works for confidential clients. No application permission exists.
* The token issuer must be the user's **home** tenant; Microsoft says to register the calling app as
  multi-tenant and sign users in through their home-tenant authority.
* A2A rules: send `A2A-Version: 1.0` (without it Work IQ assumes 0.3, and 1.0 method names return
  `-32601`); every message needs `metadata.Location` with a time zone; continue with `contextId`;
  push notifications unsupported. Agent IDs (`U_`, `T_`, `P_` prefixes) come from `list_agents` or
  the Copilot Chat URL and are opaque.
* MCP rules: mutations are blocked by tenant policy by default (Agents > Tools > Work IQ MCP >
  Policy, up to 24 hours to propagate); collections default to 25 and cap at 100; chats cap at 10 per
  request; no server-side retries.
* Tenant prerequisites: create the Work IQ service principal once
  (`POST /v1.0/servicePrincipals` with `{"appId":"fdcc1f02-fc51-4226-8753-f668596af7f7"}`), grant
  admin consent, set up Copilot Credits billing, and put the users in a spending policy that
  includes Work IQ.
* Error map: 401 wrong audience; 403 without a scope message = user not in a billing plan; 403 with
  "Required scopes" = consent missing; empty 200 = license or index not ready yet.

### 3.9 Microsoft 365 Copilot APIs (Graph)

| API | Endpoint | Permissions | License |
| --- | --- | --- | --- |
| Retrieval (v1.0) | `POST /v1.0/copilot/retrieval` | Delegated `Files.Read.All` + `Sites.Read.All`, `ExternalItem.Read.All` | Copilot license, or pay-as-you-go |
| Search (beta) | `POST /beta/copilot/search` | Delegated `Files.Read.All` + `Sites.Read.All` | Copilot license |
| Chat (beta) | `POST /beta/copilot/conversations/...` | Seven delegated Graph scopes | Copilot license |
| Meeting AI insights | `GET /v1.0/copilot/users/{id}/onlineMeetings/{id}/aiInsights` | Delegated or application `OnlineMeetingAiInsight.Read.All` | Copilot license |
| Interaction export | `GET /v1.0/copilot/users/{id}/interactionHistory/getAllEnterpriseInteractions` | Application `AiEnterpriseInteraction.Read.All` | Copilot license |
| Usage reports | `GET /v1.0/copilot/reports/getMicrosoft365Copilot...` | `Reports.Read.All` (delegated or application) | — |
| Package management | `/v1.0/copilot/admin/catalog/packages` | `CopilotPackages.Read.All` (delegated or application for reads; writes delegated only) | Agent 365 |
| Cost management (beta) | `/beta/copilot/costManagement/...` | `CopilotCostManagement-*`, mostly delegated | — |
| Agent registration (beta) | `/beta/copilot/agentRegistrations` | `AgentRegistration.Read.All` / `.ReadWrite.All` | — |

Retrieval limits: 200 requests per user per hour, at most 25 results, one data source per request,
`$batch` up to 20; invalid KQL silently runs unscoped. Client library: NuGet
`Microsoft.Agents.M365Copilot` (a module of the Agents SDK). The Chat API cannot take actions
(send mail, create events); those need Graph or Work IQ MCP.

### 3.10 Entra Agent ID

Summarized here; full treatment in [section 10](#10-entra-agent-id-what-it-is-what-we-built-and-whether-to-use-it).
A **blueprint** (an application object that holds all credentials) is instantiated in each tenant
as a **blueprint principal**, which creates **agent identities** (single-tenant service principals
that hold permissions but no credentials). An optional **agent user** pairs 1:1 with an agent
identity for systems that need a user object. Tokens are obtained by a two-step exchange in which
the blueprint impersonates the agent identity. Agent ID is GA; customer-side Conditional Access,
ID Protection, and governance for agents need Agent 365 licensing.

---

## 4. Every path between Microsoft 365 and FabrCore

### 4.1 Microsoft 365 into FabrCore

| # | Path | Mechanism | Identity FabrCore sees | Status | Notes |
| --- | --- | --- | --- | --- | --- |
| I1 | User chats with a FabrCore agent in Copilot or Teams | Custom engine agent: Azure Bot → `/api/messages/{name}` | Entra user (`tid`, `oid`) → `entra-{tid}-{oid}` with `CanonicalEntra` | **Built** | Personal scope only. Copilot surface limitations in 3.2 |
| I2 | Copilot Studio agent delegates to FabrCore | A2A 1.0, `/a2a/{agent}/message:stream` | User with a delegated token and `CanonicalEntra`; otherwise the app or the shared `a2a` principal | **Built** | Needs generative orchestration and a connection in Copilot Studio |
| I3 | Declarative agent calls FabrCore as a tool | Remote MCP plugin action | Signed-in user through Entra SSO or OAuth | Not built (B8) | Per-tenant package with an auth `reference_id` |
| I4 | Copilot Chat renders FabrCore UI | MCP apps from the same MCP server | Same as I3 | Not built | Approvals, run status, result tables |
| I5 | Declarative agent "hands off" to FabrCore | Proxy declarative agent listed in `worker_agents`, whose only action is FabrCore's MCP server | Same as I3 | Not built | Text only between agents |
| I6 | Cowork uses FabrCore | `agentConnectors` remote MCP server | OAuth or DCR | Not built | No agent component in Cowork |
| I7 | Copilot Studio uses FabrCore tools | Copilot Studio MCP connector, or Agent 365 MCP registration | OAuth | Not built | Agent 365 MCP registration is preview |
| I8 | FabrCore agent listed in Agent 365's registry (and possibly called by Microsoft later) | Agent Registration API; `a2aAuthorization` suggests future invocation | Undocumented | Not built | Preview; invocation and user-facing placement undocumented |
| I9 | Copilot answers from FabrCore knowledge | Synced or federated Copilot connector | Graph ACLs (synced) or user token (federated) | Not built | — |
| I10 | Agent 365 delivers work to a FabrCore "digital worker" | `agenticUserTemplate` + Agents SDK agentic requests | Agent user / agent identity | Not built | Requires Agent ID (section 10) |

### 4.2 FabrCore out to Microsoft 365

| # | Path | Mechanism | Identity used | Status | Notes |
| --- | --- | --- | --- | --- | --- |
| O1 | Agent sends a proactive message to a user | Durable principal outbox → Copilot relay → Agents SDK `Proactive.SendActivityAsync` | Bot app | **Built** | Works for Teams personal chat after the user has spoken once. Microsoft lists proactive notifications as **unsupported in the Microsoft 365 Copilot surface** (known issues, September 2026); an older article (July 2025) shows a Copilot pattern with `channelData.productContext = "Copilot"` and `conversationSubType = "AgentProactive"` against a `canary` host. Treat Copilot-surface proactive as unsupported until tested |
| O2 | Agent asks Microsoft 365 Copilot (Work IQ) | `remote-agent`, `provider = work-iq`, A2A 1.0 | User, delegated (`WorkIQAgent.Ask`) | **Built** | Needs the user's token (B1) |
| O3 | Agent asks a specific Copilot agent (declarative, Copilot Studio, Researcher) | Work IQ A2A at `/a2a/{agent-id}` | User, delegated | Probably works by setting the remote agent's endpoint to the agent path; unverified | No card discovery |
| O4 | Agent uses Microsoft 365 data and actions as tools | Work IQ MCP through FabrCore's MCP client with a Connections-backed resource | User, delegated | Configurable, untested | Writes blocked by tenant policy by default |
| O5 | Agent talks to a Copilot Studio agent | `remote-agent`, `provider = copilot-studio`, direct-connect client 1.8.77 | User, delegated (`CopilotStudio.Copilots.Invoke`) | **Built** | Not A2A |
| O6 | Agent calls any A2A agent | Generic A2A client | Any | Not built (B12) | Needs card discovery and a hop limit |
| O7 | Agent grounds on SharePoint, OneDrive, connectors | Retrieval API or Work IQ `retrieve` through Connections | User, delegated | Possible with Connections + agent code | No packaged tool |
| O8 | Agent reads or writes Graph | Connections HTTP client | User (OBO), app, or agent identity | **Built** (generic) | No Graph tools ship |
| O9 | Agent acts as itself in Microsoft 365 (own mailbox, Teams presence) | Entra agent user | Agent user | Not built | Needs Agent 365 provisioning |

### 4.3 The identity chain is the bottleneck

Every delegated outbound path (O2–O5, O7) needs a user token that FabrCore can exchange. Copilot and
Teams already sign the user in (the Azure Bot OAuth connection and SSO), but that token never
reaches Connections (B1). Today the only way to give Connections a user grant is a separate
consent flow in an external client app, which a Teams or Copilot user has no way to reach. Fixing
B1 is what turns FabrCore from "an agent in Copilot" into "an agent that can work with Copilot".

There are two ways to close it:

1. **Conventional OBO with the bot's app registration** (fits today's design): the SSO token's
   audience is `api://botid-{appId}`, so the bot's own app registration can exchange it on behalf of
   the user for Graph, Work IQ, or Copilot Studio. Connections already has an `OnBehalfOf` mode. The
   bot app needs the delegated permissions and admin consent.
2. **Agent ID OBO** (fits section 10): the user token must be addressed to the blueprint
   (`api://{blueprintAppId}`). Whether the Teams SSO exchange can target the blueprint is unproven.
   Microsoft's own route for this is Agent 365's agentic requests, handled by the Agents SDK.

Do option 1 now; leave option 2 to Phase D.

---

## 5. What FabrCore and Insights do today

### 5.1 FabrCore

| Area | State | Source |
| --- | --- | --- |
| Copilot/Teams channel | Several agents per host; each agent = one Entra app, one Azure Bot, one route (`/api/messages/{Name}`), one package; JWT validation per route by audience; Agents SDK config synthesized from one `Microsoft365Copilot` section | `src/FabrCore.Services.Microsoft365Copilot/` |
| Agent provisioning | An entry names an agent type and handle; missing per-user agents are created from type and models only, with no tools, prompt, args, or connection bindings | `Bridge/CopilotAgentProvisioner.cs` |
| Principal mapping | `CanonicalEntra` gives `entra-{tid}-{oid}` on both channels; the channel default is still `EntraObjectId`, the A2A default `Fixed` | `Bridge/DefaultCopilotPrincipalResolver.cs`, `FabrCore.Host/A2A/Security/A2APrincipalResolver.cs` |
| Streaming | Informative update, then the whole reply as one chunk; AI label on; feedback buttons off by default (`Streaming:EnableFeedbackLoop`), and when turned on the feedback is never handled | `Bridge/FabrCoreCopilotAgent.cs` |
| Cards | `ui.render` Adaptive Cards out; `Action.Submit` back in as `ui.action` | `Bridge/CopilotActivityMapper.cs` |
| Proactive | References stored per user (not per agent), personal chats only, at most 8; durable outbox with retries | `Bridge/CopilotConversationContext.cs`, `Bridge/CopilotPrincipalMessageRelay.cs` |
| SSO | Agents SDK handlers passed through, shared by every agent on the host; token used only for UPN mapping or the unsafe `PassUserTokenToAgent` | `Configuration/AgentsSdkConfigurationBridge.cs` |
| Package | Manifest 1.25, `bots` scope `personal`, `customEngineAgents`, placeholder developer URLs by default | `Packaging/CopilotAppPackageBuilder.cs` |
| Administration | `/fabrcoreapi/admin/v1/integrations/microsoft365` status, findings, per-agent manifest and package, diagnostics; heartbeat flags; every setting needs a restart | `Administration/` |
| A2A server | A2A 1.0 JSON-RPC and HTTP+JSON; card under every path Copilot Studio probes, with CORS; ApiKey (default), JwtBearer, or None; tasks in memory or SQL | `src/FabrCore.Host/A2A/` |
| A2A client | Only `remote-agent` with `work-iq` (A2A 1.0) and `copilot-studio` (direct connect) | `src/FabrCore.Services.RemoteAgents/` |
| Connections | Per-principal token broker: client credentials, auth code + PKCE, OBO, Entra Agent ID app-only and OBO | `src/FabrCore.Connections`, `src/FabrCore.Services.Connections` |
| MCP | Client only (stdio and HTTP, Connections-backed auth); no MCP server | `FabrCore.Sdk/FabrCoreAgentProxy.cs`, `FabrCore.Core/McpServerConfig.cs` |
| Graph | No Graph SDK or tools; Graph through Connections only | — |
| Security | Finding F-02 (HTTP APIs trust `x-user-handle`) still open; only channel and A2A routes may be exposed publicly | `docs/security-review-2026-07-07.md` |

`src/FabrCore.Services.A2A` and its test folder contain only stale `obj/` output and can be deleted.

### 5.2 Insights

Insights' Microsoft 365 page (Cluster → environment → Operations → Microsoft 365) is the "bring your
own app registration" release (plan Phase 2):

* The operator types the customer's tenant ID, the public host name, and, per agent, the client ID,
  credential type, secret and its expiry, agent type, handle, model, app name, description, and
  whether to publish it over A2A for Copilot Studio.
* Insights shows a static checklist of values for the customer to set up, composes and validates the
  host settings (including A2A JwtBearer settings), shows a diff, publishes into the environment's
  configuration with two-version optimistic concurrency, locks the keys it owns, detects drift, and
  audits by key name.
* It reads each host live (add-on present, agents served, pending restart, credential diagnostics)
  and proxies a download of each agent's `appPackage.zip`, with custom icons swapped in.
* The Connections page is a pass-through editor of per-principal connection profiles, including
  both Entra Agent ID modes. It provisions nothing in Entra.

Insights creates nothing in Entra or Azure, calls Graph only for its own operator lookup, cannot
bind an Insights tenant to an Entra tenant, allows one integration per Insights tenant in one
environment, signs operators in only from its home tenant, and has never been run against a real
customer tenant. Much of the multi-agent work is uncommitted.

---

## 6. Why onboarding is hard today

To put **one** FabrCore agent into a customer's Copilot today, someone must:

1. Create a single-tenant Entra app registration in the customer tenant and a client secret (or
   certificate or federated credential), and write down the expiry.
2. For sign-in: set the application ID URI `api://botid-{appId}`, add a scope, pre-authorize the
   Teams, Microsoft 365, and Outlook client IDs, add delegated permissions, and grant admin consent.
3. For Copilot Studio over A2A: add the `agent.invoke` scope.
4. Create an Azure Bot (which needs an Azure subscription and resource group) with the messaging
   endpoint `https://{host}{route}`, enable the Teams channel, and for SSO add an OAuth connection
   with the token exchange URL.
5. Enter all of that in Insights (which publishes `CanonicalEntra` for you), or in `fabrcore.json`
   (where `CanonicalEntra` must be set by hand because it is not the default), plus developer name
   and URLs (the defaults point at GitHub) and icons.
6. Restart the host.
7. Download the package and give it to the customer's admin, who uploads it in the Teams or
   Microsoft 365 admin center (custom app upload must be allowed).
8. Send a Teams message to prove that Microsoft can reach the host.
9. For Copilot Studio: add the A2A agent with the message endpoint, OAuth settings, create and
   authorize the connection, and turn on generative orchestration.
10. For reach-out (Work IQ, Graph, Copilot Studio as the user): enable Connections and remote
    agents in code, create a connection profile **per user** with exact allowed agent handles,
    deploy a `connectedAgents` blueprint per user, and get each user through a separate consent
    client. Teams and Copilot users have no way to do that last step.

Multiply steps 1–8 by the number of agents. Structural friction, beyond the step count:

* **N agents = N app registrations, N bots, N secrets with N expiries, N packages, N uploads.**
  Microsoft caps a manifest at one bot.
* The provisioning script covers about 40% (no exposed API, scopes, pre-authorized clients, OAuth
  connection, consent, or federated credential).
* Unsafe or misaligned defaults: channel `EntraObjectId`, A2A `Fixed`, in-memory turn state,
  placeholder manifest URLs.
* Sign-in handlers are host-wide, so only one bot on a host can sign users in.
* Proactive endpoints are per user, not per agent, so with several bots a notification can arrive
  in the wrong app.
* Agents created through Copilot have no connection bindings.
* Every setting needs a restart.
* Nothing Microsoft-side is verified: tenant ID, client IDs, scopes, bot endpoint, Teams channel,
  package upload.
* Insights cannot run staging and production integrations side by side for the same customer.

---

## 7. Target onboarding experience

The goal for a new customer, as the customer's administrator would experience it:

1. **Sign in to Insights** with their own Entra account (their Insights tenant is bound to their
   Entra tenant).
2. **Consent once** to the Vulcan365 integration app (admin consent link). Optionally consent a
   second, narrower link for delegated permissions (plan decision D4).
3. **Pick agents** from the cluster's catalog and choose where each appears: Copilot and Teams,
   Copilot Studio (A2A), declarative agents and Cowork (MCP), Agent 365 registry.
4. **Review and publish.** Insights creates the Microsoft objects, publishes host settings, applies
   them without a restart where possible, uploads the packages to the org catalog, and runs checks
   that prove Microsoft can reach the host.
5. **See status** per agent: provisioned, published, reachable, credential expiry, usage, blocked
   or not in the admin center, and Copilot Credit spend.

Principles behind it:

* **The host stays dumb.** FabrCore exposes settings, status, diagnostics, packages, and events
  over the vendor-neutral cloud-server protocol. Provisioning, consent, tenant binding, and
  Microsoft API calls live in the cloud server.
* **One Entra tenant per Insights tenant** (the plan's target model), with separate integrations per
  environment.
* **One identity story across channels**: `CanonicalEntra` everywhere, so Teams, Copilot,
  Copilot Studio, MCP, and Work IQ calls all map to the same `entra-{tid}-{oid}` principal and the
  same agent instance.
* **Secrets are fine; their bookkeeping should be automatic** (plan D2): Insights reads expiry from
  Entra rather than having it typed in, and rotates on schedule. Managed identity or federated
  credentials stay available where the hosting supports them.
* **Agent identity later, through the SDK** (plan D5, refined in section 10.10).

---

## 8. Changes to FabrCore

Grouped by theme. IDs reference the backlog in the native-integration guide (B-numbers) where one
exists. Effort is rough: S = days, M = one to two weeks, L = several weeks.

### 8.1 Channel quality (custom engine agent)

| ID | Change | Why | Effort |
| --- | --- | --- | --- |
| FC1 | **Timing guard**: send the informative update immediately; send a streaming heartbeat at least every 30 s; if the agent run exceeds a budget, send an acknowledgement and deliver the result as a follow-up activity **while the turn is still open** (Microsoft's asynchronous-message sample keeps the turn context alive); only fall back to a proactive send for work that outlives the turn, and only to Teams | Copilot requires a first response in 15 s and at most 45 s between updates; FabrCore agents can run for minutes; proactive sends in the Copilot surface are listed as unsupported | M |
| FC2 | **Incremental streaming** (B16): relay `_thinking` and partial text instead of one final chunk. The turn-scoped principal observer catches principal-directed messages, but the add-on README says token-level streaming needs a host-side observer hook that add-ons cannot reach today, so this needs a FabrCore.Host change | Matches Copilot's streaming UX | M |
| FC3 | **Feedback handler**: handle the feedback invoke activity, write it to FabrCore's evaluation and monitoring store, and turn the buttons on by default once handled | Feedback buttons are off by default and, when on, feedback is dropped; required for the store. Microsoft's known issues say feedback in the Copilot surface is not shared with the developer, so expect this to capture Teams feedback only | S |
| FC4 | **Citations**: map agent-provided sources to Agents SDK citation entities | Store requirement; better answers | S |
| FC5 | **Manifest and UX alignment**: add `copilot` to bot scopes and the same scopes to command lists (keep `personal`, offer `team`); require at least three conversation starters or a welcome message; support suggested prompts so an agent can offer two context-specific suggestions; refuse to build a package with placeholder developer URLs outside development; report p50/p75/p99 first-response latency per agent in the status API | Agent Store rules (scopes must match and include `copilot`; latency p50 ≤ 2 s, p75 ≤ 5 s, p99 ≤ 9 s) | M |
| FC6 | **Per-agent sign-in handlers**: allow `UserAuthorization:Handlers` per agent entry, each bound to that agent's Azure Bot OAuth connection | Today only one bot per host can sign users in | M |
| FC7 | **Per-agent proactive endpoints**: key stored conversation references by principal and agent (bot audience); prefer a Teams conversation over a Copilot-surface conversation for proactive sends; detect and mark Copilot-surface conversations as not proactive-eligible until proven | Avoids notifications arriving in another agent's app, and Microsoft lists Copilot-surface proactive as unsupported | M |
| FC8 | **Safe defaults**: default the channel and A2A principal strategy to `CanonicalEntra` when token validation is on; register a durable `IStorage` automatically when the integrated SQL database is enabled | Today's defaults fail the add-on's own findings | S |
| FC9 | **Group chats and channels** (B14), files (B15) | Teams collaboration is a stated custom-engine-agent strength; needs a decision on whose agent answers in a group | L |

### 8.2 Reaching FabrCore without an Azure Bot

| ID | Change | Why | Effort |
| --- | --- | --- | --- |
| FC10 | **MCP server endpoint** (B8): Streamable HTTP at `/mcp` (for example with `ModelContextProtocol.AspNetCore`), `tools/list` per authenticated user, JSON-in-text results with `results[]`/`url`/`title` for citations, MCP `annotations` on every tool, RFC 9728 protected-resource metadata at `/.well-known/oauth-protected-resource`, a 401 `WWW-Authenticate` challenge, Entra token validation with an allow-list of tenants | One endpoint serves declarative agents, MCP apps, Cowork, Copilot Studio, Agent 365's tool registry, federated connectors, and non-Microsoft clients | L |
| FC11 | **Tool surface design**: one tool per published agent (`ask_{agent}(message, threadId?)`) for small catalogs; for large ones a fixed set (`list_agents`, `send_to_agent`, `start_task`, `get_task_status`); keep under about 10 tools per plugin; return a task handle for any run that may exceed 10 s | Copilot injects every tool of a selected plugin; 10 s plugin timeout; 30 s in Cowork | M |
| FC12 | **Token-store compatibility**: accept the token store's client ID (`ab3be6b7-...`) as `azp` and the Microsoft-generated application ID URI as an audience; return 401 to trigger consent when a downstream OBO needs it | Required for Entra SSO auth configs | S |
| FC13 | **MCP apps**: optional `_meta.ui.resourceUri` widgets for approvals, run status, and result tables; CORS for the widget host | Rich UI in Copilot Chat without a bot | M |
| FC14 | **Package builders**: alongside the custom-engine-agent package, generate (a) a declarative-agent package whose action is the host's MCP server (the "proxy" agent usable as a `worker_agent`), and (b) a Cowork plugin package with `agentConnectors` and `SKILL.md` skills; expose both through the existing admin package routes | Lets a cloud server publish every surface from one host | M |
| FC15 | **Registration-ready agent card**: stable `supportedInterfaces` URL, skills, and `securitySchemes` that a future `a2aAuthorization` setting could match | Prepares for the Agent Registration API; cheap, but only worth doing alongside IN12 | S |

### 8.3 Reaching out from FabrCore

| ID | Change | Why | Effort |
| --- | --- | --- | --- |
| FC16 | **SSO-to-Connections bridge** (B1): when the channel obtains a user token, submit it as an OBO assertion to the user's Connections grain for profiles that accept the bot's audience; never put the token in agent args | Unlocks every delegated outbound path from a Copilot conversation | M |
| FC17 | **Tenant-level connection templates** (B11): a connection profile defined once per host (or published by the cloud server) and instantiated per principal on first use, with allowed agents expressed by handle rather than full `principal:handle` | Removes per-user profile creation | M |
| FC18 | **Bindings for channel-created agents**: let a Copilot agent entry reference an `AgentBindings` entry or a blueprint so channel-created agents get their tools, prompt, and connection declarations | Today they get none | S |
| FC19 | **Generic A2A client** (B12): provider `a2a` with card discovery, A2A 1.0 and 0.3, Work IQ agent targeting by ID, `Location` metadata, a hop counter, and Connections-backed auth | Call any Copilot agent, another FabrCore cluster, or a third-party A2A agent | M |
| FC20 | **Microsoft 365 tool presets**: ready-made MCP configurations for Work IQ MCP and a Retrieval API tool, both using delegated Connections resources | Most agents need "search my M365" and "act on Graph" | S |
| FC21 | **Credential providers** (B5): managed identity, federated (workload identity), and Key Vault certificate sources for Connections | Microsoft's recommended credentials for blueprints and apps | M |

### 8.4 Identity, governance, and operations

| ID | Change | Why | Effort |
| --- | --- | --- | --- |
| FC22 | **Recognize agent callers** on A2A and MCP: detect agent tokens (`xms_par_app_azp` present, `xms_act_fct` contains 11, `xms_sub_fct` 13 = agent user), map `(tid, azp)` to an `app-{tid}-{oid}` principal through an allow-list, log the blueprint ID, never authorize on it alone | Copilot Studio agents carry agent identities; Microsoft says not to authorize on the parent claim | S |
| FC23 | **Agent ID completion** (section 10.9): cache the blueprint exchange token, per-tenant authority, agent-user flow (`user_fic`), federated and managed-identity blueprint credentials | Needed before Agent ID is usable in production | M |
| FC24 | **Agentic requests** through the Agents SDK (`AgenticUserAuthorization` and related types in 1.8.77): route activities addressed to an agent instance or agent user to the right FabrCore agent and obtain its tokens through the SDK | The runtime half of Agent 365 digital workers; provisioning stays in the cloud server or the `a365` CLI | L, after verification |
| FC25 | **Turn audit and observability** (B2): OpenTelemetry spans for invoke agent, execute tool, and inference call, tagged with tenant, principal, agent, and (when present) blueprint and agent identity IDs | Agent 365 store requirement; compliance | M |
| FC26 | **Live reload** of integration settings (B17) | Every change needs a restart today | L |
| FC27 | **Close F-02** (B3) | Blocks any compliance claim | M |
| FC28 | **Doc drift**: `SKILL.md` says manifest 1.22 (code 1.25); connections doc says Copilot Studio client 1.3.171-beta (code 1.8.77); principal tables omit `CanonicalEntra`; delete `src/FabrCore.Services.A2A` leftovers | Accuracy | S |

---

## 9. Changes to Insights

| ID | Change | What it does | Effort |
| --- | --- | --- | --- |
| IN1 | **Tenant binding and customer sign-in** | Add an Entra tenant ID to the Insights tenant; accept operators from bound customer tenants (multi-tenant OIDC with an allow-list), scoped to their own tenant | M |
| IN2 | **Managed provisioning app** (plan Phase 3) | A Vulcan365 multi-tenant "Insights Microsoft Integration" app. After admin consent it creates, per agent: the app registration (or one shared identity, see 13), credential (secret with recorded expiry, or federated credential), application ID URI, SSO and `agent.invoke` scopes, pre-authorized clients, delegated grants; through Azure Resource Manager the Azure Bot, Teams channel, and OAuth connection; and through Graph the Teams app in the org catalog (`appCatalogs/teamsApps`, verify the permission). The Work IQ service principal is auto-created on first use; creating it explicitly is documented with `Application.ReadWrite.All` consent, which this app should not need, so leave that to a one-time admin step | L |
| IN3 | **Verifying checklist** | Replace the static checklist with checks that read the customer tenant: app exists, URI set, scopes exposed, consent granted, bot endpoint matches, Teams channel on, package uploaded and not blocked; show the endpoint and URI before a client ID exists | M |
| IN4 | **Credential lifecycle** | Read secret expiry from Graph; rotate on schedule and republish; offer federated credentials or certificates as alternatives; prompt for the companion values (federated client ID, certificate thumbprint) that the page does not collect today | M |
| IN5 | **Per-environment integrations** | Allow one integration per environment (staging and production side by side) and per agent tenant override | M |
| IN6 | **Sign-in settings** | Collect per-agent SSO settings (OAuth connection name, OBO connection, scopes) once FC6 exists | S |
| IN7 | **Copilot Studio assistant** | Generate the exact values for the Copilot Studio wizard, optionally generate per-tenant A2A API keys, and test the A2A endpoint from Insights | S |
| IN8 | **MCP surfaces** | Publish the declarative-agent and Cowork packages from FC14; create the token-store auth configuration (Agents Toolkit or Teams Developer Portal) or document it; set "Any Microsoft 365 organization" and "Any Teams app" | M |
| IN9 | **Work IQ readiness** | Check for the Work IQ service principal (and give the admin the one-line command if it is missing), consent for `WorkIQAgent.Ask`, Copilot Credits billing, and a spending policy for the FabrCore user group; explain 403s using per-user balance diagnostics (`accessDecisionReason`) | M |
| IN10 | **Governance mirror** | Read the Package Management API (app-only) to show each FabrCore package's block state, availability, assignments, pending requests, and 30-day usage; offer block and availability changes as admin-in-the-loop delegated actions | M |
| IN11 | **Cost view** | Tenant Copilot Credit balance (app-only), spending policies (delegated), Retrieval pay-as-you-go meter, and FabrCore's own model spend, side by side | M |
| IN12 | **Registration sync** (preview) | Register each published FabrCore agent with the Agent Registration API (`managedByAppId` = the integration app, `sourceAgentId` = agent name, `originatingStore` = "FabrCore", `agentCard` from the host); keep the returned IDs (there is no list call) | S |
| IN13 | **Agent ID provisioning** (Phase D) | Per section 10.10: one blueprint per cluster, agent identities per published agent per customer tenant, sponsor collection at onboarding, registry registration | L |
| IN14 | **Restart orchestration** | Where the deployment platform is known (Container Apps, AKS, App Service), trigger a rolling restart after a publish, or at least show one button with the exact command | S |
| IN15 | **Naming** | Microsoft now ships a "Microsoft 365 Insights Agent" for agent usage analytics; avoid confusion in customer-facing material | — |
| IN16 | **Skill copies** | The `.claude/skills` copies of the FabrCore skills in the Insights repo are stale; sync or link them | S |

---

## 10. Entra Agent ID: what it is, what we built, and whether to use it

### 10.1 The objects

| Object | Directory type | Holds | Notes |
| --- | --- | --- | --- |
| **Agent identity blueprint** | `application` (`Microsoft.Graph.AgentIdentityBlueprint`) | All credentials; required resource access; inheritable permissions; app roles and scopes; `signInAudience` | Created in one home tenant; can be multi-tenant (`AzureADMultipleOrgs`). Its only power in a tenant is creating and deleting agent identities. Disabling it blocks every agent identity under it |
| **Blueprint principal** | `servicePrincipal` (`agentIdentityBlueprintPrincipal`) | The blueprint's presence in a tenant; tokens and audit entries | Created by admin consent ("Add this agent to your organization") or explicitly through Graph. Automatically and irrevocably holds `AgentIdentity.CreateAsManager` (**250 agent identities** per principal for non-Microsoft platforms using app-only permissions; soft-deleted ones count for 30 days) |
| **Agent identity** | `servicePrincipal` (`agentIdentity`, `servicePrincipalType = ServiceIdentity`) | Permissions, audit identity | **Always single-tenant.** Cannot hold credentials (`PropertyNotCompatibleWithAgentIdentity`). Requires a sponsor. Cannot do OIDC or SAML sign-in to web apps |
| **Agent user** | `user` (`agentUser`) | User-like presence: mailbox, Teams, groups, licenses | Optional, immutable 1:1 with an agent identity, no password, authenticates only through its agent identity. Creating it through Graph creates the Entra object only; mailbox and Teams presence need the Teams / Agent 365 path |

Owners are technical administrators (users or service principals; groups cannot own). Sponsors are
accountable business owners (users or certain groups) and are **required** on every blueprint and
agent identity; an app-only creator must set one explicitly. Managers apply to agent users.

### 10.2 The token flows

All agent flows are confidential-client token exchanges in which the blueprint impersonates the
agent identity. There are no interactive flows; a blueprint redirect URI is allowed only for consent
(`response_type=none`).

**Step 1, every flow (the "T1" exchange token):**

```
POST https://login.microsoftonline.com/{tenant}/oauth2/v2.0/token
client_id={blueprint appId}
scope=api://AzureADTokenExchange/.default
fmi_path={agent identity appId}
grant_type=client_credentials
client_assertion_type=urn:ietf:params:oauth:client-assertion-type:jwt-bearer
client_assertion={managed identity token | certificate-signed JWT}     (or client_secret in dev)
```

The resource of this intermediate token is "AAD Token Exchange Endpoint: Public"
(`fb60f99c-7a34-4190-8149-302f77469936`); it cannot call Graph, and Conditional Access does not
evaluate it.

**Autonomous (app-only):** `client_id={agent identity}`, `client_assertion=T1`,
`grant_type=client_credentials`, `scope={resource}/.default`. Subject = the agent identity; uses
application permissions granted to the agent identity.

**On behalf of a user:** `client_id={agent identity}`, `client_assertion=T1`,
`grant_type=urn:ietf:params:oauth:grant-type:jwt-bearer`, `assertion={user token}`,
`requested_token_use=on_behalf_of`, `scope={resource scopes}`. The user token's audience **must be
the blueprint**: the client requests the blueprint's scope (for example
`api://{blueprintAppId}/access_agent`), and the token's `aud` is the blueprint (`api://{appId}` in
v1 tokens, the app ID GUID in v2 tokens). A Graph-audience token fails with AADSTS50013. Refresh
tokens are supported for background continuation.

**As the agent user:** T1 → T2 (`client_id={agent identity}`, `scope=api://AzureADTokenExchange/.default`,
`client_assertion=T1`, `grant_type=client_credentials`) → `grant_type=user_fic`,
`client_assertion=T1`, `user_federated_identity_credential=T2`, `user_id={agent user oid}` (or
`username`), `scope={resource}`.

In .NET, `Microsoft.Identity.Web.AgentIdentities` wraps all three: `services.AddAgentIdentities()`,
then `WithAgentIdentity(id)` (with `RequestAppToken = true` for app-only) or
`WithAgentUserIdentity(id, upnOrOid)` on `IAuthorizationHeaderProvider`, `IDownstreamApi`,
`GraphServiceClient`, or `MicrosoftIdentityTokenCredential`. For other languages and containers
Microsoft ships the Entra ID Auth SDK sidecar (`/AuthorizationHeader/{api}?AgentIdentity=...`,
`/Validate`, `/DownstreamApi`).

### 10.3 What an agent token looks like

| Claim | Meaning |
| --- | --- |
| `xms_par_app_azp` | The blueprint that created the calling agent. Present only in agent tokens. **Log it; do not authorize on it** |
| `xms_act_fct`, `xms_sub_fct` | Actor and subject facets: 11 = agent identity, 13 = agent user |
| `idtyp` | `app` (autonomous) or `user` (OBO or agent user); cannot tell a human from an agent user on its own |
| `azp` / `appid` | The calling agent identity |
| `oid` | User (OBO), agent identity (app-only), or agent user |
| `tid` | The tenant; agent tokens never cross tenants |
| `scp` / `roles` | Delegated or application permissions, including inherited ones (inherited permissions are visible only in tokens) |

`Microsoft.Identity.Web` adds `User.GetParentAgentBlueprint()` and `User.IsAgentUserIdentity()`.

### 10.4 How a third-party platform like FabrCore uses it

Microsoft's documented model for non-Microsoft platforms (Bedrock, n8n, "other agent frameworks")
is: the platform owns a blueprint; the customer's admin consents to it, which creates the blueprint
principal in their tenant; the platform then creates agent identities there through Graph using an
app-only token minted for the blueprint principal in that tenant. Credentials stay on the blueprint
in the platform's home tenant. Facts that shape the design:

* There is **no notification** when a customer approves; poll the customer tenant for the blueprint
  principal.
* Agent identities are per tenant, so FabrCore needs **one agent identity per published agent per
  customer tenant**, and must acquire tokens against the customer's tenant authority.
* Inheritable permissions apply within a tenant only; each tenant's admin consents the blueprint's
  required resource access.
* The admin consent *workflow* (request and approve) does not work for agent identity permission
  requests.
* `managerApplications` (which lets Copilot Studio and Foundry skip permissions) is reserved for
  Microsoft first-party apps.
* Blocked for agents: `Application.ReadWrite.All`, `RoleManagement.ReadWrite.All`,
  `User.ReadWrite.All`, `Directory.AccessAsUser.All`, and high-privilege directory roles.
* Graph pitfalls: send `OData-Version: 4.0` on every Agent ID call (without it a plain application
  may be created); create the blueprint principal explicitly when not using consent; Azure CLI and
  `DefaultAzureCredential` tokens are rejected with 403 (they carry `Directory.AccessAsUser.All`);
  wait 30–120 s after consent; back off on `400 Object ... not found` between dependent creates.
* Blueprints created through raw Graph must also be posted to the Agent 365 registry through the
  Agent Registry API (the `a365` CLI does this; one article says the Agents SDK does too).

### 10.5 Design patterns mapped to FabrCore

Microsoft's guidance: **one blueprint per trust boundary**; **one agent identity per logical
agent**; agent users only when a user object is required. Anti-patterns: identities per scale-out
replica, per session or memory partition, or per object.

For FabrCore that means:

| FabrCore concept | Agent ID mapping |
| --- | --- |
| Cluster (shared runtime, secrets, network) | One blueprint (a trust boundary). A customer-dedicated cluster gets its own |
| Published agent (a Copilot entry or A2A agent) | One agent identity **per customer tenant** |
| Per-user agent instance (`entra-{tid}-{oid}:{handle}`) | **No identity.** Instances run as the published agent's identity, acting on behalf of the user |
| Silo, grain activation, conversation, thread | No identity |
| "Digital worker" agent with its own mailbox | Agent identity plus one agent user, provisioned through Agent 365 |

This matches the proposal already in section 5 of the native-integration guide.

### 10.6 Governance and licensing the customer gets

With Agent 365 licensing (Microsoft 365 E7; or E5 plus Agent 365; or Agent 365 plus Entra P1 for
Conditional Access and governance and P2 for ID Protection), customer admins can: target Conditional
Access at the FabrCore blueprint, individual agents, or custom security attributes (CA does not
apply to blueprint-only exchanges, and API keys bypass it entirely); see risk detections (offline,
not real time) and block risky agents; run access packages with expiry, sponsor reviews, and
automatic sponsor transfer; disable one agent or the whole blueprint from the admin center or My
Account; and see agent-attributed sign-in and audit logs (`agentSignIn`, `agenticAppInstance`).
Without the licenses, they still get the identities, the logs, and the kill switch. Network controls
(Global Secure Access) are documented for Copilot Studio agents only.

### 10.7 What FabrCore built

Only in Connections ([connections-and-microsoft-integration.md](../connections-and-microsoft-integration.md#optional-entra-agent-id)):

* `ConnectionAuthentication.AgentIdApplication` and `AgentIdOnBehalfOf`
  (`src/FabrCore.Connections/Connections.cs`), with profile fields `ClientId` (blueprint),
  `CredentialReference` (blueprint credential), `AgentIdentityClientId`, and `BlueprintAudience`.
* `OAuthProvider.Acquire` (`src/FabrCore.Services.Connections/OAuthProvider.cs`) performs step 1
  with `fmi_path`, then the app-only or OBO step with T1 as the client assertion, exactly as in
  10.2. `ConnectionGrain` validates OBO assertions against the profile authority with audience
  `BlueprintAudience`, and requires `tid`, `oid`, `scp`, and `entra-{tid}-{oid}` to equal the grain's
  owner.
* Off unless the host sets `FabrCore:Connections:EntraAgentIdEnabled = true`; a profile or blueprint
  cannot turn it on.
* One unit test (`OAuthProviderTests.AgentIdExchangesParentAssertionForChildToken`); never run
  against Entra.

It creates nothing in the directory, is not used by the Copilot channel or A2A, has no agent-user
flow, does not cache T1, supports only secret and certificate-file credentials, and is configured
per owner principal.

### 10.8 How to test what we built

A one-hour runbook for a **developer tenant** (not a customer tenant). It proves the app-only path,
which needs no user token.

**1. Create the blueprint** (PowerShell 7 with `Microsoft.Graph`; Azure CLI tokens will not work).
The signed-in user needs **Agent ID Administrator** (Agent ID Developer can create blueprints but
cannot add a secret or certificate), and step 3 needs **Privileged Role Administrator** to grant an
application permission:

```powershell
Connect-MgGraph -TenantId <tenant> -Scopes `
  "AgentIdentityBlueprint.Create","AgentIdentityBlueprint.AddRemoveCreds.All", `
  "AgentIdentityBlueprint.UpdateAuthProperties.All","AgentIdentityBlueprintPrincipal.Create", `
  "AgentIdentity.Create.All","AppRoleAssignment.ReadWrite.All","User.Read"
$me = (Invoke-MgGraphRequest GET "https://graph.microsoft.com/v1.0/me").id
$h  = @{ "OData-Version" = "4.0" }

$bp = Invoke-MgGraphRequest POST `
  "https://graph.microsoft.com/v1.0/applications/microsoft.graph.agentIdentityBlueprint" -Headers $h `
  -Body (@{ displayName = "FabrCore dev blueprint"
            "sponsors@odata.bind" = @("https://graph.microsoft.com/v1.0/users/$me")
            "owners@odata.bind"   = @("https://graph.microsoft.com/v1.0/users/$me") } | ConvertTo-Json)

# Dev-only credential. Production: certificate or federated credential.
$secret = Invoke-MgGraphRequest POST `
  "https://graph.microsoft.com/v1.0/applications/$($bp.id)/addPassword" -Headers $h `
  -Body (@{ passwordCredential = @{ displayName = "dev" } } | ConvertTo-Json)

Invoke-MgGraphRequest POST `
  "https://graph.microsoft.com/v1.0/servicePrincipals/microsoft.graph.agentIdentityBlueprintPrincipal" `
  -Headers $h -Body (@{ appId = $bp.appId } | ConvertTo-Json)
```

**2. Create an agent identity** using the blueprint's own app-only token (the blueprint principal
holds `AgentIdentity.CreateAsManager`; wait a minute after step 1):

```powershell
$t = Invoke-RestMethod -Method POST "https://login.microsoftonline.com/<tenant>/oauth2/v2.0/token" -Body @{
  client_id = $bp.appId; client_secret = $secret.secretText
  scope = "https://graph.microsoft.com/.default"; grant_type = "client_credentials" }
$ai = Invoke-RestMethod -Method POST `
  "https://graph.microsoft.com/beta/serviceprincipals/Microsoft.Graph.AgentIdentity" `
  -Headers @{ Authorization = "Bearer $($t.access_token)"; "OData-Version" = "4.0" } `
  -ContentType "application/json" -Body (@{
    displayName = "FabrCore dev agent"; agentIdentityBlueprintId = $bp.appId
    "sponsors@odata.bind" = @("https://graph.microsoft.com/v1.0/users/$me") } | ConvertTo-Json)
```

**3. Grant the agent identity an application permission**, for example Graph `User.Read.All`
(find the role ID on the Graph service principal):

```powershell
$graph = (Invoke-MgGraphRequest GET "https://graph.microsoft.com/v1.0/servicePrincipals?`$filter=appId eq '00000003-0000-0000-c000-000000000000'").value[0]
$role  = $graph.appRoles | Where-Object value -eq "User.Read.All"
Invoke-MgGraphRequest POST "https://graph.microsoft.com/v1.0/servicePrincipals/$($ai.id)/appRoleAssignments" `
  -Body (@{ principalId = $ai.id; resourceId = $graph.id; appRoleId = $role.id } | ConvertTo-Json)
```

**4. Configure FabrCore.** Host code registers Connections (see the
[connections guide](../connections-and-microsoft-integration.md#host-setup)):

```csharp
builder.AddFabrCoreServer(new FabrCoreServerOptions {
    AdditionalAssemblies = [typeof(ConnectionsExtensions).Assembly] });
builder.Services.AddFabrCoreConnections(builder.Configuration);
var app = builder.Build();
app.UseFabrCoreServer();
app.MapFabrCoreConnections();
```

Host bootstrap configuration:

```json
{
  "FabrCore": {
    "Connections": { "Enabled": true, "EntraAgentIdEnabled": true },
    "ConnectionCredentials": { "agentid-blueprint": { "Secret": "<blueprint secret>" } }
  }
}
```

Save a profile for a service principal through the admin API
(`PUT /fabrcoreapi/admin/v1/principals/{owner}/connections/graph-agentid` with `If-Match: *`, or
`FabrCoreConnectionAdministrationClient.SaveAsync`):

```json
{
  "name": "graph-agentid",
  "enabled": true,
  "provider": "microsoft",
  "authentication": 3,
  "authority": "https://login.microsoftonline.com/<tenant>/v2.0",
  "clientId": "<blueprint appId>",
  "credentialReference": "agentid-blueprint",
  "agentIdentityClientId": "<agent identity appId>",
  "allowedAgents": ["<owner>:<agent handle>"],
  "resources": {
    "graph": {
      "baseUrl": "https://graph.microsoft.com/v1.0/",
      "scopes": ["https://graph.microsoft.com/.default"]
    }
  }
}
```

`authentication` is sent as a number because the admin route binds `ConnectionProfile` without a
string-enum converter: 0 `AuthorizationCode`, 1 `ClientCredentials`, 2 `AgentIdOnBehalfOf`,
3 `AgentIdApplication`, 4 `OnBehalfOf` (`src/FabrCore.Connections/Connections.cs`). Insights' Connections
page uses the same numbers.

Deploy the agent with a `connectedAgents` blueprint whose connection references
`{ "ownerPrincipal": "<owner>", "name": "graph-agentid" }` under an alias (for example `graph`), then
in the agent call `Connections.GetHttpClientAsync("graph", "graph", ct)` and request `users?$top=1`.

**5. Check the result.** Decode the access token (the test can log claims, never the token): `azp`
should be the agent identity, `xms_par_app_azp` the blueprint, `roles` should contain
`User.Read.All`. In the Entra admin center, Sign-in logs filtered by Agent type = Agent Identity
should show the sign-in. Then disable the blueprint and confirm that acquisition fails: that is the
customer's kill switch. `ConnectionGrain` caches each resource token until a minute before it
expires, so either wait for expiry or re-save the profile (which invalidates the cache) before the
next call; otherwise the call succeeds on the cached token and proves nothing.

**6. Optional OBO test.** This exercises a FabrCore limit as well as Entra.
`OAuthProvider.Validate` accepts exactly one audience (`BlueprintAudience ?? ClientId`) and only the
issuer from the profile authority's metadata (the v2 issuer for a `/v2.0` authority). A blueprint
created through Graph issues **v1** access tokens by default (issuer `https://sts.windows.net/{tid}/`,
`aud` = `api://{appId}`), which fail the issuer check. So:

* PATCH the blueprint with `identifierUris = ["api://{blueprintAppId}"]`, an `access_agent`
  delegated scope, and `api.requestedAccessTokenVersion = 2`, so tokens carry the v2 issuer and the
  app ID GUID as `aud`;
* make the scope inheritable for Graph, or grant consent with `client_id = {agent identity}`;
* save an `AgentIdOnBehalfOf` (2) profile with `blueprintAudience = {blueprintAppId}` (the GUID)
  under the **user's** principal `entra-{tid}-{oid}`, with `allowedAgents` listing that user's agent
  handle;
* the host needs ordinary user authentication for `/fabrcoreapi/connections/v1/*` that maps the
  caller to `entra-{tid}-{oid}` (the default resolver does);
* sign in with any client app requesting `api://{blueprintAppId}/access_agent` and submit the token
  to `POST /fabrcoreapi/connections/v1/assertion`.

If this fails on issuer or audience, that is gap 10.9 item 8, not an Entra problem.

Record what happens in [section 13](#13-open-questions-to-settle-in-a-real-tenant).

### 10.9 Gaps before production use

1. No provisioning: blueprints, principals, agent identities, sponsors, grants, and registry
   registration are all manual.
2. Delegated mode needs a token addressed to the blueprint. Teams SSO produces one addressed to the
   bot (`api://botid-{appId}`). Unresolved.
3. Connection profiles are per owner principal; delegated profiles would be needed per user
   (fixed by FC17).
4. No managed identity or federated blueprint credential (FC21); Microsoft calls secrets
   unsuitable for production blueprints.
5. T1 is requested on every acquisition; the authority is fixed per profile, so multi-tenant use
   needs one profile per customer tenant.
6. No agent-user flow.
7. Inbound agent tokens are not recognized on A2A (FC22).
8. Assertion validation accepts one audience and one issuer (the authority's v2 metadata issuer),
   so v1 tokens and alternative audience spellings (`api://{appId}` versus the GUID) are rejected.
   This also matters for the SSO bridge (FC16), whose bot tokens use `api://botid-{appId}`.

### 10.10 Verdict and recommendation

**Is it useful?** Yes, for four specific outcomes, none of which FabrCore needs to reach Copilot
today:

| Outcome | Needs Agent ID? |
| --- | --- |
| Users chat with FabrCore agents in Copilot and Teams | No (Azure Bot + app registration) |
| Copilot Studio calls FabrCore | No (A2A with app registration or API key) |
| FabrCore calls Work IQ, Graph, Copilot Studio as the user | No (OBO with the bot's app registration, once B1 exists) |
| Each FabrCore agent appears as a governed agent in the customer's Entra and Agent 365 inventory, with its own audit trail, Conditional Access, sponsor, and kill switch | **Yes** |
| FabrCore agents run autonomously (scheduled, event-driven) with least-privilege permissions per agent | **Yes**, better than one shared app |
| A FabrCore agent has its own mailbox and Teams presence ("digital worker") | **Yes**, plus Agent 365 |
| Agent Registration API entries linked to an identity (`agentIdentityId`) | Optional |

It is also likely to become a procurement question: customers that buy Agent 365 will expect every
agent, including third-party ones, to have an Entra agent identity.

**Recommendation:**

1. **Keep the code** and run the 10.8 runbook once in a dev tenant now; it costs an hour and tells
   us whether the protocol implementation is right.
2. **Do not build directory provisioning in FabrCore.** It belongs in the cloud server (IN13).
3. **Ship the app-registration path first** (Phases 0–3 of the plan). Agent ID does not remove the
   Azure Bot, the app package, or the Teams channel; it adds an identity alongside them.
4. **Adopt Agent ID in Phase D**, in this order (recognizing inbound agent tokens, FC22, already
   lands in Phase A because Copilot Studio agents carry agent identities today): one
   Vulcan365 multi-tenant blueprint per cluster with a federated or certificate credential;
   consent-based onboarding that collects a sponsor; one agent identity per published agent per
   tenant, created by Insights, which also posts each agent card to the Agent 365 registry;
   autonomous agents use their identity through Connections; then Agent 365 digital workers using
   the Agents SDK's agentic token support at runtime (FC24).
5. **Do not create identities per user or per agent instance.** Users act through OBO; instances
   share the published agent's identity.

---

## 11. How-to recipes

### 11.1 Publish a FabrCore agent in Copilot and Teams (today)

Follow the add-on README and the production how-to in section 11 of the native-integration guide.
Minimum host code:

```csharp
builder.AddFabrCoreServer();
builder.AddMicrosoft365Copilot();
var app = builder.Build();
app.UseFabrCoreServer();
app.UseMicrosoft365Copilot();
```

Minimum configuration (one entry per agent; each needs its own app registration and Azure Bot):

```json
"Microsoft365Copilot": {
  "TenantId": "<customer tenant>",
  "Principal": { "Strategy": "CanonicalEntra" },
  "Manifest": { "DeveloperName": "Vulcan365", "PublicHostName": "agents.contoso.com",
                "WebsiteUrl": "https://...", "PrivacyUrl": "https://...", "TermsOfUseUrl": "https://..." },
  "Proactive": { "Enabled": true },
  "Agents": [
    { "Name": "assistant", "Route": "/api/messages", "ClientId": "<bot app id>", "ClientSecret": "<secret>",
      "AgentType": "chat-agent", "Models": "default",
      "Manifest": { "Name": "Assistant", "Description": "General-purpose assistant." } }
  ]
}
```

Then restart, run `POST /fabrcoreapi/admin/v1/integrations/microsoft365/diagnostics`, download
`GET .../agents/{name}/app-package`, have an admin upload it, and send a message from Teams.

### 11.2 Proactive messages

From any agent: `await SendToUserAsync("Your report is ready.")`, or target the channel with
`new PrincipalDeliveryTarget("m365copilot")`. The user must have messaged the bot at least once in a
personal chat. Expect delivery in Teams. Do not promise delivery in the Copilot surface until it is
tested (section 4.2, O1). See [principal-message-relays.md](../principal-message-relays.md).

### 11.3 Copilot Studio calls FabrCore (A2A)

See [a2a.md](../a2a.md). Checklist: `A2A:Enabled`, `PublicBaseUrl`, JwtBearer against the customer
tenant with `RequiredScopes = agent.invoke`, `Principal:Strategy = CanonicalEntra`, the same agent
type and handle in `AgentBindings` as in the Copilot entry; in Copilot Studio add an A2A agent with
the `/a2a/{agent}/message:stream` endpoint, OAuth 2.0 that sends a token for the signed-in user,
create and authorize the connection, write a specific description, and turn on generative
orchestration. Keep status-code pages and fallback authorization off `/a2a` and `/.well-known`.

### 11.4 FabrCore calls Microsoft 365 Copilot or a Copilot agent (Work IQ A2A)

Tenant setup (once): create the Work IQ service principal, grant admin consent for
`WorkIQAgent.Ask` to the app that will perform OBO, set up Copilot Credits billing, add the users to
a spending policy that includes Work IQ.

FabrCore setup: a connection profile (OBO with the bot app once B1 exists; authorization code
otherwise) with resource `workiq` → base URL `https://workiq.svc.cloud.microsoft/`, scope
`api://workiq.svc.cloud.microsoft/WorkIQAgent.Ask`, and a `connectedAgents` blueprint:

```json
{ "handle": "copilot", "agentType": "remote-agent",
  "args": { "provider": "work-iq", "connection": "work", "resource": "workiq",
            "endpoint": "https://workiq.svc.cloud.microsoft/a2a/", "timeZone": "America/Chicago" } }
```

To target a specific Copilot agent, set `endpoint` to `https://workiq.svc.cloud.microsoft/a2a/{agent-id}/`
(unverified; see 13). Get agent IDs from Work IQ MCP `list_agents` or the Copilot Chat URL.

### 11.5 FabrCore uses Microsoft 365 as tools (Work IQ MCP)

Configure an HTTP MCP server for the agent with URL `https://workiq.svc.cloud.microsoft/mcp`,
`Connection` set to the agent's connection alias and `Resource` to a resource whose scope is
`api://workiq.svc.cloud.microsoft/WorkIQAgent.Ask`. Expect `fetch`, `call_function`, `ask`,
`retrieve`, and `list_agents` to work and write tools to be denied until the tenant admin changes the
Work IQ MCP policy. Surface policy denials to the user; do not retry them.

### 11.6 FabrCore talks to a Copilot Studio agent

`remote-agent` with `provider = copilot-studio`, `endpoint` = the agent's direct-connect URL, and a
delegated connection with scope `https://api.powerplatform.com/CopilotStudio.Copilots.Invoke`. See
the connections guide.

### 11.7 A declarative agent calls FabrCore (after FC10)

1. Host: enable the MCP endpoint; protect it with an Entra app registration (multi-tenant when
   Vulcan365 hosts it).
2. Create the auth configuration: Teams Developer Portal → Tools → Microsoft Entra SSO client ID
   registration (or OAuth client registration) with base URL = the MCP URL, "Any Microsoft 365
   organization", "Any Teams app"; note the registration ID and generated application ID URI; add
   that URI to the app's `identifierUris`, add the `oAuthConsentRedirect` redirect, pre-authorize
   `ab3be6b7-f5df-413d-ac2d-abf1e3fd9c0b`.
3. Package: a declarative agent whose plugin manifest (2.4) has:

```json
"runtimes": [{
  "type": "RemoteMCPServer",
  "spec": { "url": "https://agents.contoso.com/mcp" },
  "run_for_functions": ["*"],
  "auth": { "type": "OAuthPluginVault", "reference_id": "<registration id>" }
}],
"functions": []
```

4. Upload, test with `-developer on` in Copilot Chat to see tool selection.

Agents Toolkit ("Declarative Agent → Add an Action → Start with an MCP Server", version 6.12+) or
`wiqd agent add action` generate the same files.

### 11.8 Cowork uses FabrCore (after FC10)

Package manifest 1.28/1.29 with:

```json
"agentConnectors": [{
  "id": "fabrcore", "displayName": "FabrCore agents", "description": "...",
  "toolSource": { "remoteMcpServer": {
    "mcpServerUrl": "https://agents.contoso.com/mcp",
    "mcpToolDescription": { "file": "tools/fabrcore.json" },
    "authorization": { "type": "OAuthPluginVault", "referenceId": "<registration id>" } } }
}],
"agentSkills": [{ "folder": "./skills/fabrcore-agents" }]
```

The skill's `SKILL.md` explains when to use which FabrCore tool. Validate with
`wiqd plugin validate --mode deep`; upload in the Microsoft 365 admin center.

### 11.9 Register a FabrCore agent in Agent 365 (preview)

```http
POST https://graph.microsoft.com/beta/copilot/agentRegistrations
Content-Type: application/json

{
  "displayName": "Contoso Sales Assistant",
  "description": "...",
  "managedByAppId": "<integration app id>",
  "createdBy": "<integration app id>",
  "sourceAgentId": "sales",
  "originatingStore": "FabrCore",
  "sourceCreatedDateTime": "2026-10-24T00:00:00Z",
  "sourceLastModifiedDateTime": "2026-10-24T00:00:00Z",
  "agentCard": { "...": "the host's A2A agent card" }
}
```

Keep the returned `id`; `PATCH` on every card change, `DELETE` on retirement. Confirm whether
app-only `AgentRegistration.ReadWrite.All` is accepted, and where the agent appears.

### 11.10 Publish FabrCore knowledge into Copilot (synced connector)

Create an `externalConnection`, register a schema with semantic labels (`title`, `url`,
`lastModifiedDateTime`, `iconUrl`), push `externalItem`s with ACLs mapped from FabrCore principals to
Entra users and groups (external groups for FabrCore-native groups), and have the admin enable
connector results. Requires an app with `ExternalConnection.ReadWrite.OwnedBy` and
`ExternalItem.ReadWrite.OwnedBy` and admin consent.

---

## 12. Roadmap

Mapped to the phases in the [cloud-managed integrations plan](../cloud-managed-copilot-integrations-plan.md).

| Phase | Scope | Items | Exit criterion |
| --- | --- | --- | --- |
| **0. Prove it** (now) | Run today's flow in a real tenant: two agents, SSO, A2A from Copilot Studio, proactive to Teams and to the Copilot surface, Work IQ remote agent; run the Agent ID runbook in a dev tenant | — | Each row of 4.1 and 4.2 marked "works", "fails", or "unsupported" with evidence |
| **A. Quick wins** | Channel quality and safe defaults; recognize agent callers; commit the multi-agent work | FC1, FC3, FC4, FC5, FC8, FC22, FC28; IN7, IN14, IN16 | A package that passes `atk validate` and meets the store's functional rules, with first-response latency measured against the p50/p75/p99 targets |
| **B. Managed onboarding** (plan Phase 3) | Consent once, Insights provisions everything | IN1–IN6; FC6, FC7 | A new tenant onboarded with no manual Entra or Azure steps except consent and package approval |
| **C. Work with Copilot** (plan Phase 4) | Delegated reach-out from Copilot conversations; MCP exposure | FC16–FC20, FC10–FC14; IN8, IN9 | A Copilot user asks a FabrCore agent to use Work IQ; a declarative agent calls a FabrCore agent |
| **D. Identity** | Agent ID and Agent 365 | FC15, FC21, FC23, FC24; IN12, IN13 | FabrCore agents listed in the customer's Agent Registry with identities and audit |
| **E. Govern and scale** (plan Phase 5) | Governance mirror, cost, observability, live reload, compliance | FC2, FC9, FC25–FC27; IN10, IN11 | Customer admin sees usage, block state, and spend; turns are audited |

---

## 13. Open questions to settle in a real tenant

| # | Question | Why it matters | How to settle |
| --- | --- | --- | --- |
| Q1 | Do the Copilot API terms' "ISVs ... must register as multi-tenant" and "not human directed ... bots" clauses apply to a Vulcan365-operated custom engine agent with a per-customer single-tenant app? | Could invalidate the per-customer app design (plan section 4) | Legal review; ask Microsoft partner channel |
| Q2 | Does proactive delivery work in the Copilot surface, in Teams only, or neither for our bot? | Known issues say unsupported; an older article shows a pattern | Phase 0 test with both endpoints |
| Q3 | Is a bot with scope `personal` only visible in Copilot, or is `copilot` required? | FC5 | Phase 0, with and without the scope |
| Q4 | Does Copilot Studio send `A2A-Version: 1.0`, and do its agents call with agent-identity tokens? | FabrCore's A2A is 1.0-only; FC22 | Log headers and claims on the A2A endpoint |
| Q5 | Does the Work IQ agent path (`/a2a/{agent-id}`) work through `remote-agent` unchanged? | O3 | Phase 0 |
| Q6 | Can an Azure Bot in Vulcan365's subscription reference an app registration in the customer tenant (plan D3)? | IN2 placement | Phase 0 |
| Q7 | Can a Teams SSO token be issued for a blueprint's audience, or is Agent 365's agentic route the only delegated path for agent identities? | 10.9 item 2 | Dev-tenant experiment; SDK decompile |
| Q8 | Is the 250-agent-identity cap per blueprint principal (per tenant) or per blueprint? The docs say both | Scale of one blueprint per cluster | Ask Microsoft; test |
| Q9 | Inheritable permissions limit: 50 resource apps (how-to) or 10 (overview, 40 scopes each)? | Blueprint design | Test |
| Q10 | Does deleting a blueprint cascade to agent users (deletion article) or orphan them (FAQ)? | Offboarding | Test; clean up explicitly either way |
| Q11 | Does the Agent Registration API accept app-only writes, and where does a registered agent appear for users? | IN12 | Test |
| Q12 | What does Agents SDK 1.8.77's agentic support actually do with an incoming agentic activity? | FC24 | Decompile, as was done for 1.6.150 |
| Q13 | Cowork: is the MCP tool-description file required? | FC14 | Upload test |
| Q14 | Are custom actions (MCP plugins) available to unlicensed Copilot Chat users with metering? The licensing table did not survive extraction | Economics of I3 | Check the live page |
| Q15 | Does Microsoft's "M365 Insights Agent" report on third-party custom engine agents? | IN10 overlap | Test |
| Q16 | Does Work IQ need a Copilot license per user? The Work IQ overview says it is independent of Copilot licensing and billed in credits; the Foundry connection article says each user needs a Copilot license | The "serve unlicensed users" argument for O2–O4 | Test with an unlicensed user in a spending policy |
| Q17 | Can unlicensed users use the Copilot APIs? The licensing article says no; the Retrieval API offers pay-as-you-go for unlicensed users | O7 economics | Test Retrieval pay-as-you-go |

---

## 14. Reference

### 14.1 Fixed identifiers and URLs

| Item | Value |
| --- | --- |
| Enterprise token store client ID (pre-authorize for MCP/API SSO) | `ab3be6b7-f5df-413d-ac2d-abf1e3fd9c0b` |
| Plugin OAuth redirect | `https://teams.microsoft.com/api/platform/v1.0/oAuthRedirect` |
| Plugin SSO consent redirect | `https://teams.microsoft.com/api/platform/v1.0/oAuthConsentRedirect` |
| Work IQ app ID / resource | `fdcc1f02-fc51-4226-8753-f668596af7f7` / `api://workiq.svc.cloud.microsoft` |
| Work IQ scope | `api://workiq.svc.cloud.microsoft/WorkIQAgent.Ask` (delegated, admin consent) |
| Work IQ endpoints | `https://workiq.svc.cloud.microsoft/a2a/`, `/mcp`, `/rest/conversations` |
| Agent ID exchange scope | `api://AzureADTokenExchange/.default` with `fmi_path` |
| Token exchange resource | "AAD Token Exchange Endpoint: Public", `fb60f99c-7a34-4190-8149-302f77469936` |
| Microsoft Graph app ID | `00000003-0000-0000-c000-000000000000` |
| Bot Framework token scope | `https://api.botframework.com/.default` |
| Agent and title ID prefixes | `U_` user (sideloaded), `T_` tenant (org catalog), `P_` public (store) |
| MCP apps widget host | `{sha256(mcp domain)}.widget-renderer.usercontent.microsoft.com` |
| Microsoft client IDs to pre-authorize for SSO | Listed in `docs/skills/fabrcore-microsoft365copilot/references/entra-sso-setup.md`; the Agent Store guideline adds Word/PowerPoint/Excel `3068386c-7a16-4f6a-a664-043b6b232816` and Bing `9ea1ad79-fdb6-4f9a-8bc3-2b70f96e34c7` (verify against the live page) |

### 14.2 Limits

| Limit | Value |
| --- | --- |
| Custom engine agent first response / between stream updates | 15 s / 45 s |
| Agent Store latency and availability | p50 ≤ 2 s, p75 ≤ 5 s, p99 ≤ 9 s; 99.9% |
| Plugin execution timeout (developer mode) / Cowork tool call | 10 s / 30 s |
| Plugins always in prompt / tools before quality drops | 5 / about 10 |
| Tool and description length ignored beyond | 1,024 characters |
| Declarative agent instructions / starters / actions | 8,000 characters / 12 / 10 |
| Cowork skills / connectors per package | 20 / 10 |
| Retrieval API | 200 requests per user per hour; 25 results; one source per call |
| Agent identities per blueprint principal (app-only, non-Microsoft) | 250 (soft-deleted count for 30 days) |
| Agent identity sponsors / agent user sponsors | 100 (at most 5 groups) / 5 |
| Permission propagation after consent | 30–120 s or more |
| Work IQ MCP collections / chats per request | default 25, max 100 / 10 |
| Synced connector external group memberships per user | under 2,049 (over 10,000 fails) |

### 14.3 Permissions FabrCore and Insights will need

| Purpose | Permission | Type |
| --- | --- | --- |
| Work IQ (any interface) | `WorkIQAgent.Ask` | Delegated |
| Retrieval API | `Files.Read.All`, `Sites.Read.All`, `ExternalItem.Read.All` | Delegated |
| Create blueprints | `AgentIdentityBlueprint.Create` (+ `.AddRemoveCreds.All`, `.UpdateAuthProperties.All`) | Delegated or application |
| Create blueprint principal | `AgentIdentityBlueprintPrincipal.Create` | Delegated or application |
| Create agent identities | `AgentIdentity.Create.All` (or the principal's automatic `AgentIdentity.CreateAsManager`) | Delegated or application |
| Create agent users | `AgentIdUser.ReadWrite.IdentityParentedBy` (admin-granted to the blueprint principal) | Application |
| Grant app roles / delegated grants | `AppRoleAssignment.ReadWrite.All`, `DelegatedPermissionGrant.ReadWrite.All` | Delegated or application |
| Agent registration | `AgentRegistration.ReadWrite.All` | Delegated (application to verify) |
| Package governance | `CopilotPackages.Read.All` (app) / `CopilotPackages.ReadWrite.All` (delegated writes) | — |
| Cost | `CopilotCostManagement.Read.All` (app); `CopilotCostManagement-Policy.ReadWrite.All` etc. (delegated) | — |
| Usage reports | `Reports.Read.All` | Delegated or application |
| Synced connector | `ExternalConnection.ReadWrite.OwnedBy`, `ExternalItem.ReadWrite.OwnedBy` | Application |

### 14.4 Manifest and schema versions

| Artifact | Version notes |
| --- | --- |
| App manifest | ≥ 1.21 for custom engine agents; 1.25 emitted by FabrCore; ≥ 1.25 for channel agents in new Teams Store submissions from July 2026; 1.28/1.29 for Cowork plugins (`agentSkills`, `agentConnectors`); ≥ 1.29 for agent-connector dynamic discovery. Schema 1.28 rejects unknown root properties |
| Declarative agent | 1.8 current; 1.6 added `worker_agents`; 1.9 needed for custom skills (preview) |
| Plugin manifest | 2.4 adds `RemoteMCPServer`; 2.3 Office add-in local functions; 2.2 `security_info` |
| A2A | 1.0 (FabrCore server and Work IQ); Work IQ also accepts 0.3 without the version header |

### 14.5 Microsoft tools worth knowing

| Tool | Use |
| --- | --- |
| Microsoft 365 Agents Toolkit (`atk`) | Scaffold, provision, validate (`atk validate --app-package-file-path`), package, publish; OAuth and API-key registrations; Agents Playground for local testing without a tenant or tunnel |
| Work IQ Dev Tools (`wiqd`, preview; `wiqd plugin` alpha) | Declarative agents and plugin packages; deep validation; sharing; imports Claude, Cursor, Open Plugin formats |
| Work IQ CLI (`@microsoft/workiq`) | `workiq ask`, `workiq mcp`, `workiq list-agents` (experimental) |
| Agent Evaluations CLI (`@microsoft/m365-copilot-eval`, `runevals`) | Evaluates deployed declarative agents through Work IQ, scores with Foundry; a model for a FabrCore eval harness |
| Developer mode | `-developer on` in Copilot Chat (licensed users) shows agent IDs, tool selection, latency |
| Agent 365 CLI (`a365 setup all`) | Provisions blueprint, identities, permissions, and registry registration |
| Entra Agent ID skill | `GitHub-Copilot-for-Azure/plugin/skills/entra-agent-id/SKILL.md`; automates blueprint setup with the pitfalls in 10.4 |
| Entra ID Auth SDK sidecar | Token acquisition for non-.NET or containerized agents (`entra-agentid-samples`) |
