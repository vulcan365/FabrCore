# Factual-edge loss diagnosis - 2026-09-05

Most missing required relationships originate in extraction or representation, not SQL
vector search. Across the six prior batch-size runs, 36 positive fact checks yielded:

| Stage diagnosis under stricter v3 labels | Checks |
| --- | ---: |
| Required typed fact persisted | 14 |
| Required edge not emitted and no candidate representation found | 7 |
| Alternative endpoint, predicate, direction or description representation | 12 |
| Matching raw edge, but an exact endpoint entity name is missing | 3 |

No required matching raw edge with all exact endpoints present was unexplainedly lost
in these snapshots. This is evidence about these six runs, not a general SQL audit.
The alternative bucket is diagnostic: it does not mean every candidate is a false fact;
some encode a related claim without meeting the required typed contract.

## Concrete causes

- JSON/UTF-8: one four-section run emitted JSON text USES UTF-8 but supplied no JSON text
  entity. The existing persistence step correctly cannot link that endpoint. Mapping
  JSON text to JSON would infer a concept equivalence, so the new resolver leaves it alone.
- IANA/text-csv: one sixteen-section run emitted IANA ESTABLISHES text/csv but named the
  entity Internet Assigned Numbers Authority (IANA). The literal dictionary lookup misses it.
- UUID/GUID: another sixteen-section run emitted UUIDs RELATED_TO GUIDs, while the entities
  were Universally Unique IDentifiers (UUIDs) and Globally Unique IDentifiers (GUIDs).
  Both endpoints fail the literal lookup despite explicit initialisms in the entity names.
- ECMA-404 publisher: five runs omitted Ecma International; the remaining run emitted
  SIGNED_BY rather than PUBLISHED_BY. Endpoint repair alone cannot supply the intended fact.
- UUID/URN: candidates included reversed USES, RELATED_TO, and an example URN value;
  other runs omitted a relevant edge. Generic relation reversal or type substitution
  would require interpreting the source, not merely fixing a name.

Sources were the exact cached RFC text used for ingestion, verified by content hash.
The relevant passages remain RFC8259 section 8.1 and references 14.1, RFC4180 sections
2, 4 and 7.1, and RFC9562 section 1. No new corpus or external facts were introduced.

## Eval correction

The v2 JSON pattern also accepted JSON parser USES UTF-8 as though its subject were the
JSON format. That credited one wrong-subject graph. Added corpus-relations-v3.json with
anchored identity patterns for JSON, IANA, UUID/GUID and URN; registry names, examples and
email addresses no longer substitute for these entities. Corrected the CSV source-section
references from 5 to 4 and from 10 to 7.1. Required predicates and six positive facts are
unchanged. Original manifests and reports are preserved. The six old runs rescore from
15/36 under v2 to 14/36 under v3; do not compare those versions without rescoring snapshots.

## Conservative code improvement

Added an opt-in deterministic resolver, controlled by
GraphRag:Ingestion:ResolveExtractionEndpointAliases (console --endpoint-aliases on).
It runs after global extraction merge and before deduplication/persistence. It recognizes
unique explicit parenthesized initialisms whose capital letters match the expansion.
It supports expansion-first and initialism-first names, gives exact names precedence,
and refuses ambiguous matches. No fuzzy matching, inferred plural stripping, new entities,
descriptions, relation types or direction changes are performed. Unconventional capitalization
can deliberately leave a valid alias unresolved. The default remains off.

Offline replay predicts recovery of two of the three dropped required facts: IANA/text-csv
and UUID/GUID. The two affected sixteen-section runs each rise from 3/6 to 4/6; the other
four runs are unchanged. Total prediction is 16/36, versus 14/36 persisted under v3.
These are counterfactual checks of captured output, not rewritten database graphs.
The replay does not simulate source-order duplicate merging or SQL contribution updates.
The ingestion unit test verifies that enabled resolution supplies the existing entity
name to persistence while keeping the same entity count and one LLM call.

## Live validation

Two fresh eight-section passes used mini, strict JSON, the v3 labels, all application caches
off and endpoint aliases enabled. Results were 27.312 and 29.193 seconds, seven actual LLM
calls each, and 1/6 and 4/6 required facts. All six documents completed, all 179 chunks per
pass remained embedded, Recall@3 was 100% and MRR@3 was 1.0. No extra repair LLM calls were
introduced. The live runs are smoke/integration validation, not a matched causal quality
comparison: model outputs vary, and endpoint normalization cannot recover un-emitted facts.

Keep the resolver opt-in. The finding supports fixing explicit name mismatches; it does
not support claims that overall extraction quality or ingestion latency is solved.

## Trace tooling and validation

New trace command reads saved raw responses, graph snapshots and exact corpus text without
calling an LLM or writing to SQL. It records source evidence availability, raw matches,
candidates, expected endpoint entity inventories, missing exact endpoint names and persisted
outcomes. It rejects source hash mismatches and reports lacking captured raw responses.
New live reports also include FactTraces. Alias replay results are labeled predictions.

87 unit tests passed, covering explicit and ambiguous initialisms, exact-name precedence,
refusal of JSON text/JSON and RFC4180/CSV inference, missing-endpoint diagnostics, alternative
predicate diagnostics, corrected JSON identity matching and actual ingestion-stage wiring.
Build and scoped whitespace checks passed. No SQL migration was needed.

```powershell
dotnet run --project src/FabrCore.Services.GraphRag.EvalConsole -- trace --input artifacts/graphrag-evals/batch-size-experiment-2026-09-05.json --manifest src/FabrCore.Services.GraphRag.EvalConsole/corpus-relations-v3.json --output artifacts/graphrag-fact-trace-v3
```

Artifacts:

- artifacts/graphrag-fact-trace-v3/trace.json - six prior runs rescored and traced.
- artifacts/graphrag-evals/20260906T010523-75e203b8660845798fcd8885b3d64c7a - two live validation passes.
- artifacts/graphrag-fact-trace-live-v3/trace.json - live response diagnosis with run/iteration/scope.

The next extraction experiment should target typed claims and endpoint identity together,
for example compact entity IDs referenced by edges with explicit source-supported relation
instructions. Evaluate the unchanged v3 factual requirements; do not accept lower output
volume solely because graph count and retrieval smoke gates pass.
