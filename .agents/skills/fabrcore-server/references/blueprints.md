# Canonical Blueprint Reference

Use `FabrCore.Core.Blueprints.FabrCoreBlueprint` as the source-controlled configuration
document shared by local development, Surface, and Forge fleet delivery.

## Shape

```json
{
  "name": "workspace-defaults",
  "description": "Default agents and squads",
  "version": "1.0.0",
  "agents": [
    {
      "handle": "assistant",
      "agentType": "your-agent-alias",
      "models": "default",
      "systemPrompt": "Help the principal with their workspace."
    }
  ],
  "swarm": {
    "squads": []
  }
}
```

`agents` contains normal `AgentConfiguration` records. Other top-level properties are captured
in `FabrCoreBlueprint.Extensions`. The package that owns an extension registers an
`IBlueprintExpander`; Surface registers the `swarm` expander.

## Apply or store

Apply without storing:

```http
POST /fabrcoreapi/Agent/blueprint
x-user-handle: principal-handle
```

Store and later apply:

```text
GET    /fabrcoreapi/Blueprint
GET    /fabrcoreapi/Blueprint/{name}
PUT    /fabrcoreapi/Blueprint/{name}
DELETE /fabrcoreapi/Blueprint/{name}
POST   /fabrcoreapi/Blueprint/{name}/apply
```

All resource operations are partitioned by `x-user-handle`. A bare agent handle is scoped to
that principal. A fully qualified handle must use the same principal prefix.

## Lifecycle rules

- Applying is idempotent for already configured agents.
- Applying ignores incoming `ForceReconfigure = true`; use `/agent/create` for intentional
  reconfiguration.
- Omitted agents are not deleted.
- One invalid agent produces a failed result while remaining expanded configurations continue.
- Extension expansion occurs Host-side before agent ensure processing.
- Forge can deliver the same canonical document with `ApplyOnRefresh`; the Host applies it only
  when a new cloud configuration version is accepted.

The SDK `AgentBlueprintRequest` and `EnsureBlueprintAgentsAsync` remain agents-only compatibility
surfaces. They cannot carry extension sections. Use the canonical REST resource for new work.
