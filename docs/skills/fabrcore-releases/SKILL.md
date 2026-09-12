---
name: fabrcore-releases
description: "Upgrade FabrCore applications across 1.6, 1.7, 1.8, and 2.0 GA. Use for release migration, package consolidation, configuration and ACL migration, API compatibility, and upgrade verification."
metadata:
  author: FabrCore
  version: 2.0.0
---

# FabrCore release upgrades

Upgrade consuming applications to the requested release. This guide treats FabrCore 2.0.0 as GA.
It does not publish packages or deploy applications merely because an upgrade was requested.

## Select the migration path

Inspect project/central package references, lock files, target frameworks, startup registration,
runtime configuration, custom providers, transport clients, and persisted state. Establish the
actual source version and requested target; do not infer the installed version from old comments.

| Starting point | Target | Read and apply |
| --- | --- | --- |
| 1.5 or early 1.6 | 1.6.3 | [1.6](references/1.x.md#16-baseline) |
| 1.6.x | 1.7.x | [1.7](references/1.x.md#16-to-17) |
| 1.7.x | 1.8.x | [1.8](references/1.x.md#17-to-18) |
| 1.8.x | 2.0.0 | [2.0](references/2.0.md) |
| 1.6.x or 1.7.x | 2.0.0 | All intervening sections above, then the 2.0 guide |

Apply intervening changes cumulatively; installing/deploying each intermediate version is not
required. Do not reintroduce retired 1.x packages while upgrading directly to 2.0.
Keep directly referenced FabrCore packages on the chosen release, remove retired references,
and rebuild dependent libraries as well as the application. Use explicit versions, not a floating
latest version. The 2.0 baseline is .NET 10, Orleans 10.3.1, and Agent Framework 1.20.0.

## Perform and verify

1. Inventory applicable changes and distinguish required migration from optional feature adoption.
2. Preserve deployment storage identities and connection mappings. Export ACL and back up state
   before changing the old installation; rehearse persistent-state migration on a restored copy.
3. Update code, package references, templates, startup configuration, and deployment secrets/config
   together. Existing `FabrCore.Services.*` namespaces can remain even when assemblies move.
4. Restore/build the consumer solution and run its relevant tests. Exercise startup/readiness,
   model resolution, a normal chat/tool turn, and affected transports. For SQL deployments, test
   restart persistence, migrated ACL allow/deny behavior, Memory recall, and GraphRAG search.
5. Report exact source/target versions, changed files, data migration/cutover steps, checks actually
   run, and unresolved compatibility issues. Never claim that a successful build proves persisted
   state or mixed-version Orleans compatibility.

For rollback, retain the old binaries/configuration and a consistent pre-upgrade database backup.
Do not point old binaries at newly migrated schemas or promise automatic schema downgrade.

## Distribution and maintenance

Copy this entire `fabrcore-releases` folder, including `references` and `scripts`, to the AI tool's
skill directory, or attach the folder and ask the AI to read `SKILL.md`. No sibling skill is required.
Example request: "Use fabrcore-releases to upgrade this solution from 1.6.3 to 2.0.0."

The bundled ACL utility is a copy of the FabrCore 2.0 `scripts/Migrate-Acl.ps1`; keep them synchronized.
Release attribution for 1.x is based on tagged source comparisons, not the cumulative
"Unreleased" heading retained in older release-note files. Maintain one section per release
boundary and verify changes against the corresponding source before adding future releases.
