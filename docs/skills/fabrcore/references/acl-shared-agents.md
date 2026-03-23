# Access Control & Shared Agents

## Overview

FabrCore includes an ACL (Access Control List) system that controls which clients can access agents owned by other users or the system. This enables **shared agents** — agents owned by one entity (e.g., `"system"`) that multiple users can communicate with.

By default, agents are scoped to their owner: a `ClientContext` with handle `"alice"` can only access agents prefixed with `"alice:"`. The ACL system opens controlled cross-owner access.

## Key Concepts

- **Owner** — The prefix before the colon in a handle (e.g., `"system"` in `"system:automation_agent-123"`)
- **System agents** — Agents with owner `"system"`, created server-side, accessible to all users by default
- **ACL rule** — A pattern-based rule granting specific permissions from a caller to a target owner's agents
- **ACL provider** — Pluggable interface (`IAclProvider`) for evaluating and managing rules

## Handle Routing

Handles follow the format `"owner:agentAlias"`. Routing rules:

| Handle Format | Behavior |
|---------------|----------|
| `"assistant"` (bare alias) | Auto-prefixed with caller's owner: `"alice:assistant"` |
| `"system:assistant"` (fully-qualified) | Used as-is — cross-owner routing |

Cross-owner routing works automatically at the transport level. The ACL system controls **authorization** — whether the caller is allowed to access the target.

## Implicit Rules

- **Own-agent access is always allowed.** If the caller's owner matches the target owner, no ACL evaluation occurs. This is a zero-overhead short-circuit.
- **Default seed rule:** If no ACL rules are configured, the system seeds `system:* -> * -> Message,Read` — all users can message and read system agents.

## ACL Rules

### Rule Structure

```csharp
public class AclRule
{
    public string OwnerPattern { get; set; }   // Target agent's owner
    public string AgentPattern { get; set; }   // Target agent's alias
    public string CallerPattern { get; set; }  // Who is allowed
    public AclPermission Permission { get; set; }
}
```

### Pattern Matching

| Pattern | Matches | Example |
|---------|---------|---------|
| `"*"` | Anything | `"*"` matches all owners |
| `"prefix*"` | Starts-with | `"automation_*"` matches `"automation_agent-123"` |
| `"group:name"` | Group members (CallerPattern only) | `"group:admins"` matches members of the admins group |
| `"exact"` | Case-insensitive literal | `"system"` matches only `"system"` |

### Permissions

```csharp
[Flags]
public enum AclPermission
{
    None      = 0,
    Message   = 1,   // Send messages (SendMessage, SendAndReceiveMessage)
    Configure = 2,   // Create or reconfigure the agent (CreateAgent)
    Read      = 4,   // Read threads, state, health
    Admin     = 8,   // Modify ACL rules
    All       = Message | Configure | Read | Admin
}
```

### Evaluation Order

1. **Own-agent check** — If caller == target owner, allow with `All` permissions (no rules evaluated)
2. **Rule scan** — Rules are evaluated in order. First match wins:
   - If the matched rule grants the required permission → **allow**
   - If the matched rule doesn't grant the required permission → **deny** (with reason)
3. **No match** → **deny**

## Configuration

### fabrcore.json

Configure ACL rules and groups in the `Acl` section of `fabrcore.json` (alongside `ModelConfigurations` and `ApiKeys`):

```json
{
  "ModelConfigurations": [ ... ],
  "ApiKeys": [ ... ],
  "Acl": {
    "Rules": [
      {
        "OwnerPattern": "system",
        "AgentPattern": "*",
        "CallerPattern": "*",
        "Permission": "Message,Read"
      },
      {
        "OwnerPattern": "shared",
        "AgentPattern": "analytics_*",
        "CallerPattern": "group:premium",
        "Permission": "Message,Read"
      },
      {
        "OwnerPattern": "admin",
        "AgentPattern": "*",
        "CallerPattern": "group:admins",
        "Permission": "Message,Configure,Read"
      }
    ],
    "Groups": {
      "admins": ["alice", "bob"],
      "premium": ["alice", "charlie", "dave"]
    }
  }
}
```

If no rules are configured, the default rule `system:* -> * -> Message,Read` is seeded automatically.

### Rule Examples

