# Compact memory selection IDs — September 7, 2026

Decision: retain short request-local selection labels as an **experimental opt-in**, not a new default.
Keep the established GUID path and all accepted baselines. This changes the representation
of references, not stored IDs, memory content, candidate limits or relevance instructions.

Each selection request normally sends GUIDs in both the manifest and response-schema enum.
`Retrieval.UseCompactSelectionIds = true` replaces them with labels such as `"0"`, `"1"` and
maps the response back to stored GUIDs within that invocation. Excluded memories are removed
before labels are assigned. Unknown labels are discarded; deduplication, result limits and
empty-selection behavior remain in place. No shared mapping or retrieval cache is introduced.

## Paired SQL and harness experiment

Three iterations per variant, using Azure `gpt-5.4-mini`, with fresh SQL scopes and the same
fixtures/options per pair. Main and harness ran control then compact; holdout reversed the
order. Both variants used the same build within this experiment. All **156/156 checks per
variant passed** (312 total); existing comparison gates reported no regressions.

| Workload | Input tokens: GUID → compact | Input reduction | Output tokens: GUID → compact | Chat calls |
| --- | ---: | ---: | ---: | ---: |
| Main retrieval | 18,475 → 12,714 | 31.2% | 827 → 378 | 21 → 21 |
| Business holdout retrieval | 24,816 → 16,440 | 33.8% | 977 → 434 | 24 → 24 |
| Full harness answers | 53,009 → 49,776 | 6.1% | 1,237 → 778 | 45 → 47 |

The harness includes answer generation and its own providers/tool loop. Two additional calls
in the compact variant offset part of the retrieval savings. Provider latency spikes occurred;
these runs do **not** establish a speed improvement or a model-call reduction. Substring answer
checks remain limited, and three repetitions do not establish statistical equivalence.

Retained artifact:
`artifacts/memory-selection-experiments/20260907T145029-530e09bacdbe4b0eb61203fddb190bd8/experiment.json`.
Each referenced report includes corpus, options, binary hashes, individual checks and calls.
Reported token counts are provider usage, not dollar-cost estimates. Cached-token counts were
not captured in this first pair matrix.

GraphRag's passive call instrumentation informed the additional cached-input/reasoning-token
fields now recorded by Memory's evaluator. These fields do not enable caching or alter prompts.
Missing provider usage remains unavailable, rather than being interpreted as zero.

## Stress test and precision follow-up

The 200-header selector fixture has three Atlas memories and 197 unrelated inspection records.
Four questions cover current state, historical state, procedure and abstention. Three seeded
shuffles change relevant-item positions; both variants see the same order within each pass,
and control/candidate execution order alternates. Expected IDs are explicit and scoring requires
an exact set, so retrieving extra facts is a failure. This fixture is synthetic, not an independently
curated industry benchmark, and bypasses SQL, embeddings and the header-scan limit.

| Selector prompt | GUID exact selections | Compact exact selections | Input tokens: GUID → compact |
| --- | ---: | ---: | ---: |
| Existing prompt | 11/12 | 10/12 | 197,196 → 113,880 |
| Smallest sufficient set instruction | 12/12 | 11/12 | 197,856 → 114,540 |

Both variants included every required memory and abstained correctly. Failures were extra
same-project memories: a procedure answer sometimes also retrieved current/historical deployment
regions, and one GUID historical selection added the current state. No label mapped to an invalid
stored ID. The stricter instruction improved the observed exact-selection counts but **did not
remove the compact variant's precision regression**. It is exposed separately as
`Retrieval.PreferMinimalSelection` / `--minimal-selection true`, also defaulting to false.
It has not yet been tested across the full paired SQL/harness matrix.

Gross input savings at 200 headers were about 42%. However, the provider reported 34,560 cached
input tokens for the original GUID run versus zero for compact, and 30,720 versus zero after the
prompt refinement. Gross token reductions are therefore not billing reductions. No speed or cost
claim is made. The first matrix's output reductions include fewer reference tokens, not necessarily
shorter retrieved memories or shorter final answers.

Retained stress reports:

- `artifacts/memory-selection-scale/20260907T145511-7e3cd9ffa5d24f068ad6da6dfc33c300/report.json`
- `artifacts/memory-selection-scale/20260907T145756-21f9f3773c5d43f88158c79613dd6168/report.json`

Each directory stores its fixture, expected IDs, actual IDs, shuffled header order, raw selection
responses, token usage and binary hash. Reports remain `complete` when evaluation finishes with
quality failures; the command exits with a failing gate. No failed run replaced a baseline.

Next experiment: a separate evidence-precision gate or candidate-selection method, tested against
multi-memory questions and adversarial near matches. Do not solve this by setting a universal
top-1 limit, hiding extra selections, or relaxing the exact-set scorer.

## Reproduce and enable

The current `selection-scale` command emits the expanded version-2 fixture. The version-1
artifacts above remain historical records; do not compare their totals with version 2.
See the [verification follow-up](memory-selection-verification-experiment.md) for the new cases.

```powershell
.\scripts\Run-MemorySelectionExperiment.ps1 -Iterations 3
dotnet run --project src/FabrCore.Services.Memory.EvalConsole -- selection-scale --iterations 3
dotnet run --project src/FabrCore.Services.Memory.EvalConsole -- selection-scale --iterations 3 --minimal-selection true
dotnet run --project src/FabrCore.Services.Memory.EvalConsole -- run --mode harness --iterations 3 --compact-ids true
```

```csharp
services.AddAgentMemoryServices("MemoryDb", options =>
    options.Retrieval.UseCompactSelectionIds = true);
```

Both new settings remain `false` by default. Validate representative application workloads before enabling either.
This optimization does not extend recall beyond the existing header scan limit, decide semantic
conflicts, or eliminate the retrieval model call.

Final validation: **94/94 Memory tests passed**, including mapping/exclusion/abstention tests,
SQL integration and the existing two live-model quality checks. Build and whitespace checks passed.
The experimental live stress failures above are separate from that passing regression suite.
