# FabrCore

[![License](https://img.shields.io/badge/license-Apache%202.0-blue.svg)](LICENSE)
[![NuGet](https://img.shields.io/nuget/v/FabrCore.Core.svg)](https://www.nuget.org/packages/FabrCore.Core)
[![.NET 10](https://img.shields.io/badge/.NET-10.0-purple.svg)](https://dotnet.microsoft.com/)
[![Orleans 10](https://img.shields.io/badge/Orleans-10.0-blue.svg)](https://learn.microsoft.com/en-us/dotnet/orleans/)

**A .NET framework for building distributed AI agent systems on [Orleans](https://learn.microsoft.com/en-us/dotnet/orleans/) and [Microsoft Agent Framework](https://github.com/microsoft/Agents).**

FabrCore provides the building blocks for creating, hosting, and connecting to AI agents that run as Orleans grains. Agents are durable, scalable, and communicate through a structured message-passing architecture with built-in support for LLM providers, tool execution, MCP integration, and real-time monitoring.

**Website:** [fabrcore.ai](https://fabrcore.ai) | **Built by:** [Vulcan365 AI](https://vulcan365.ai)

> **New to FabrCore?** Start with the [blogs and guides on fabrcore.ai/blogs](https://fabrcore.ai/blogs) for walkthroughs, architecture deep-dives, and real-world patterns.

---

## Key Features

- **Distributed AI Agents** -- Orleans grains with durable state, timers, reminders, and health monitoring
- **Microsoft Agent Framework** -- Built on `Microsoft.Agents.AI` with `ChatClientAgent`, sessions, and thread patterns
- **Multi-LLM Support** -- Azure OpenAI, OpenAI, Anthropic, and custom providers via `Microsoft.Extensions.AI`
- **Plugins and Tools** -- Stateful plugins and stateless standalone tools with dependency injection
- **MCP Integration** -- Model Context Protocol servers via Stdio and HTTP transports
- **Inter-Agent Messaging** -- Fan-out, pipeline, supervisor patterns with ACL-based access control
- **Real-Time Monitoring** -- Agent message traffic, events, LLM request/response capture, and token tracking
- **ChatDock UI** -- Floating chat panel component for Blazor Server with multi-instance support
- **Audio Transcription** -- Azure OpenAI gpt-4o transcription model support
- **Testing Harness** -- In-memory agent testing with mock and live LLM modes

## Architecture

```
+---------------------------------------------------+
|                Your Application                    |
+-------------------+-------------------------------+
|  FabrCore.Client  |        FabrCore.Host          |
|  ClientContext     |  AgentGrain (Orleans 10)      |
|  ChatDock (UI)    |  API / Chat Completions       |
|  Health Monitor   |  WebSocket Middleware          |
+-------------------+-------------------------------+
|                  FabrCore.Sdk                      |
|  FabrCoreAgentProxy  *  ChatClientAgent            |
|  Plugins  *  Tools  *  MCP  *  Agent Monitor       |
+---------------------------------------------------+
|                 FabrCore.Core                      |
|  Interfaces  *  Data Models  *  Grain Abstractions |
+---------------------------------------------------+
```

## Packages

| Package | Description |
|---------|-------------|
| **[FabrCore.Core](https://www.nuget.org/packages/FabrCore.Core)** | Core interfaces, data models, and grain abstractions |
| **[FabrCore.Sdk](https://www.nuget.org/packages/FabrCore.Sdk)** | Agent SDK -- `FabrCoreAgentProxy`, plugins, tools, MCP, monitoring |
| **[FabrCore.Host](https://www.nuget.org/packages/FabrCore.Host)** | Server host -- Orleans silo, REST API, chat completions, WebSocket |
| **[FabrCore.Client](https://www.nuget.org/packages/FabrCore.Client)** | Client library -- `ClientContext`, `ChatDock` Blazor component, health monitoring |

## Quick Start

### 1. Install packages

```bash
dotnet add package FabrCore.Host
```

`FabrCore.Host` pulls in `FabrCore.Sdk` and `FabrCore.Core` transitively. For client applications, add `FabrCore.Client` instead.

### 2. Create an agent

```csharp
using FabrCore.Sdk;
using FabrCore.Core;

[AgentAlias("my-assistant")]
public class MyAssistantAgent : FabrCoreAgentProxy
{
    public override async Task<AgentMessage> OnMessage(AgentMessage message)
    {
        var (agent, session) = await Host.CreateChatClientAgent(
            modelName: "AzureProd",
            instructions: "You are a helpful assistant."
        );

        var response = await agent.SendAsync(session, message.Text);
        return message.ToReply(response);
    }
}
```

### 3. Configure the server

```csharp
var builder = WebApplication.CreateBuilder(args);

builder.AddFabrCoreServer();

var app = builder.Build();
app.UseFabrCoreServer();
app.Run();
```

### 4. Configure model access

Copy `FabrCore.json.example` to `FabrCore.json` in your project root and add your LLM provider configuration:

```json
{
  "ModelConfigurations": [
    {
      "Name": "AzureProd",
      "Provider": "Azure",
      "Uri": "https://your-resource.cognitiveservices.azure.com/",
      "Model": "gpt-4.1-mini",
      "ApiKeyAlias": "AZURE_KEY"
    }
  ],
  "ApiKeys": [
    {
      "Alias": "AZURE_KEY",
      "Value": "your-api-key-here"
    }
  ]
}
```

> **Note:** `FabrCore.json` is gitignored by default to prevent accidental secret commits.

## Documentation

Full documentation is available in the [`docs/skills`](docs/skills/) directory:

| Topic | Description |
|-------|-------------|
| [FabrCore Overview](docs/skills/fabrcore/SKILL.md) | Architecture, prerequisites, and project templates |
| [Agent Development](docs/skills/fabrcore-agent/SKILL.md) | Building agents with lifecycle methods, state, timers, and reminders |
| [Microsoft Agent Framework](docs/skills/fabrcore-agentframework/SKILL.md) | `ChatClientAgent`, sessions, thread patterns, and `Microsoft.Extensions.AI` |
| [Server Setup](docs/skills/fabrcore-server/SKILL.md) | Orleans silo, REST API, WebSocket, LLM providers, and system agents |
| [Client Setup](docs/skills/fabrcore-client/SKILL.md) | Blazor Server clients, Orleans connectivity, and agent messaging |
| [ChatDock Component](docs/skills/fabrcore-chatdock/SKILL.md) | Floating chat panel UI with customizable positions and multi-instance |
| [Plugins and Tools](docs/skills/fabrcore-plugins-tools/SKILL.md) | Stateful plugins, stateless tools, and DI integration |
| [MCP Integration](docs/skills/fabrcore-mcp/SKILL.md) | Model Context Protocol servers via Stdio and HTTP transports |
| [Messaging and Access Control](docs/skills/fabrcore-messaging/SKILL.md) | Inter-agent communication patterns, routing, and ACL rules |
| [Agent Monitor](docs/skills/fabrcore-agentmonitor/SKILL.md) | Message traffic monitoring, LLM call capture, and token tracking |
| [Orleans Configuration](docs/skills/fabrcore-orleans/SKILL.md) | Clustering, persistence, streaming, reminders, and multi-silo |
| [Testing](docs/skills/fabrcore-testing/SKILL.md) | In-memory test harness with mock and live LLM modes |
| [Audio Transcription](docs/skills/fabrcore-transcription/SKILL.md) | Azure OpenAI gpt-4o audio transcription |

**Check out the [FabrCore Blog](https://fabrcore.ai/blogs)** for tutorials, architecture deep-dives, integration guides, and best practices.

## Technology Stack

- **.NET 10** -- Latest .NET runtime
- **Orleans 10.0** -- Distributed actor framework for grain-based agents
- **Microsoft.Agents.AI 1.0** -- Microsoft Agent Framework for AI agent patterns
- **Microsoft.Extensions.AI** -- Unified AI abstractions for multi-provider LLM support
- **Blazor Server** -- Real-time web UI for agent interaction

## Building from Source

```bash
dotnet build src/FabrCore.sln
```

## License

Licensed under the [Apache License, Version 2.0](LICENSE).

See [NOTICE](NOTICE) for attribution requirements.

## Contributing

Contributions are welcome! Please open an issue or pull request on [GitHub](https://github.com/vulcan365/FabrCore).

## Links

- [FabrCore Website](https://fabrcore.ai)
- [FabrCore Blog](https://fabrcore.ai/blogs)
- [Vulcan365 AI](https://vulcan365.ai)
- [NuGet Packages](https://www.nuget.org/packages?q=FabrCore)
- [Orleans Documentation](https://learn.microsoft.com/en-us/dotnet/orleans/)
- [Microsoft Agent Framework](https://github.com/microsoft/Agents)