| OwnerPattern | AgentPattern | CallerPattern | Permission | Meaning |
|--------------|-------------|---------------|------------|---------|
| `system` | `*` | `*` | `Message,Read` | Anyone can message any system agent |
| `system` | `automation_*` | `group:managers` | `Message,Read,Configure` | Managers can use and configure automation agents |
| `shared` | `report-builder` | `alice` | `Message` | Only Alice can message the shared report-builder |
| `*` | `*` | `group:superadmins` | `All` | Super admins have full access to all agents |

## Creating System Agents

System agents are created **server-side** using `IFabrCoreAgentService`. They are not created from a `ClientContext`.

### Server-Side Creation

```csharp
// Inject IFabrCoreAgentService
public class MyStartupService : IHostedService
{
    private readonly IFabrCoreAgentService _agentService;

    public MyStartupService(IFabrCoreAgentService agentService)
    {
        _agentService = agentService;
    }

    public async Task StartAsync(CancellationToken ct)
    {
        // Creates agent with grain key "system:automation_agent-123"
        await _agentService.ConfigureSystemAgentAsync(new AgentConfiguration
        {
            Handle = "automation_agent-123",
            AgentType = "automation-agent",
            SystemPrompt = "You are an automation assistant.",
            Models = ["default"]
        });
    }

    public Task StopAsync(CancellationToken ct) => Task.CompletedTask;
}
```

### From an API Controller

```csharp
[ApiController]
[Route("api/[controller]")]
public class SystemAgentController : ControllerBase
{
    private readonly IFabrCoreAgentService _agentService;

    public SystemAgentController(IFabrCoreAgentService agentService)
    {
        _agentService = agentService;
    }

    [HttpPost("create/{entityId}")]
    public async Task<AgentHealthStatus> CreateAutomationAgent(string entityId)
    {
        return await _agentService.ConfigureSystemAgentAsync(new AgentConfiguration
        {
            Handle = $"automation_agent-{entityId}",
            AgentType = "automation-agent",
            SystemPrompt = "You are an automation assistant for this entity."
        });
    }
}
```

### ConfigureSystemAgentAsync

```csharp
// Convenience method — equivalent to ConfigureAgentAsync("system", config)
Task<AgentHealthStatus> ConfigureSystemAgentAsync(
    AgentConfiguration config,
    HealthDetailLevel detailLevel = HealthDetailLevel.Basic);
```

The agent grain key becomes `"system:{config.Handle}"`. For example, `Handle = "automation_agent-123"` produces grain key `"system:automation_agent-123"`.

## Messaging System Agents from Clients

Users send messages to system agents using the full `"owner:alias"` handle:

### From ClientContext

```csharp
var context = await clientContextFactory.GetOrCreateAsync("alice");

// The colon tells the system this is a cross-owner handle — no re-prefixing
var response = await context.SendAndReceiveMessage(new AgentMessage
{
    ToHandle = "system:automation_agent-123",
    Message = "Run the daily report"
});
```

### From ChatDock (Blazor)

```razor
<ChatDock UserHandle="@userId"
          AgentHandle="system:automation_agent-123"
          AgentType="automation-agent" />
```

When `AgentHandle` contains a colon, `ChatDock` treats it as a cross-owner agent:

- **No auto-creation** — If the agent is not configured, ChatDock displays an error instead of attempting to create it. Cross-owner/shared agents must be created server-side (via `IFabrCoreAgentService.ConfigureSystemAgentAsync` or equivalent). ChatDock only auto-creates agents that the current user owns (bare alias handles).
- **Response matching** — ChatDock matches incoming messages using the full cross-owner handle as `FromHandle` (e.g., `"system:automation_agent-123"`), rather than prefixing with the user's owner.
- **ACL enforcement** — The ACL check in `ClientGrain` verifies the user has `Message` permission before delivering.

## Discovering Shared Agents

Users can discover agents they have permission to access:

```csharp
var context = await clientContextFactory.GetOrCreateAsync("alice");

// Returns all non-own agents where Alice has at least Message permission
List<AgentInfo> sharedAgents = await context.GetAccessibleSharedAgents();

foreach (var agent in sharedAgents)
{
    Console.WriteLine($"{agent.Key} ({agent.AgentType}) - {agent.Status}");
    // Output: "system:automation_agent-123 (automation-agent) - Active"
}
```

## Custom ACL Provider

The default `InMemoryAclProvider` loads rules from configuration and evaluates them in memory. For database-backed ACL, implement `IAclProvider`:

