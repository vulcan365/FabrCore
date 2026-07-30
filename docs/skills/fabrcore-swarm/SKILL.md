---
name: fabrcore-swarm
description: >
  FabrCore Surface Swarm — blueprint-defined, supervised multi-agent squads with triage,
  dependency-wave planning, bounded parallel execution, verification, retries, replanning,
  budgets, and subject-matter-expert consultation. Use for SurfaceSquadType.Swarm,
  SurfaceSquadService, SurfaceSwarm* runtime types, swarm blueprint extensions, swarm.* wire
  messages, squad-* handles, or troubleshooting a Swarm squad.
---

# FabrCore Swarm

Swarm is the supervised squad runtime in the OSS `FabrCore.Surface` package. It is configured
declaratively in the canonical `FabrCoreBlueprint`; do not build an interactive creation flow
in OSS.

## Current names

- Namespace: `FabrCore.Surface.Ai.Swarm`
- Squad type: `SurfaceSquadType.Swarm`
- Service: `ISurfaceSquadService` / `SurfaceSquadService`
- Runtime types: `SurfaceSwarm*`
- Message vocabulary: `swarm.*`
- Generated aliases: `squad-{slug}`, with `-planner`, `-supervisor`, and `-verifier` shells
- Blueprint extension: top-level `"swarm"` with a `"squads"` array

The old one-shot implementation is retired. Never introduce `SwarmV2`, `swarm2.*`,
`squad2-*`, `surface.squads`, or dual old/new argument stamping.

## Blueprint-first setup

```json
{
  "name": "research-team",
  "version": "1",
  "agents": [
    {
      "handle": "researcher",
      "agentType": "surface",
      "models": "default",
      "systemPrompt": "Research carefully and cite evidence."
    }
  ],
  "swarm": {
    "squads": [
      {
        "squadType": "swarm",
        "name": "Research Desk",
        "description": "Plans, executes, and verifies research tasks.",
        "orchestratorModel": "default",
        "plannerModel": "default",
        "agents": [
          {
            "handle": "researcher",
            "name": "Researcher",
            "agentType": "surface",
            "role": "executor"
          }
        ]
      }
    ]
  }
}
```

Store and apply through the canonical host API:

```text
PUT  /fabrcoreapi/Blueprint/research-team
POST /fabrcoreapi/Blueprint/research-team/apply
```

The host invokes `SurfaceSwarmBlueprintExpander`. It converts each Swarm definition to
agent configurations and provisions them under the authenticated principal.

## Execution model

1. The orchestrator triages the request into direct or planned execution and chooses risk,
   approval, concurrency, and verification settings.
2. The planner creates a dependency graph with acceptance criteria.
3. Deterministic validation rejects invalid graphs or role assignments.
4. The host-code supervisor dispatches ready dependency waves up to the bounded concurrency
   limit.
5. The verifier checks each task fail-closed. Failures can retry with feedback.
6. Stall detection can replan or consult an SME, within hard budgets.
7. Task, progress, artifact, and policy ledgers persist in agent state.

The supervisor is deterministic host code; it is not another unconstrained planning LLM.

## Roles

- `executor`: may receive planned tasks.
- `subjectMatterExpert`: consultation only; validation must not schedule it as an executor.
- `helper`: supporting member that is not the default task owner.

## Budgets

`SurfaceSwarmBudgets` supplies safe defaults:

- 20 rounds
- 30 minutes wall-clock
- 2 task attempts and 2 validation attempts
- 2 replans and 2 consecutive stalls
- 180 seconds per task
- 3 maximum concurrent tasks
- 4 SME consultations per planning pass

Keep these bounded. If harness agents join a squad, Swarm owns run-level budgets, the runtime
safety scope owns turn-level budgets, and harness loop caps remain local to that agent.

## Programmatic use

Blueprints are the normal path. For tests or package internals, resolve
`ISurfaceSquadService` and use `SurfaceSquadService` with a
`SurfaceSwarmSquadDefinition`. Register Surface with:

```csharp
builder.Services.AddFabrCoreSurfaceComponents();
```

That registration also adds `SurfaceSwarmBlueprintExpander`.

## Verification

For Swarm changes, run:

```powershell
dotnet test src\FabrCore.Surface.Tests\FabrCore.Surface.Tests.csproj --configuration Release
dotnet build src\FabrCore.sln --configuration Release
```

Pay special attention to dependency validation, role enforcement, retry/replan budget
exhaustion, persisted ledgers, canonical wire names, and generated handles.
