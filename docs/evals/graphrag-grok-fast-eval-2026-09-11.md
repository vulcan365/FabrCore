# Grok 4.1 Fast non-reasoning ingestion evaluation — September 11, 2026

**Decision: technically compatible, but not a demonstrated ingestion upgrade.**
The Azure deployment `grok-4-1-fast-non-reasoning` completed all nine tested document
ingestions without chat errors or response-format adapters. It was slower than the
current model in the matched policy comparison, and strict factual coverage did not
improve. Keep the existing ingestion model. Production configuration and source code
were not changed for this experiment.

## Setup and probes

The experiment used an ignored local `grok-fast-eval` alias with Provider `Azure`,
the existing Azure resource/key reference, and the exact new deployment name. It
did not use the sample's older direct-xAI `grok` alias. Settings copied from the
current GraphRAG alias were an 8,192-token output budget, 75,000-token configured
context budget, and 180-second network timeout; reasoning effort was unset. The
configured context budget is a test setting, not a measured maximum context window.

A tiny extraction probe correctly returned three named entities and the two directed
relationships in all formats: JSON object mode in 1.946 s, JSON Schema in 1.055 s,
and plain text in 1.358 s. The response model identifier matched the deployed name.

The existing eval console built successfully with zero warnings/errors. No application
code edits were needed, so unit tests were not rerun for this configuration/report task.
All full runs used the unmodified document-plan pipeline with JSON Schema, eight
sections per extraction batch, four allowed concurrent chat calls, fresh SQL scopes,
and application result/taxonomy/embedding caches disabled. Existing shared taxonomy
was retained; no database was created or globally cleared. Provider prompt caching
was still reported, as detailed below.

## Technical documents

The complete RFC 8259 JSON specification (28,360 characters) and RFC 4180 CSV
specification (12,931 characters) came from `corpus-relations-v3.json`. Each has two
strict positive edge labels. Runs used one concurrent document and no truncation.

| Configuration / pass | JSON ingestion | CSV ingestion | Positive edge checks | Chat input / output tokens |
| --- | ---: | ---: | ---: | ---: |
| Grok, standard, pass 1 | 13.66 s | 12.04 s | 1/4 | 14,992 / 2,414 |
| Grok, standard, pass 2 | 13.66 s | 11.15 s | 0/4 | 14,992 / 2,385 |
| Grok, explicit relation guidance | 10.54 s | 14.92 s | 0/4 | 16,435 / 2,738 |
| Earlier same-session gpt-5.4-mini control | 8.75 s | 6.61 s | 2/4 | 14,953 / 4,538 |

All rows passed ingestion/retrieval smoke gates and produced 69 JSON chunks and 32
CSV chunks. The Grok rows each used four chat calls with zero errors. Standard pass
1 retained JSON's UTF-8 edge; the other technical labels failed. For CSV, the trace
showed the IANA registration edge was not emitted despite the endpoints being
available, and RFC 2234 was missing as an expected endpoint. This was not merely a
retrieval failure. Explicit generic relation guidance (`--relations defined`) did
not improve the selected labels.

The technical control is the fresh control from the immediately preceding Phi
follow-up, not an alternating randomized control in this run. Its strict JSON labels
also failed; both CSV labels passed. The small label set is diagnostic and is not a
general estimate of graph recall or precision.

## Matched policy comparison

Both models ran the same full `corpus-policy-v2.json` snapshots with two concurrent
documents, schema responses, eight-section batches, and otherwise matching settings.
Grok ran first and the control ran after it completed. Total source content was
59,152 characters across open-data policy, open-data governance, and CISA open-source
policy. These are fixed historical evaluation snapshots, not current-policy advice.

| Measurement | Grok 4.1 Fast non-reasoning | Current gpt-5.4-mini |
| --- | ---: | ---: |
| Ingestion batch wall time | 33.986 s | 17.850 s |
| Positive factual checks | 4/11 | 5/11 |
| Negative checks | 1/1 | 1/1 |
| Chat calls / errors | 7 / 0 | 7 / 0 |
| Input tokens | 18,926 | 19,167 |
| Output tokens | 7,005 | 9,825 |
| Provider cached input tokens | 2,363 | 0 |
| Retrieval Recall@3 / MRR@3 | 100% / 1.0 | 100% / 1.0 |

