# Phi-4-mini-reasoning GraphRAG evaluation — September 11, 2026

Follow-up: [JSON object mode, guided prompts, and normalization experiments](graphrag-phi4-mini-reasoning-followup-2026-09-11.md)
extend these initial results. The recommendation remains to keep Phi out of ingestion.

Do not select this deployment as a secondary ingestion model yet. It was reachable,
but none of the completed Phi console runs successfully ingested the test document.
Smaller batches and output budgets did not resolve the failures. No production
model aliases, defaults, or extraction code were changed.

## Method

Built the existing GraphRAG eval console against the current working tree, including
the ongoing Host/Core consolidation. Build passed with one existing CS8767 warning
in `OrleansEntityStorageProvider.cs`. No source changes requiring unit tests were made.

Used the first document in `corpus-relations-v3.json`: the entire RFC 8259 JSON
specification, 28,360 characters, SHA-256
`61a5378f4255c720beb2a4b4a63b29540147c140f36988bf086291989b4cd2d7`.
The manifest supplies two positive factual edge labels. The console used the existing
`localhost/graphrag` database, fresh `eval:graphrag:*` scopes, document-plan mode,
one document at a time, four allowed concurrent chat calls, and disabled application
result/taxonomy/embedding caches. All passes started with nine shared taxonomy rows.
No database creation or global cleanup was performed. Embeddings remained
`text-embedding-ada-002`, 1,536 dimensions.

The local Phi alias reused the configured Azure endpoint/key reference with deployment
`Phi-4-mini-reasoning`, a 180-second network timeout, and no reasoning-effort setting.
Configuration copies containing credentials are in ignored local artifacts only.

## Results

Times are console `IngestionWallMs`; call/token totals cover measured chat requests,
not embeddings, retrieval, warmup, or a provider billing export.

| Model / response | Output budget | Sections per batch | Wall time | Chat calls / errors | Reported input / output tokens | Result |
| --- | ---: | ---: | ---: | ---: | ---: | --- |
| Phi / schema, initial | 8,192 | 8 | 1.05 s | 3 / 3 | unknown | Failed; no chunks/entities |
| Phi / schema, reduced | 2,048 | 1 | 41.42 s | 16 / 8 | 7,406 / 4,032 | Failed; no chunks/entities |
| Phi / plain prompt | 2,048 | 1 | Stopped after approximately 10 minutes | Incomplete | Incomplete | Not a completed measurement |
| Phi / schema, smaller output | 1,024 | 1 | 23.71 s | 16 / 11 | 4,596 / 2,161 | Failed; no chunks/entities |
| gpt-5.4-mini / schema, control 1 | 8,192 | 8 | 15.34 s | 3 / 0 | 10,632 / 4,234 | Ingestion/retrieval smoke passed |
| gpt-5.4-mini / schema, control 2 | 8,192 | 8 | 7.96 s | 3 / 0 | 10,632 / 2,781 | Ingestion/retrieval smoke passed |

Controls produced 69 embedded chunks each; respectively 54/33 extracted entities
and 114/68 relationships. Both had Recall@3 = 100% and MRR@3 = 1.0.
Both nevertheless failed **both strict factual edge labels** (`json-utf8` and
`ecma404-publisher`). UTF-8 appeared through a different subject/relation; the
expected publisher edge was absent. Successful console gates do not establish
factual graph quality. Phi's failed ingestions do not support a meaningful quality
comparison on persisted edges.

Control 2 reported 9,984 cached input tokens from provider prompt caching, despite
application caches being disabled. The two control timings are exploratory, not a
stable latency estimate. Phi used different budgets/batches to accommodate serving
limits. The first reduced schema run overlapped the start of the plain-prompt attempt
by several seconds and must not be treated as an isolated latency benchmark.
The control started after the local Phi processes ended; outstanding remote work
after a forced process stop cannot be ruled out.

## Observed serving and output problems

1. The deployment returned HTTP 400 saying `max_model_len=max_total_tokens=4096`
   when asked for 8,192 output tokens. With 2,048 output tokens it also rejected a
   prompt with at least 2,049 input tokens because their sum exceeded 4,096.
   This is a measured deployment constraint, not a claim about the model architecture.
