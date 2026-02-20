# FabrCore

[![License](https://img.shields.io/badge/license-Apache%202.0-blue.svg)](LICENSE)
[![NuGet](https://img.shields.io/nuget/v/FabrCore.Core.svg)](https://www.nuget.org/packages/FabrCore.Core)

**An Orleans-based framework for building distributed AI agent systems in .NET.**

FabrCore provides the building blocks for creating, hosting, and connecting to AI agents that run as [Orleans](https://learn.microsoft.com/en-us/dotnet/orleans/) grains. Agents are durable, scalable, and communicate through a structured message-passing architecture.

## Architecture

```
┌─────────────────────────────────────────────────┐
│                  Your Application                │
├──────────────────┬──────────────────────────────┤
│   FabrCore.Client    │         FabrCore.Host            │
│  ClientContext   │   AgentGrain (Orleans)       │
│  ChatDock (UI)   │   API Controllers            │
│                  │   WebSocket Middleware        │
├──────────────────┴──────────────────────────────┤
│                   FabrCore.Sdk                       │
│  FabrCoreAgentProxy · TaskWorkingAgent · ChatClient  │
├─────────────────────────────────────────────────┤
│                  FabrCore.Core                        │
│  Interfaces · Data Models · Grain Abstractions   │
└─────────────────────────────────────────────────┘
```

## Packages

| Package | Description |
|---------|-------------|
| **FabrCore.Core** | Core interfaces and data models for the FabrCore agent framework |
| **FabrCore.Sdk** | SDK for building AI agents — `FabrCoreAgentProxy`, `TaskWorkingAgent`, chat client extensions |
| **FabrCore.Host** | Orleans server host — grains, API controllers, WebSocket middleware, streaming |
| **FabrCore.Client** | Client library — `ClientContext`, `ChatDock` Blazor component |

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
        // Create an AI-powered chat agent
        var (agent, session) = await Host.CreateChatClientAgent(
            modelName: "AzureProd",
            instructions: "You are a helpful assistant."
        );

        var response = await agent.SendAsync(session, message.Text);
        return message.ToReply(response);
    }
}
```

### 3. Configure the host

```csharp
var builder = WebApplication.CreateBuilder(args);

// Configure FabrCore with Orleans
builder.AddFabrCoreServer();

var app = builder.Build();
app.UseFabrCoreServer();
app.Run();
```

### 4. Configure model access

Copy `fabrcore.json.example` to `fabrcore.json` in your project root and fill in your API keys:

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

> **Note:** `fabrcore.json` is gitignored by default to prevent accidental secret commits.

## Configuration

### Orleans Clustering

FabrCore supports multiple Orleans clustering providers out of the box:

- **Azure Storage** — `Microsoft.Orleans.Clustering.AzureStorage`
- **SQL Server (ADO.NET)** — `Microsoft.Orleans.Clustering.AdoNet`
- **Localhost** — for development

Configure clustering through standard Orleans configuration in your host's `appsettings.json`.

### WebSocket API

FabrCore includes WebSocket middleware for real-time agent communication. See the [WebSocket documentation](src/FabrCore.Host/WebSocket/README.md) for protocol details and usage.

### Client Library

The `FabrCore.Client` package provides `ClientContext` for connecting to agents and a `ChatDock` Blazor component for building chat UIs. See the [Client documentation](src/FabrCore.Client/README.md) for details.

## Building from Source

```bash
dotnet build src/FabrCore.sln
```

## License

Licensed under the [Apache License, Version 2.0](LICENSE).

See [NOTICE](NOTICE) for attribution requirements.

## Contributing

Contributions are welcome! Please open an issue or pull request on [GitHub](https://github.com/vulcan365/FabrCore).
