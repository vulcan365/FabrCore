# Frozen Memory release defaults

Decision: September 7, 2026. Freeze the existing compatibility defaults for release preparation.
Do not promote an experimental retrieval strategy or an evaluation baseline automatically.
This is a configuration freeze, not a declaration that release validation or deployment is complete.

| Setting | Frozen value |
| --- | --- |
| Warm selection / header scan | 5 memories / 200 headers |
| Graph recall hops / freshness warning | 1 / 1 day |
| Hot index | 20 pointers / 3,000 estimated tokens |
| Semantic / recent candidate limits, when enabled | 20 / 20 |
| Matched chunks per memory, when enabled | 1 |
| Selection previews / chunk-selection previews | 0 / 0 |
| Semantic candidates, compact IDs, minimal selection, verification | Off |
| Diverse/hybrid candidates, matched evidence, chunk selection | Off |
| Redundant-selection shortcut, beginning/end windows, LLM planner | Off |
| Automatic consolidation / summary tree | Off / off |

Ordinary default recall selects headers and loads primary chunks. Do not describe this as
automatic semantic recall across every historical chunk. Matched-chunk recall is opt-in;
its selected bodies share a 12,000-character budget, with up to 4,096 per entity. When its
second selector is enabled, it sees full bounded bodies by default. Preview experiments do
not change stored content or make partial evidence complete. Graph expansion has separate
behavior outside the matched-body budget.

Both direct service calls and plugin tools remain supported. Harness memory is explicitly
attached with `WithMemory` or `WithMemoryLifecycle`, with a default 12,000-character injected
context cap and tools off unless requested. `ForInternalAgent` defaults to `OwnOnly`;
`CoreAndOwn` reads own-first plus core and writes only to own. Shared core writes require
an explicit binding and appropriate execution policy.

`MemoryReleaseDefaultsTests` locks the retrieval policy and core bounds against accidental
changes. Intentional changes require updating this decision and comparing the intended
configuration against retained evaluations. The benchmark registry is not changed by this freeze.

Release readiness still includes the complete eval matrix, representative harness/subagent
failure and cancellation paths, shared corrections/extraction retries, persistence recovery,
and retention/erasure expectations. Experimental options stay available for measured application
needs; published claims must distinguish those options from default behavior.
