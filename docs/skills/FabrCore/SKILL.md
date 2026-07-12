---
name: fabrcore
description: >
  FabrCore overview — architecture, quick reference, prerequisites, NuGet packages, and project templates
  for building distributed AI agent systems with .NET and Orleans.
  Use for general FabrCore questions, "what is FabrCore", architecture overview, getting started, NuGet packages.
  For specific topics use the specialized skills:
  fabrcore-agent (agent development), fabrcore-agentframework (Microsoft Agent Framework),
  fabrcore-plugins-tools (plugins/tools), fabrcore-server (server setup),
  fabrcore-orleans (Orleans configuration), fabrcore-messaging (messaging),
  fabrcore-acl (access control: principals, roles, groups, permission grants, enforcement
  modes, cross-principal agent-to-agent security, security audit),
  fabrcore-mcp (MCP integration), fabrcore-spiffe (verifiable execution, signed evidence,
  optional SPIFFE/SVID signing, trust bundles, evidence stores, attested external effects),
  fabrcore-microsoft365copilot (surface agents in Microsoft 365 Copilot/Teams via the
  FabrCore.Services.Microsoft365Copilot addon).
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
| Agent | Business logic actor | `FabrCoreAgentProxy`, `TryGetStateAsync` | fabrcore-agent |
| Agent Blueprint | Admin ensure list for principal-scoped agents | `AgentBlueprintRequest`, `POST /fabrcoreapi/Agent/blueprint` | fabrcore-server |
| Agent Eviction | Hard-delete an agent instance | `AgentEvictionResult`, `DELETE /fabrcoreapi/Agent/{handle}` | fabrcore-server, fabrcore-orleans |
| Agent Framework | LLM agent runtime | `AIAgent`, `AgentSession` | fabrcore-agentframework |
| Plugin | Stateful tool collection | `IFabrCorePlugin` | fabrcore-plugins-tools |
| Standalone Tool | Single static method | `[ToolAlias]` attribute | fabrcore-plugins-tools |
| Registry Metadata | Capabilities & notes | `[FabrCoreCapabilities]`, `[FabrCoreNote]` | fabrcore-agent, fabrcore-plugins-tools |
| Server/Host | Orleans silo + REST API | `AddFabrCoreServer()`, `UseTimeProvider(...)` | fabrcore-server (includes full REST API docs with I/O models) |
| Orleans | Distributed runtime | Clustering, Persistence, `TimeProvider` | fabrcore-orleans |
| API Client | HTTP client access | `FabrCore.Sdk.IFabrCoreHostApiClient` | fabrcore-server |
| Entity Storage | Typed key/value app data | `IFabrCoreStorageProvider`, `FabrCore.Sdk.IFabrCoreHostApiClient` | fabrcore-server, fabrcore-orleans |
| Handle Access | Principal handle/agent handle parsing | `IFabrCoreAgentHost.GetUserHandle()` (legacy name), `GetAgentHandle()` | fabrcore-agent |
| Messaging | Agent communication | `AgentMessage`, `AgentMessage.IsSystemMessage`, `HandleUtilities` | fabrcore-messaging |
| ACL | Principals, roles, groups, permission grants | `IAclEvaluator`, `PermissionGrant`, `AclController` | fabrcore-acl |
| Security Audit | ACL decisions, boundary crossings | `IAuditProvider`, `AuditEvent` | fabrcore-acl |
| MCP | External tool protocol | `McpServerConfig` | fabrcore-mcp |
| Microsoft 365 Copilot | Copilot/Teams channel addon | `AddMicrosoft365Copilot()`, `Microsoft365CopilotOptions` | fabrcore-microsoft365copilot |
| Configuration | Agent definition | `AgentConfiguration` | fabrcore-server |
| Telemetry | W3C TraceContext on every message | `AgentMessageTelemetry`, `StampFromActivity`, `StartIngressActivity` | fabrcore-messaging (surface), fabrcore-server (exporter setup) |
| Verifiable Execution | Signed/tamper-evident agent/event evidence | `IVerifiableExecutionStore`, `IVerifiableExecutionSigner`, `VerifiableExecutionEnvelope` | fabrcore-spiffe |

## Blueprints

A Blueprint is a caller-supplied, idempotent manifest of agents to ensure for one principal. It is not a Host-startup setting, stored server-side template, or continuous reconciler: the application must call `POST /fabrcoreapi/Agent/blueprint` (or `EnsureBlueprintAgentsAsync`) when it provisions a principal, such as at first sign-in or workspace initialization. Existing configured agents keep their configuration; use `/agent/create` for an intentional reconfiguration.

Use **fabrcore-server** for the request contract, bootstrap pattern, and manifest asset. Use **fabrcore-testing** to verify lifecycle behavior against a running Host.

## Architecture Overview

## Principal vs User Naming

FabrCore's authorization, routing, storage partitioning, and ACL model use **principals**. Some public compatibility names still say `user` or `users`, including `x-user-handle`, `x-fabrcore-userhandle`, `GetUserHandle()`, `HasUserHandle()`, `UserHandle` DTO fields, and `/fabrcoreapi/Diagnostics/users`. Treat those names as legacy wire/API names: the value is a **principal handle**, not a separate end-user profile.

FabrCore layers on top of Orleans (distributed actor model) and Microsoft.Extensions.AI:

