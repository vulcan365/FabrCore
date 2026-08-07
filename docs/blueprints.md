# FabrCore blueprints

Blueprints are the OSS configuration experience for agents and Surface squads. They are
plain JSON, belong in source control, and use the same document from local development
through Forge fleet distribution.

The canonical envelope is `FabrCore.Core.Blueprints.FabrCoreBlueprint`:

- `name`, `description`, and `version` identify the document.
- `agents` contains normal `AgentConfiguration` objects.
- package-owned top-level sections are extensions. Surface contributes `squads`, an
  array that defines Orchestrator and Task squads.

See the commented
[`blueprint.sample.jsonc`](../samples/FabrCore.SampleApp/blueprint.sample.jsonc) file for
a copy-me template. Remove comments before submitting it as JSON.

## Apply and store

Apply a document directly:

```http
POST /fabrcoreapi/Agent/blueprint
x-user-handle: developer1
Content-Type: application/json
```

The host runs every registered `IBlueprintExpander`, then ensures the resulting agents
exist. Relative handles are scoped to `x-user-handle`; cross-principal handles are
rejected.

Blueprint resources use one per-principal store:

```text
GET    /fabrcoreapi/Blueprint
GET    /fabrcoreapi/Blueprint/{name}
PUT    /fabrcoreapi/Blueprint/{name}
DELETE /fabrcoreapi/Blueprint/{name}
POST   /fabrcoreapi/Blueprint/{name}/apply
```

Surface uses these endpoints. Its old private
`surface/command-center/blueprint` storage record is no longer written.

## Squads

`squadType: "orchestrator"` provisions a single router agent that delegates each request
to the best squad member. `squadType: "task"` provisions a task coordinator built on the
Microsoft Agent Framework harness primitives: it tracks the plan as a model-owned todo list
(`todos_*`), delegates concurrently to executor members and consults SME members through
background-agent tools (`background_agents_*`), and loops until no todos and no delegations
remain outstanding. Generated handles use `squad-{slug}` plus `-{member}` suffixes.

`taskOptions` for a task squad:

| Key | Default | Effect |
| --- | --- | --- |
| `workerModelName` | `default` | Model backing the coordinator. Members use their own configured models. |
| `personaPrompt` | none | Appended to the coordinator's instructions. |
| `clientAgentOverlay` | none | Prepended to every delegation message sent to a member. |
| `delegationTimeoutSeconds` | `120` | A delegation exceeding this is abandoned and reported to the coordinator as failed. |
| `maxLoopIterations` | `10` | Safety cap on coordinator re-invocations within one run. |

A run completes inside a single turn, so the todo list does not carry across user messages.
Members that fail their health probe are excluded from the roster at activation and named in
the coordinator's health metrics.

The retired Swarm runtime (`squadType: "swarm"`, the `"swarm"` blueprint extension, and
`SwarmV2`/`swarm2.*`/`squad2-*` names) is not part of the OSS API. Existing deployments
should migrate stored definitions to `orchestrator` or `task` squads under the top-level
`"squads"` extension before adopting this release.

## Administration security

Versioned Memory and GraphRAG administration APIs require the `FabrCoreAdmin` policy.
Configure a cluster-scoped key outside source control:

```json
{
  "FabrCore": {
    "AdminAuthentication": {
      "ApiKey": "<secret>",
      "PrincipalId": "forge-cluster-admin"
    }
  }
}
```

Clients send `Authorization: Bearer <secret>` plus `x-user-handle` for ACL and audit
attribution.
