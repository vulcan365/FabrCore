# Phi ingestion follow-up — September 11, 2026

**Decision: this deployment is not viable for GraphRAG ingestion.** Six fresh
document attempts failed, including JSON object mode, guided prompts, split retries,
and an eval-only array normalization experiment. A fresh primary-model control
completed both documents. No production model or ingestion behavior was changed.

This extends the [initial evaluation](graphrag-phi4-mini-reasoning-eval-2026-09-11.md)
after the user requested another tuning pass against the newly added deployment.
It evaluates `Phi-4-mini-reasoning` through the existing configured Azure resource;
the sample configuration did not contain a new Phi alias, so the experiment reused
the ignored local alias pointing to that exact deployment name.

## Compatibility and tuning

A fresh direct probe extracted Acme, Beta, and Atlas plus the two expected directed
relationships correctly in **2.824 seconds / 78 output tokens** using `json_object`,
temperature zero, and a short system instruction. JSON Schema still returned HTTP
422, explicitly allowing only `text` or `json_object`. Plain text took 13.574 seconds,
used 529 output tokens, and did not return a directly parseable final JSON object.
This small probe showed a viable transport format, not document-ingestion quality.

The console now supports three explicit experiments:

- `--response json`: provider JSON object mode; original ingestion prompts/settings.
- `--response json-guided`: JSON mode plus a system reminder about flat object arrays,
  field types, exact source identifiers, concise descriptions, and temperature zero.
- `--response json-normalized`: guided mode plus deterministic flattening of nested
  arrays under `entities` and `relationships`. Every object is preserved; primitives
  or null members cause normalization to be rejected. No identifiers, fields, or
  facts are supplied or corrected. Changed responses retain `RawProviderResponseJson`
  alongside the `ExtractionResponseJson` passed to ingestion and used by traces.

These adapters are confined to the eval console. Production schema selection and
parsing are unchanged. JSON modes remain incompatible with schema-only experiments.
The exact system guidance is versioned in `JsonObjectChatClient.cs`.

## Document results

Full sources from `corpus-relations-v3.json`: RFC 8259 (28,360 characters) and
RFC 4180 (12,931 characters). CSV's SHA-256 is
`5d9ec035aa40c0108362cb767c9e2d08c30f7225cb2a4715f484cb13c29b5f21`;
JSON's hash is unchanged from the initial evaluation. No prefix truncation was used.

All Phi attempts used a 1,024-token output budget, configured context 4,096,
fresh scopes, one concurrent document, four allowed chat calls, and application
caches off. Guided JSON on RFC 8259 used two sections per batch; all other Phi
attempts used one. The current `graphrag` control retained schema mode, eight sections
per batch, and its 8,192-token output budget. This compares practical pipeline
configurations, not isolated model latency under identical generation settings.

| Model / configuration | Document | Document wall time | Chat calls | Input / output tokens | Outcome |
| --- | --- | ---: | ---: | ---: | --- |
| Phi / JSON object | JSON | 73.90 s | 16 | 16,387 / 4,488 | Failed |
| Phi / guided, two-section batches | JSON | 84.68 s | 21 | 27,854 / 10,217 | Failed, including split retries |
| Phi / guided, first run | CSV | 26.20 s | 8 | 9,896 / 3,031 | Failed |
| Phi / guided, second fresh run | CSV | 31.98 s | 8 | 9,896 / 3,545 | Failed |
| Phi / guided + array flattening | JSON | 70.26 s | 16 | 18,755 / 9,070 | Failed |
| Phi / guided + array flattening | CSV | 28.65 s | 8 | 9,896 / 3,732 | Failed |
| gpt-5.4-mini / current schema configuration | JSON | 8.75 s | 3 | 10,632 / 3,279 | Ingestion/retrieval passed; strict facts 0/2 |
| gpt-5.4-mini / current schema configuration | CSV | 6.61 s | 1 | 4,321 / 1,259 | Ingestion/retrieval and strict facts 2/2 passed |