2. A direct JSON Schema probe returned HTTP 422: the response format must be `text`
   or `json_object`. Console schema requests were inconsistent: some returned usable
   JSON, others returned 422. Schema cannot be assumed reliable on this deployment.
   We did not determine why the serving responses differed.
3. A 256-output-token connectivity probe exhausted its budget on reasoning without
   producing the requested final JSON. In the full plain-prompt run, the log recorded
   eleven unusable `length` completions, a JSON parse failure, and individual completed
   responses around 50 seconds and 117 seconds. The attempt was stopped at roughly
   ten minutes. Its incomplete checkpoint is excluded from token/cost totals.

Microsoft's [official model card](https://huggingface.co/microsoft/Phi-4-mini-reasoning)
documents a 128K context and says the model was designed and tested for math
reasoning. That does not establish a separate 128K output allowance on this endpoint,
or speed and extraction quality on GraphRAG.

## Cost and decision

Using the user-supplied, **unverified** prices of $0.075 per million input tokens and
$0.30 per million output tokens, reported successful-response usage would cost about
$0.001765 for the 2,048-token schema attempt and $0.000993 for the 1,024-token attempt.
These are partial usage estimates for **failed ingestions**, not actual billed totals.
Failed requests have missing usage; the stopped run and initial connectivity probe
are not included. We did not validate the deployment's billing meter or control prices.
There is no demonstrated cost saving per successfully ingested document.

Keep the existing ingestion model. Before another comparison, inspect the Foundry
deployment's effective input/output limits and consistent structured-output support.
If those can be resolved, begin with a small extraction compatibility test, then repeat
the full labeled technical and policy corpora. A successful alternative format would
also need validation of final JSON handling, truncation, retained facts, and total
cost including any fallback to the primary model. This test does not justify adding
automatic Phi routing or fallback overhead to production.

## Reproduction and artifacts

Run from the repository root after building:

```powershell
dotnet build src/FabrCore.Services.GraphRag.EvalConsole --no-restore -v quiet
dotnet run --project src/FabrCore.Services.GraphRag.EvalConsole --no-build -- run --model graphrag --manifest src/FabrCore.Services.GraphRag.EvalConsole/corpus-relations-v3.json --max-documents 1 --max-chars 0 --response schema --sections-per-batch 8 --document-concurrency 1 --iterations 2 --output artifacts/graphrag-phi/control
dotnet run --project src/FabrCore.Services.GraphRag.EvalConsole --no-build -- run --models artifacts/graphrag-phi/models-1024.local.json --model phi4-mini-reasoning --manifest src/FabrCore.Services.GraphRag.EvalConsole/corpus-relations-v3.json --max-documents 1 --max-chars 0 --response schema --sections-per-batch 1 --document-concurrency 1 --output artifacts/graphrag-phi/schema-1024
```

The ignored local `models-1024.local.json` is a copy of the existing model/key config
with the additional Phi alias: Provider `Azure`, the existing Azure URI/key alias,
Model `Phi-4-mini-reasoning`, MaxOutputTokens `1024`, ContextWindowTokens `4096`,
TimeoutSeconds `180`, ReasoningEffort `null`. `models.local.json` uses output `2048`;
it was initially `8192`/context `128000` for the first failed smoke run before being
adjusted. The ordinary `graphrag` alias remains unchanged.

Local report directories beneath `artifacts/graphrag-phi`:

- `schema-smoke/20260911T120544-3186ee9b883e417b87ba70db298d8522`
- `bounded-schema/20260911T120614-b468b90450794baaa3824ce4ecb45106`
- `prompt/20260911T120648-02ab653139af44ddb3f9154026639db8` — incomplete
- `schema-1024/20260911T121717-bb398b1634334ed69ea183c5a09a3da2`
- `control/20260911T121755-a8f06cfe58bd46ffacd86b4309b45067`

Completed directories contain `report.json` and `report.md`, including raw model
responses and persisted graph checks. `schema-probe-error.json`, per-attempt logs,
and `summary.json` are in the parent directory. Artifacts are local/ignored; the
summary in this document is retained in the repository for review.