```
┌─────────────────────────────────────────────┐
│  Host Layer (Orleans Silo + ASP.NET Core)   │
│  AgentGrain, PrincipalGrain, REST API       │
│  WebSocket, Streams                         │
├─────────────────────────────────────────────┤
│  SDK Layer                                  │
│  FabrCoreAgentProxy, Plugins, Tools, MCP    │
├─────────────────────────────────────────────┤
│  Core Layer (Shared Contracts)              │
│  Interfaces, Models, Configuration          │
└─────────────────────────────────────────────┘
```

- **FabrCore.Core** — Interfaces (`IAgentGrain`, `IPrincipalGrain`), models (`AgentConfiguration`, `AgentMessage`, `EventMessage`, `AgentHealthStatus`, `AgentEvictionResult`), verifiable execution contracts, Orleans surrogates
- **FabrCore.Sdk** — Agent base class (`FabrCoreAgentProxy`), plugin system, tool registry, chat client factory, MCP integration, compaction, state persistence, Host API client, typed entity storage contracts, blueprint ensure client types, LLM evidence integration
- **FabrCore.Host** — Orleans grains (`AgentGrain`, `PrincipalGrain`), REST API controllers, streaming, WebSocket, agent service, verifiable execution recording/signing/verification

## Prerequisites

- .NET 10 SDK
- Visual Studio 2022+ (17.x) or VS Code with C# Dev Kit
- An LLM API key (Azure OpenAI, OpenAI, OpenRouter, Grok, or Gemini)

## NuGet Packages

For the server (Orleans silo):
```
dotnet add package FabrCore.Host
```

For agents (class library):
```
dotnet add package FabrCore.Sdk
```

Create `fabrcore.json` in the server project root with your LLM provider configuration.

## Required Using Directives

```csharp
using FabrCore.Core;          // AgentMessage, AgentConfiguration, MessageKind
using FabrCore.Core.Acl;      // IAclEvaluator, PermissionGrant, AclPrincipal, FabrPermissions
using FabrCore.Sdk;           // FabrCoreAgentProxy, StateReadResult, IFabrCoreAgentHost, IFabrCorePlugin, IFabrCoreStorageProvider, IFabrCoreHostApiClient
using FabrCore.Host;          // AddFabrCoreServer, UseFabrCoreServer, FabrCoreServerOptions
using Microsoft.Agents.AI;    // AIAgent, AgentSession, ChatClientAgent
using Microsoft.Extensions.AI; // ChatMessage, ChatRole, IChatClient, AITool
```

## Typed Entity Storage

FabrCore exposes a typed entity key/value store through `FabrCore.Sdk` without exposing Orleans storage types to consumers.

```csharp
public interface IFabrCoreStorageProvider
{
    Task<T?> GetAsync<T>(string container, string entityKey, CancellationToken cancellationToken = default);
    Task UpsertAsync<T>(string container, string entityKey, T value, CancellationToken cancellationToken = default);
    Task<bool> DeleteAsync(string container, string entityKey, CancellationToken cancellationToken = default);
}
```

Use it for application-level typed data such as preferences, cached lookup results, workflow checkpoints, or small JSON-serializable records. Values are stored internally as JSON inside a FabrCore envelope with value type and timestamps; callers read and write normal .NET types.

Storage is backed by the configured Orleans provider named `FabrCoreOrleansConstants.StorageProviderName` (`"fabrcoreStorage"`). Localhost uses whatever storage Orleans was configured with for localhost, and SQL/Azure/custom modes persist according to their provider.

Principal handle partitioning is part of the Host API: `x-user-handle` is the principal handle partition, `container` is the logical bucket, and `entityKey` is the key inside that `principalHandle/container`. The same `container/entityKey` can exist independently for different principal handles.

Prefer agent custom state (`GetStateAsync`, `TryGetStateAsync`, `SetState`, `FlushStateAsync`) for state private to a single agent. `GetStateAsync<T>` returns `default` for missing, null, or undefined values; use `TryGetStateAsync<T>` when unreadable persisted JSON should be migrated, reset, or ignored instead of failing initialization. Prefer typed entity storage when API clients, host services, plugins, or multiple agents need to share app data by `principalHandle/container/entityKey`.

## Project Templates

Use the asset templates in `assets/` to quickly scaffold new components:
- `assets/agent-template.cs` — Agent class template
- `assets/plugin-template.cs` — Plugin class template
- `assets/tool-template.cs` — Standalone tool template
- `assets/server-program.cs` — Server Program.cs
- `assets/fabrcore-json-openai.json` — OpenAI provider config
- `assets/fabrcore-json-azure.json` — Azure OpenAI provider config
- `assets/appsettings-server.json` — Server appsettings
- `assets/server-csproj.xml` — Server .csproj
- `assets/agents-csproj.xml` — Agents library .csproj

## Scaffolding a Complete System

To scaffold a full FabrCore solution (server + agents):

1. Create solution and projects:
```powershell
dotnet new sln -n MySystem
dotnet new web -n MySystem.Server -o src/MySystem.Server
dotnet new classlib -n MySystem.Agents -o src/MySystem.Agents
dotnet sln add src/MySystem.Server src/MySystem.Agents
```

2. Add NuGet packages:
```powershell
dotnet add src/MySystem.Server package FabrCore.Host
dotnet add src/MySystem.Agents package FabrCore.Sdk
```

3. Add project references:
```powershell
dotnet add src/MySystem.Server reference src/MySystem.Agents
```

4. Copy and customize the asset templates for each project.

For detailed reference on any topic, use the specialized skills listed in the Quick Reference table above.
