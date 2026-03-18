# FabrCore Architecture

## Overview

FabrCore is an open-source .NET 10 framework for building distributed AI agent systems. It combines Microsoft Orleans (virtual actor model) with Microsoft.Extensions.AI and the Microsoft Agent Framework to provide a production-grade platform for AI agent development.

## Technology Stack

| Layer | Technology | Purpose |
|-------|-----------|---------|
| Distributed Runtime | Microsoft Orleans 10.x | Virtual actors (grains), clustering, persistence, streaming |
| AI Abstractions | Microsoft.Extensions.AI | IChatClient, AITool, ChatMessage abstractions |
| Agent Framework | Microsoft.Agents.AI | ChatClientAgent, AgentThread, compaction strategies |
| LLM Providers | Azure.AI.OpenAI | OpenAI, Azure OpenAI, OpenRouter, Grok, Gemini |
| MCP | ModelContextProtocol.Client | MCP server connectivity for external tools |
| Observability | OpenTelemetry | Distributed tracing, metrics, logging |
| Client UI | Blazor Server | Interactive server-side rendering with SignalR |

## Four-Layer Architecture

### FabrCore.Core — Shared Contracts

The foundation layer containing interfaces and models shared by all other layers.

**Grain Interfaces:**
- `IAgentGrain` — Agent grain contract: configure, message, health, threads, custom state
- `IClientGrain` — Client grain contract: subscribe, send messages, create agents, track agents
- `IAgentManagementGrain` — Registry grain: register/query agents and clients
- `IClientGrainObserver` — Observer pattern for async message delivery to clients

**Key Models:**
- `AgentConfiguration` — Agent definition (handle, type, models, system prompt, plugins, tools, MCP servers, args)
- `AgentMessage` — Universal message type (routing, content, metadata, files, state, args)
- `AgentGrainState` — Persistent grain state (configuration, message threads, custom state)
- `AgentHealthStatus` — Health reporting at basic/detailed/full levels
- `McpServerConfig` — MCP server connection configuration (Stdio or Http transport)
- `StoredChatMessage` — Persistent chat message format
- `HandleUtilities` — Handle normalization: `EnsurePrefix` (bare alias to `owner:agent`), `StripPrefix`, `BuildPrefix`

**Orleans Serialization:**
All models include Orleans surrogates and converters via `[GenerateSerializer]` for efficient cross-silo communication.

### FabrCore.Sdk — Agent Development Kit

The SDK layer provides the tools for building agents, plugins, and tools.

**Agent Base Class:**
- `FabrCoreAgentProxy` — Abstract base class all agents extend
  - `CreateChatClientAgent(modelName)` — Creates a ChatClientAgent with automatic tool resolution, history persistence, and compaction
  - `ResolveConfiguredToolsAsync()` — Resolves plugins, standalone tools, and MCP tools
  - `TryCompactAsync()` — Triggers message compaction when context window fills
  - `GetStateAsync<T>(key)` / `SetState(key, value)` / `FlushStateAsync()` — Custom state persistence
  - OpenTelemetry metrics: `agent.configured`, `agent.messages.processed`, `agent.stream.messages`

**Plugin System:**
- `IFabrCorePlugin` — Plugin interface with `InitializeAsync(config, serviceProvider)`
- `FabrCoreToolRegistry` — Resolves tools from plugins and standalone methods via reflection
- `FabrCoreRegistry` — Assembly scanner for `[AgentAlias]`, `[PluginAlias]`, `[ToolAlias]` attributes
- `AIFunctionFactory` — Creates `AITool` instances from plugin methods automatically

**Chat Client Factory:**
- `FabrCoreChatClientService` — Creates `IChatClient` for any supported provider
  - Fetches model configuration from Host API (`/fabrcoreapi/ModelConfig/model/{name}`)
  - Supports: OpenAI, Azure, OpenRouter, Grok, Gemini
  - Configurable timeouts, max output tokens, context window sizes

**Agent Host Interface:**
- `IFabrCoreAgentHost` — The interface agents use to interact with the framework
  - `GetHandle()` — Get the agent's handle
  - `SendAndReceiveMessage()` / `SendMessage()` / `SendEvent()` — Inter-agent communication
  - `RegisterTimer()` / `RegisterReminder()` — Scheduling
  - Chat history providers and thread management
  - Custom state persistence

### FabrCore.Host — Server Infrastructure

The host layer provides the Orleans silo and REST API.

**Orleans Grains:**
- `AgentGrain` — Core actor implementing `IAgentGrain` + `IFabrCoreAgentHost` + `IRemindable`
  - Persistent state via `IPersistentState<AgentGrainState>`
  - Auto-restores configuration on reactivation
  - Stream subscriptions for chat and event namespaces
  - Timer and reminder management
  - Registers with `AgentManagementGrain` on activation

