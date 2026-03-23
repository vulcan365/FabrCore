# Bug: ChatDock fails to connect to pre-configured cross-owner agents

## Summary

When a ChatDock connects to a cross-owner agent (e.g., `system:automation-xxx`), it never checks the agent's actual health on the server. It only checks `IsAgentTracked` on the user's local client context, which will always return `false` for agents owned by a different user. The ChatDock then assumes the agent is not configured and returns an error, even when the agent is healthy and running.

## Steps to Reproduce

1. Create a system-owned agent server-side via `ConfigureSystemAgentAsync`
2. Add a ChatDock for a different user pointing to the system agent:
   ```razor
   <ChatDock UserHandle="default-user"
             AgentHandle="system:automation-xxx"
             AgentType="automation-agent"
             Position="ChatDockPosition.BottomLeft" />
   ```
3. The agent is confirmed healthy (logs show `Agent configuration completed successfully`)
4. ChatDock displays: `"Shared agent 'system:automation-xxx' is not configured. It must be created server-side."`

## Root Cause

In `ChatDock.razor` `ConnectAsync()` (lines 545-566):

```csharp
// Step 1: Quick check if agent is tracked by this client
var isTracked = await _clientContext.IsAgentTracked(AgentHandle);

if (isTracked)
{
    // Step 2: Agent is tracked — check its actual health/configured state
    _agentHealth = await _clientContext.GetAgentHealth(AgentHandle, HealthDetailLevel.Basic);
}

// Step 3: ...
var isCrossOwner = AgentHandle.Contains(':');

if (_agentHealth is null || !_agentHealth.IsConfigured)
{
    if (isCrossOwner)
    {
        _error = $"Shared agent '{AgentHandle}' is not configured. It must be created server-side.";
        return;
    }
    // ... create agent for own-user case
}
```

The flow:
1. `IsAgentTracked("system:automation-xxx")` → **false** (not in `default-user`'s tracked list)
2. `_agentHealth` stays **null** (`GetAgentHealth` is only called when `isTracked == true`)
3. `isCrossOwner` → **true** (handle contains `:`)
4. Since `_agentHealth is null` AND `isCrossOwner` → error and early return

The ChatDock correctly avoids creating cross-owner agents, but it never checks whether the agent already exists on the server before giving up.

## Suggested Fix

After the `isTracked` check, call `GetAgentHealth` directly for untracked cross-owner agents. `GetAgentHealth` goes directly to the `AgentGrain` (not through the user's tracked list) and will return the real health state:

```csharp
var isTracked = await _clientContext.IsAgentTracked(AgentHandle);

if (isTracked)
{
    _agentHealth = await _clientContext.GetAgentHealth(AgentHandle, HealthDetailLevel.Basic);
}
else if (AgentHandle.Contains(':'))
{
    // Cross-owner agent — not tracked locally, but may exist server-side.
    // GetAgentHealth goes directly to the grain, bypassing the tracked list.
    try
    {
        _agentHealth = await _clientContext.GetAgentHealth(AgentHandle, HealthDetailLevel.Basic);
    }
    catch
    {
        // Agent grain doesn't exist or is unreachable — will be handled below
    }
}
```

This way:
- **Agent exists and is configured** → `_agentHealth.IsConfigured == true` → ChatDock connects normally
- **Agent doesn't exist** → `_agentHealth` is null or not configured → existing error message fires correctly

## Impact

Any ChatDock pointing to a shared/system agent will fail to connect, making the cross-owner agent + ACL pattern unusable from the UI. This blocks the core use case of system agents being accessible to multiple users via ChatDock.

## Environment

- FabrCore 0.5.59-local
- .NET 10
- Blazor Server with prerendering
