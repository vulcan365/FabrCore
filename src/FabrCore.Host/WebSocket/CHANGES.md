# WebSocket Implementation Changes

## Summary

Updated the Fabr WebSocket implementation to use the `x-fabr-userid` header (or query parameter fallback) for client identification instead of requiring a `sethandle` command.

## Changes Made

### 1. WebSocketMiddleware.cs

**Modified:** Header/Query Parameter Extraction
- Now extracts user ID from `x-fabr-userid` header (preferred)
- Falls back to `userid` query parameter for browser compatibility
- Rejects connections missing user ID with HTTP 400
- Passes user ID to WebSocketSession constructor

**Code Location:** Lines 50-76

### 2. WebSocketSession.cs

**Modified:** Constructor and Initialization
- Added `userId` parameter to constructor
- Changed `handle` from nullable to required field
- Automatically initializes ClientGrain on session start
- Removed manual handle setter

**Modified:** Command Processing
- Removed `sethandle` command case
- Removed `HandleSetHandleCommandAsync` method
- Removed `unsubscribe` command case
- Removed `HandleUnsubscribeCommandAsync` method
- Updated error messages to remove "Call sethandle first" references
- Commands now only support: `createagent`
- Unsubscription happens automatically on session disposal/disconnect

**Added:** InitializeClientGrainAsync Method
- New method called automatically in StartAsync
- Subscribes to ClientGrain using user ID from header/query
- Includes comprehensive logging and error handling

**Code Locations:**
- Constructor: Lines 58-72
- StartAsync: Lines 77-112
- InitializeClientGrainAsync: Lines 117-149
- ProcessCommandAsync: Lines 260-331 (sethandle and unsubscribe removed)
- HandleUnsubscribeCommandAsync: Removed (automatic in DisposeAsync)

### 3. FabrHostExtensions.cs

**No changes required** - WebSocket middleware already registered

### 4. README.md

**Updated:** Documentation Structure
- Added "Authentication" section explaining header requirement
- Removed "SetHandle Command" documentation
- Updated numbering for remaining commands (CreateAgent is now #1)
- Updated example session flow to remove sethandle step
- Updated JavaScript examples to show both Node.js (with headers) and browser (with query parameters) approaches
- Updated troubleshooting section

**Key Sections Updated:**
- Lines 19-30: Authentication section added
- Lines 59: CreateAgent is now the only command
- Lines 115-122: Session Lifecycle section added (explains automatic subscription management)
- Lines 201-229: Session flow updated (removed sethandle and unsubscribe steps)
- Lines 231-318: JavaScript examples updated
- Lines 371-383: Troubleshooting updated

### 5. TestClient.html

**Modified:** Connection Flow
- Removed "Client Handle" input and "Set Handle" button
- Removed "Unsubscribe" button
- Added "User ID" input field
- Automatically appends user ID as query parameter to WebSocket URL
- Updated UI messaging to reflect automatic initialization
- Added informational banner about browser limitations

**Code Changes:**
- Lines 175-180: Browser note banner
- Lines 190-193: User ID input (replaces client handle)
- Lines 223: Removed unsubscribe button
- Lines 298-326: Updated connect() function to use query parameter
- Lines 373-402: Updated createAgent() to check for currentUserId
- Removed unsubscribe() function

## Migration Guide

### For Existing Clients

**Before:**
```javascript
ws = new WebSocket('ws://localhost:5000/ws');
ws.onopen = () => {
  ws.send(JSON.stringify({
    MessageType: 'command',
    Message: 'sethandle',
    State: { handle: 'user123' }
  }));
};
```

**After (Node.js with headers):**
```javascript
ws = new WebSocket('ws://localhost:5000/ws', {
  headers: { 'x-fabr-userid': 'user123' }
});
ws.onopen = () => {
  // Session is already initialized, start using it
  ws.send(JSON.stringify({
    MessageType: 'command',
    Message: 'createagent',
    State: { agentHandle: 'myagent', agentType: 'MyAgent' }
  }));
};
```

**After (Browser with query parameter):**
```javascript
ws = new WebSocket('ws://localhost:5000/ws?userid=user123');
ws.onopen = () => {
  // Session is already initialized, start using it
  ws.send(JSON.stringify({
    MessageType: 'command',
    Message: 'createagent',
    State: { agentHandle: 'myagent', agentType: 'MyAgent' }
  }));
};
```

## Benefits

1. **Simplified Flow**: Client handle is set automatically on connection
2. **Better Security**: User ID comes from connection metadata, not client messages
3. **Cleaner API**: Only one command to remember (createagent)
4. **Browser Compatible**: Query parameter fallback for browser clients
5. **Immediate Ready**: Session is ready to use as soon as connection opens
6. **Automatic Cleanup**: No need to manually unsubscribe before disconnecting

## Backward Compatibility

⚠️ **Breaking Changes**:
1. The `sethandle` command is no longer supported
2. The `unsubscribe` command is no longer supported

Clients attempting to use these commands will receive an "Unknown command" error.

All existing clients must be updated to:
- Provide user ID via header or query parameter during connection
- Remove manual unsubscribe calls (cleanup is automatic on disconnect)

## Testing

### Web Chat Interface

A full-featured chat interface has been created in Demo.Server at `src/Demo.Server/wwwroot/chat.html`:

**Features:**
- Manual User ID input for testing (x-fabr-userid)
- Agent creation form with all configuration options
- Real-time chat interface with message history
- Visual distinction between user, agent, system, and error messages
- Settings persistence via localStorage
- Responsive design for desktop and mobile

**To use:**
1. Start Demo.Server: `dotnet run` from `src/Demo.Server`
2. Navigate to `http://localhost:5000/` (redirects to chat automatically)
3. Enter your User ID and connect
4. Create an agent and start chatting

See `src/Demo.Server/wwwroot/README.md` for detailed documentation.

## Security Considerations

- **Header-based authentication** (x-fabr-userid) is preferred for production
- **Query parameter** should only be used for browser clients in controlled environments
- Consider implementing proper authentication middleware before the WebSocket handler
- Validate and sanitize user IDs
- In production, use authenticated tokens instead of plain user IDs