- `ClientGrain` — Client connection management
  - Observer pattern for async message delivery
  - Pending message queue for offline clients
  - Tracked agent cache

- `AgentManagementGrain` — Global agent/client registry
  - Single-instance grain (key: 0)
  - Tracks active/deactivated agents with timestamps

**Services:**
- `FabrCoreAgentService` — Unified facade for agent operations
  - Agent configuration and batch creation
  - Message routing (sync and async)
  - Health status queries
  - Thread and custom state management
  - Discovery (agent types, plugins, tools)

**REST API Controllers:**
- `AgentController` — Agent CRUD, messaging, health
- `DiscoveryController` — Type/plugin/tool registry queries
- `ModelConfigController` — LLM model configuration and API keys
- `DiagnosticsController` — System diagnostics and statistics
- `FileController` — File upload/download
- `EmbeddingsController` — Text embedding generation

**Infrastructure:**
- `FabrCoreHostExtensions` — `AddFabrCoreServer()` / `UseFabrCoreServer()` extension methods
- Clustering modes: Localhost (dev), SqlServer, AzureStorage
- Persistence: Memory (dev), ADO.NET (SqlServer), Azure Tables/Blobs
- Streaming: Memory provider (local), Azure provider (distributed)

### FabrCore.Client — UI Integration

The client layer provides Blazor components and Orleans client connectivity.

**Core Classes:**
- `ClientContext` — Thread-safe client bound to a user handle
  - `SendAndReceiveMessage()` — Request-response messaging
  - `SendMessage()` — Fire-and-forget messaging
  - `SendEvent()` — Event broadcasting
  - `CreateAgent()` — Dynamic agent creation
  - `GetAgentHealth()` — Health monitoring
  - Observer auto-refresh every 3 minutes

- `ClientContextFactory` — Creates and optionally caches `ClientContext` instances
- `DirectMessageSender` — Lightweight fire-and-forget sender (no ClientContext needed)
- `FabrCoreHostApiClient` — REST client for Host API

**Blazor Components:**
- `ChatDock` — Full-featured chat component
  - Parameters: UserHandle, AgentHandle, AgentType, SystemPrompt, Title, Icon, Position, AdditionalArgs
  - Features: Markdown rendering, thinking indicators, health status, lazy loading, unread badges
  - Positions: BottomLeft, BottomRight, Left, Right
  - Events: OnMessageReceived, OnMessageSent

- `ChatDockManager` — Coordinates multiple ChatDock instances

**Extension Methods:**
- `AddFabrCoreClient()` — Registers Orleans client, contexts, senders
- `UseFabrCoreClient()` — Post-configuration setup
- `AddFabrCoreClientComponents()` — Registers ChatDockManager for Blazor

## Data Flow

### Message Flow (Client to Agent)

```
Client (Blazor)
  └─> ClientContext.SendAndReceiveMessage()
        └─> ClientGrain (Orleans)
              └─> AgentGrain.OnMessage()
                    └─> FabrCoreAgentProxy.OnMessage()
                          └─> ChatClientAgent.InvokeAsync()
                                ├─> LLM API Call
                                ├─> Tool Invocation (if needed)
                                └─> Response
                          └─> AgentMessage.Response()
              └─> Return to ClientGrain
        └─> Observer callback to client
  └─> UI Update
```

### Agent-to-Agent Communication

```
AgentA.OnMessage()
  └─> fabrAgentHost.SendAndReceiveMessage("agentB", message)
        └─> AgentGrain.ResolveTargetHandle("agentB") → "user1:agentB"
        └─> AgentGrain(user1:agentB).OnMessage()
              └─> AgentB.OnMessage()
              └─> Response
        └─> Return to AgentA
  └─> Continue processing
```

Note: Bare alias `"agentB"` is auto-resolved to `"user1:agentB"` by `ResolveTargetHandle()`. Fully-qualified handles (e.g., `"user2:agentB"`) pass through for cross-owner routing.

### Stream-Based Events

```
AgentA sends event
  └─> fabrAgentHost.SendEvent("agentB", event)
        └─> Orleans Stream (AgentEvent namespace)
              └─> AgentGrain(B) subscription
                    └─> AgentB.OnEvent()
```

## Orleans Key Concepts for FabrCore

- **Grains** — Virtual actors that are automatically activated/deactivated. Each agent is a grain identified by a string key (the agent handle).
- **Silos** — Server processes that host grains. FabrCore.Host configures one or more silos.
- **Clustering** — How silos discover each other. Localhost for dev, SqlServer/Azure for production.
- **Persistence** — Grain state survives restarts. Configured per clustering mode.
- **Streams** — Pub/sub messaging between grains. Used for chat and event delivery.
- **Reminders** — Persistent timers that survive grain deactivation and silo restarts.
- **Timers** — Non-persistent timers for periodic tasks within an active grain.
