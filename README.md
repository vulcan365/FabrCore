# FabrCore

[![License](https://img.shields.io/badge/license-Apache%202.0-blue.svg)](LICENSE)
[![NuGet](https://img.shields.io/nuget/v/FabrCore.Core.svg)](https://www.nuget.org/packages/FabrCore.Core)
[![.NET 10](https://img.shields.io/badge/.NET-10-purple.svg)](https://dotnet.microsoft.com/)

**Build AI agents in .NET. Run them as distributed Orleans actors. Connect them to your applications, tools, and knowledge.**

FabrCore combines [Microsoft Agent Framework](https://github.com/microsoft/agent-framework)
with Orleans to provide agent hosting, conversations, tool execution, inter-agent messaging,
and application integration. Start with a standalone host and Surface chat UI, or configure
SQL for persistent state, access control, long-term memory, and GraphRAG.

Built by [Vulcan365 AI](https://vulcan365.ai). Explore [fabrcore.ai](https://fabrcore.ai)
and the [guides and tutorials](https://fabrcore.ai/blogs).

**Upgrading to 2.0?** This breaking release consolidates packages and changes database and ACL
configuration. Read the [2.0 release notes](RELEASE_NOTES.md) and
[migration guide](docs/database-modes.md#acl-administration-and-migration) before upgrading.

## What you can build

- **Conversational and task-oriented agents** with model access, tools, plugins, MCP servers,
  timers, reminders, and structured message handling.
- **Multi-agent workflows** with private internal specialists, background delegation,
  model-managed todos, plan/execute modes, bounded loops, and blueprint-defined squads.
- **Knowledge-backed applications** with explicitly scoped Memory and GraphRAG in SQL mode.
- **Interactive workspaces** using Surface: Blazor chat, command center, Adaptive Cards,
  and squads without requiring a Forge account.
- **Connected agents** through HTTP, WebSocket v2, A2A, Microsoft 365 Copilot, and Teams.
- **Observable execution** with message and LLM monitoring, token usage, security audit,
  and optional signed execution evidence.

The SDK uses `Microsoft.Extensions.AI` abstractions for model access. Context management
preserves task instructions and tool-call relationships, bounds older tool output, and validates
historical summaries before saving them. See [compaction correctness](docs/compaction-correctness.md)
and [harness efficiency](docs/harness-efficiency.md) for behavior and tuning guidance.

## Start standalone, add persistence when needed

| Capability | Default standalone host | Host with a FabrCore SQL connection |
| --- | --- | --- |
| Agents, harnesses, tools, MCP, blueprints, skills, squads | Available | Available |
| Surface and application integrations | Available when configured | Available when configured |
| Runtime state and conversations | In memory; lost on process restart | Persistent with the default SQL Orleans provider |
| Cross-principal agent ACL | Trusted workspace; no grant enforcement | Enforced principals, roles, groups, and grants |
| Long-term Memory and GraphRAG | Unavailable | Available; select plugins and scopes per agent |
| Security audit, execution evidence, A2A task snapshots | In-memory defaults | SQL-backed defaults |

Standalone mode still applies authentication, privileged administration authentication, and
storage/session ownership checks. Use SQL-mode ACL enforcement when hosting mutually untrusted
principals. Default standalone runtime state does not survive a process restart.

Supply `ConnectionStrings:FabrCore` through application configuration or a secret provider to
enable the SQL feature set. The database must already exist and support the SQL Server 2025 /
Azure SQL vector and graph schemas. Configure the required chat and embedding models as well.

```json
{
  "ConnectionStrings": {
    "FabrCore": "Server=localhost;Database=fabrcore;Integrated Security=true;TrustServerCertificate=true"
  }
}
```

This example uses local-development connection settings. See [standalone and SQL modes](docs/database-modes.md)
for deployment configuration, existing split databases, schema provisioning, and Azure/custom
Orleans providers. Database selection requires a restart; a failed SQL startup never silently
becomes a standalone host. Host includes SQL services, but standalone startup does not activate
them. SDK-only agent libraries have no SQL implementation dependency.

## Try the sample

Install the **.NET 10 SDK**, then use a fresh checkout:

```powershell
git clone https://github.com/vulcan365/FabrCore.git
cd FabrCore
Copy-Item samples/FabrCore.SampleApp/fabrcore.sample.json samples/FabrCore.SampleApp/fabrcore.json
```

Edit `samples/FabrCore.SampleApp/fabrcore.json` and configure the `default` model and its API-key
alias. Enable only the integrations you intend to use. Local `fabrcore.json` files are gitignored.

```powershell
dotnet run --project samples/FabrCore.SampleApp --launch-profile http
```

Open **http://localhost:5248/surface**. The sample runs Host and Surface together with localhost
Orleans, a development fallback identity, and in-memory demo data. SQL, Forge, and Microsoft 365
credentials are not required for this standalone experience. Model calls use your configured provider.

## Add FabrCore to your application

Install the host in an ASP.NET Core application:

```shell
dotnet add package FabrCore.Host
```

```csharp
using FabrCore.Host;

var builder = WebApplication.CreateBuilder(args);
builder.AddFabrCoreServer();

var app = builder.Build();
app.UseFabrCoreServer();
app.Run();
```

Host brings SDK and Core transitively. Put model configuration in `fabrcore.json`; put host
settings such as `FabrCore:Database` and connection strings in application configuration.
For a separate agent class library, reference `FabrCore.Sdk`.

An agent handles messages through `FabrCoreAgentProxy`:

```csharp
using FabrCore.Core;
using FabrCore.Sdk;
using Microsoft.Agents.AI;

[AgentAlias("my-assistant")]
public sealed class MyAssistantAgent(
    AgentConfiguration config,
    IServiceProvider services,
    IFabrCoreAgentHost host) : FabrCoreAgentProxy(config, services, host)
{
    private ChatClientAgentResult chat = null!;

    public override async Task OnInitialize()
    {
        chat = await CreateChatClientAgent(
            chatClientConfigName: "default",
            threadId: "main");
    }

    public override async Task<AgentMessage> OnMessage(AgentMessage message)
    {
        var result = await chat.Agent.RunAsync(message.Message ?? "", chat.Session);
        var reply = message.Response();
        reply.Message = result.Text;
        return reply;
    }
}
```

Configure a `default` model, reference the agent assembly from the host, and provision
`my-assistant` through a [blueprint](docs/blueprints.md) or the Host API. Application assemblies
and referenced FabrCore dependencies are discovered automatically. For todos, delegation,
and completion loops, use the [FabrCore harness](docs/skills/fabrcore-harness/SKILL.md).

## Packages

| Package | Purpose |
| --- | --- |
| [FabrCore.Core](https://www.nuget.org/packages/FabrCore.Core) | Shared interfaces, models, protocols, and service contracts |
| [FabrCore.Sdk](https://www.nuget.org/packages/FabrCore.Sdk) | Agent development, harnesses, tools, MCP, and typed HTTP clients |
| [FabrCore.Host](https://www.nuget.org/packages/FabrCore.Host) | Orleans runtime and APIs, integrated SQL Server, Memory, GraphRAG, ACL, and operational stores |
| [FabrCore.Client.Orleans](https://www.nuget.org/packages/FabrCore.Client.Orleans) | Direct Orleans client with host-assisted gateway discovery |
| [FabrCore.Client.WebSocket](https://www.nuget.org/packages/FabrCore.Client.WebSocket) | Typed WebSocket v2 client with reconnect, replay, and acknowledgements |
| [FabrCore.Host.AzureStorage](https://www.nuget.org/packages/FabrCore.Host.AzureStorage) | Optional Azure Storage provider for Orleans |
| [FabrCore.Host.Testing](https://www.nuget.org/packages/FabrCore.Host.Testing) | In-memory Host/A2A integration-test helpers |
| [FabrCore.Services.Microsoft365Copilot](https://www.nuget.org/packages/FabrCore.Services.Microsoft365Copilot) | Microsoft 365 Copilot and Teams channel integration |
| [FabrCore.Surface](https://www.nuget.org/packages/FabrCore.Surface) | Blazor workspace, chat, Adaptive Cards, and squads |

Most applications connect through the HTTP API or WebSocket client. Agent creation and
blueprint provisioning remain HTTP operations. A2A endpoints are built into Host and enabled
through configuration; the Microsoft 365 channel is a separate integration package.

The former `FabrCore.Host.SqlServer`, `FabrCore.Services.Contracts`, `FabrCore.Services.Memory`,
and `FabrCore.Services.GraphRag` packages are retired in 2.0. Their implementations and contracts
have moved into Host, Core, and SDK. See the [package migration table](RELEASE_NOTES.md#breaking-changes-and-upgrade).

## Documentation

| Topic | Start here |
| --- | --- |
| 2.0 upgrade and database choices | [Release notes](RELEASE_NOTES.md) · [Database modes and migration](docs/database-modes.md) |
| Release upgrades | [1.6 / 1.7 / 1.8 to 2.0 skill](docs/skills/fabrcore-releases/SKILL.md) |
| Agent development | [Agent guide](docs/skills/fabrcore-agent/SKILL.md) · [Internal specialists](docs/skills/fabrcore-agent/references/internal-agent-composition.md) |
| Harnesses and context management | [Harness guide](docs/skills/fabrcore-harness/SKILL.md) · [Compaction](docs/compaction-correctness.md) |
| Hosting and model configuration | [Server guide](docs/skills/fabrcore-server/SKILL.md) · [Orleans configuration](docs/skills/fabrcore-orleans/SKILL.md) |
| Tools and integrations | [Plugins/tools](docs/skills/fabrcore-plugins-tools/SKILL.md) · [MCP](docs/skills/fabrcore-mcp/SKILL.md) · [A2A](docs/a2a.md) |
| Applications and orchestration | [Sample application](samples/FabrCore.SampleApp) · [Blueprints](docs/blueprints.md) |
| Memory behavior and evaluation | [Release defaults](docs/memory-release-defaults.md) · [Readiness review](docs/memory-readiness-review.md) |
| Operations | [Monitoring](docs/skills/fabrcore-agentmonitor/SKILL.md) · [Cloud Server protocol](docs/cloud-server-protocol.md) |
| Builds and releases | [Build instructions](builds/README.md) |

FabrCore is open source and works without Forge. Forge is the separate commercial operations
and governance product for fleet administration, identity/team management, incidents, and
configuration distribution. Cloud Server integration is optional.

## Build and test

The current source targets **.NET 10**, **Orleans 10.3.1**, and **Microsoft Agent Framework 1.20.0**.

See [Orleans 10.3 adoption](docs/orleans-10.3-adoption.md) for contract checks, storage compatibility,
telemetry, and optional durable SQL streams (preview provider).
With PowerShell 7 installed:

```powershell
./scripts/Build.ps1                  # Release build, deterministic tests, and nine packages
./scripts/Build.ps1 -Version 2.0.0-blah # Build and pack an explicit version
./scripts/Test-BuildScripts.ps1      # Validate inventory and release-script behavior
```

SQL integration tests and live model evaluations are explicit workflows. See
[build instructions](builds/README.md) for prerequisites, local package feeds, and release previews.

## Contributing and license

Issues and pull requests are welcome at [vulcan365/FabrCore](https://github.com/vulcan365/FabrCore).
Please include reproduction steps and relevant tests for behavior changes.

FabrCore is licensed under [Apache 2.0](LICENSE). See [NOTICE](NOTICE) for attribution requirements.
