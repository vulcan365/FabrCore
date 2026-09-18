# FabrCore guided tour — full guide outline

Status: proposed information architecture for review, before writing or building the web guide. Research baseline: current FabrCore 2.0 source and development skills, the live FabrCore documentation, and read-only `C:/repos/FabrCore-V365/src/FabrCore.Insights*` source, reviewed September 13, 2026. This document proposes pages and examples; it does not claim that those examples or pages have already been built or tested.

**Site update note:** The website repository at `C:/repos/Vulcan365-Corporate-Websites/src/FabrCore.Ai` has been updated for FabrCore 2.0 but has not been published to `https://fabrcore.ai` yet. Use its `Pages/Docs/*.cshtml` content and explicit `@page` routes alongside the framework source and skills when authoring the guide. Check every guide reference link again after publication; existing public URLs may still describe earlier versions. This outline does not include website deployment work.

## The story

**Build an operations assistant that grows with your application.** Start with an agent answering on a developer workstation. Give it business tools, remembered state, a usable interface, and a harness that completes multi-step work. Add specialists, durable knowledge, Microsoft 365 access and channels, then operate the same application across environments with your own cloud server or Vulcan365 Insights.

The sample application is **Operations Desk**. Its first agent echoes a request without a model key; its first AI version answers an operations question. Later it looks up a service request, gathers policy evidence, drafts a response, collects a user's decision through a card, and delivers a follow-up. Graph and other external mutations are explicit application actions. Early chapters use small local fixtures, so readers do not need SQL, an Azure tenant, or Insights to get started.

Each numbered item below is a proposed page. Modules form an expandable hierarchy, and pages have one continuous previous/next reading order. All features belong somewhere in that order, including optional and advanced features; readers can complete the core app and return to the clearly marked integration labs later.

## Guide layout and reading experience

- **Left navigation:** module → page, current-page highlight, module progress, expand/collapse, and search. Keep it to two navigation levels; page sections appear in a right-side “On this page” list.
- **Main column:** outcome, why this matters now, prerequisites, implementation steps, explanation, verification, troubleshooting, and the next story beat. Include breadcrumbs and previous/next at both useful entry and exit points. On mobile, use a navigation drawer.
- **Progress:** local, optional completion tracking with “Resume the tour.” Every page remains directly addressable and readable without an account. Show prerequisite badges such as Localhost, SQL, Microsoft tenant, or Cloud administration.
- **Configuration switch:** persistent, keyboard-accessible **Files & code** / **Vulcan365 Insights** tabs at configuration exercises. Files & code is the default. Switching replaces only the equivalent configuration instructions, preserving the story, C# implementation, and reading position. A short flyout may explain Insights, but the primary instructions should not depend on a modal.
- **Insights tab anatomy:** prerequisite connection to a cluster/environment; source-verified screen and field names; equivalent configuration keys; publish/apply steps; expected result; live/restart/reactivation behavior. Show “Host code required” or “Client app required” where applicable instead of inventing an Insights switch. Link to module 13 for initial onboarding.
- **Code presentation:** copyable, named C# files with namespaces, package prerequisites, concise explanations, and complete runnable milestone downloads. JSON blocks always name their owning file/resource. Use small changes in-page and offer the complete resulting file alongside them. Use C# as the primary API example language, with HTTP/JSON only where it explains a contract.
- **Reference links:** place “Read the reference — opens in a new tab” beside the relevant explanation, not only at the bottom. Render reference links with `target="_blank" rel="noopener noreferrer"`, an external-link indicator, and accessible new-tab text. Guide navigation stays in the same tab. Source links in this outline are authoring references; apply this behavior when rendering the web guide.
- **Milestone checks:** show a concrete expected response, state change, trace, or UI result, plus one likely failure and its fix. Offer short concept checks at module boundaries. Optional labs state their prerequisites and provide a clear return to the main reading order.

## Configuration and design principles to carry through every page

1. **Separate infrastructure, models, and agent behavior.** Standard Host runtime settings belong under `FabrCore` in `appsettings.json`; .NET connection strings stay top-level. Standard Host reads `ModelConfigurations` and `ApiKeys` from `fabrcore.json`. Agent behavior belongs in `AgentConfiguration`, plugin settings, and canonical blueprint resources. Do not teach a single giant `fabrcore.json` containing unrelated host sections.
2. **Name the exceptions.** GraphRAG ingestion currently uses the legacy top-level `GraphRag:Ingestion` section. Microsoft 365 Copilot may explicitly add `fabrcore.json` to `IConfiguration` for its addon section. Surface has its own documented configuration file. Evidence signing requires Host builder APIs; there is no general built-in `FabrCore:VerifiableExecution` JSON binding.
3. **Separate identity from presentation.** Principals partition agents and data; handles route work; channels describe ingress/presentation context. Legacy API names containing “user” can still carry principal handles. Neither caller-supplied headers nor arbitrary arguments establish trusted identity.
4. **Make system-owned names recognizable.** Reserve underscore-prefixed areas for documented FabrCore system use. Teach supported `_Harness*` arguments through their public configuration contract, while keeping internal snapshot keys and control channels out of application data. Application-owned arguments use non-underscore, descriptive names.
5. **Prefer small, explicit capabilities.** Keep business policy in agents, reusable operations in plugins, orchestration ownership clear, and credentials in trusted infrastructure. Choose a plain AI agent, harness, private specialist, addressable agent, or squad deliberately.
6. **Explain lifetime before persistence promises.** Localhost standalone state is ephemeral. Grain deactivation, process restart, reset/reconfiguration, thread clearing, and eviction have different consequences. SQL feature selection and Orleans provider selection are related but distinct.
7. **Show the effective configuration.** Explain local provider precedence, cloud precedence, environment overlays, code overrides, and apply timing using the implementation that owns each setting. Publishing a document does not itself restart a host or reconfigure every existing agent.
8. **Keep cloud administration optional.** The open runtime, SDK, and cloud protocol support independently built applications and cloud servers. Insights is Vulcan365's cloud server and operations experience. User-facing consent remains the client application's responsibility.

## Module 1 — Get a complete app running on localhost

**Outcome:** a developer runs a host, creates an agent, and gets a reply from a C# client, then upgrades the same agent to use a model.

### 1.1 Meet Operations Desk and the platform

Explain the end-to-end request path: client → Host → Orleans-managed agent → SDK behavior → optional model/tools. Introduce Core, Host, SDK, protocol clients, and optional Surface. Show the final app briefly, then identify the much smaller starting point and the features that are intentionally added later.

### 1.2 Create the solution and start the host

Create server, agents library, and console client projects targeting the current .NET 10 baseline. Show package/project references, `Program.cs`, `AddFabrCoreServer`, `UseFabrCoreServer`, and explicit Localhost settings. Explain assembly discovery/registration, addresses and ports, development logging, and why no feature database is required. Check readiness and discovery before adding AI.

### 1.3 Write and call your first agent

Implement a complete echo-style `FabrCoreAgentProxy` with constructor, `[AgentAlias]`, `OnInitialize`, and `OnMessage`. Use `AgentConfiguration` and the typed Host API client to provision and send a message under a sample principal. Explain the difference between registering an agent type and creating an instance. Include both agent and console-client C# files and the expected response.

### 1.4 Configure a model and light up AI

Add a minimal `fabrcore.json`, named model and credential alias, then replace the echo implementation using `CreateChatClientAgent` and `AgentSession`. Offer OpenAI and Azure model configurations, explain deployment name versus configuration name, and use placeholders for credentials. Show precisely how files are located/copied and how credentials are supplied through the supported model-store path; do not assume normal .NET secrets automatically replace JSON model-store values.

**Insights alternative:** Models and Credentials in the cluster configuration editor, effective environment values, and the local CloudServer bootstrap still required to reach Insights.

### 1.5 Understand what you just configured

