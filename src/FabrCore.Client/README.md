# Fabr.Client

Client library for connecting to and interacting with Fabr agents.

## Usage

### Recommended: Factory Pattern (New)

The factory pattern provides proper initialization and automatic resource cleanup:

```csharp
using Fabr.Client;
using Microsoft.Extensions.DependencyInjection;

// The factory is registered automatically when using AddFabrClient()
var factory = serviceProvider.GetRequiredService<IClientContextFactory>();

// Create a fully-initialized client context
// The context is ready to use immediately - no need to call SetHandle separately
await using var clientContext = await factory.CreateAsync("my-client-handle");

// Subscribe to messages
clientContext.AgentMessageReceived += (sender, message) =>
{
    Console.WriteLine($"Received message from {message.FromHandle}: {message.Content}");
};

// Send a message to an agent
var request = new AgentMessage
{
    FromHandle = "my-client-handle",
    ToHandle = "agent-handle",
    Content = "Hello, agent!"
};

var response = await clientContext.SendAndReceiveMessage(request);
Console.WriteLine($"Agent responded: {response.Content}");

// Automatic cleanup when exiting the 'await using' block
// No need to call Unsubscribe() manually
```

### Alternative: Direct Instantiation (Legacy)

⚠️ **Deprecated**: This approach is maintained for backward compatibility but is not recommended for new code.

```csharp
using Fabr.Client;
using Microsoft.Extensions.DependencyInjection;

var clientContext = serviceProvider.GetRequiredService<IClientContext>();

// Must manually call SetHandle before using
await clientContext.SetHandle("my-client-handle");

try
{
    // Use the client context...
    var response = await clientContext.SendAndReceiveMessage(request);
}
finally
{
    // Must manually unsubscribe
    await clientContext.Unsubscribe();
}
```

## Key Features

### Factory Pattern Benefits

1. **Single-Step Initialization**: Create and initialize in one call
2. **Guaranteed Valid State**: Returned context is always ready to use
3. **Automatic Cleanup**: `IAsyncDisposable` ensures proper resource cleanup
4. **Compile-Time Safety**: No runtime errors from forgetting to call `SetHandle()`
5. **SOLID Principles**: Better separation of concerns and dependency management

### Client Context Operations

- **SendAndReceiveMessage**: Synchronous request-response communication
- **SendMessage**: Asynchronous fire-and-forget messaging
- **CreateAgent**: Create new agent instances
- **AgentMessageReceived**: Event for receiving asynchronous messages

### Observability

The client includes built-in metrics and tracing:

- **Metrics**: Connection counts, message processing duration, error rates
- **Distributed Tracing**: Full OpenTelemetry support for debugging
- **Structured Logging**: Comprehensive logging at all levels

## Configuration

Add Fabr client to your application:

```csharp
using Fabr.Client;

var builder = Host.CreateApplicationBuilder(args);

// Adds Orleans client and Fabr client services
builder.AddFabrClient();

var host = builder.Build();

// Optional post-configuration
host.UseFabrClient();

await host.RunAsync();
```

### Connection Retry Configuration

The Fabr client supports configurable connection retry logic, which is especially useful for local development when the Fabr server may not be running when client applications start.

**appsettings.json:**

```json
{
  "Orleans": {
    "ClusterId": "fabr-cluster",
    "ServiceId": "fabr-service",
    "ClusteringMode": "Localhost",
    "ConnectionRetryCount": 10,
    "ConnectionRetryDelay": "00:00:05",
    "GatewayListRefreshPeriod": "00:00:30"
  }
}
```

**Configuration Options:**

| Option | Default | Description |
|--------|---------|-------------|
| `ConnectionRetryCount` | 5 | Maximum number of retry attempts. Set to 0 to disable retries. |
| `ConnectionRetryDelay` | 3 seconds | Delay between retry attempts. |
| `GatewayListRefreshPeriod` | 30 seconds | How often the client refreshes the gateway list. |

**Log Output Example:**

When the Fabr server is not available at startup, you'll see logs like:

```
info: Fabr.Client.FabrClientConnectionRetryFilter[0]
      Fabr client connection retry filter initialized. MaxRetries: 10, RetryDelay: 00:00:05
warn: Fabr.Client.FabrClientConnectionRetryFilter[0]
      Orleans client connection attempt 1 of 11 failed. Error: SiloUnavailableException: Unable to connect. Retrying in 00:00:05...
info: Fabr.Client.FabrClientConnectionRetryFilter[0]
      Initiating Orleans client connection attempt 2 of 11...
warn: Fabr.Client.FabrClientConnectionRetryFilter[0]
      Orleans client connection attempt 2 of 11 failed. Error: SiloUnavailableException: Unable to connect. Retrying in 00:00:05...
...
info: Fabr.Client.FabrClientConnectionRetryFilter[0]
      Orleans client connected successfully after 4 attempt(s)
```

**Metrics:**

The connection retry filter emits the following metrics:

- `fabr.client.connection.retry_attempts` - Number of retry attempts made
- `fabr.client.connection.success` - Number of successful connections (with attempt count tag)
- `fabr.client.connection.failures` - Number of failures after all retries exhausted

## Advanced Usage

### Creating Agents

```csharp
await using var clientContext = await factory.CreateAsync("my-client");

var agentConfig = new AgentConfiguration
{
    Handle = "my-agent",
    AgentType = "MyCustomAgent",
    SystemPrompt = "You are a helpful assistant"
};

await clientContext.CreateAgent(agentConfig);
```

### Event-Driven Messaging

```csharp
await using var clientContext = await factory.CreateAsync("my-client");

// Subscribe to incoming messages
clientContext.AgentMessageReceived += async (sender, message) =>
{
    Console.WriteLine($"Message from {message.FromHandle}: {message.Content}");

    // Send a response
    var response = new AgentMessage
    {
        FromHandle = "my-client",
        ToHandle = message.FromHandle,
        Content = "Message received!"
    };

    await clientContext.SendMessage(response);
};

// Keep the application running to receive messages
await Task.Delay(Timeout.Infinite);
```

## Migration Guide

### From Direct Instantiation to Factory Pattern

**Before:**
```csharp
var context = serviceProvider.GetRequiredService<IClientContext>();
await context.SetHandle("my-handle");
try
{
    // Use context...
}
finally
{
    await context.Unsubscribe();
}
```

**After:**
```csharp
var factory = serviceProvider.GetRequiredService<IClientContextFactory>();
await using var context = await factory.CreateAsync("my-handle");
// Use context...
// Automatic cleanup via IAsyncDisposable
```

## Best Practices

1. **Always use the factory pattern** for new code
2. **Use `await using`** to ensure proper resource cleanup
3. **Handle `AgentMessageReceived` events** for asynchronous messaging
4. **Use structured logging** to track message flows
5. **Monitor metrics** for performance and reliability insights

## See Also

- [Fabr.Core](../Fabr.Core/) - Core domain models and interfaces
- [Fabr.Host](../Fabr.Host/) - Server-side agent hosting
- [Fabr.Sdk](../Fabr.Sdk/) - Advanced SDK features
