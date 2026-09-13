# Verify selected memory evidence — September 7, 2026

This follow-up targets the extra same-project memories observed in the
[compact-ID experiment](memory-selection-experiment.md). The first selector and its limits
stay intact. An optional second call reviews only the selected headers when more than one
memory was selected. It can keep a subset, including all or none, but cannot add references.

`Retrieval.VerifyMultiMemorySelection` is false by default. The verifier uses the same relevance
client and short local references. Invalid output or provider failure preserves the original
selection; cancellation propagates. It does not verify stored facts against original sources,
recover missing candidates, or validate single-memory selections. Header truncation can limit
what either selection stage knows.
It applies to header-based selection only; vector/archive results and later graph expansion
are outside this verifier. The paired console runs disable graph expansion as before.

## Expanded selector experiment

Version 2 retains the 200-header fixture and adds a two-part question and a date comparison.
Each has an explicit two-memory expected set. Three seeded shuffles and rotating variant order
produce 18 exact-set checks per variant. All variants use the minimal-set prompt. The fixture
remains synthetic and bypasses SQL and embeddings; it is not an industry benchmark.

| Variant | Exact checks | Model calls | Input tokens | Output tokens | Cached input tokens |
| --- | ---: | ---: | ---: | ---: | ---: |
| GUID references | 18/18 | 18 | 296,826 | 687 | 53,760 |
| Compact references | 18/18 | 18 | 171,852 | 336 | 98,560 |
| Compact + verifier | 18/18 | 25 | 173,635 | 484 | 116,480 |

In the verified variant, the first selector included a deployment-region fact for a procedure
question on pass 3. The verifier removed that extra fact. It retained both required memories
in all six genuine multi-part/comparison cases. Thus one trace shows an actual precision repair,
while all contemporary unverified control checks also passed. This is **not** statistical proof
of improved accuracy.

The verified variant used 41.5% fewer gross input tokens than GUID selection, but 7 additional
calls. Compared with unverified compact selection, verification added 1,783 input and 148 output
tokens. Cached-token counts differed markedly across variants, and provider throttling/retries
occurred during the run. Do not interpret these data as latency or dollar-cost improvements.

Retained report:
`artifacts/memory-selection-scale/20260907T150743-70dc8fe9eabd4a37af22e4c2e92c2b6f/report.json`.
Its adjacent fixture and per-case records preserve expected/selected IDs, header order, both
responses and token usage. Version-1 and version-2 totals are not directly comparable.

## SQL and harness confirmation

Three iterations per variant on the main, business-holdout and harness fixtures passed
**156/156 checks per variant (312 total)**. All comparison commands succeeded. Both variants
used the minimal prompt; the candidate adds compact IDs and conditional verification.

| Workload | Checks per variant | Input: GUID → verified compact | Input reduction | Output: GUID → verified compact | Calls |
| --- | ---: | ---: | ---: | ---: | ---: |
| Main retrieval | 51/51 | 19,525 → 15,085 | 22.7% | 732 → 488 | 21 → 26 |
| Business holdout | 54/54 | 25,832 → 19,006 | 26.4% | 877 → 542 | 24 → 29 |
| Full harness answers | 51/51 | 53,963 → 51,293 | 4.9% | 1,106 → 977 | 45 → 52 |

Retained matrix:
`artifacts/memory-selection-experiments/20260907T150939-05eab84b62124c788ce537e2197ed4f2/experiment.json`.
Main/harness ran control first; holdout reversed that order. Raw reports record options, binary
hashes, per-call usage and checks. These small-fixture gates use the existing substring scorer;
the scale fixture's exact-ID scorer is stricter about unnecessary retrieved memories.

Decision: retain this as an opt-in profile for workloads where selection precision warrants an
extra call. It repaired one observed over-selection and passed the multi-memory gates, but all
contemporary controls also passed. Do not change global defaults or claim general superiority
from these repetitions. The full-harness token benefit is modest and comes with more calls.

## Reproduce

```powershell
dotnet run --project src/FabrCore.Services.Memory.EvalConsole -- selection-scale --iterations 3 --minimal-selection true --verify-selection true
.\scripts\Run-MemorySelectionExperiment.ps1 -Iterations 3 -MinimalSelection -VerifySelection
```

The scale command adds a third verified compact variant. The paired SQL/harness script compares
GUID control with compact + verification; both get the minimal prompt. This combination is an
opt-in profile, not a new default:

```csharp
services.AddAgentMemoryServices("MemoryDb", options => {
    options.Retrieval.UseCompactSelectionIds = true;
    options.Retrieval.PreferMinimalSelection = true;
    options.Retrieval.VerifyMultiMemorySelection = true;
});
```

Response schema names are now recorded in call measurements, allowing selector, verifier and
answer-generation tokens to be separated in later reports. Original baselines remain untouched.

Validation: **103/103 Memory tests passed**, including verifier reference bounds, valid subsets,
multi-memory preservation, empty selection, malformed/unknown responses, provider-failure fallback
and cancellation, alongside existing SQL and live-model checks. No Memory tests were skipped.
