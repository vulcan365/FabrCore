# FabrCore blueprints

Blueprints are the OSS configuration experience for agents and Swarm squads. They are
plain JSON, belong in source control, and use the same document from local development
through Forge fleet distribution.

The canonical envelope is `FabrCore.Core.Blueprints.FabrCoreBlueprint`:

- `name`, `description`, and `version` identify the document.
- `agents` contains normal `AgentConfiguration` objects.
- package-owned top-level sections are extensions. Surface contributes `swarm`, whose
  `squads` array defines Swarm, Orchestrator, and Task squads.

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

## Swarm

`squadType: "swarm"` is the supervised runtime: fast triage, planning, supervised
dependency-wave execution, per-task verification, and bounded retries. Its generated
handles use `squad-{slug}` plus `-planner`, `-supervisor`, and `-verifier` suffixes.

The retired implementation and the temporary `SwarmV2`, `swarm2.*`, and `squad2-*`
names are not part of the OSS API. Existing V365 deployments should migrate stored
definitions before adopting this release.

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