Walk through `appsettings.json`, `fabrcore.json`, and the agent definition side by side. Explain dependency injection, options, environment variables, aliases, defaults, and source ownership. Restart the standalone app to demonstrate what is lost and what remains on disk. End with a downloadable working checkpoint and a short “host, agent, client” concept check.

**References:** [Quick Start](https://fabrcore.ai/docs), [Server](https://fabrcore.ai/docs/server), [Configuration](https://fabrcore.ai/docs/configuration), [Client](https://fabrcore.ai/docs/client); [development skill](skills/fabrcore/SKILL.md), [server skill](skills/fabrcore-server/SKILL.md), [database modes](database-modes.md).

## Module 2 — Design agents and messages that compose

**Outcome:** Operations Desk has a well-defined contract, safe lifecycle behavior, and observable routing.

### 2.1 Separate the FabrCore agent from the AI agent

Explain `FabrCoreAgentProxy` as the managed application boundary and `AIAgent`/`ChatClientAgent` as the reasoning runtime inside it. Show initialization, DI, status, health, events, busy handling, cancellation, and cleanup. Add registry descriptions, capabilities, notes, and visibility metadata so tools, discovery, and later delegation can explain what the agent does.

### 2.2 Address principals, agents, and conversations

Explain full and relative handles, `HandleUtilities`, principal ownership, reserved/system agents, and legacy user-named methods/headers. Compare agent identity, thread ID, model session, and channel identity with two C# client examples that keep conversations isolated.

### 2.3 Use messages, channels, and arguments intentionally

Introduce `AgentMessage`, `EventMessage`, response helpers, message kind/type, sender/recipient, correlation, `Channel`, and `Args`. Compare agent configuration arguments with per-message arguments. Show a request carrying `RequestId` and a presentation hint, an agent reading those values, and a channel-aware response. Cover addon-stamped metadata, files/non-text inputs where supported, and trace propagation without treating caller data as authorization.

### 2.4 Understand `_args` and all system-owned underscore areas

Address the requested `_args` terminology explicitly: the inspected public contracts expose **`Args`**, not a literal `_args` member. Explain system arguments within that collection, including `_Harness*`, separately from ordinary application arguments. Inventory `_status`, `_thinking`, `_error`, reserved `_admin`/`_agent_administration` channels, `_harness_session:{threadId}` storage, and other documented underscore keys as the guide is authored. All underscore-prefixed `MessageType` values are reserved system/control traffic; use `IsSystemMessage` and show why these do not become ordinary chat history. Do not invent `_args` JSON or imply every `Args` entry is internal.

### 2.5 Route work, events, and progress

Add request/response between two agents, one-way events, stream subscriptions, and progress/status updates. Explain router, chain, and fan-out/gather patterns and how they differ from the external A2A protocol. Show concurrent arrival and `OnMessageBusy` behavior, timeouts, and error handling through focused C# examples.

**Insights alternative:** agent discovery and configuration; message/event monitoring. Internal names are explained, not exposed as editable application fields.

**References:** [Agents](https://fabrcore.ai/docs/agents), [Communication](https://fabrcore.ai/docs/communication), [Agent Framework](https://fabrcore.ai/docs/agent-framework); [agent skill](skills/fabrcore-agent/SKILL.md), [messaging skill](skills/fabrcore-messaging/SKILL.md), [channel identity](channel-agent-identity.md).

## Module 3 — Give agents tightly integrated tools and plugins

**Outcome:** the assistant can look up and update fixture service requests through explicit business operations.

### 3.1 Choose a local method, standalone tool, or plugin

Compare agent-local tools, `[ToolAlias]` standalone methods, and `IFabrCorePlugin` with a small decision table. Implement the same lookup in a simple form, then explain why shared dependencies, per-agent settings, state, lifecycle, or messaging justify a plugin.

### 3.2 Build a plugin that participates in the agent lifecycle

Write a complete service-request plugin with DI, initialization, settings, trusted handle/context access, cancellation, disposal, status updates, and inter-agent calls. Explain how the owning proxy and its `AIAgent` share capabilities without giving the model arbitrary infrastructure access. Include registration and plugin settings in the agent configuration.

### 3.3 Design tools the model can use correctly

Show descriptions, typed parameters/results, errors, bounded outputs, idempotent operations, and structured output through a C# result record. Resolve configured tools once and compare code-added tools with config-selected plugins/tools. Explain operation permissions and external side effects; revisit the same plugin when evidence recording is added.

### 3.4 Add MCP tools

Configure stdio and HTTP MCP servers, tool discovery/filtering, transport lifetime, environment/configuration inputs, and error handling. Combine MCP tools with local tools and plugins without duplicate registration. Introduce connection/resource references for authenticated MCP, linking forward to module 10 for the complete identity flow.

### 3.5 Compose private AI specialists inside an agent

Use `CreateInternalAgentAsync` for a bounded policy reviewer with separate model/context, telemetry attribution, tool scope, timeout, and concurrency policy. Contrast private specialists with full handle-addressable agents and show agent-as-tool and background adapters. Explain risk-classified tool restrictions and lost in-flight work on deactivation.

### 3.6 Configure a reusable C# scripting environment

Add the optional FabrCore.Scripting package and runtime registration. Define a CSharpScriptingPluginBase with exact developer-chosen NuGet versions, imports, instructions and limits. Select its alias in FabrCoreAgentProxy before resolving tools. Explain reuse across agents, separate dependency graphs, one scripting plugin per agent and fresh out-of-process execution. Run a no-model Newtonsoft.Json checkpoint.

### 3.7 Return script results, artifacts and failures

Trace JSON input through the worker and ScriptExecutionResult back to the AIFunction/model. Exercise structured return values, console output, compilation errors and bounded artifacts. Explain cache prewarming, SDK/runtime requirements, private feeds, per-invocation cleanup and the OS isolation boundary. Keep database authorization in trusted tools and render validated result data with known Blazor components.

**Insights alternative:** discover registered agents/plugins/tools and configure an agent's selected capabilities. C# implementation and deployment of assemblies remain developer work.

**References:** [Tools](https://fabrcore.ai/docs/tools), [MCP](https://fabrcore.ai/docs/mcp), [Agent Framework](https://fabrcore.ai/docs/agent-framework); [plugins/tools skill](skills/fabrcore-plugins-tools/SKILL.md), [scripting skill](skills/fabrcore-scripting/SKILL.md), [scripting reference](scripting.md), [MCP skill](skills/fabrcore-mcp/SKILL.md), [internal composition](skills/fabrcore-agent/references/internal-agent-composition.md).

## Module 4 — Manage state, sessions, and context

**Outcome:** the assistant remembers the current conversation and application state within the configured storage lifetime, without unbounded prompts.

### 4.1 Persist agent state and shared application records

Use `GetStateAsync`, `TryGetStateAsync`, `SetState`, and `FlushStateAsync` for private agent state; use `IFabrCoreStorageProvider` and the Host API for typed principal-scoped shared records. Demonstrate missing/corrupt state handling, schema evolution, and partitioning. Compare these stores with files, chat history, and long-term Memory.

### 4.2 Manage chat history and AI sessions

Explain persistent history providers, `AgentSession` state, thread creation/selection/clearing, and streaming/non-streaming calls. Show single-thread, per-conversation, and stateless patterns. Explain why arbitrary session state is not automatically durable just because chat history is stored.

### 4.3 Configure models for different jobs

Give answering, reasoning, extraction, embeddings, and optional transcription distinct named configurations. Explain supported providers, compatible endpoints, model-specific capabilities, timeout, token/context limits, reasoning options, and cost/latency tradeoffs. Keep provider/model availability versioned rather than promising identical support across providers.

### 4.4 Bound context with the compaction ladder

Explain reversible in-run context reduction, durable history summarization, per-turn/prompt budgets, and stopping behavior. Show a before/after tool-heavy conversation and JSON settings, plus a C# customization hook. Cover tool-call/result integrity, cancellation, leases, and how to report incomplete work. Keep long-term Memory as a separate feature introduced later.

### 4.5 Schedule work and manage agent lifetime

Add a timer and reminder to the sample, explain clock injection/`TimeProvider`, and compare deactivation, reset/reconfiguration, untracking, and hard eviction. Show what happens to state, subscriptions, sessions, and pending work. Explicitly distinguish in-memory reminders from reminders backed by persistent storage.

**Insights alternative:** model/runtime settings and agent lifecycle actions; explain restart versus agent reactivation and the scope of destructive actions.

**References:** [Persistence](https://fabrcore.ai/docs/persistence), [Compaction](https://fabrcore.ai/docs/compaction), [Configuration](https://fabrcore.ai/docs/configuration); [Agent Framework skill](skills/fabrcore-agentframework/SKILL.md), [Orleans skill](skills/fabrcore-orleans/SKILL.md), [compaction correctness](compaction-correctness.md).

## Module 5 — Build a remote WebSocket chat client and user experience

**Outcome:** build a standalone chat client that reaches Operations Desk over HTTPS/WebSocket without direct access to the Orleans cluster, then add a richer interface and actionable cards. Run the client as a separate process from the Host so this boundary is visible from the first exercise.

**Primary sample: .NET MAUI chat app targeting Android.** Use an Android emulator first, then a physical device connecting to the published Host endpoint. Build a real chat screen with agent selection, transcript, composer, progress, and connection status. Keep the connection service reusable for other .NET clients; a console smoke test can supplement the mobile app. Validate the MAUI Android target and FabrCore client package compatibility when implementing the sample.

**Companion sample: server-integrated Blazor web app with `SurfaceChatLink`.** This is a complete second client walkthrough, with FabrCore Host and Blazor Interactive Server in the same application. Readers choosing an embedded web experience can enter at 5.6 after the early localhost modules; readers following the whole tour build both interfaces against the same Operations Desk agents. The browser talks to the Blazor server through its interactive circuit; the server owns the principal-scoped Surface integration with FabrCore. Do not require browser-side Orleans access or have readers reimplement the MAUI WebSocket receive loop for this component.

### 5.1 Design a chat client that cannot access Orleans directly

Draw the path **remote chat client → HTTPS/WSS endpoint → FabrCore Host → private Orleans cluster → agent**. Explain that the client needs the application's published Host endpoints and authenticated access, not Orleans membership, storage credentials, or access to silo/gateway ports. Compare a desktop/console client, a separate web application's backend, and a browser protocol client. Use `FabrCore.Client.WebSocket` for the C# walkthrough; explain the HTTP SDK's complementary role and when a trusted backend may instead use `FabrCore.Client.Orleans`. Distinguish application chat from cloud administration long polling.

### 5.2 Configure the Host endpoint and authenticate the connection

Show host `appsettings.json`, authentication registration in `Program.cs`, and client endpoint configuration for localhost followed by a remote HTTPS/WSS address. Obtain a short-lived, single-use ticket through authenticated `POST /fabrcoreapi/ws/ticket`; explain `/ws`, the `fabrcore.v2` and ticket subprotocols, and the initial `hello` with stable `clientId`. Explain authenticated principal binding and why caller-supplied sender fields or legacy identity query/header selectors cannot choose the WebSocket principal. Cover browser allowed origins, headless clients, TLS termination, proxy upgrade forwarding and idle timeouts. Keep privileged provisioning credentials out of end-user clients.

### 5.3 Implement the complete C# chat client

Build a runnable `OperationsDesk.ChatClient` .NET MAUI project targeting Android with `FabrCoreWebSocketClientOptions`, an authenticated ticket-request `HttpClient`, `FabrCoreWebSocketClient`, and `ConnectAsync`. Provide `MauiProgram.cs` DI registration, a reusable connection service, and complete receive-loop C# files. Explain emulator-to-workstation addressing versus a physical device's reachable HTTPS endpoint: `localhost` on the device is not the workstation. Cover Android network permissions and trusted development certificates without disabling certificate validation. List tracked/shared agents, select an authorized target, and send `AgentMessage` values with channel, application arguments, and correlation. Compare `SendMessageAndReceiveAsync` with `SendMessageAsync` plus `ReadDeliveriesAsync`; explain that acceptance is not the final agent reply. Agent creation, reconfiguration, and blueprint application use authorized HTTP/application provisioning before chat, not invented WebSocket commands.

### 5.4 Turn WebSocket deliveries into a usable chat interface

Build `ChatPage.xaml`, its C# view model, transcript items, and connection-state models using MAUI bindings, an agent picker, message list, composer, send command, and progress/error indicators. Correlate replies with requests; handle ordinary replies, asynchronous/proactive delivery, `_status`, `_thinking`, and `_error` without inserting control traffic as normal transcript messages. Explain main-thread UI updates, concurrent send/receive, cancellation/disposal, agent health, errors, and multiple conversations. Use a public-client sign-in flow with protected credential storage; no embedded client secret or Host admin key. Cross-reference module 10 for the complete Entra flow. Distinguish a stream of delivery frames from model token streaming and demonstrate only the content updates the agent actually emits. Keep the MAUI chat view independent of Surface's Blazor components; card rendering on Android requires a separately verified renderer or a text/action fallback. Include a small browser wire-envelope companion for non-.NET clients without assuming the MAUI implementation runs in a browser.

### 5.5 Recover chat after disconnection and prove the network boundary

Use stable client identity, `IFabrCoreWebSocketCheckpointStore`, explicit `AcknowledgeAsync`, reconnect with a fresh ticket, and ordered at-least-once delivery. Explain when the application considers a delivery processed, duplicate suppression, checkpoint persistence and isolation by authenticated account/client, and why the in-memory checkpoint store does not survive a client restart. Handle `ResyncRequired`/gap through authorized HTTP resynchronization of the relevant application state/history. Do not blindly resend an uncertain business request or promise exactly-once effects. Verify initial chat, progress, dropped network, replay, client restart, expired authentication, and unavailable host. Finish with a client running outside the cluster network with only the HTTPS/WSS endpoint reachable; no Orleans client/provider configuration is required.

Add Android-specific exercises for Wi-Fi/mobile-network changes, background/suspend, resume, process termination, and sign-out/account switching. Persist checkpoints and transcript state consistently, reconnect/resynchronize on resume, and cleanly separate each account's local data. Explain that a suspended Android app cannot rely on a continuously running WebSocket; OS push notifications are a separate optional integration, not an automatic consequence of FabrCore proactive delivery. Show both emulator and physical-device verification steps.

### 5.6 Host FabrCore inside a Blazor Server application

Build an `OperationsDesk.Web` sample with FabrCore Host, agent assemblies, and `FabrCore.Surface` in the same process. Provide complete `Program.cs`, configuration files, Razor imports, layout/assets, and Interactive Server registration/render-mode setup. Explain the config-driven Surface registration path, producer services, component registration, and authenticated `ISurfacePrincipalContextProvider`/scoped workspace. Show how the signed-in principal reaches the correct agent and why workspace/transcript state must not be shared across users. Include the optional `/surface` command-center route and required route/assembly configuration without making command-center navigation a prerequisite for embedding chat on a business page. Identify optional admin components separately.

### 5.7 Embed SurfaceChatLink on a business page

Add a complete `ServiceRequest.razor` example with `<SurfaceChatLink>` beside the request details and agent/blueprint provisioning in trusted application code. Explain `AgentHandle` as a bare handle resolved for the current principal or a fully qualified authorized target; omitting it follows the workspace's selected agent. Cover `Title`, `Tooltip`, `Icon`, `WelcomeMessage`, `Position`, and `InitialSize`. The icon renders where the component is placed; `Position` places the opened panel, so show explicit page/layout CSS if a viewport-fixed launcher is desired. Demonstrate opening, sending, progress/error display, resizing, and minimizing with a runnable checkpoint.

### 5.8 Connect page behavior to chat and agent lifecycle

Show C# handlers for `OnMessageSent` and `OnMessageReceived`, including custom `data-changed`/`ui-update` messages, `Args`/`Data`, and a refresh of the service-request page after an agent operation. Explain callback return values: `false` suppresses that item only in the link panel, while the shared workspace history remains available to `/surface`. Add a page-scoped `CreateAgent` delegate using `SurfaceChatLinkCreateAgentContext`, explain that creation is user-triggered rather than automatic on load, and compare the default `AllowExternalAgent` behavior with strict manual creation. Cover readiness text, healthy pre-provisioned agents, `AllowReset`, and the distinction between reset and hard eviction. Keep page context and authorization checks in trusted code.

### 5.9 Share history, cards, and notifications across the web app

Explain how `SurfaceChatLink`, `/surface`, and `SurfaceNotify` share a principal-scoped `SurfaceWorkspaceService`, transcript, and unread state. Add a header notification component and demonstrate moving between the embedded panel and command center without losing the conversation. Cover multiple links/target agents, unread clearing, control-message activity indicators, and the panel Clear button's local display behavior: it does not delete underlying chat or agent Memory. Show Adaptive Card rendering/action parity through `SurfaceAdaptiveCardHost` and link to the full card-authoring exercise in 5.10. Verify two-user isolation, bidirectional transcript visibility, callback suppression, create/reset behavior, and navigation/circuit lifecycle; state storage-mode limits on restart recovery.

### 5.10 Render and handle Adaptive Cards

Return a deterministic `AdaptiveCardSurfaceEnvelope` showing a service request, then route a form submission through `SurfaceActions` and trusted agent code. Explain template expansion, validation, `ui.render`/`ui.action`, origin/principal checks, allowed URLs/actions, and text-only fallbacks. Introduce model-planned display-only cards as an optional variant, not a substitute for explicit business action routing.

### 5.11 Work with files and audio

Upload/download files with the Host file API and explain temporary file paths, TTL, cleanup, ownership, and deployment storage. Add an optional Azure OpenAI transcription plugin that accepts a recording and returns text for the existing workflow. Cover streams, chunking, cancellation, supported response formats, and verification of current service limits.

### 5.12 Deliver results after the original turn

Use `SendToUserAsync`, targets, and an `IPrincipalMessageRelay` to deliver completion notifications. Explain persisted envelopes/checkpoints, retry/expiry/deduplication and actual storage lifetime. Show how UI notifications and later Teams/Copilot delivery connect to the same idea without assuming every channel has identical guarantees.

**Insights alternative:** configure supported host WebSocket/origin, delivery, and file settings through the catalog and inspect diagnostics; show restart requirements. Where an Insights gateway is used, explain its published endpoint and verified routing/authentication setup. Insights is optional: the remote client can connect to the application's exposed FabrCore Host directly. Building the client, authenticating its users, persisting checkpoints, and handling cards remain application work.

**References:** [WebSocket](https://fabrcore.ai/docs/websocket), [Blazor](https://fabrcore.ai/docs/blazor), [Server](https://fabrcore.ai/docs/server); [Surface skill](skills/fabrcore-surface/SKILL.md), [transcription skill](skills/fabrcore-transcription/SKILL.md), [principal delivery skill](skills/fabrcore-principal-delivery/SKILL.md), [principal relays](principal-message-relays.md).

**SurfaceChatLink authoring references:** [component usage, parameters and lifecycle](skills/fabrcore-surface/references/chat-link.md), [Blazor integration](skills/fabrcore-surface/references/integration.md), [card/action routing](skills/fabrcore-surface/references/action-routing.md). Use these for exact Razor/C# examples and check the corresponding public site links for 2.0 content during the later website update.

## Module 6 — Use the full harness for multi-step work

**Outcome:** the assistant investigates a request, tracks remaining work, consults specialists, and reports completion honestly.

### 6.1 Upgrade the assistant to a harness

Show a complete `CreateFabrCoreHarnessAgent` implementation, carrying forward configured tools, tracked model, history, and compaction. Explain `AsFabrCoreHarnessAgent` for callers outside a proxy and the infrastructure they must supply themselves. Always run through `FabrCoreHarnessResult.RunAsync` to preserve snapshot behavior.

### 6.2 Plan, execute, and bound the loop

Configure todos, initial/allowed modes, mode transitions, loop conditions, iteration limits, and timeout budgets through documented `_Harness*` arguments. Show reading remaining todos and reporting partial completion. Distinguish planning mode and an application-owned approval workflow from a general built-in durable tool-approval feature, which the current harness does not implement.

### 6.3 Delegate to addressable agents

Add policy and request-history agents, `AgentRosterBuilder`, background task start/wait/continue/result tools, capability descriptions, ACL-aware handles, and failure reporting. Show code/config variants and compare external FabrCore delegates with the private specialists from 3.5.

### 6.4 Publish and assign runtime skills

Build a policy-review skill package with instructions/resources, publish immutable principal-scoped versions, and pin `_HarnessSkills` in a blueprint. Explain exact-version resolution, activation caching, scope, limits, update/delete behavior, and trust. Distinguish these runtime skills from the development-assistant skills in `/docs/skills` and A2A card skill metadata.

### 6.5 Resume sessions and handle lost work

Demonstrate snapshots, restore, corruption/size limits, thread clearing, and durable-provider requirements. Show `DescribeLostDelegations`, explain why in-flight background tasks become Lost, and provide a user-visible recovery path rather than automatically repeating uncertain side effects. Include a restart experiment after SQL is enabled in module 8.

### 6.6 Tune the harness without changing its agent class

Provide an annotated `_Harness*` configuration index covering every supported key, parser/casing rules, code-overrides-args precedence, memory integration entry points, and lifecycle effects. Explain deliberately excluded shared-directory skill discovery/file memory and provider-specific hosted tools. End with a complete multi-step checkpoint.

**Insights alternative:** agent arguments, canonical blueprint editor/deployment, and runtime skill administration through supported API/UI surfaces. Verify an actual skill-management screen before describing click instructions; otherwise provide the typed administration API path.

**References:** [Harness](https://fabrcore.ai/docs/harness), [Harness Skills](https://fabrcore.ai/docs/harness-skills); [harness skill](skills/fabrcore-harness/SKILL.md), [configuration](skills/fabrcore-harness/references/configuration.md), [durability](skills/fabrcore-harness/references/durability.md), [runtime skills](skills/fabrcore-harness/references/skills.md).

## Module 7 — Package the app with blueprints and squads

**Outcome:** one reusable definition provisions the cooperating agents for each principal.

### 7.1 Create a canonical blueprint

Move the growing agent definitions into `FabrCoreBlueprint`. Explain stored CRUD versus direct apply, principal substitution, dependencies, system agents, and extension preservation. Use canonical APIs for extension-aware work; identify agents-only compatibility APIs without making them the new tutorial path.

### 7.2 Preview, deploy, and update deliberately

Show C# preview/deployment calls, revisions/digests, ensure versus update, activation/reconfiguration implications, and per-agent results. Demonstrate why reapplying an ensure-only blueprint may not change an existing agent. Explain conflict handling and why preview must not perform external effects.

### 7.3 Coordinate a Surface squad

Add a `squads` blueprint extension with orchestrator and task agents, shared objective, status, and UI. Explain when host-owned orchestration is preferable to a model-owned harness plan and how squad members can still use harnesses internally. Cover delegation failure, cancellation, and result collection.

### 7.4 Extend blueprints for your own product

Implement a small `IBlueprintExpander`/preview extension and explain validation, canonical storage, and lossless round-tripping. Preview the `connectedAgents` extension used later. This is the first product-building seam for readers creating their own platform on FabrCore.

**Insights alternative:** configuration Blueprints section, deployment editor, principal/environment target, preview, reviewed publication, operation status, and conflicts.

**References:** [Blueprints](https://fabrcore.ai/docs/blueprints), [Surface Squads](https://fabrcore.ai/docs/surface-squads); [blueprints](blueprints.md), [server blueprint reference](skills/fabrcore-server/references/blueprints.md), [Surface skill](skills/fabrcore-surface/SKILL.md).

## Module 8 — Turn the workstation app into a durable platform

**Outcome:** the same app survives restart and has an explicit identity and authorization boundary.

### 8.1 Enable the SQL feature database

Add `ConnectionStrings:FabrCore`, required chat/embedding aliases, and a compatible existing SQL Server 2025/Azure SQL database. Explain integrated Orleans defaults, ACL, Memory, GraphRAG, monitoring/evidence stores, automatic initialization versus managed migrations, schema validation, and fail-fast readiness. Show that enabling SQL makes services available but does not automatically attach knowledge tools to every agent.

### 8.2 Choose Orleans clustering and persistence

Compare Localhost, integrated SQL, optional Azure Storage, and custom providers. Explain cluster/service IDs, membership, streams, reminders, named storage, ports, provider-neutral clients, post-provider customization, and recovery. Show how explicit Orleans choices can coexist with SQL feature databases; an Orleans SQL provider alone is not the feature-database switch.

### 8.3 Authenticate callers and establish principal ownership

Put authentication at the host/client boundary, preserve trusted identity through proxies, and explain principal partitions versus external accounts. Show ASP.NET Core integration and the reason raw forwarded principal headers must come only from trusted infrastructure. Prepare for Entra in module 10.

### 8.4 Configure and test ACL policy

Introduce principals, roles, groups, grants, allow/deny precedence, same/cross-principal access, system-agent access, and application-defined permissions. Compare standalone trusted messaging with SQL enforced policy and AuditOnly behavior, including read filtering. Show C# management/evaluation plus an allowed and denied cross-principal request.

### 8.5 Protect data and verify recovery

Cover security audit providers, encrypted data-protection key rings and certificate rotation where required, secret management, backup/restore, schema migration, and retention. Run a milestone restart test for agent state, chat, harness snapshots, reminders, and delivery checkpoints; explicitly expose any lost in-flight work.

**Insights alternative:** catalog-backed runtime settings, principals/access screens, agent lifecycle and capability checks. Database provisioning, host certificates, network policy, and required restarts stay explicit infrastructure steps.

**References:** [Access Control](https://fabrcore.ai/docs/access-control), [Server](https://fabrcore.ai/docs/server), [Persistence](https://fabrcore.ai/docs/persistence); [database modes](database-modes.md), [Orleans skill](skills/fabrcore-orleans/SKILL.md), [ACL skill](skills/fabrcore-acl/SKILL.md).

## Module 9 — Add durable Memory and grounded knowledge

**Outcome:** the assistant recalls useful facts and answers policy questions with scoped source evidence.

### 9.1 Choose the right place for knowledge

Compare chat history, agent state, typed storage, runtime skills, Memory, and GraphRAG. Use one service-request example to show where a verified preference, procedural instruction, transaction record, and policy document belong. Explain that a scope name is not an authorization mechanism.

### 9.2 Save and recall Memory from C# and tools

Use `IAgentMemoryProvider`/`IAgentMemoryService`, stable trusted scopes, and the `agent-memory` plugin. Cover Fact/Rule/Instruction/Observation/Procedural types, provenance, point-in-time facts, updates by ID, corrections, and explicit deletion. Show both direct code and model-tool examples without duplicate registration.

### 9.3 Integrate Memory with harnesses and internal agents

Add `WithMemoryLifecycle`, bounded recall, memory-aware compaction, and internal-agent scope policies. Explain Hot/Warm/Cold semantics, index caps, archive search, consolidation, extraction failures, and retention limitations. Show a remembered preference surviving restart and a correction superseding stale knowledge.

### 9.4 Ingest policy documents into GraphRAG

Configure models, 1536-dimensional embeddings for the current GraphRAG implementation, database/schema prerequisites, scope, ingestion instructions, and tuning. Walk through upload, extraction, graph construction, job status, incremental/repeated ingestion, failures, and provenance using C# service calls.

### 9.5 Search and answer with evidence

Use `IKnowledgeSearchService` and supported agent/plugin integrations for scoped vector/graph retrieval, source excerpts/citations, and grounded responses. Cover administration/query surfaces, deletion, caches, model selection, and ingestion/evaluation tradeoffs. Show an answer with supporting evidence and an honest “not enough evidence” case.

**Insights alternative:** relevant runtime/model settings and discovered administrative capabilities. Do not promise dedicated Memory/GraphRAG screens based only on generic host capabilities; use source-confirmed screens or API instructions.

**References:** [Memory skill](skills/fabrcore-services-memory/SKILL.md), [Memory architecture](skills/fabrcore-services-memory/references/architecture.md), [GraphRAG skill](skills/fabrcore-graphrag/SKILL.md), [GraphRAG setup](skills/fabrcore-graphrag/references/service-setup.md), [Memory defaults](memory-release-defaults.md). The updated website source provides `/docs/knowledge` for Memory and GraphRAG; use it as the planned public reference and verify it after publication.

## Module 10 — Integrate deeply with the Microsoft ecosystem

**Outcome:** Operations Desk can securely use Microsoft resources and participate in Microsoft agent experiences. This is a full module, not a single connector page.

### 10.1 Choose the Microsoft integration path

Draw the four distinct flows: people → FabrCore through Teams/Copilot; Copilot Studio → FabrCore through inbound A2A; FabrCore → Graph/APIs/MCP through connections; FabrCore → Copilot Studio/Work IQ through outbound remote agents. Identify Azure OpenAI, Entra, Azure Bot Service, client consent, and application hosting responsibilities for each.

### 10.2 Add Entra sign-in to your application

Show an ASP.NET Core/Blazor host authenticating users, resolving stable FabrCore principals, and carrying trusted identity across a reverse proxy if used. Explain tenant/client IDs, callbacks, token audience validation, application roles versus FabrCore ACL, local development, and deployed URLs. Separate this app sign-in from Insights operator sign-in and from delegated downstream consent.

### 10.3 Enable the optional connections broker

Register connections contracts/client and host broker, configure protection appropriate to ephemeral or persistent storage, and create a connection profile with owner, allowed full agent handles, resources, credential references, and revision. Compare delegated authorization-code/PKCE, OBO, and app-only flows. Explain that connection and remote-agent features are independent opt-ins.

### 10.4 Build user consent into your client

Implement begin/complete/callback/status/disconnect in C# with `FabrCoreConnectionsClient`, registered redirect URIs, PKCE/state, and user-proof validation. Explain renewal, cancellation, reauthorization after profile changes, and the difference between disconnecting local authorization and disabling an app profile. Tokens never enter prompts, ordinary arguments, or cloud operation logs.

### 10.5 Build Graph-backed plugins

Evolve the fixture plugin into a scoped Microsoft Graph client using `Connections.GetHttpClientAsync`; use raw token access only when a trusted SDK requires it. Plan C# examples for profile lookup, calendar availability, mail drafting, and SharePoint/OneDrive policy retrieval. Give each operation its own permissions/consent explanation and distinguish reads, drafts, and explicit writes. These are application plugin recipes, not claims of built-in first-party FabrCore Graph tools.

### 10.6 Authenticate Microsoft-facing MCP tools

Connect an HTTP MCP server through its connection alias and resource. Explain scope/resource alignment, delegated versus application capabilities, renewal and denial, and how tool resolution composes with the existing plugin set. Verify each Microsoft service's supported auth and endpoint contract when writing the runnable lab.

### 10.7 Put the assistant in Teams and Microsoft 365 Copilot

Install/register the Microsoft365Copilot addon, map `/api/messages`, and configure channel agent identity. Cover Azure Bot provisioning, app registration, app package/manifest, publishing to the intended tenant, local tunneling, and proxy routing. Explain the explicit addon configuration-loading exception for `fabrcore.json` and show the complete registration C#.

### 10.8 Add SSO, streaming, and proactive Microsoft delivery

Walk through Entra user SSO/OBO and per-user versus shared-agent choices. Trace addon channel/argument metadata through the same `AgentMessage` contract. Configure streamed responses and opt-in proactive delivery/relay behavior; explain conversation binding, response-format limitations, and why Surface card actions cannot be assumed to work unchanged on every channel.

### 10.9 Let Copilot Studio call FabrCore through A2A

Publish explicit agents or discover eligible types, build agent-card metadata, choose principal mapping and authentication, and connect the correct message endpoint in Copilot Studio. Explain anonymous discovery versus authenticated calls, CORS/proxy middleware, task/SSE behavior, metadata and non-text parts, and text-only response handling. Include C# request/agent code plus JSON config.

### 10.10 Call Copilot Studio and Work IQ from FabrCore

Register remote agents and deploy `connectedAgents` blueprints. Cover Studio direct-connect/Client SDK versus Work IQ A2A, provider-specific delegated scopes, full handles, remote conversation ownership, task IDs, status/resume/cancel, uncertain outcomes, and rebind behavior. Do not infer outbound compatibility from inbound A2A tests.

### 10.11 Use optional Entra Agent ID

Explain parent blueprint credentials, child identity, delegated versus application exchange, assertions, and explicit feature enablement. Show the profile and trusted SDK call path. State which identity provisioning, sponsorship, governance, and consent steps occur in Microsoft infrastructure and are not performed by FabrCore.

### 10.12 Run the Microsoft integration checkpoint

Follow one signed-in user's request from client → agent → Graph/knowledge → optional remote specialist → Teams or web response. Show correlation, consent denial, least-privilege failures, expired authorization, and channel formatting. Separate deterministic protocol tests from actual tenant consent, provisioning, and live interoperability verification.

**Insights alternative throughout:** Environment Connections profile management, host capability checks, agent/blueprint settings, and monitoring. User login/consent lives in the external client; Microsoft resource provisioning lives in Microsoft administration tools. Insights encrypted handoffs are covered in module 13.

**References:** [Microsoft 365 Copilot](https://fabrcore.ai/docs/microsoft-365-copilot), [Communication](https://fabrcore.ai/docs/communication); [connections integration](connections-and-microsoft-integration.md), [connections skill](skills/fabrcore-connections/SKILL.md), [Copilot skill](skills/fabrcore-microsoft365copilot/SKILL.md), [Entra SSO](skills/fabrcore-microsoft365copilot/references/entra-sso-setup.md), [Bot provisioning](skills/fabrcore-microsoft365copilot/references/azure-bot-provisioning.md), [A2A skill](skills/fabrcore-a2a/SKILL.md). Add current official Microsoft references per lab during authoring.

## Module 11 — Publish interoperable interfaces

**Outcome:** other applications can use the assistant without adopting its UI or internal runtime.

### 11.1 Offer OpenAI-compatible chat completions

Configure the Host ChatCompletion surface and call it from a standard C# client. Explain agent selection, identity/authentication, supported request/streaming behavior, error mapping, and the limits of compatibility. Keep named underlying model configuration separate from the externally exposed agent endpoint.

### 11.2 Publish a general-purpose A2A service

Extend the Microsoft-focused introduction with explicit/discovered agents, exclusions, live handles, primary cards, runtime-skill metadata, caller isolation, task retention, cancellation, and reconnect. Show customization through principal resolver, card factory, task store, and provisioner interfaces. Explain standalone in-memory task lifetime versus SQL-backed task snapshots, ownership and execution leases. SQL snapshots do not provide SSE replay, and interrupted execution fails rather than automatically repeating effects.

### 11.3 Build an addon or embed FabrCore in another product

Show how a host API, background service, or library can reuse agents, embeddings, file/storage APIs, discovery, and typed clients. Define registration/configuration ownership, custom providers, capability discovery, and a settings-catalog contributor. Keep public extension contracts distinct from internal implementation details.

**References:** [Server](https://fabrcore.ai/docs/server), [Client](https://fabrcore.ai/docs/client); [A2A skill](skills/fabrcore-a2a/SKILL.md), [server skill](skills/fabrcore-server/SKILL.md), [OSS boundary](skills/fabrcore-oss-aug2026/references/boundary.md).

## Module 12 — Observe, verify, and test the application

**Outcome:** a developer can explain a request's behavior, measure quality/cost, and distinguish monitoring from signed evidence.

### 12.1 Trace a complete request

Configure OpenTelemetry traces, meters, exporters, and a local viewer. Follow W3C trace context through messages/events, LLM calls, plugins, private specialists, and background work. Include C# child activities and custom background-call attribution, plus an optional Aspire-based developer setup.

### 12.2 Inspect messages, events, LLM calls, and usage

Enable the mode-appropriate monitor, capture options, bounded buffers/retention, live notifications, paged queries, token summaries, and custom providers. Show tool-call snapshots, busy-routing attribution, failures, and usage reports. Explain redaction, incomplete/evicted capture, silo-local versus shared coverage, and why a monitor row is not cryptographic proof.

### 12.3 Add verifiable execution progressively

Move from off to local certificate signing, then durable store and customer certificate/KMS/HSM integration, with SPIFFE/SVID as an advanced optional trust path. Show actual Host builder C#, signed chains, agent/workload identities, export/verify APIs, and trust bundles. Do not imply SQL alone enables signing or that a sample JSON section enables the built-in feature.

### 12.4 Record external effects and verify across clusters

Instrument the service-request/Graph plugin with supported HTTP/database/storage/library evidence helpers. Explain hashes/redaction, causal links, completeness, unchanged signed exports, custom signers/stores, and independent verification. Separate attested observations from proof that every external effect was captured; separate workload identity from agent ACL permission.

### 12.5 Test agents, plugins, and lifecycle deterministically

Build C# tests using `FabrCoreTestHarness`, `FakeChatClient`, and `TestFabrCoreAgentHost`. Cover routing, arguments/system messages, tool selection, busy handling, state resilience, trace propagation, blueprints, timer/time behavior, and isolation. Explain the distinction between the testing harness and the runtime agent harness.

### 12.6 Evaluate real models, Memory, and GraphRAG

Add Microsoft.Extensions.AI.Evaluation quality/safety/groundedness metrics, fixtures, cached reports, and separate live integration runs. Use representative retrieval/correction cases and measure token/latency tradeoffs. Include SQL, WebSocket replay, cloud protocol, and Microsoft tenant acceptance checks with explicit evidence of what ran.

**Insights alternative:** Environment Monitoring, usage views, diagnostic sessions, operation results, and source-supported evidence queries/export. Explain capability and store-coverage limits when interpreting results.

**References:** [Telemetry](https://fabrcore.ai/docs/telemetry), [Monitoring](https://fabrcore.ai/docs/monitoring), [Verifiable Execution](https://fabrcore.ai/docs/verifiable-execution), [Testing](https://fabrcore.ai/docs/testing); [monitoring skill](skills/fabrcore-agentmonitor/SKILL.md), [evidence skill](skills/fabrcore-spiffe/SKILL.md), [testing skill](skills/fabrcore-testing/SKILL.md), [agent evaluation](agent-eval.md).

## Module 13 — Operate with your own cloud server or Insights

**Outcome:** readers can manage the app across environments and understand enough of the open contract to build their own cloud implementation.

### 13.1 Understand the cloud boundary

Explain the difference between the app's FabrCore host, a user-facing client, a cloud configuration/administration server, and an optional gateway. Draw configuration v1, heartbeat, outbound administration v2, and application WebSocket traffic separately. Explain open contracts versus Insights product features and why localhost remains independent.

### 13.2 Connect the running app to Vulcan365 Insights

Create/select tenant, cluster, environment and cluster key; configure `FabrCore:CloudServer` locally; verify the silo roster and effective configuration version. Explain enrollment/bootstrap settings, key rotation/revocation, environment names, and first-connect versus later offline behavior from the protocol. The files path remains documented alongside this alternative.

### 13.3 Manage configuration as a versioned resource

Map local models/credentials/runtime settings/blueprints to the Insights configuration editor. Explain base versus environment overlays, model replacement by name, credential alias merge, settings merge, ETag/effective version, preview, publish, history, and rollback-as-new-version. Show live/restart-required catalog semantics and host-reported applied versus pending settings. Publishing does not restart instances.

### 13.4 Manage principals, access, agents, and blueprints

Walk the source-backed environment screens and equivalent `FabrCoreAdministrationClient` APIs: discovery, CRUD, revisions, preview/apply, configuration updates, lifecycle actions, operations, and paged principals/ACL collections. Explain conflict recovery, unavailable capabilities, environment targeting, and the distinction between management test messages and read-only diagnostic sessions.

### 13.5 Ask diagnostic questions safely

Use the dedicated diagnostic administration session to inspect an agent's source snapshot. Explain operator identity, isolated history/context, reserved channels, retained transcript, and prohibited production tools/connections/memory writes. Show how this differs from sending an ordinary business message to the agent; diagnostics must not mutate the target thread or resume its harness work.

### 13.6 Build your own cloud configuration server

Start from the open reference cloud sample and create a small ASP.NET Core implementation in C#. Implement cluster authentication, configuration envelopes, effective-document hashing/ETag/304, versioning, environment overlays, heartbeats, and refresh requests. Explain bootstrap keys the cloud cannot override, secret handling, offline behavior, and compatibility/version negotiation.

### 13.7 Implement outbound remote administration

Add long-poll connect/response endpoints, command identity/correlation, durable queues, leases, expiry, retries, duplicate handling, response limits, and cancellation. Use the host's administration/capability contracts instead of a private control protocol. Explain how to display operations and partial results without assuming every silo was queried or every timeout means the command did not run.

### 13.8 Extend your cloud console with catalog and connection support

Build a settings UI from the catalog, preserve addon sections and unknown blueprint extensions, and distinguish live from restart-required changes. Add profile administration and encrypted client handoffs: client encrypts with a short-lived cluster challenge, the cloud relays the envelope, and the host validates owner proof and single use. Operator authority is not user consent.

### 13.9 Add fleet operations and an optional gateway

Use Insights as a read-only architectural reference for tenant/cluster/environment selection, host status, configuration history, fleet operations, and gateway policy/routing. Explain environment selection, target authentication, allowed origins, streaming/proxy timeouts, and online/offline targets. Clearly label Insights-specific screens and commercial capabilities; do not reproduce private implementation code as an OSS sample.

### 13.10 Complete the deployment and operations runbook

Take the sample through staging and production: build/deploy host and agents, SQL migrations, secrets/certificates, health/readiness, cluster networking, environment config, rollout validation, rollback, retention, and incident diagnosis. Include Azure hosting/storage/Key Vault patterns as deployment choices with explicit infrastructure prerequisites. End with an end-to-end exercise using both a directly managed host and a cloud-managed environment.

**References:** [cloud protocol](cloud-server-protocol.md), [cloud administration](cloud-administration.md), [cloud administration skill](skills/fabrcore-cloud-administration/SKILL.md), [reference cloud sample](../samples/FabrCore.ReferenceCloud/README.md), [diagnostic sessions](skills/fabrcore-agent/references/admin-diagnostics.md), [evidence exports](skills/fabrcore-spiffe/references/cloud-export.md), [connections integration](connections-and-microsoft-integration.md).

## Module 14 — Finish, extend, and keep the app current

### 14.1 Review the complete application

Revisit the initial diagram with every added feature, owning project, configuration resource, persistence boundary, and credential boundary. Provide the complete sample solution, milestone comparison, and a feature-to-page index. Separate prerequisites for optional integrations from the minimum runnable app.

### 14.2 Choose your next architecture

Compare a single-process embedded app, separate host/UI, automation server, multi-silo service, Microsoft-connected assistant, and independently operated cloud product. Explain when a simpler architecture is sufficient and which extension points support growth without rewriting agent behavior.

### 14.3 Upgrade and maintain the guide's app

Cover 1.x-to-2.0 package/API/schema migrations, current namespaces, removed compatibility surfaces, and coordinated package versions. Show how to use the development skill distribution to maintain an app and how it differs from runtime skills. Establish a repeatable snippet build, link check, configuration validation, and milestone test process for future guide releases.

**References:** [release skill](skills/fabrcore-releases/SKILL.md), [2.0 migration reference](skills/fabrcore-releases/references/2.0.md), [skill distribution](skills/README.md), [OSS boundary](skills/fabrcore-oss-aug2026/references/boundary.md).

## Companion reference pages and authoring inventories

These supplement the linear tour; they do not replace explanations in the story.

| Companion | Planned contents |
| --- | --- |
| Configuration ownership map | Every public setting/property, owning file/resource, default, prerequisites, secret/reference semantics, apply timing, guide page, Insights mapping, and source symbol. Include the live settings catalog and addon contributors; mark code-only configuration explicitly. |
| Agent and plugin configuration map | Full `AgentConfiguration`, plugin/tool/MCP selections and settings, streams, model aliases, system prompt, ordinary `Args`, documented system arguments, blueprints and extensions. |
| System names glossary | `_args` terminology clarification; public `Args`; supported underscore configuration keys; reserved message types/channels; internal state keys; legacy principal/user naming. Never suggest manipulating internal storage directly. |
| Protocol and API map | HTTP SDK, WebSocket v2, backend Orleans, events/streams, principal delivery, chat completions, A2A, Microsoft activity ingress, outbound remote agents, and cloud v1/v2/admin interfaces. |
| Storage and lifetime map | Files, Orleans state, typed storage, chat/session/harness snapshots, Memory, GraphRAG, audit/monitor/evidence, remote tasks, connection protection, and local/SQL/custom-provider behavior. |
| C# example gallery | Complete host/agent/client; standalone remote WebSocket chat project with authenticated tickets, send/receive, transcript, checkpoints and reconnect; message routing; plugin/tool; internal specialist; Surface/card handler; harness/delegation; blueprint expansion; storage/Memory/GraphRAG; Graph/consent; Microsoft channels; remote agents; telemetry/evidence; custom providers/cloud server; deterministic tests/evals. |
| Troubleshooting index | Symptom → relevant milestone → expected observation → source-backed fix, covering startup/configuration, routing, busy agents, auth/ACL, tools, context, lost tasks, replay, SQL schemas, Microsoft consent, cloud overlays, and incomplete monitoring/evidence. |

## Coverage checklist against the development skills

| Skill family | Primary guide coverage |
| --- | --- |
| `fabrcore`, `fabrcore-server` | 1, 4, 8, 11, 13; configuration/API inventories |
| `fabrcore-scripting` | 3.6–3.7; reusable environments, JSON results, artifacts and deployment |
| `fabrcore-agent`, `fabrcore-agentframework` | 2–4, 6; private composition and diagnostic boundaries |
| `fabrcore-messaging` | 2, 5, 10–11; channels, Args, system/control traffic, correlation |
| `fabrcore-plugins-tools`, `fabrcore-mcp` | 3, 10, 12 |
| `fabrcore-harness` | 6; blueprints/squads in 7; Memory in 9 |
| `fabrcore-orleans` | 1, 4–5, 8, 13 |
| `fabrcore-surface` | 5, 7; optional operations components in 12–13 |
| `fabrcore-principal-delivery`, `fabrcore-transcription` | 5, 10 |
| `fabrcore-acl` | 8; cross-principal integrations and cloud administration |
| `fabrcore-services-memory`, `fabrcore-graphrag` | 9, 12 |
| `fabrcore-connections` | 3.4, 10, 13.8 |
| `fabrcore-microsoft365copilot`, `fabrcore-a2a` | 10–11 |
| `fabrcore-agentmonitor`, `fabrcore-spiffe` | 12–13 |
| `fabrcore-testing` | Milestone checks throughout; full test/eval workflow in 12 |
| `fabrcore-cloud-administration` | 7, 12–13 |
| `fabrcore-releases`, `fabrcore-oss-aug2026` | Version/product boundaries throughout; 14 |

Feature completeness during build-out requires reconciling the companion inventories with current public option types, SDK APIs, registry discovery, Host controllers, and the settings catalog. A skill-to-module map alone is not proof that every option has been explained. Give each inventory item a guide page or an explicit internal/unsupported/not-applicable classification.

## Insights source map for the future page authors

All paths here are read-only research references under `C:/repos/FabrCore-V365/src/`; they are not dependencies of the tutorial app or links to publish to end users.

| Source | Use in the guide |
| --- | --- |
| `FabrCore.Insights/README.md`, `CloudServer/InsightsCloudServerController.cs`, `CloudServer/InsightsConnectController.cs` | Cloud role, v1 config/heartbeat and v2 outbound administration, authentication, versioning and tenancy. |
| `FabrCore.Insights.App/Components/Pages/ClusterConfigEditor.razor` | Verified editor sections: Models, Credentials, Runtime settings, Blueprints; review/publish/history and restart notice. |
| `FabrCore.Insights.App/Components/Pages/ConfigurationModelsForm.razor`, `RuntimeSettingsForm.razor`, `RuntimeSettingControl.razor` | Model/runtime field mappings and catalog-driven controls. |
| `FabrCore.Insights.App/Components/Pages/ClusterKeysTab.razor`, `ClusterEnvironmentsTab.razor`, `ClusterStatusTab.razor` | Enrollment keys, environment selection, and host roster/status. |
| `FabrCore.Insights.App/Components/Pages/AgentBuilder.razor`, `AgentAdministrationPanel.razor`, `BlueprintDeploymentEditor.razor` | Agent construction/configuration/lifecycle and blueprint preview/deployment. |
| `FabrCore.Insights.App/Components/Pages/EnvironmentPrincipals.razor`, `EnvironmentAccess.razor`, `EnvironmentConnections.razor` | Principal/ACL administration and source-backed connection profile controls; external-client consent boundary. |
| `FabrCore.Insights.App/Components/Pages/EnvironmentMonitoring.razor`, `EnvironmentDiagnostics.razor`, `EnvironmentOperations.razor` | Monitoring, isolated diagnostics and operation status. |
| `FabrCore.Insights.App/Components/Pages/ClusterGatewayTab.razor`, `GatewayPolicyEditor.razor`, `FabrCore.Insights.Gateway/` | Optional gateway policy and deployment/reference architecture; verify behavior before writing procedures. |
| `FabrCore.Insights.Contracts/`, `FabrCore.Insights.Tests/`, `FabrCore.Insights.Gateway.Tests/` | DTO/contract and test cross-checks for UI claims. Source existence does not establish hosted availability or live validation. |

## Research findings to resolve during build-out

### Updated website source and reference routes

The local website is an ASP.NET Core Razor Pages project. The following routes were verified from its `Pages/Docs` source after the repository update. They are **source-verified, publication pending**, not a claim that the 2.0 content is already available online. Use `https://fabrcore.ai` plus these routes in the rendered guide after checking publication.

| Guide coverage | Website source under `Pages/Docs/` | Reference route |
| --- | --- | --- |
| Runtime/database modes | `DatabaseModes.cshtml` | `/docs/database-modes` |
| Memory and GraphRAG | `Knowledge.cshtml` | `/docs/knowledge` |
| Connections and credentials | `Connections.cshtml` | `/docs/connections` |
| Microsoft integration choices | `MicrosoftIntegrations.cshtml` | `/docs/microsoft-integrations` |
| Inbound/outbound agent interoperability | `A2A.cshtml` | `/docs/a2a` |
| Cloud management | `CloudAdministration.cshtml` | `/docs/cloud-administration` |
| Development skills | `Skills.cshtml` | `/docs/skills` |
| 2.0 migration | `Upgrade200.cshtml` | `/docs/upgrade-2-0` |
| MAUI client's transport reference | `WebSocket.cshtml`, `Client.cshtml` | `/docs/websocket`, `/docs/client` |
| Server-integrated Blazor client | `Blazor.cshtml` | `/docs/blazor` |

`Blazor.cshtml` currently provides general integration guidance, not the detailed `SurfaceChatLink` recipe planned in 5.6–5.9. Keep the component skill/reference as the detailed authoring source and add the planned walkthrough; a general Blazor reference link does not replace it.

### Live-site observations and source reconciliation

- The [live homepage](https://fabrcore.ai/) still includes older names such as `FabrAgentProxy` and `fabr.json`. Current source uses `FabrCoreAgentProxy` and `fabrcore.json`. Teach current names; record the older site content for the later website update.
- The [live Quick Start](https://fabrcore.ai/docs) and [Configuration reference](https://fabrcore.ai/docs/configuration) still describe separate SQL Host packaging and some old configuration placement. The inspected 2.0 source/skills integrate SQL services into Host and put runtime settings under `FabrCore`. Mark these reference links for a 2.0 compatibility check and revisit them when the site is updated later.
- Dedicated links for newer features were absent from the originally inspected live navigation. The updated local website now supplies the routes listed above. Use that local content for planning and recheck public destinations after publication rather than treating these pages as still needing to be created.
- Validate package/feed availability, addon boundaries (including optional Surface administration), and all C# signatures against a pinned guide sample baseline. Preserve current working-tree research without treating every locally implemented feature as already deployed to the public website or Insights service.
- Do not promote design-plan documents or experimental evaluation findings into supported feature promises. In particular, general durable harness tool approval is not implemented; in-flight background work is not restored automatically; standalone A2A task storage is in memory while SQL mode persists task snapshots; connection protocol tests do not establish live Microsoft tenant compatibility. The A2A distinction was rechecked against the updated website, framework skill, and `SqlA2ATaskStore` source.

## Acceptance criteria for the subsequent guide build

- A fresh workstation can finish module 1 using the documented packages/files and C# sample without SQL or Insights.
- Module 5 produces a runnable .NET MAUI Android WebSocket chat client that works with only HTTPS/WSS access to the Host, without direct Orleans connectivity. Its checkpoint verifies authentication, requests/replies, progress, duplicate/replay handling, gap recovery, and mobile suspend/resume on an emulator and a physical device.
- Module 5 also produces a runnable Blazor Interactive Server app hosting FabrCore in the same process, with `SurfaceChatLink` embedded on a business page. Its checkpoint verifies principal isolation, page callbacks, explicit agent creation/reset, shared command-center history, Adaptive Cards, and `SurfaceNotify` unread behavior.
- Every numbered page has a clear outcome, why/how explanation, prerequisites, example plan, verification, troubleshooting, source references, and a next step. Optional labs keep the linear navigation intact.
- Every configurable public feature is assigned a page in the configuration inventory, with exact ownership and apply timing; documented code-only features are not presented as fictitious JSON settings.
- Files & code remains the default. Every Insights alternative maps to verified source/UI or a supported API, with prerequisites and remaining host/client responsibilities stated.
- Complete C# milestone projects build and appropriate deterministic/integration checks run. External-service labs identify credentials, provisioning, and live validation separately.
- All reference links resolve and open in a new tab in the rendered guide; guide previous/next navigation stays in the same tab. Navigation, configuration tabs, code copying, and progress work with keyboard and mobile layouts.
- The final checkpoint includes host, SDK/client, custom agents/plugins, harness/skills/delegation, messages/channels/Args, persistence/ACL, UI/delivery, Memory/GraphRAG, Microsoft integrations, interoperability, monitoring/evidence/testing, and both direct and cloud operations.
