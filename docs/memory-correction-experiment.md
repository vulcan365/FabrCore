# Corrections and smaller candidate pools

Follow-up: [bounded candidate diversity](memory-diverse-candidates-experiment.md) recovers
the missing procedure while retaining the failing controls documented here.

The September 7 correction experiment validates public API corrections, but finds a recall
coverage gap for combined questions in a dense same-subject collection. Neither semantic
configuration meets the all-checks gate. No production defaults or registered baselines
were changed.

## Three-repetition comparison

Report: `artifacts/memory-correction/20260907T153436-588dc5065ee74c43aa0602fd4bd08ec4/report.json`.
The adjacent fixture retains all entity IDs, content, provenance and exact expected sets.

The fixture starts with five target memories and inserts 240 later distractors: Atlas sandbox
regions and Atlas inspection tickets. All targets are verified outside the newest-200 cap.
It then uses `UpdateMemoryAsync` to change production from eastus to westus3 and a checkpoint
from step 18 to step 21, reading back both descriptions and primary content. An explicit
September 1 historical snapshot remains unchanged. Corrected entities become recent again;
the historical snapshot, procedure and preference remain outside the recent-header window.

Each variant gets the same eight questions after corrections and a freshly constructed
service provider. Variant order rotates across three repetitions. Both semantic configurations
use compact IDs and minimal selection, without verification or graph expansion. The recent
control uses the same prompt options. Exact-set scoring also checks full content and source
metadata, so returning an updated ID with stale content would fail.

| Measurement | Recent 200 | Semantic 20 + recent 20 | Semantic 8 + recent 8 |
| --- | ---: | ---: | ---: |
| Exact checks | 9/24 | 21/24 | 21/24 |
| Chat calls | 24 | 24 | 24 |
| Gross input tokens | 233,838 | 50,331 | 25,065 |
| Output tokens | 426 | 432 | 432 |
| Cached input tokens | 151,552 | 28,672 | 0 |

Both semantic variants pass current, historical, procedure, comparison, preference,
checkpoint and abstention questions on every repetition. Both fail the combined request
for current region **and** deployment procedure on every repetition, returning only the
region. The recent-only control passes current region, updated checkpoint and abstention.

The 8+8 pool uses 50.2% less gross input than 20+20, but is not a successful replacement:
both fail a required task. Cache behavior differs, and both semantic variants add one query
embedding per recall. Uncached input is actually higher with 8+8 (25,065 versus 21,659).
These are not measured dollar-cost or latency savings.

## Scope and reproduction

A one-repetition diagnostic run reproduced the same scores (3/8, 7/8, 7/8):
`artifacts/memory-correction/20260907T153639-aa076908d05d47a387779fe47fbc9ff2/report.json`.
For the combined question, repeated discovery omitted the expected procedure from **both**
candidate pools (36 unique headers for 20+20; 15 for 8+8). This locates the observed failure
before selection: region-like neighbors crowd out the procedure. A selection verifier can
only remove selected memories, so it cannot recover this missing evidence.

The next retrieval experiment should test bounded query decomposition or candidate diversity
for combined requests, preserving this failing fixture as a control. Simply shrinking the
pool or adding a second selection pass is not supported by these results.

```powershell
dotnet run --project src/FabrCore.Services.Memory.EvalConsole -- corrections --iterations 3
```

The command retains isolated SQL scopes, checkpoints and failing controls; exit 2 means a
semantic variant failed a quality check. Version 2 additionally repeats candidate discovery
after failed semantic checks, recording candidates and missing expected IDs. Its diagnostic
embedding costs are separate from measured recall costs. These diagnostics are a repeat
of discovery on the unchanged scope, not an interception of the original request.

This is a synthetic fixture with eight authored questions, not an external benchmark.
Initial facts and distractors are batch-seeded directly; only corrections use the public
write API. Updates are sequential and by explicit ID: this does not establish automatic
contradiction resolution, concurrent-writer semantics or extraction correctness. Service
reconstruction is not a process crash/restart test. The same seeded scope is reused across
query repetitions. Reports record binary hashes and before/after mutation content.
