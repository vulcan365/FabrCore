# Blueprint Provisioning Reference

Use a Blueprint to idempotently ensure a baseline agent set for one principal. It is an application-supplied request payload, not a persisted Host resource: FabrCore does not enumerate principals or apply Blueprints during startup, and `Name`/`Version` are echoed only for caller traceability.

## Lifecycle

1. Application code chooses the principal and calls `POST /fabrcoreapi/Agent/blueprint`, normally at tenant creation, first sign-in, or workspace initialization.
2. FabrCore validates every entry's handle and principal scope, then scopes bare agent handles to `x-user-handle`.
3. New agents are configured and tracked by that principal.
4. A tracked configured agent returns health without being reconfigured. A tracked `NotConfigured` agent is configured from the Blueprint.
5. The response contains one `AgentHealthStatus` per requested agent. Continue or retry based on individual results.

The Host does not remove agents absent from a later Blueprint. Treat a changed `version` as an application deployment signal: call `/agent/create` with the desired configuration when a deliberate upgrade is required.

## Handle and reconfiguration rules

- Send `x-user-handle` for the target principal.
- A bare handle such as `assistant` becomes `principal:assistant`.
- A fully qualified handle is accepted only when its principal prefix equals `x-user-handle`; cross-principal Blueprint handles return `400 Bad Request`.
- Blueprint processing overrides `ForceReconfigure` to `false`. Use `POST /fabrcoreapi/Agent/create` to intentionally change an existing agent.
- Handle/scoping validation occurs before processing agents. Once processing begins, a configuration error for one agent produces an unhealthy result and the remaining agents are still attempted.

## SDK bootstrap

```csharp
using FabrCore.Core;
using FabrCore.Sdk;

var blueprint = new AgentBlueprintRequest
{
    Name = "workspace-defaults",
    Version = "1.0.0",
    Agents =
    [
        new AgentConfiguration
        {
            Handle = "assistant",
            AgentType = "your-agent-alias",
            Models = "default",
            SystemPrompt = "Help the principal with their workspace."
        }
    ]
};

var result = await hostApi.EnsureBlueprintAgentsAsync(principalHandle, blueprint, cancellationToken: cancellationToken);

if (result.FailureCount != 0)
{
    // Inspect result.Results and retry or surface provisioning failure as appropriate.
}
```

Use the same payload with the REST endpoint; `assets/agent-blueprint.json` is a copyable request template. `SuccessCount` counts only `Healthy` results, so a configured `Degraded` or `Unhealthy` agent is reported as a non-success even though Blueprint processing did not reconfigure it.
