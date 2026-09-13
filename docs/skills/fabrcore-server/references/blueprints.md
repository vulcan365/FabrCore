# Canonical Blueprint Reference

Use `FabrCore.Core.Blueprints.FabrCoreBlueprint` as the source-controlled configuration
document shared by local development, Surface, and vendor-neutral cloud fleet delivery.

## Shape

For reusable authenticated bindings, the optional Connections service registers
the top-level `connectedAgents` extension. It supplies group defaults, per-agent
overrides, and deployment-time `$principal` substitution. See
[the connection guide](../../fabrcore-connections/references/integration.md#reusable-blueprints).
Preview does not create profiles, consent, identities, or tokens. Provision profiles
and exact agent grants independently through connection administration.

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
  "squads": []
}
```

`agents` contains normal `AgentConfiguration` records. Other top-level properties are captured
in `FabrCoreBlueprint.Extensions`. The package that owns an extension registers an
`IBlueprintExpander`; Surface registers the `squads` expander.

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

## Legacy apply lifecycle rules

- Applying is idempotent for already configured agents.
- Applying ignores incoming `ForceReconfigure = true`; use `/agent/create` for intentional
  reconfiguration.
- Omitted agents are not deleted.
- One invalid agent produces a failed result while remaining expanded configurations continue.
- Extension expansion occurs Host-side before agent ensure processing.
- Cloud servers can deliver the canonical document with `ApplyOnRefresh`. The Host routes
  delivery through cluster-coordinated blueprint management with deployment receipts. Optional
  `DeploymentId` and `ApplyMode` fields default compatibly; a removed envelope entry does not
  delete the definition or any agent.

The SDK `AgentBlueprintRequest` and `EnsureBlueprintAgentsAsync` remain agents-only compatibility
surfaces. They cannot carry extension sections. Use the canonical REST resource for new work.

## Administration workflows in FabrCore 2.0

Use `/fabrcoreapi/admin/v1/principals/{principal}/blueprint-management/summaries`
for typed paged summaries (default 100, maximum 1,000), with offset and revision.
Do not deserialize legacy name lists as summary objects. Save/delete at
`/blueprint-management/{name}` using If-Match; create with quoted `*`, update with
the current definition revision. Clone/import preserve the complete JSON document.
GET `/blueprints/{name}` exports it. Mutations are serialized per principal.

POST `/blueprint-management/validate` validates an unsaved document; GET
`/blueprint-management/{name}/preview` returns the saved revision and expansion
digest. Previews must be effect-free; extension packages explicitly implement
`IBlueprintPreviewExpander`. Missing dependencies, duplicate handles, unsupported
extensions and cross-principal handles are rejected.

Submit a persistent operation with kind deploy, blueprintName and deployment
{ operationId, mode, revision, expansionDigest }. `ensure` creates missing agents;
`update` also reconfigures existing agents. Neither deletes omitted agents.
Review per-agent results and explicitly retry selected failed agentHandles with a
new operation ID against the reviewed expansion. Incomplete work is not replayed.
Summary DeployedRevision/DeploymentStatus/DefinitionDrift concern saved/deployed
definitions; automatic live-agent configuration drift detection is not implemented.
See the [administration protocol](../../fabrcore-cloud-administration/references/administration.md).
