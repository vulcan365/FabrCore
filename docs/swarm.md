# Swarm architecture

FabrCore Swarm is the supervised multi-agent squad runtime in `FabrCore.Surface`. It is OSS,
principal-scoped, Orleans-hosted, and configured through the canonical blueprint API.

## Runtime flow

```text
user request
  -> orchestrator triage (direct or plan, risk, approval, concurrency)
  -> planner dependency graph + acceptance criteria
  -> deterministic plan validation
  -> optional approval gate
  -> host-code supervisor dispatches ready dependency waves
  -> executor agents
  -> fail-closed verifier
  -> retry / replan / SME consultation within budgets
  -> final result
```

The runtime persists four ledgers:

- Task ledger: dependency graph, assignment, status, attempts, acceptance criteria.
- Progress ledger: rounds, stalls, replans, timestamps, and run state.
- Artifact ledger: task outputs and verification feedback.
- Policy ledger: triage decision, approval state, concurrency, verification, and budgets.

The supervisor is deterministic host code. LLMs triage, plan, perform domain work, and verify;
they do not get to bypass dependency, role, or budget checks.

## Agents and roles

Every Swarm squad has generated orchestrator, planner, supervisor, and verifier shells plus
the declared members. Generated aliases use `squad-{slug}` and the `-planner`,
`-supervisor`, and `-verifier` suffixes.

Member roles are:

- `executor`: eligible for planned work.
- `subjectMatterExpert`: consultation only.
- `helper`: a supporting member that is not a default executor.

Plan validation rejects missing dependencies, cycles, unknown members, SME task assignment,
and concurrency above the configured ceiling.

## Safety and liveness

`SurfaceSwarmBudgets` bounds rounds, wall-clock duration, task attempts, validation attempts,
replans, consecutive stalls, task duration, SME consultations, and concurrency. Verification
is fail-closed: an unavailable or invalid verifier response does not silently mark work
complete.

A short drive-loop timer advances persisted work and recovers from dropped progress messages.
The current timer is an in-memory Orleans timer; deployments needing idle-resume guarantees
beyond grain reactivation should use a durable reminder in a future compatible revision.

## Blueprint contract

Swarm uses the top-level `swarm` extension of `FabrCoreBlueprint`:

```json
{
  "name": "research-team",
  "version": "1",
  "agents": [],
  "swarm": {
    "squads": [
      {
        "squadType": "swarm",
        "name": "Research Desk",
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

`SurfaceSwarmBlueprintExpander` runs host-side and emits the shell/member
`AgentConfiguration` records. See [blueprints.md](blueprints.md) for CRUD/apply endpoints.

## Compatibility

The imported runtime is named simply Swarm. The retired one-shot implementation and its
temporary `SwarmV2`, `swarm2.*`, and `squad2-*` vocabulary are not public aliases. Stored V365
definitions must be migrated before upgrading:

1. Change squad type `SwarmV2` to `Swarm`.
2. Change message/config keys from `swarm2.*` to `swarm.*`.
3. Change generated aliases from `squad2-*` to `squad-*`.
4. Move the old `surface.squads` payload to the canonical top-level `swarm` extension.
5. Reapply the blueprint with `forceReconfigure` when existing grains must reload runtime
   metadata.

Do not dual-write old and new keys; migration should be explicit and one-way.
