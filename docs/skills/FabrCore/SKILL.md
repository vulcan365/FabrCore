---
name: fabrcore
description: >
  FabrCore overview — architecture, quick reference, prerequisites, NuGet packages, and project templates
  for building distributed AI agent systems with .NET and Orleans.
  Use for general FabrCore questions, "what is FabrCore", architecture overview, getting started, NuGet packages.
  For specific topics use the specialized skills:
  fabrcore-agent (agent development), fabrcore-agentframework (Microsoft Agent Framework),
  fabrcore-plugins-tools (plugins/tools), fabrcore-server (server setup),
  fabrcore-orleans (Orleans configuration), fabrcore-client (Blazor client),
  fabrcore-chatdock (ChatDock component), fabrcore-messaging (messaging/ACL),
  fabrcore-mcp (MCP integration).
allowed-tools: "Bash(dotnet:*) Bash(mkdir:*) Bash(ls:*) Bash(pwsh:*) Bash(powershell:*) Bash(git:*) Bash(dir:*)"
metadata:
  author: FabrCore
  version: 1.0.0
  documentation: https://fabrcore.ai/docs
---

# FabrCore Development Skill

Build distributed AI agent systems with FabrCore — an open-source .NET 10 framework for creating, hosting, and orchestrating AI agents on Microsoft Orleans.

## Quick Reference

| Concept | Type | Key Class/Interface | Skill |
|---------|------|-------------------|-------|
| Agent | Business logic actor | `FabrCoreAgentProxy` | fabrcore-agent |
| Agent Framework | LLM agent runtime | `AIAgent`, `AgentSession` | fabrcore-agentframework |
| Plugin | Stateful tool collection | `IFabrCorePlugin` | fabrcore-plugins-tools |
| Standalone Tool | Single static method | `[ToolAlias]` attribute | fabrcore-plugins-tools |
| Registry Metadata | Capabilities & notes | `[FabrCoreCapabilities]`, `[FabrCoreNote]` | fabrcore-agent, fabrcore-plugins-tools |
| Server/Host | Orleans silo + REST API | `AddFabrCoreServer()` | fabrcore-server (includes full REST API docs with I/O models) |
| Orleans | Distributed runtime | Clustering, Persistence | fabrcore-orleans |
| Client | Blazor UI + Orleans client | `AddFabrCoreClient()` | fabrcore-client |
| ChatDock | Floating icon → chat overlay | `<ChatDock>` component | fabrcore-chatdock |
| Handle Access | Owner/agent handle parsing | `IFabrCoreAgentHost.GetOwnerHandle()`, `GetAgentHandle()` | fabrcore-agent |
| Messaging | Agent communication | `AgentMessage`, `HandleUtilities` | fabrcore-messaging |
| MCP | External tool protocol | `McpServerConfig` | fabrcore-mcp |
| Configuration | Agent definition | `AgentConfiguration` | fabrcore-server |

## Architecture Overview

FabrCore layers on top of Orleans (distributed actor model) and Microsoft.Extensions.AI:

```
┌─────────────────────────────────────────────┐
│  Client Layer (Blazor Server)               │
│  ChatDock, ClientContext, DirectMessage      │
├─────────────────────────────────────────────┤
│  Host Layer (Orleans Silo + ASP.NET Core)   │
│  AgentGrain, REST API, WebSocket, Streams   │
├─────────────────────────────────────────────┤
│  SDK Layer                                  │
│  FabrCoreAgentProxy, Plugins, Tools, MCP    │
├─────────────────────────────────────────────┤
│  Core Layer (Shared Contracts)              │
│  Interfaces, Models, Configuration          │
└─────────────────────────────────────────────┘
```

- **FabrCore.Core** — Interfaces (`IAgentGrain`, `IClientGrain`), models (`AgentConfiguration`, `AgentMessage`, `AgentHealthStatus`), Orleans surrogates
- **FabrCore.Sdk** — Agent base class (`FabrCoreAgentProxy`), plugin system, tool registry, chat client factory, MCP integration, compaction, state persistence
- **FabrCore.Host** — Orleans grains (`AgentGrain`, `ClientGrain`), REST API controllers, streaming, WebSocket, agent service
- **FabrCore.Client** — Blazor components (`ChatDock`), `ClientContext`/`ClientContextFactory`, Orleans client configuration

## Prerequisites

- .NET 10 SDK
- Visual Studio 2022+ (17.x) or VS Code with C# Dev Kit
- An LLM API key (Azure OpenAI, OpenAI, OpenRouter, Grok, or Gemini)

## NuGet Packages

For the server (Orleans silo):
```
dotnet add package FabrCore.Host
```

For the client (Blazor UI):
```
dotnet add package FabrCore.Client
```

For agents (class library):
```
dotnet add package FabrCore.Sdk
```

Create `fabrcore.json` in the server project root with your LLM provider configuration.

## Required Using Directives

```csharp
using FabrCore.Core;          // AgentMessage, AgentConfiguration, MessageKind
using FabrCore.Core.Acl;      // IAclProvider, AclRule, AclPermission
using FabrCore.Sdk;           // FabrCoreAgentProxy, IFabrCoreAgentHost, IFabrCorePlugin, FabrCoreCapabilitiesAttribute, FabrCoreNoteAttribute
using FabrCore.Client;        // IClientContextFactory, IClientContext
using FabrCore.Host;          // AddFabrCoreServer, UseFabrCoreServer, FabrCoreServerOptions
using Microsoft.Agents.AI;    // AIAgent, AgentSession, ChatClientAgent
using Microsoft.Extensions.AI; // ChatMessage, ChatRole, IChatClient, AITool
```

## Project Templates

Use the asset templates in `assets/` to quickly scaffold new components:
- `assets/agent-template.cs` — Agent class template
- `assets/plugin-template.cs` — Plugin class template
- `assets/tool-template.cs` — Standalone tool template
- `assets/server-program.cs` — Server Program.cs
- `assets/client-program.cs` — Client Program.cs
- `assets/fabrcore-json-openai.json` — OpenAI provider config
- `assets/fabrcore-json-azure.json` — Azure OpenAI provider config
- `assets/appsettings-server.json` — Server appsettings
- `assets/appsettings-client.json` — Client appsettings
- `assets/server-csproj.xml` — Server .csproj
- `assets/client-csproj.xml` — Client .csproj
- `assets/agents-csproj.xml` — Agents library .csproj
- `assets/chatdock-example.razor` — ChatDock usage example
- `assets/agent-chat-example.razor` — Manual messaging example

## Scaffolding a Complete System

To scaffold a full FabrCore solution (server + client + agents):

1. Create solution and projects:
```powershell
dotnet new sln -n MySystem
dotnet new web -n MySystem.Server -o src/MySystem.Server
dotnet new razorclasslib -n MySystem.Client -o src/MySystem.Client
dotnet new classlib -n MySystem.Agents -o src/MySystem.Agents
dotnet sln add src/MySystem.Server src/MySystem.Client src/MySystem.Agents
```

2. Add NuGet packages:
```powershell
dotnet add src/MySystem.Server package FabrCore.Host
dotnet add src/MySystem.Client package FabrCore.Client
dotnet add src/MySystem.Agents package FabrCore.Sdk
```

3. Add project references:
```powershell
dotnet add src/MySystem.Server reference src/MySystem.Agents
dotnet add src/MySystem.Client reference src/MySystem.Agents
```

4. Copy and customize the asset templates for each project.

For detailed reference on any topic, use the specialized skills listed in the Quick Reference table above.
