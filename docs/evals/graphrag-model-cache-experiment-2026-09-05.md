# GraphRAG model and cache experiment — September 5, 2026

## Decision

Keep the configured `default` extraction model (`gpt-5.4-mini`) for ingestion.
The configured `email-graph` alias (`gpt-5.4-nano`) was slower on this corpus even
on a repeat pass with substantial provider cache reuse. No default model or
production prompt was changed during this experiment.

## Method

Four sequential console runs used mini → nano → nano → mini order. Each run used
the document extraction plan, the same instructions and cached RFC text, 1536-D
`text-embedding-ada-002` embeddings, and a fresh evaluation scope in
`localhost/graphrag`. Both configured chat models use reasoning effort `none` and
a 75,000-token context setting. The provider reported zero reasoning tokens for
these calls. No competing live model tests ran during this experiment.

Inputs were the full JSON/CSV RFCs and the same first 30,000 characters of the UUID
RFC as the baseline: 71,291 source characters and 179 retrieval chunks per pass.
The shared taxonomy was not reset. These are repeat-input measurements, not
controlled cold-cache trials. Provider caches, routing, and taxonomy growth can
affect results. Two runs per model are exploratory evidence, not a statistical
benchmark or a production latency guarantee.

## Results

| Order | Model | Ingestion seconds | Chat calls | Input tokens | Cached input tokens | Output tokens | Retry calls | Entities | Non-provenance edges |
| --- | --- | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: |
| 1 | gpt-5.4-mini | 29.723 | 7 | 24,779 | 22,784 | 10,248 | 0 | 143 | 121 |
| 2 | gpt-5.4-nano | 106.768 | 9 | 29,738 | 1,536 | 21,145 | 2 | 182 | 128 |
| 3 | gpt-5.4-nano | 86.710 | 7 | 24,869 | 17,408 | 17,291 | 0 | 179 | 133 |
| 4 | gpt-5.4-mini | 29.143 | 7 | 24,869 | 14,848 | 9,480 | 0 | 125 | 98 |

Mean corpus ingestion time: mini **29.433 seconds**, nano **96.739 seconds**.
Mini generated an average of 9,864 output tokens; nano generated 19,218.
The first nano CSV response contained a trailing comma, causing the current parser
to reject it and split/retry the extraction in two calls. The second nano pass
had no retries, yet still took about 2.9 times as long as the mini mean.

Every pass completed, embedded every chunk, met expected entity-name/edge/taxonomy
smoke gates, and achieved Recall@3=100% and MRR@3=1.000. These retrieval questions
exercise chunk retrieval; they do not establish equal factual graph quality.
Node and edge counts differ substantially. More nodes are not necessarily better:
precision, typed-relation correctness, and taxonomy accuracy still need labeled
evaluation. No billed-price comparison was performed.

## Are we using LLM caching?

There are distinct mechanisms:

| Mechanism | Current behavior |
| --- | --- |
| Identical-document reuse | Completed documents with unchanged content/instructions skip ingestion. It is not model-version-aware; changing models requires a forced rebuild or a fresh scope. |
| Embedding deduplication | Exact input strings are deduplicated within one embedding operation; this is not a persistent embedding cache. |
| Chat-client reuse | The service caches the client/model resolution, not extraction responses. |
| Provider prompt caching | Confirmed by live usage: e.g. mini's first pass reused 22,784 input tokens, and nano's repeat pass reused 17,408. |
| Application extraction-result cache | Not implemented. Fresh scopes and changed documents still make extraction calls. |

Provider prompt caching reuses eligible prompt computation; it does not return a
stored graph response or remove output generation. OpenAI documents prefix matching
and recommends stable instructions before variable content; the actual cache hits
above were measured from our Azure-backed responses, not inferred from OpenAI
availability claims. [Prompt caching documentation](https://developers.openai.com/api/docs/guides/prompt-caching).

Current prompts put document-specific metadata early, limiting shared prefixes
between different documents/sections. Exact repeated inputs can still get strong
cache reuse, as measured here. Reordering prompts may help new-document workloads,
but it will not eliminate generation cost, and cache eligibility depends on the
provider/model. Do not pad prompts merely to chase cache hits.

A future persistent extraction cache should use scope, exact extraction input,
model/deployment version, prompt/schema version, and instructions in its identity.
Taxonomy-dependent outputs also require the relevant taxonomy context in their
cache identity. This would avoid repeat generation on unchanged source windows
without reusing graph facts under an incompatible model or context.

## Next experiment

Test schema-enforced JSON responses while keeping mini, corpus, extraction
instructions, and concurrency fixed. `CreateExtractionChatOptions` currently sets
only an optional output token limit; JSON is requested in prompt text rather than
through an API response schema. The nano trailing-comma failure demonstrates the
cost of relying on prose for output formatting. Follow with a compact extraction
schema/description-length experiment and labeled factual edge checks. Lower output
volume should not be accepted if required facts disappear.

## Instrumentation and validation

The console now wraps the SDK chat client to record per-call latency, input/output,
cached input, reasoning tokens, and error type. It forwards the original options
and returns the original response. It does not change caching or add response reuse.
Missing provider usage remains null, distinct from a reported zero. Cached input
tokens are a subset of input tokens and must not be added to them. The wrapper
measures the non-streaming path used by ingestion, not streaming calls.

All 60 unit tests pass, including a regression test for transparent forwarding,
zero versus missing cache usage, drain behavior, and failed-call telemetry.

Reports with complete graph snapshots and usage:

- Mini 1: `artifacts/graphrag-evals/20260905T190442-896398ee1b224080912400ed70d547e3/report.json`
- Nano 1: `artifacts/graphrag-evals/20260905T190515-70a48016742646c7a9808eaefd495463/report.json`
- Nano 2: `artifacts/graphrag-evals/20260905T190704-aebd063049f84eaca030228770bdf3f2/report.json`
- Mini 2: `artifacts/graphrag-evals/20260905T190833-5796f22897e5406aba4ab2107c030903/report.json`

Reproduce individual runs from the repository root:

```powershell
dotnet run --project src/FabrCore.Services.GraphRag.EvalConsole -- run --model default
dotnet run --project src/FabrCore.Services.GraphRag.EvalConsole -- run --model email-graph
```
