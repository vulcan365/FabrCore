# Same-type evidence under candidate diversity

Follow-up: [hybrid allocation](memory-hybrid-candidates-experiment.md) preserves all five
facts while also passing the retained mixed-type correction fixture.

This experiment tests the known tradeoff in taxonomy-based candidate diversity: a question
may need several memories of one type, while other types contain only irrelevant material.
The fixture and labels are new synthetic data, authored for this failure mode; they are not
an independently curated external benchmark.

Five separate Fact memories hold Atlas production's region, owner, backup retention,
recovery point objective and encryption setting. Then 240 distractors are inserted across
Fact, Rule, Procedural and Observation types. Every target is verified outside the most recent
200 headers and every primary chunk is read back before scoring. The distractors deliberately
use controlled type labels to isolate candidate allocation; this is not an ingestion-taxonomy
quality test.

Seven questions request each individual value, all five values together, and an unknown
monthly hosting cost. Full expected entity sets, content and provenance are checked.
The return limit is five, so returning all five is possible. Ordinary and diverse semantic
retrieval are compared at 8+8 and 20+20 budgets on identical stored data. Variant order
rotates between repetitions. All variants use compact IDs and minimal selection without
verification or graph expansion.

## Reproduce

Completed report:
`artifacts/memory-same-type/20260907T160453-f32735a6063a476eb8d8562f730a9d3b/report.json`.
The adjacent fixture preserves SQL IDs and exact expected evidence; all four variants ran
to completion. The overall quality gate failed because diverse 8+8 missed required facts.

| Three repetitions | Ordinary 8+8 | Diverse 8+8 | Ordinary 20+20 | Diverse 20+20 |
| --- | ---: | ---: | ---: | ---: |
| Exact checks passed | **21/21** | **18/21** | **21/21** | **21/21** |
| Chat calls | 21 | 21 | 21 | 21 |
| Gross input tokens | 22,608 | 23,739 | 47,928 | 47,973 |
| Output tokens | 396 | 378 | 396 | 396 |
| Cached input tokens | 0 | 0 | 19,712 | 25,088 |

Each variant uses one query embedding per question. Failed-case diagnostic embeddings are
reported separately. Diverse 8+8 misses only the five-fact question, on every repetition;
all single-fact and abstention questions pass. Repeated candidate discovery confirms that
three of the five expected IDs are absent before selection. With four populated types,
the eight semantic slots admit only two Fact entities; recent headers add distractors.

The smaller diverse pool uses 5.0% more gross input than ordinary 8+8 here while losing
evidence. Diverse 20+20 recovers all five, but uses roughly twice the input of ordinary 8+8.
Cache differences prevent treating those ratios as dollar-cost ratios. No latency claim is
made; provider latency warnings were observed.

Decision: keep diversity opt-in and do not promote the previously successful diverse 8+8
configuration. Its mixed-type correction success does not generalize to this same-type
request. A future hybrid allocation should preserve high-similarity slots while adding
diverse candidates, and must pass both this fixture and the retained correction fixture.
Passing diverse 20+20 here is not a universal budget recommendation. Production retrieval
code and all registered baselines were unchanged in this experiment.

```powershell
dotnet run --project src/FabrCore.Services.Memory.EvalConsole -- same-type --iterations 3
```

The command uses the shared SQL experiment runner, skips correction mutations, and records
version `same-type-v1` in a new isolated scope under `artifacts/memory-same-type`. Failed
semantic checks include repeated candidate discovery with diagnostic embedding costs
separated from recall costs. The gate fails if either diverse variant fails; all control
results remain in the report. Existing corrections and registered baselines are preserved.

This measures retrieval over directly seeded memories. It does not test extraction, actual
long-running sessions, deletion, concurrent corrections or process-crash recovery. Exact-set
success on this fixture does not establish a universally safe candidate budget.
