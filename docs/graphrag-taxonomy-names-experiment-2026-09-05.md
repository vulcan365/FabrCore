# Structured taxonomy names experiment, 2026-09-05

Keep this change opt-in. Separating taxonomy labels from domain metadata improved
one recurring category-selection behavior, but did not improve throughput or labeled
fact coverage in these four passes. Continue using mini and two concurrent documents.

## Changes

`GraphRag:Ingestion:UseStructuredTaxonomyNames` (default false) and console
`--taxonomy-names current|structured` (default current):

- Combined prompts serialize existing domain/category records as JSON, with name,
  domainName and description as separate fields, instead of appending `(in Domain)`
  to the displayed category name. The separate classifier already used JSON records.
- Combined/classifier prompts and response-schema name descriptions require exact
  supplied name values when reusing a label. The graph-only schema is unchanged.
- After extraction, a name marked isNew=false but absent from the supplied taxonomy
  snapshot causes the document's taxonomy assignment to be skipped, with a warning.
  Extracted entities and relationships are retained; no repair LLM call is added.
  Previously such a response could reach persistence and create a new taxonomy row.
- The flag participates in extraction cache identity. No existing taxonomy rows are
  renamed or removed. Explicit isNew=true proposals remain allowed. Existing labels
  with parenthetical text remain valid exact names; this is not fuzzy normalization.

The guard is checked before entity/category embeddings and persistence. It can leave
an ingested document without taxonomy, making the evaluation smoke gate fail; it does
not fail or discard the graph merely to repair classification. Rebuild an unchanged
source explicitly if applying a new setting to previously ingested content.

## Controlled live test

Order: current, structured, structured, current. Each is a fresh scope on the existing
localhost/graphrag database, with full policy-v1 Markdown (59,152 characters), mini,
strict schema responses, eight sections per batch, two concurrent documents, shared
chat limit four, and result/taxonomy/embedding caches off. Instructions and the nine
positive typed-fact labels are unchanged. Labels are never sent to the extraction model.

| Variant | Corpus seconds | Required facts | Persisted edge contributions | Output tokens |
| --- | ---: | ---: | ---: | ---: |
| current | 15.302 | 7/9 | 145 | 11284 |
| structured | 13.990 | 5/9 | 114 | 11054 |
| structured | 17.284 | 6/9 | 129 | 10763 |
| current | 14.317 | 6/9 | 139 | 11228 |

Mean current: 14.810s. Mean structured: 15.637s (5.6% longer). The individual ranges
overlap; two observations per variant are insufficient to establish a latency effect.
Every pass completed all three documents, retained 163 embedded chunks, and returned
the correct document at rank one for all three retrieval questions. All used seven chat
calls and peaked at four simultaneous calls. No SQL/provider errors were reported.
Edge totals count document contributions rather than distinct scope edges.

## Taxonomy observations

Current selected `Open Data Policy and Information Governance (in Engineering)` for
the governance document in both runs. Structured selected the plain name in both.
However, structured CISA selected the existing decorated name in one of two runs.
The plain and decorated categories were already present before these runs. Both are
exact valid names under the new guard. All four runs began with nine taxonomy rows;
a final read-only SQL count also returned nine. Neither variant created a new row,
so these live runs do not demonstrate duplicate-creation prevention. The scripted
regression test covers the absent-name/isNew=false case directly.

All twelve document classifications still chose Engineering. The formatting change
does not establish a better domain taxonomy or resolve the model's reuse bias.
No taxonomy cleanup was performed, so existing duplicate rows remain available.

## Quality and decision

Current retained 7/9 and 6/9 facts; structured retained 5/9 and 6/9. Retrieval smoke
success is not full graph correctness. These small samples cannot distinguish random
extraction variation from a prompt effect, but they do not support a quality win.
Do not promote the flag to the production default on this evidence.

Provider prompt-cache hits occurred in all runs (13,568; 6,400; 13,056; 17,152 input
tokens respectively). Application caches were off. Taxonomy descriptions can be
updated by earlier ingestion and model scheduling varies, so the reversed order is
not an identical-context replay. The persisted graph and raw responses are retained.

92 unit tests passed, including structured prompt field separation, acceptance of
known reused names and explicitly new names, rejection of an unknown reused category
while preserving entities without another call, taxonomy schema guidance, and unchanged
graph schema. Four live passes passed embedding/retrieval smoke gates, not all facts.

Next useful quality experiment: extract policy responsibilities as explicit actor,
relationship and object fields constrained by a small relation vocabulary, with labeled
obligation strength and factual direction checks. Keep mini and document concurrency
at two. Measure required-fact retention before considering any output-volume reduction.

## Reproduction and artifacts

Run the following with VARIANT set to current, structured, structured, current:

```powershell
dotnet run --project src/FabrCore.Services.GraphRag.EvalConsole -- run --model graphrag --manifest src/FabrCore.Services.GraphRag.EvalConsole/corpus-policy-v1.json --max-chars 0 --response schema --sections-per-batch 8 --document-concurrency 2 --taxonomy-names VARIANT --endpoint-aliases off --result-cache off --taxonomy-cache off --embedding-cache off --reingest fresh --iterations 1
```

Summary: artifacts/graphrag-evals/taxonomy-names-experiment-2026-09-05.json.
Full report.json and report.md reside under artifacts/graphrag-evals in these runs:

- `20260906T015500-05866d5eb94f4dc18de1bcaa0e58e8a7` (current)
- `20260906T015518-d235195630104333ae847154571b16ef` (structured)
- `20260906T015535-ba61df414d254009aa6da2bf52299b92` (structured)
- `20260906T015555-b1f1f1d924d149839d5abb02d4a4d92d` (current)