Grok took about **90% longer**, with approximately **29% fewer output tokens**.
Both models passed the OMB/EOP, OSTP/EOP, CISA/FOSS, and CISA/CC0 checks. The primary
model additionally passed Project Open Data's GitHub relationship. Neither model
passed the open-data policy document's positive labels in this configuration.
The negative obligation check passed for both, but its corresponding positive edge
was missing, so that negative result alone is not evidence of obligation accuracy.

This is one matched policy pass per model, not a sustained-load or tail-latency study.
Nine taxonomy rows existed at the start of each policy pass. Read `IngestionWallMs`
for the parallel batch comparison; summing document durations would overstate time.

## Cost and adoption

Grok's lower token count could matter at a sufficiently low effective rate, but the
test did not verify the Azure deployment's billing meter. The retrieved official
[Azure Foundry Grok pricing table](https://azure.microsoft.com/en-us/pricing/details/ai-foundry-models/grok/)
listed Grok 4.1 Fast Global with dynamically rendered `$-` prices, so no numeric
Azure price or billed savings is asserted. Direct-xAI pricing should not be silently
substituted for this Azure deployment's rate.

Grok is a working experimental alternative, unlike the tested Phi deployment, but
these runs do not justify automatic ingestion routing or changing defaults. The
current model was faster and retained more of the policy labels. A later adoption
decision would require verified effective prices and a broader held-out quality test,
including precision, qualifiers, and exceptions; the present sparse labels do not
prove either model's overall correctness.

## Reproduction and artifacts

Run from the repository root. The local model configuration contains credentials and
is ignored by Git; the `grok-fast-eval` alias is described above.

```powershell
dotnet build src/FabrCore.Services.GraphRag.EvalConsole --no-restore -v quiet
dotnet run --project src/FabrCore.Services.GraphRag.EvalConsole --no-build -- run --models artifacts/graphrag-grok/models.local.json --model grok-fast-eval --manifest src/FabrCore.Services.GraphRag.EvalConsole/corpus-relations-v3.json --max-documents 2 --max-chars 0 --response schema --sections-per-batch 8 --document-concurrency 1 --iterations 2 --output artifacts/graphrag-grok/technical
dotnet run --project src/FabrCore.Services.GraphRag.EvalConsole --no-build -- run --models artifacts/graphrag-grok/models.local.json --model grok-fast-eval --manifest src/FabrCore.Services.GraphRag.EvalConsole/corpus-policy-v2.json --max-chars 0 --response schema --sections-per-batch 8 --document-concurrency 2 --output artifacts/graphrag-grok/policy
dotnet run --project src/FabrCore.Services.GraphRag.EvalConsole --no-build -- run --model graphrag --manifest src/FabrCore.Services.GraphRag.EvalConsole/corpus-policy-v2.json --max-chars 0 --response schema --sections-per-batch 8 --document-concurrency 2 --output artifacts/graphrag-grok/policy-control
```

The relation-guidance run used the technical command with `--iterations 1` and
`--relations defined`. No Phi JSON adapters or response normalization were used.

Completed report directories under `artifacts/graphrag-grok`:

- `technical/20260911T130022-27b1c19eb7e24975a2d56f0daa6300d3`
- `policy/20260911T130149-6aac72f413794bcc97dda85faa410eb7`
- `policy-control/20260911T130248-9bceb8d475a542a0a2f44ab69903a97d`
- `technical-guidance/20260911T130328-aba3c120785645279b2b73b4a09be6eb`

The parent contains probe responses, logs, `summary.json`, and the ignored local model
configuration. Reports preserve raw extraction responses, source hashes, token usage,
persisted graphs, and fact traces. The earlier technical primary control is
`artifacts/graphrag-phi/refresh-control/20260911T125425-58e27d3174c24ace8a608856a0499368`.
Provider cached inputs for Grok technical passes were 767 and 9,455 tokens, and
1,873 for the relation-guidance pass; disabled application caches do not imply a
provider-cold run. Local artifacts are ignored; this report retains the conclusions.