```csharp
public interface IAclProvider
{
    // Evaluate access
    Task<AclEvaluationResult> EvaluateAsync(
        string callerOwner, string targetOwner, string agentAlias, AclPermission required);

    // Rule management
    Task<List<AclRule>> GetRulesAsync();
    Task AddRuleAsync(AclRule rule);
    Task RemoveRuleAsync(AclRule rule);

    // Group management
    Task<Dictionary<string, HashSet<string>>> GetGroupsAsync();
    Task AddToGroupAsync(string groupName, string member);
    Task RemoveFromGroupAsync(string groupName, string member);
}
```

### Register Custom Provider

```csharp
builder.AddFabrCoreServer(new FabrCoreServerOptions
{
    AdditionalAssemblies = [typeof(MyAgent).Assembly]
}.UseAclProvider<SqlAclProvider>());
```

### Example: Database-Backed Provider

```csharp
public class SqlAclProvider : IAclProvider
{
    private readonly MyDbContext _db;

    public SqlAclProvider(MyDbContext db)
    {
        _db = db;
    }

    public async Task<AclEvaluationResult> EvaluateAsync(
        string callerOwner, string targetOwner, string agentAlias, AclPermission required)
    {
        // Own-agent always allowed
        if (string.Equals(callerOwner, targetOwner, StringComparison.OrdinalIgnoreCase))
            return new AclEvaluationResult(true, AclPermission.All);

        // Query database for matching rules
        var rules = await _db.AclRules
            .Where(r => r.TargetOwner == targetOwner || r.TargetOwner == "*")
            .OrderBy(r => r.Priority)
            .ToListAsync();

        // Evaluate rules...
        foreach (var rule in rules)
        {
            if (Matches(rule, callerOwner, targetOwner, agentAlias))
            {
                if (rule.Permission.HasFlag(required))
                    return new AclEvaluationResult(true, rule.Permission);
                return new AclEvaluationResult(false, rule.Permission, "Insufficient permission");
            }
        }

        return new AclEvaluationResult(false, AclPermission.None, "No matching rule");
    }

    // ... other interface methods
}
```

## Enforcement Points

ACL is enforced in `ClientGrain` — the trust boundary between clients and the Orleans cluster:

| Method | Permission Required | Notes |
|--------|-------------------|-------|
| `SendAndReceiveMessage` | `Message` | Checked after handle resolution, before grain call |
| `SendMessage` | `Message` | Checked after handle resolution, before stream publish |
| `SendEvent` | `Message` | Checked on channel-based events (not named streams) |
| `CreateAgent` | `Configure` | Checked for cross-owner handles only |

Agent-to-agent communication within the cluster is **trusted** and bypasses ACL. Server-side calls via `IFabrCoreAgentService` also bypass ACL since they don't go through `ClientGrain`.

### Error Handling

When ACL denies access, `ClientGrain` throws `UnauthorizedAccessException`:

```
UnauthorizedAccessException: Access denied: 'alice' cannot Message on 'private:secret-agent'.
No ACL rule matched for caller 'alice' accessing 'private:secret-agent'
```

## Runtime Rule Management

The `IAclProvider` supports runtime rule and group management (in-memory only for the default provider):

```csharp
// Inject IAclProvider in your service or controller
var aclProvider = serviceProvider.GetRequiredService<IAclProvider>();

// Add a rule at runtime
await aclProvider.AddRuleAsync(new AclRule
{
    OwnerPattern = "system",
    AgentPattern = "premium_*",
    CallerPattern = "group:premium",
    Permission = AclPermission.Message | AclPermission.Read
});

// Add user to a group
await aclProvider.AddToGroupAsync("premium", "newuser123");

// Remove a rule
await aclProvider.RemoveRuleAsync(new AclRule
{
    OwnerPattern = "system",
    AgentPattern = "premium_*",
    CallerPattern = "group:premium",
    Permission = AclPermission.Message | AclPermission.Read
});
```

Note: The default `InMemoryAclProvider` does not persist runtime changes. Changes are lost on restart. For persistent rule management, implement a custom `IAclProvider` backed by a database.

## Performance

- **Own-agent messages** (the common case): Zero ACL overhead — `clientId == targetOwner` short-circuits immediately
- **Cross-owner messages**: One `IAclProvider.EvaluateAsync()` call per message. The default `InMemoryAclProvider` evaluates purely in memory with no async I/O
- **Custom providers**: Can implement caching strategies as needed (e.g., cache rules with TTL, use Orleans grain for distributed cache)
