---
name: fabrcore-mcp
description: >
  FabrCore MCP (Model Context Protocol) integration — configure MCP servers for agents,
  Stdio and HTTP transport, config-driven vs code-driven setup, tool resolution, and troubleshooting.
  Triggers on: "MCP", "Model Context Protocol", "McpServerConfig", "ConnectMcpServerAsync",
  "MCP tools", "stdio MCP", "HTTP MCP", "MCP server", "McpTransportType",
  "MCP integration", "external tools MCP".
  Do NOT use for: plugins or standalone tools — use fabrcore-plugins-tools.
  Do NOT use for: general agent development — use fabrcore-agent.
allowed-tools: "Bash(dotnet:*) Bash(mkdir:*) Bash(ls:*) Bash(pwsh:*) Bash(powershell:*) Bash(git:*) Bash(dir:*)"
---

# FabrCore MCP Integration

FabrCore natively supports the Model Context Protocol (MCP) for connecting agents to external tool servers. MCP tools are resolved alongside plugin and standalone tools and presented to the LLM automatically.

## McpServerConfig Schema

```csharp
public class McpServerConfig
{
    public string Name { get; set; }                        // Unique server name
    public McpTransportType TransportType { get; set; }     // Stdio or Http

    // Stdio transport
    public string Command { get; set; }                     // Executable path
    public List<string> Arguments { get; set; }             // Command arguments
    public Dictionary<string, string> Env { get; set; }     // Environment variables

    // Http transport
    public string Url { get; set; }                         // Server endpoint URL
    public Dictionary<string, string> Headers { get; set; } // HTTP headers
}

public enum McpTransportType { Stdio, Http }
```

## Configuring MCP Servers

Add MCP server configurations to `AgentConfiguration.McpServers`:

### Stdio Transport

For local MCP servers that communicate via stdin/stdout:

```json
{
  "McpServers": [
    {
      "Name": "filesystem",
      "TransportType": "Stdio",
      "Command": "npx",
      "Arguments": ["-y", "@anthropic/mcp-filesystem", "/data"],
      "Env": { "NODE_ENV": "production" }
    }
  ]
}
```

### HTTP Transport

For remote MCP servers accessible via HTTP:

```json
{
  "McpServers": [
    {
      "Name": "remote-api",
      "TransportType": "Http",
      "Url": "https://api.example.com/mcp",
      "Headers": {
        "Authorization": "Bearer your-token"
      }
    }
  ]
}
```

## How MCP Tools Are Resolved

When an agent initializes via `ResolveConfiguredToolsAsync()`:

1. Reads `config.McpServers`
2. Creates an `IMcpClient` connection for each server
3. Calls `ListToolsAsync()` to discover available tools
4. Converts MCP tools to `AITool` instances
5. Merges them with plugin and standalone tools
6. All tools are passed to the LLM in `ChatOptions.Tools`

The MCP client lifecycle is managed by `FabrCoreAgentProxy`:
- Connected during `OnInitialize()`
- Disposed when the agent grain deactivates

## Config-Driven (Recommended)

MCP servers listed in `AgentConfiguration.McpServers` are automatically connected by `ResolveConfiguredToolsAsync()`. This is **fail-open** — if an MCP server can't connect, the agent continues without those tools:

```csharp
var tools = await ResolveConfiguredToolsAsync();
// Includes MCP tools if connections succeeded
```

## Code-Driven

For programmatic control where exceptions propagate (you handle errors):

```csharp
var mcpTools = await ConnectMcpServerAsync(new McpServerConfig
{
    Name = "my-mcp-server",
    TransportType = McpTransportType.Stdio,
    Command = "npx",
    Arguments = ["-y", "@my-org/mcp-server"],
    Env = new() { ["API_KEY"] = "secret" }
});
tools.AddRange(mcpTools);
```

HTTP MCP server:

```csharp
var mcpTools = await ConnectMcpServerAsync(new McpServerConfig
{
    Name = "remote-tools",
    TransportType = McpTransportType.Http,
    Url = "https://tools.example.com/mcp",
    Headers = new() { ["Authorization"] = "Bearer token" }
});
```

## Combining MCP with Plugins and Tools

MCP tools coexist with plugin and standalone tools:

```json
{
  "Handle": "research-agent",
  "AgentType": "researcher",
  "Plugins": ["web-browser"],
  "Tools": ["format-json"],
  "McpServers": [
    {
      "Name": "search-api",
      "TransportType": "Http",
      "Url": "https://search.example.com/mcp"
    }
  ]
}
```

The LLM sees all tools from all sources and can use any of them.

## Common MCP Servers

### File System Access

```json
{
  "Name": "filesystem",
  "TransportType": "Stdio",
  "Command": "npx",
  "Arguments": ["-y", "@anthropic/mcp-filesystem", "/allowed/path"]
}
```

### Git Operations

```json
{
  "Name": "git",
  "TransportType": "Stdio",
  "Command": "npx",
  "Arguments": ["-y", "@anthropic/mcp-git"]
}
```

### Database Access

```json
{
  "Name": "postgres",
  "TransportType": "Stdio",
  "Command": "npx",
  "Arguments": ["-y", "@anthropic/mcp-postgres"],
  "Env": { "DATABASE_URL": "postgresql://user:pass@localhost/db" }
}
```

## Programmatic Configuration

```csharp
var config = new AgentConfiguration
{
    Handle = "my-agent",
    AgentType = "my-agent",
    McpServers =
    [
        new McpServerConfig
        {
            Name = "filesystem",
            TransportType = McpTransportType.Stdio,
            Command = "npx",
            Arguments = ["-y", "@anthropic/mcp-filesystem", "/data"]
        },
        new McpServerConfig
        {
            Name = "api",
            TransportType = McpTransportType.Http,
            Url = "https://api.example.com/mcp",
            Headers = new() { ["Authorization"] = "Bearer token" }
        }
    ]
};
```

## Troubleshooting

**Connection refused:**
- Verify the MCP server is installed and accessible
- For Stdio: check that the command is in PATH
- For Http: verify the URL and network connectivity

**Tools not appearing:**
- Check server logs for MCP client errors
- Verify the MCP server implements `ListToolsAsync` correctly
- Ensure the `Name` doesn't conflict with other MCP servers

**Tool invocation errors:**
- Check the tool's input schema matches what the LLM provides
- Review server-side logs for MCP tool execution
- Verify authentication tokens are valid

**Performance:**
- Stdio MCP servers have process startup overhead on first call
- Http MCP servers depend on network latency
- Consider using plugins for performance-critical tools
