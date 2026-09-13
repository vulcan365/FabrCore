# GraphRAG evidence validation experiment — September 5, 2026

## Applied baseline changes

- Added a dedicated `graphrag` model alias for `gpt-5.4-mini`, reasoning effort
  `none`, to the sample model template and local Azure model configuration. The
  general-purpose `default` alias is unchanged. Ingestion already prefers the
  `graphrag` alias when no explicit model is supplied.
- The eval console now defaults to `graphrag`. Its settings explicitly retain
  document extraction sections, bounded chat/embedding concurrency, batched
  embeddings, and disabled schema/relation/description experiments.
- Kept the existing performance changes: non-overlapping extraction sections,
  overlapping retrieval chunks, once-per-document taxonomy, embedding batching
  and deduplication, and phase/token/cache telemetry. SQL VECTOR is unchanged.

## Experiment and decision

Added opt-in `UseExtractionEvidence` / `--evidence strict`. Graph and combined
responses require an `evidence` string on every relationship. Before graph writes,
the service checks that the passage exists in the actual batch text, both endpoint
entities exist in the response, and selected asymmetric relationships do not point
both ways. A second structural check after merging catches conflicts across batches.

This is **not ready for production**. Every evidence-strict document failed:
six attempts on the original corpus and one GZIP attempt. The evidence-off controls
completed. The model frequently abbreviated, reformatted, or paraphrased passages
instead of supplying exact text. Some edges also had missing endpoint entities.

Validation failures fail the document rather than silently deleting edges or
issuing repair calls. The shorter times of failed runs are not ingestion speedups.
Evidence mode remains disabled by default.

## Method

All trials used the same mini deployment, reasoning disabled, strict JSON, current
relation instructions, no description-length target, the same concurrency and
embeddings, and fresh scopes in `localhost/graphrag`. The only intervention was
adding the evidence field/instructions and validation. Taxonomy output has no
evidence field, though evidence mode uses a distinct schema name. Evidence
instructions consume additional input-budget space.

The original corpus stayed at 71,291 characters / 179 chunks; order was off →
strict → strict → off. GZIP was 25,037 characters / 62 chunks, tested off → strict.
No concurrent live model tests ran. Setup and embedding warmup were outside timing.
Provider caches, generation variability, and shared taxonomy remain confounders.

## Results

| Corpus / mode | Completed documents | Attempt seconds | Calls | Input tokens | Cached input | Output tokens | Quote mismatches* | Missing endpoints* |
| --- | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: |
| Original / off 1 | 3/3 | 29.343 | 7 | 25,784 | 3,328 | 9,389 | Not checked | 9 |
| Original / strict 1 | 0/3 | 19.552 | 7 | 26,293 | 0 | 7,891 | 21 | 1 |
| Original / strict 2 | 0/3 | 17.604 | 7 | 26,293 | 23,808 | 6,525 | 11 | 0 |
| Original / off 2 | 3/3 | 31.310 | 7 | 25,784 | 20,480 | 9,907 | Not checked | 3 |
| GZIP / off | 1/1 | 9.632 | 3 | 8,937 | 4,096 | 3,967 | Not checked | 6 |
| GZIP / strict | 0/1 | 13.704 | 3 | 9,141 | 0 | 5,017 | 15 | 1 |

*Counts are per-response, full-document diagnostic checks in the console. Actual
ingestion validates against the smaller batch and checks directions after merging,
so these counts do not capture every possible ingestion validation failure. The
same edge may have multiple issues. Missing endpoints are checked within each
response; an endpoint might be present in a different batch, so these counts are
not identical to the number of edges dropped at final persistence.

All evidence-off controls embedded every chunk and passed retrieval smoke gates.
Failed strict scopes returned no retrieval hits. They are quality failures, not
successful runs with fewer facts. No evidence repair calls were made. Provider
usage from failed documents remains available even when SQL phase metrics are absent.

## Findings and limitations

Observed quote mismatches included ellipses, rewritten table fragments, joined
header text, and added quotation delimiters. These are not all fabricated facts;
some are formatting differences rejected by an intentionally strict check. The
validator only normalizes whitespace and does not strip quote delimiters, apply
fuzzy matching, or treat paraphrases as verbatim evidence.

Control responses also had edges whose endpoint names were absent from their
entity arrays. Existing graph persistence only keeps edges whose endpoints resolve,
so successful ingestion can still omit relationships. This is a more actionable
structural defect than relying on total node/edge counts as a quality measure.

A matching quote cannot establish that the predicate is entailed, that direction
is correct, or that negation was respected. A regression test explicitly documents
this limit. Reciprocal USES, DEPENDS_ON, and REFERENCES edges are allowed because
they can be real; the asymmetric conflict check is limited to AUTHORED_BY,
PUBLISHED_BY, SIGNED_BY, ESTABLISHES, and PART_OF. No validator here proves that
all relevant facts were extracted.

Raw provider responses are now retained by the eval console, including evidence
text. Production ingestion does not persist evidence as a new SQL property.
These snapshots support inspection of failures without additional LLM calls.

## Next experiment

Avoid asking the model to regenerate long source passages. Supply stable source
span identifiers and have relationships reference those identifiers; validate
identifier existence and endpoint completeness deterministically. Source IDs
establish provenance, not semantic truth, so retain labeled fact/direction checks.
Then consider one bounded repair request for structurally invalid output, measuring
completion rate, additional calls/tokens, and total latency against the unchanged
baseline. Do not enable whole-document failure on quote mismatches in production.

## Validation and reproduction

All **70 unit tests pass**, including the Azure evidence-schema payload, actual
batch quote matching, missing endpoints, cross-batch direction-check inputs,
allowed reciprocal citations, and fail-closed extraction without repair. Six live
experiment passes completed as evaluations; three strict passes correctly reported
failed ingestion rather than success. Production experiments remain off.

A final run with no experiment flags verified the actual new defaults: dedicated
mini alias, prompt-only responses, and evidence off. All three documents completed,
all 179 chunks were embedded, and retrieval smoke gates passed. This run took about
36.6 seconds, illustrating latency variability. Its report is
`20260905T195453-d59b1ae460984c52b81f5e1f3b4d3f55` under the same artifact root.

```powershell
# Recommended baseline, using the dedicated mini alias:
dotnet run --project src/FabrCore.Services.GraphRag.EvalConsole -- run

# Evidence experiment:
dotnet run --project src/FabrCore.Services.GraphRag.EvalConsole -- run --response schema --evidence strict --manifest src/FabrCore.Services.GraphRag.EvalConsole/corpus-relations-v2.json
```

Use `--evidence off` for the schema control. Reports are under
`artifacts/graphrag-evals/`, each with `report.md` and `report.json`:

1. `20260905T195149-4cbb6b25cac74cd3a2e872339714c262`
2. `20260905T195220-e581be14cdbd4341a44d2426679ab398`
3. `20260905T195242-5199f7a0a4ce4d49a8c54e28f01d5136`
4. `20260905T195302-69c6330bcc764d92a6d60ca8fb675658`
5. `20260905T195336-f0377c5a999f49bd89df9e2c4f8b4bf6`
6. `20260905T195347-14c98ead1e514a67abf249e05daa2d03`
