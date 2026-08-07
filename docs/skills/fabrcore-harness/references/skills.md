# Harness Skill Publishing and Runtime

FabrCore harness skills are immutable, administrator-published Agent Skills. They are scoped to one
principal and assigned to an agent by exact version:

```json
"_HarnessSkills": "policy-review@1.2.0,invoice-rules@2026-08-01"
```

A version change requires force reconfiguration or eviction. Deleting a version prevents new
activations from loading it; an already active agent keeps its activation-local immutable cache.

## Why Storage, not Files

ZIP is transport only. The Host reads and validates the request body directly and never uploads the
archive through `/fabrcoreapi/File`. The File API is temporary, TTL-tracked content; a TTL of zero
expires immediately rather than meaning “forever.” Skills use principal-scoped typed Storage, whose
lifetime is controlled only by explicit deletion.

The container is `fabrcore.harness-skills`:

```text
packages/{name}/{version}/manifest
packages/{name}/{version}/resources/{resourceId}
```

`resourceId` is the SHA-256 of the normalized logical path. Resources are independent entities and
are read lazily. Publication writes resources first, then the manifest as the commit marker, then
updates a principal-keyed Orleans catalog grain backed by `fabrcoreStorage`. An interrupted publish
without a manifest is invisible. Delete removes the manifest first, then the catalog entry, then
best-effort resource cleanup.

Storage has no skill TTL. Localhost mode still uses process-local Orleans memory storage and does
not survive restart. Use SQL Server, Azure Blob, Azure Table, or another durable Orleans provider for
restart persistence.

## Package shape

PUT one ZIP containing either:

```text
SKILL.md
references/policy.md
```

or one matching top-level directory:

```text
policy-review/SKILL.md
policy-review/references/policy.md
```

The `name` in YAML frontmatter must exactly match the route. V1 accepts UTF-8 text resources with
extensions `.md`, `.json`, `.yaml`, `.yml`, `.csv`, `.xml`, and `.txt`, at most two
directories below the skill root.

V1 rejects scripts, executable extensions, symlinks, absolute and traversal paths, backslash paths,
duplicate normalized paths, multiple roots, invalid UTF-8/frontmatter, and files outside the skill
root. Limits are:

- `SKILL.md`: 256 KiB
- one resource: 512 KiB
- archive entries: 128, including `SKILL.md`
- total uncompressed text: 4 MiB
- logical resource path: 256 characters
- serialized manifest/resource safety ceiling: 700 KiB

The serialized ceiling leaves headroom below Azure Table's 1 MiB entity limit even when JSON escaping
expands the content.

## Administration API

All endpoints require the `FabrCoreAdmin` policy and use the remote-administration audit path:

```text
GET    /fabrcoreapi/admin/v1/principals/{principalId}/skills
GET    /fabrcoreapi/admin/v1/principals/{principalId}/skills/{name}/versions/{version}
PUT    /fabrcoreapi/admin/v1/principals/{principalId}/skills/{name}/versions/{version}
DELETE /fabrcoreapi/admin/v1/principals/{principalId}/skills/{name}/versions/{version}
```

PUT uses `Content-Type: application/zip`. Repeating the same `name@version` and package digest is
idempotent; different content at an existing version returns `409 Conflict`. Audit records include
actor, target principal, reference, digest when available, outcome, and command ID—never skill text.

Typed SDK methods on `IFabrCoreHostApiClient` are
`ListHarnessSkillsAsync`, `GetHarnessSkillAsync`, `PublishHarnessSkillAsync`, and
`DeleteHarnessSkillAsync`. Host and Cloud Server capability metadata advertise `skills`.

## Runtime behavior

`CreateFabrCoreHarnessAgent` resolves only the current agent principal. It loads and validates every
pinned manifest during `OnInitialize`, reports all missing/malformed/corrupt references together,
and caches manifests for the activation. Resource entities are read only when
`read_skill_resource` is called, integrity-checked, then cached after a successful read.

The harness composes Microsoft's `AgentSkillsProvider` only when `AgentSkillsSource` is non-null.
It never discovers from the silo current directory. FabrCore's prompt directs the model to
`load_skill` and `read_skill_resource`. Stored skills expose no `AgentSkillScript`;
`run_skill_script` therefore reports the script unavailable. Only these read-only skill operations
have approval disabled by default; unrelated tool approval behavior is untouched.

