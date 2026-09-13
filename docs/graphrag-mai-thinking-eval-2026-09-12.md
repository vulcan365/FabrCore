# MAI-Thinking-1 GraphRAG evaluation — September 12, 2026

**Decision: works in plain-prompt mode, but does not outperform the current ingestion
model in the matched tests.** MAI completed all seven evaluated document ingestions.
It matched mini's selected factual-check counts while taking approximately 5–6 times
longer and generating more output tokens. Keep the current ingestion model.
No production configuration, parser, or application code was changed.

## Compatibility and configuration

The experiment used the exact Azure deployment `MAI-Thinking-1`; the response model
identifier was `mai-thinking-1`. An ignored local `mai-thinking-eval` alias reused
the existing Azure endpoint/key reference, an 8,192-token output budget, configured
context budget of 75,000 tokens, and 180-second network timeout. Reasoning effort
was unset. These budgets are experiment settings, not measured model maximums.

Both `json_object` and `json_schema` probes were rejected with the message that
structured `response_format` is not enabled for this model. Plain prompting returned
the tiny extraction correctly in 4.42 seconds: 87 input tokens and 288 output tokens,
including 198 reasoning tokens. Full MAI runs therefore used `--response prompt`.
No JSON adapter, normalization, or output repair was introduced. Schema-only eval
features cannot be assumed available on this deployment.

The eval console built successfully with zero warnings/errors. No code changed, so
unit tests were not rerun for this configuration and report task. Existing unrelated
workspace changes were left intact.

## Matched comparisons

All runs used the full source text, fresh evaluation scopes in the existing SQL
database, the document-plan pipeline, eight sections per extraction batch, and four
allowed concurrent chat calls. Application result/taxonomy/embedding caches were
disabled. Embeddings remained the configured 1,536-dimensional model. No database
creation or global cleanup was performed.

Technical documents were RFC 8259 JSON (28,360 characters) and RFC 4180 CSV (12,931
characters) from `corpus-relations-v3.json`, processed serially. Policy used all three
fixed historical documents in `corpus-policy-v2.json` (59,152 characters), with two
concurrent documents. Policy snapshots are evaluation material, not current-policy
assertions. Source hashes and persisted graph traces are retained in each report.

| Matched workload | MAI batch time | Mini batch time | MAI positive checks | Mini positive checks |
| --- | ---: | ---: | ---: | ---: |
| Policy, both plain prompt | 111.07 s | 20.71 s | 6/11 | 6/11 |
| Technical, both plain prompt + explicit relation guidance | 89.20 s | 14.39 s | 3/4 | 3/4 |

MAI was **5.36× slower** on policy and **6.20× slower** on guided technical extraction.
These are single-pass comparisons on small labeled corpora, not tail-latency or
sustained-load measurements. Equal counts do not mean identical edges: MAI retained
the CIO working-group policy edge where mini retained the open-formats obligation.
In guided technical extraction, MAI missed the ECMA-404 publisher edge; mini missed
the IANA registration edge. Neither model passed every positive check.

All MAI and control passes completed ingestion and retrieval smoke checks with
Recall@3 = 100% and MRR@3 = 1.0. Both policy models passed the one negative check,
but the corresponding positive nonproprietary-formats edge was absent, limiting
what that negative pass establishes. Sparse strict edge checks do not measure
overall graph precision or recall.

## All runs and token usage

Times below are `IngestionWallMs`, including the parallel policy batch rather than
the sum of overlapping document times. Reported output tokens **already include**
reasoning tokens; do not add the reasoning column again.

| Model / settings | Batch time | Calls / errors | Input tokens | Output tokens | Reasoning subset | Positive checks |
| --- | ---: | ---: | ---: | ---: | ---: | ---: |
| MAI technical, ordinary prompt | 91.32 s | 4 / 0 | 14,735 | 9,312 | 3,612 | 1/4 |
| MAI policy, ordinary prompt | 111.07 s | 7 / 0 | 18,491 | 23,291 | 6,691 | 6/11 |
| Mini policy, current schema mode | 14.99 s | 7 / 0 | 19,578 | 10,869 | 0 | 3/11 |
| MAI technical, relation guidance | 89.20 s | 4 / 0 | 16,059 | 8,825 | 3,356 | 3/4 |
| Mini policy, matching plain prompt | 20.71 s | 7 / 0 | 18,669 | 13,888 | 0 | 6/11 |
| Mini technical, matching prompt/guidance | 14.39 s | 4 / 0 | 16,059 | 4,481 | 0 | 3/4 |

