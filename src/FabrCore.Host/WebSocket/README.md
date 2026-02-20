# FabrCore WebSocket Interface

This WebSocket interface provides a way for clients that cannot use the ClientContext (e.g., web browsers, non-.NET clients) to interact with the FabrCore system via WebSocket connections.

## Overview

The WebSocket session wraps the `ClientGrain` in the same way that `ClientContext` does, providing:
- Automatic client handle initialization from HTTP header
- Command handling for system operations (createagent, unsubscribe, etc.)
- Message routing to/from agents
- Observer pattern for receiving messages from agents

## Endpoint

Connect to: `ws://your-fabrcore-host/ws`

For secure connections: `wss://your-fabrcore-host/ws`

## Authentication

**Required Header:** `x-fabrcore-userid`

The client handle is determined by the `x-fabrcore-userid` header sent during the WebSocket handshake. This header is required and cannot be empty.

**Example:**
```
x-fabrcore-userid: user123
```

The session will automatically initialize and subscribe to the ClientGrain using this user ID upon connection.

## Message Format

All messages are sent as JSON-encoded `AgentMessage` objects with the following structure:

```json
{
  "Id": "unique-message-id",
  "ToHandle": "target-agent-handle",
  "FromHandle": "sender-handle",
  "OnBehalfOfHandle": null,
  "DeliverToHandle": null,
  "Channel": null,
  "MessageType": "command|chat|response|etc",
  "Message": "message content",
  "IsResponse": false,
  "DataType": "json|text|etc",
  "Data": null,
  "Files": [],
  "State": {},
  "TraceId": "trace-id"
}
```

## Commands

Commands are special messages with `MessageType: "command"` that control the WebSocket session behavior.

### CreateAgent Command

Creates a new agent instance associated with your client handle.

**Request (using State):**
```json
{
  "MessageType": "command",
  "Message": "createagent",
  "State": {
    "agentHandle": "my-agent",
    "agentType": "Demo.Server.DemoAgent1",
    "models": "openai:gpt-4",
    "systemPrompt": "You are a helpful assistant.",
    "args": "{\"key\":\"value\"}"
  }
}
```

**Request (using Data):**
```json
{
  "MessageType": "command",
  "Message": "createagent",
  "DataType": "json",
  "Data": "<base64-encoded-json-AgentConfiguration>"
}
```

Where the AgentConfiguration JSON structure is:
```json
{
  "Handle": "my-agent",
  "AgentType": "Demo.Server.DemoAgent1",
  "Models": "openai:gpt-4",
  "SystemPrompt": "You are a helpful assistant.",
  "Streams": [],
  "Args": {
    "key": "value"
  }
}
```

**Response:**
```json
{
  "MessageType": "command_response",
  "Message": "success",
  "IsResponse": true,
  "State": {
    "command": "createagent",
    "status": "success"
  }
}
```

## Session Lifecycle

The WebSocket session automatically manages the ClientGrain subscription:

- **On Connect**: Automatically subscribes to ClientGrain using the user ID
- **On Disconnect**: Automatically unsubscribes and cleans up resources

You don't need to manually manage subscriptions or unsubscriptions.

## Sending Messages to Agents

Once you've connected (with the `x-fabrcore-userid` header) and created an agent, you can send messages to agents.

**Request:**
```json
{
  "ToHandle": "my-client-handle:my-agent",
  "FromHandle": "my-client-handle",
  "MessageType": "chat",
  "Message": "Hello, agent! How are you?"
}
```

**Note:** Agent handles are prefixed with your client handle (e.g., `my-client-handle:my-agent`).

## Receiving Messages from Agents

When agents send messages to your client, they will be automatically forwarded to your WebSocket connection. These messages come through the observer pattern.

**Received Message Example:**
```json
{
  "Id": "response-message-id",
  "ToHandle": "my-client-handle",
  "FromHandle": "my-client-handle:my-agent",
  "MessageType": "chat",
  "Message": "Hello! I'm doing well, thank you for asking.",
  "IsResponse": true,
  "TraceId": "original-trace-id"
}
```

## Error Handling

If an error occurs during command processing, you'll receive an error response:

```json
{
  "MessageType": "command_response",
  "Message": "error",
  "IsResponse": true,
  "State": {
    "command": "createagent",
    "status": "error",
    "error": "Client not initialized. Call sethandle command first."
  }
}
```

For general errors:
```json
{
  "MessageType": "error",
  "Message": "Error message details",
  "IsResponse": true
}
```

## Example Session Flow

1. **Connect to WebSocket (with header)**
   ```
   Connect to ws://localhost:5000/ws
   Header: x-fabrcore-userid: user123
   ```

   The session automatically initializes and subscribes to the ClientGrain.

2. **Create Agent**
   ```json
   Send: {
     "MessageType": "command",
     "Message": "createagent",
     "State": {
       "agentHandle": "assistant",
       "agentType": "Demo.Server.DemoAgent1",
       "models": "openai:gpt-4"
     }
   }
   Receive: {"MessageType": "command_response", "Message": "success", ...}
   ```

3. **Chat with Agent**
   ```json
   Send: {
     "ToHandle": "user123:assistant",
     "FromHandle": "user123",
     "MessageType": "chat",
     "Message": "What's the weather like?"
   }
   Receive: {
     "ToHandle": "user123",
     "FromHandle": "user123:assistant",
     "MessageType": "chat",
     "Message": "I'm a demo assistant and don't have access to weather data...",
     "IsResponse": true
   }
   ```

