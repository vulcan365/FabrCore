# Memory taxonomy

Types describe the knowledge; temperatures describe retrieval lifecycle. All five types are allowed by default.

| Type | Use | Example |
|---|---|---|
| Fact | Verified domain knowledge | API responses use camelCase. |
| Rule | Business constraints and policies | Refunds above the configured threshold require approval. |
| Instruction | Explicit preferences or standing directives | The user prefers concise answers. |
| Observation | Inferences or dated situational evidence | Timeout frequency increased during this test. |
| Procedural | Repeatable workflows with trigger and ordered steps | Validate, deploy, then smoke-test a release. |

All types are entity nodes. Relationships are separate edges, not Rule entities themselves. Extraction can add `related_to` edges; choosing a type does not automatically create a relationship.

`AllowedMemoryTypes` restricts accepted categories. It does not make a service read-only, validate truth, or reject every ephemeral statement. Application policy and extraction prompts determine content quality. Stored Instructions do not override current system policy or authorize actions.

Use `SaveProcedure` for structured steps stored under `ProceduralSteps.MetadataKey` as well as readable content. Use `isPointInTime` for captured state, regardless of type. Snapshots are always accompanied by a freshness warning when recalled. Age-based consolidation skips Instructions; other non-indexed memories become candidates after 30 days, or 3 days for snapshots. These are candidate thresholds, not guaranteed TTLs; consolidation must run and model confirmation can affect the result.