The initial policy comparison against schema-mode mini appeared to favor MAI on
quality (6 versus 3 positives). That advantage disappeared when mini used matching
plain-prompt settings. This does not establish that disabling schema generally
improves mini: there was one pass per configuration, and prior controls also varied.

In MAI's original technical run, the descriptions often contained the relevant facts
but the edges used the wrong subject or type: RFC 8259 rather than JSON as the UTF-8
subject, `AUTHORED_BY` for a publisher, and `PRODUCES` for IANA registration. Existing
`--relations defined` guidance improved its score from 1/4 to 3/4, but mini reached
the same count with the same guidance much faster. No expected labels were changed.

The guided MAI technical run reported 818 provider-cached input tokens; all other
listed passes reported zero. Disabled application caches do not imply provider-cold
execution. Runs were sequential: MAI technical, MAI policy, mini schema policy,
MAI guided technical, mini prompt policy, mini guided technical.

MAI generated about 68% more output tokens on matched policy extraction and 97%
more on matched guided technical extraction. These usage totals exclude embeddings,
retrieval, and warmup. Deployment billing rates were not verified, so dollar costs
and cost savings are not asserted. Token overhead plus the measured latency does
not support this model as an ingestion speed improvement.

## Reproduction and artifacts

Run from the repository root using the ignored local model/key configuration:

```powershell
dotnet build src/FabrCore.Services.GraphRag.EvalConsole --no-restore -v quiet
dotnet run --project src/FabrCore.Services.GraphRag.EvalConsole --no-build -- run --models artifacts/graphrag-mai/models.local.json --model mai-thinking-eval --manifest src/FabrCore.Services.GraphRag.EvalConsole/corpus-policy-v2.json --max-chars 0 --response prompt --sections-per-batch 8 --document-concurrency 2 --output artifacts/graphrag-mai/policy
dotnet run --project src/FabrCore.Services.GraphRag.EvalConsole --no-build -- run --model graphrag --manifest src/FabrCore.Services.GraphRag.EvalConsole/corpus-policy-v2.json --max-chars 0 --response prompt --sections-per-batch 8 --document-concurrency 2 --output artifacts/graphrag-mai/policy-prompt-control
dotnet run --project src/FabrCore.Services.GraphRag.EvalConsole --no-build -- run --models artifacts/graphrag-mai/models.local.json --model mai-thinking-eval --manifest src/FabrCore.Services.GraphRag.EvalConsole/corpus-relations-v3.json --max-documents 2 --max-chars 0 --response prompt --relations defined --sections-per-batch 8 --document-concurrency 1 --output artifacts/graphrag-mai/technical-guidance
```

For the guided technical control, omit `--models` and use `--model graphrag`.
For ordinary technical extraction, omit `--relations defined`. The schema policy
control uses `--model graphrag --response schema`.

Completed local report directories under `artifacts/graphrag-mai`:

- `technical/20260912T171545-74a993538f9c4bec8efdadbff4c36ea9`
- `policy/20260912T171732-4fa840de4f2c440ebb359f840cd4422e`
- `policy-control/20260912T171939-dbbf116b03cc49eaaa31d1cbde3cf636`
- `technical-guidance/20260912T172011-20a432fafa554b5793a0a9f58121cbdf`
- `policy-prompt-control/20260912T172201-1d86e063a1e9457cbbf17e1bd4b8e180`
- `technical-guidance-control/20260912T172240-74626787c443462399f4952acc053747`

Each contains JSON/Markdown reports with raw final responses, graph checks, and
usage. The parent contains probe results, logs, and `summary.json`. Local artifacts
and credential-bearing model configuration are ignored by Git; this document retains
the measured conclusion for review. Production aliases and defaults remain unchanged.