All six failed Phi attempts reported zero persisted chunks/entities. Control JSON
produced 69 embedded chunks, 37 entities, and 77 relationships; CSV produced 32
embedded chunks, 18 entities, and 36 relationships. Combined control Recall@3 was
100%, MRR@3 was 1.0, and no provider cached input tokens were reported. The JSON
control's strict edge-label failures remain a separate quality issue; successful
ingestion is not proof of complete factual extraction.

## Why the alternatives still failed

JSON object mode removed the HTTP format errors in the full-document attempt, but
returned nested arrays where GraphRAG requires objects. One response also invented
shortened RFC identifiers such as `RFC 46` and `RFC 7F`, and attached an incorrect
relationship. The guided prompt explicitly reinforced source identifiers and the
flat-array contract, yet malformed shapes persisted, including after split retries.

The final adapter flattened one JSON-document response and two CSV-document responses.
Other malformed values could not be safely normalized. CSV also returned an
unfinished JSON payload reported by the service as finish reason `stop`.
Neither document completed. Repairing those outputs would require more than removing
array nesting, and completion alone would still not establish factual correctness.

## Cost and recommendation

Across the six Phi attempts, reported chat usage was **92,684 input / 34,083 output
tokens over 77 calls**. At the user's unverified $0.075/$0.30 per-million-token rates,
that is about **$0.0172** for these failed attempts, excluding probes, embeddings,
retrieval, and control calls. This is a hypothetical token-rate calculation, not a
billing export or a measured saving. There were no successful Phi ingestions from
which to calculate cost per usable document. Adding primary-model fallback would
add this failed-attempt work before the successful ingestion.

Keep `gpt-5.4-mini` as the ingestion model. The small direct probe is insufficient
to endorse Phi even as a classifier or other secondary ingestion stage. Do not
introduce automatic routing based on model size or nominal token price. Revisit only
after a materially different serving/model configuration or a separately evaluated,
much narrower task; do not broaden to the policy corpus while basic technical
document extraction fails.

## Reproduction and review

Run from the repository root using the ignored local Phi model/key configuration
described in the initial report:

```powershell
dotnet run --project src/FabrCore.Services.GraphRag.Tests --no-restore -- --filter FullyQualifiedName~Unit.EvalConsoleTests
dotnet run --project src/FabrCore.Services.GraphRag.EvalConsole --no-build -- run --models artifacts/graphrag-phi/models-1024.local.json --model phi4-mini-reasoning --manifest src/FabrCore.Services.GraphRag.EvalConsole/corpus-relations-v3.json --max-documents 2 --max-chars 0 --response json-normalized --sections-per-batch 1 --document-concurrency 1 --output artifacts/graphrag-phi/json-normalized
dotnet run --project src/FabrCore.Services.GraphRag.EvalConsole --no-build -- run --model graphrag --manifest src/FabrCore.Services.GraphRag.EvalConsole/corpus-relations-v3.json --max-documents 2 --max-chars 0 --response schema --sections-per-batch 8 --document-concurrency 1 --output artifacts/graphrag-phi/refresh-control
```

Twelve eval-console unit tests passed, including default telemetry behavior, preservation
of caller options/messages, JSON guidance, lossless array flattening, rejecting invalid
members, retaining the original response, and preserving usage. Build succeeded.
The full GraphRAG unit suite then passed: **100 tests, zero failures or skips**.

Reports beneath `artifacts/graphrag-phi` (local and ignored):

- `json-1024/20260911T124520-528386ab31a841f9b0b275eaa4a7e059`
- `json-guided-2/20260911T124814-41e74d7c841943d2b98efb7ffb87522f`
- `json-guided-csv/20260911T124954-2c704f1a81c94345a0bd3c2db791604b`
- `json-normalized/20260911T125227-559071551f584c94834bda3125a11f27`
- `refresh-control/20260911T125425-58e27d3174c24ace8a608856a0499368`

Each contains completed JSON/Markdown reports. `refresh-probes.json` retains direct
probe responses; `refresh-summary.json` summarizes Phi document attempts. Logs and
the filtered CSV manifest are in the same parent directory. Credentials remain in
ignored local configuration files, never in this report or tracked code.
