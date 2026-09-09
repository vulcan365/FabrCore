# Query-matched chunk evidence

The opt-in `Retrieval.UseMatchedChunkEvidence` addresses the multi-chunk read-path gap.
Semantic candidate recall retains its query embedding. After selecting entities it loads
each entity's closest embedded chunk, rather than always loading chunk 0. When selection
previews are enabled, they also come from the closest chunk. The original query embedding
is reused; no extra embedding or model call is required.

Returned entries expose `RecalledChunkId`, `RecalledChunkIndex` and `IsContentTruncated`.
Formatted recall context includes this provenance. Bodies are capped at 4,096 characters
each and 12,000 characters shared across selected entities. Preview bodies retain their
separate 4,096-character aggregate cap. These are body-character caps, not whole-context
token caps. SQL filters scope and cold entities before choosing a chunk; distance ties use
chunk index and ID. The service also enforces length limits if a custom store returns
overlong evidence.

The option requires semantic candidates and only changes that selection/load path.
Extraction receipts, direct retrieval and updates retain primary-chunk semantics. Returned
bounded evidence is not a whole-document replacement suitable for a blind update. Existing
graph expansion still has its own behavior and is disabled in this experiment; its body
content is not covered by the selected-evidence cap. Entities without embedded chunks are
omitted by the matched read instead of silently substituting a primary body.

## Experiment

Completed reports:

- Rich headers: `artifacts/memory-multi-chunk/20260907T165106-161db3ae0682436daa5d168bbcda57be/report.json`
- Opaque headers: `artifacts/memory-multi-chunk/20260907T165206-74ecf6bbd881481293f08ef221e030f5/report.json`

| Three repetitions | Rich primary | Rich matched | Opaque primary | Opaque matched |
| --- | ---: | ---: | ---: | ---: |
| Exact body/provenance checks | 3/21 | **21/21** | 3/21 | **21/21** |
| Selection calls | 21 | 21 | 21 | 21 |
| Gross input tokens | 29,694 | 29,862 | 29,442 | 29,610 |
| Output tokens | 396 | 396 | 376 | 396 |
| Cached input tokens | 23,040 | 15,360 | 14,080 | 14,080 |

The candidate passes **42/42 combined**, returning all 36 known-answer bodies from chunk 1
with the correct chunk IDs, and abstaining on all six unknown checks. The controls pass
only abstention. Gross selection input rises about 0.6%, with the same 42 selection and
query-embedding calls. SQL now searches for matched evidence; cache differences and the
new read work mean this is not a dollar-cost or latency claim.

All **116 Memory tests passed**. New tests validate selecting a non-primary chunk, source
identity, SQL truncation, cold/scope filtering, a 12,000-character aggregate body bound even
with overlong custom-store results, formatted provenance, and one query embedding per recall.
Primary stored content remains unchanged. Relationship loading for selected entries was
preserved after the fixture runs; those runs have graph hops disabled, and their binary
hashes identify the measured builds. The final test build includes that preservation.

The contemporary two-iteration harness comparison passed **34/34 in both variants**, with
no comparison regressions. Total chat calls were 32 versus 31; gross input 37,679 versus
36,459; output 638 versus 515; cached input 18,432 versus 19,584. This small paired run
does not establish repeatable end-to-end savings. It verifies the existing single-chunk
harness corpus remains functional with matched loading and formatted chunk provenance.
Reports:

- `artifacts/memory-matched-harness-false/20260907T165429-2bd57c0fc85148b2bf2c10d8e791c70b/report.json`
- `artifacts/memory-matched-harness-true/20260907T165511-662a708f788f40df933aa02e70cb1eb5/report.json`

Both conditions use the retained multi-chunk fixture: 245 entities, generic chunk 0, evidence
in chunk 1, and five targets outside the recent-200 window. Each condition compares primary
and matched retrieval at hybrid 8+8 with 256-character previews, over three repetitions.
Variant order alternates. Rich headers isolate body loading; opaque headers also test the
matched-preview selection path. Exact success requires correct IDs, full expected content,
source metadata and the actual seeded chunk ID/index without truncation. The full bodies
are short in this fixture; truncation is separately tested.

```powershell
dotnet run --project src/FabrCore.Services.Memory.EvalConsole -- multi-chunk --iterations 3 --header-mode rich --matched-chunks true
dotnet run --project src/FabrCore.Services.Memory.EvalConsole -- multi-chunk --iterations 3 --header-mode opaque --matched-chunks true
```

`multi-chunk-v2` reports preserve all failing controls and gate on the matched candidate.
Ordinary corpus/harness runs also accept `--matched-chunks true` with semantic retrieval.
Custom stores may implement the optional matched-evidence capability or reject unsupported
use. No SQL schema or default/baseline promotion is part of this change.

This is a synthetic regression suite, not independent validation. Only one matched chunk
per entity is returned; facts split across multiple chunks in one entity remain an open
case. A matching chunk can itself contain relevant evidence beyond the bounded prefix.
Selection, preview and final reads do not share a database snapshot, so concurrent edits
can change the winning chunk between calls. Provenance identifies the returned chunk,
not an immutable historical revision. Exact SQL vector ranking adds work that must be
measured under larger sustained workloads.