4. **Disconnect**
   ```
   Close WebSocket connection
   ```

   The session automatically unsubscribes and cleans up resources.

## JavaScript Example

**Note:** JavaScript WebSocket API doesn't support custom headers during the handshake. You have two options:

1. **Use a WebSocket library that supports headers** (e.g., `ws` in Node.js)
2. **Pass the user ID as a query parameter** and update the middleware to read from query string

### Option 1: Node.js with 'ws' library

```javascript
const WebSocket = require('ws');

const ws = new WebSocket('ws://localhost:5000/ws', {
  headers: {
    'x-fabrcore-userid': 'user123'
  }
});

ws.on('open', () => {
  console.log('Connected to FabrCore WebSocket');

  // Connection is automatically initialized, create agent immediately
  ws.send(JSON.stringify({
    MessageType: 'command',
    Message: 'createagent',
    State: {
      agentHandle: 'assistant',
      agentType: 'Demo.Server.DemoAgent1',
      models: 'openai:gpt-4'
    }
  }));
});

ws.on('message', (data) => {
  const message = JSON.parse(data);
  console.log('Received:', message);

  // Handle chat responses
  if (message.MessageType === 'chat' && message.IsResponse) {
    console.log('Agent says:', message.Message);
  }
});

ws.on('error', (error) => {
  console.error('WebSocket error:', error);
});

ws.on('close', () => {
  console.log('Disconnected from FabrCore WebSocket');
});

// Function to send a chat message
function sendMessage(text) {
  ws.send(JSON.stringify({
    ToHandle: 'user123:assistant',
    FromHandle: 'user123',
    MessageType: 'chat',
    Message: text
  }));
}
```

### Option 2: Browser with Query Parameter

If you need to use the browser's native WebSocket API, pass the user ID as a query parameter:

```javascript
// Note: This requires updating the middleware to read from query string
const userId = 'user123';
const ws = new WebSocket(`ws://localhost:5000/ws?userid=${userId}`);

ws.onopen = () => {
  console.log('Connected to FabrCore WebSocket');

  // Create agent immediately
  ws.send(JSON.stringify({
    MessageType: 'command',
    Message: 'createagent',
    State: {
      agentHandle: 'assistant',
      agentType: 'Demo.Server.DemoAgent1',
      models: 'openai:gpt-4'
    }
  }));
};

// ... rest of the code
```

## Architecture

The WebSocket implementation consists of:

1. **WebSocketMiddleware** (`WebSocketMiddleware.cs`)
   - Handles incoming WebSocket connections at `/ws`
   - Extracts user ID from header or query parameter
   - Creates WebSocketSession instances
   - Manages connection lifecycle

2. **WebSocketSession** (`WebSocketSession.cs`)
   - Wraps ClientGrain similar to ClientContext
   - Automatically subscribes on connection and unsubscribes on disposal
   - Implements IClientGrainObserver to receive messages
   - Processes commands and routes messages
   - Serializes/deserializes JSON messages

3. **Integration** (in `FabrCoreHostExtensions.cs`)
   - Enables WebSocket support via `app.UseWebSockets()`
   - Registers WebSocket middleware

## Telemetry and Metrics

The WebSocket implementation includes comprehensive metrics:

- `fabrcore.websocket.sessions.created` - Number of sessions created
- `fabrcore.websocket.sessions.closed` - Number of sessions closed
- `fabrcore.websocket.messages.received` - Messages received from clients
- `fabrcore.websocket.messages.sent` - Messages sent to clients
- `fabrcore.websocket.commands.processed` - Commands processed
- `fabrcore.websocket.connections.accepted` - Connections accepted
- `fabrcore.websocket.connections.rejected` - Connections rejected
- `fabrcore.websocket.errors` - Error count

All operations are also traced via OpenTelemetry for distributed tracing.

## Security Considerations

1. **Authentication**: Consider adding authentication before accepting WebSocket connections
2. **Authorization**: Validate that clients can only access their own handles
3. **Rate Limiting**: Implement rate limiting to prevent abuse
4. **Input Validation**: All commands validate input parameters
5. **TLS/SSL**: Use `wss://` in production environments

## Troubleshooting

### Connection Rejected - Missing x-fabrcore-userid Header
- Ensure the `x-fabrcore-userid` header is sent during the WebSocket handshake
- If using a browser's native WebSocket API, consider using query parameters instead
- If using Node.js, use the `ws` library which supports headers

### Connection Rejected - Empty Header
- Ensure the `x-fabrcore-userid` header has a non-empty value

### "Client not initialized" Error
- This shouldn't occur if the connection was successful
- The client is automatically initialized upon connection

### Messages Not Received
- Connection is automatically subscribed upon successful WebSocket handshake
- Check that the agent handle includes your client prefix (e.g., `user123:agent`)
- Verify the WebSocket connection is still open

### Agent Creation Fails
- Ensure the AgentType exists and is properly registered
- Check that all required configuration parameters are provided
- Verify the handle is unique

## Testing

### Web Chat Interface (Recommended)

A full-featured chat interface is available in Demo.Server:

1. Start Demo.Server: `dotnet run` from `src/Demo.Server`
2. Navigate to `http://localhost:5000/` (automatically redirects to chat)
3. Enter your User ID and click "Connect"
4. Create agents and start chatting immediately

The chat interface includes:
- User ID configuration for testing
- Agent creation form
- Real-time chat with message history
- Visual message types (user, agent, system, error)
- Settings persistence via localStorage

See `src/Demo.Server/wwwroot/README.md` for detailed documentation.
