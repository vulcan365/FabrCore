# Diversity within a bounded semantic candidate pool

Follow-up: [same-type evidence experiment](memory-same-type-experiment.md) found that diverse
8+8 loses three required facts on a five-fact question. Its success below does not justify
promoting that configuration as a default.

This experiment addresses the correction fixture's known failure: dense region facts push
the required procedure out of the semantic candidate pool for a combined question.

`Retrieval.DiversifySemanticCandidates` is an opt-in modifier of `UseSemanticCandidates`.
SQL ranks distinct entities by their closest chunk within each memory taxonomy type, then
interleaves type ranks before applying the existing candidate limit. Similarity breaks ties
within each round. This is a bounded candidate allocation across existing types, not an
extra model call or query-decomposition step. The ordinary relevance selector can still
reject all candidates.

The SQL scope, cold-memory and already-surfaced filters apply before ranking. Multiple chunks
still count as one entity. No new schema, storage taxonomy or embeddings are required.
Custom stores can optionally implement `FindDiverseCandidateHeadersAsync`; the default
interface implementation throws if this unsupported option is explicitly enabled.
The option has no effect unless semantic candidates are also enabled.

## Comparison design

Completed report:
`artifacts/memory-correction/20260907T155854-fb807f506b984bd7b8f0983a7e7a0733/report.json`.

| Three repetitions | Recent 200 | Semantic 20+20 | Semantic 8+8 | Diverse 8+8 |
| --- | ---: | ---: | ---: | ---: |
| Exact checks | 9/24 | 21/24 | 21/24 | **24/24** |
| Chat calls | 24 | 24 | 24 | 24 |
| Gross input tokens | 233,838 | 50,199 | 25,065 | 24,624 |
| Output tokens | 426 | 444 | 432 | 438 |
| Cached input tokens | 142,080 | 26,880 | 0 | 0 |

The diverse variant recovered both required memories for all three combined-question
repetitions and preserved every other labeled case. The ordinary semantic variants still
missed the procedure on each combined question. Gross selection input fell 50.9% against
20+20 and 1.8% against ordinary 8+8. Each semantic variant used 24 query embedding calls.
Diagnostic embeddings for failed controls are separately recorded and excluded from recall
costs. The 20+20 control had cache hits while neither 8+8 variant did; these results establish
token usage, not priced savings or a latency improvement.

All 109 Memory tests passed, including a new real-SQL case where dense close facts exclude
a procedure under ordinary ranking but the diverse pool retrieves it within the same limit.
The test also checks exclusions and cold memories. A service test checks both candidate
paths and verifies that diversity still uses only one query embedding. Build and whitespace
checks passed.

Contemporary harness runs also passed **34/34 in each variant**, with no comparison
regressions. Both used semantic 8+8; only the candidate enabled diversity. Total harness
chat calls were 31 versus 32, and input tokens were 33,639 versus 35,647 (output 569 versus
563). Thus this small end-to-end run shows no token saving; the reduction above is specific
to selection in the dense correction fixture. Reports:

- `artifacts/memory-diverse-harness-control/20260907T160129-3c78464d22ed42f28b6fb0a0710e780d/report.json`
- `artifacts/memory-diverse-harness-candidate/20260907T160216-726c56426b6e41c897f8d3ac994eb4a1/report.json`

`corrections --iterations 3 --diverse-candidates true` retains the existing recent-200,
semantic 20+20 and semantic 8+8 controls and adds diverse semantic 8+8. All share the same
SQL fixture and corrections, compact IDs, minimal-selection prompt, model, and question
labels. The order rotates between repetitions. The experiment retains every control failure;
with the fourth variant enabled, its exit gate requires every diverse-candidate check to
pass. Report version 3 records the variant list explicitly.

The fixture is deliberately the previous failing synthetic regression. It is not independent
evidence of general accuracy. Type diversity cannot guarantee coverage across many facts of
the same type, broad queries, or incorrectly classified memories. Rare but irrelevant types
can consume slots that would otherwise go to relevant facts. Exact SQL ranking also adds
storage work; unchanged model-call counts do not establish unchanged latency.

## Reproduce

```powershell
dotnet run --project src/FabrCore.Services.Memory.EvalConsole -- corrections --iterations 3 --diverse-candidates true
dotnet run --project src/FabrCore.Services.Memory.EvalConsole -- run --mode harness --iterations 2 --compact-ids true --minimal-selection true --semantic-candidates true --candidate-budget 8 --diverse-candidates true
```

`--candidate-budget` sets both semantic and recent limits for an ordinary corpus run; the
correction experiment's four variants have fixed budgets. Defaults and registered baselines
are not automatically changed by either command.
