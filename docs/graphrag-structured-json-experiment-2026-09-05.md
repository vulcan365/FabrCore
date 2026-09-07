# GraphRAG structured JSON and compact-description experiment

## Decision

Keep the existing production defaults while retaining the new opt-in schema path
for evaluation. Strict schema responses averaged **26.518 seconds**, versus
**30.020 seconds** for prompt-only JSON (11.7% faster in this sample). However,
the required typed-edge checklist matched fewer facts. The compact-description
variant averaged **27.563 seconds** and did not improve on plain schema responses.
Neither variant meets the quality condition for adoption.

The baseline also fails half of the six required edge checks. This experiment
exposes a graph representation/coverage problem that existing chunk-retrieval
smoke tests cannot detect. Address relation direction and representation before
accepting lower output volume as an optimization.

## Implementation

- `GraphRag:Ingestion:UseExtractionJsonSchema` defaults to `false`. When enabled,
  the local chat path supplies separate schemas for graph, taxonomy, and combined
  extraction, preserving the original prompt text, output token budget, and retry
  behavior. Schemas require fields/types and disallow additional properties.
  Entity/relationship vocabulary remains string-valued as in the current parser;
  semantic correctness and confidence calibration are not enforced by the schema.
- A transport test using the installed Azure adapter verifies the actual request
  contains `response_format.type=json_schema` and `json_schema.strict=true`.
- The remote Host API does not transport response schemas. Enabling this feature
  there fails explicitly; it does not silently run prompt-only extraction.
- `ExtractionDescriptionTargetChars` defaults to `0`. The compact trial uses
  `120`, adding only schema description guidance to preserve facts while aiming
  for shorter descriptions. The installed adapter strips `maxLength`, discovered
  by the transport test, so this is explicitly a soft target. Output is never
  truncated to meet it.
- The evaluator accepts `--response prompt|schema` and `--description-chars N`,
  records these settings, and scores optional source-backed `ExpectedEdges` in
  the corpus manifest. No additional LLM judge or extraction calls are introduced.

OpenAI distinguishes schema adherence from semantic correctness in its
[structured outputs documentation](https://developers.openai.com/api/docs/guides/structured-outputs).
Provider compatibility here was additionally established by live Azure runs.

## Controlled variables and limitations

Six sequential passes ran prompt → schema → compact → compact → schema → prompt.
Every pass used `default` / `gpt-5.4-mini`, reasoning effort `none`, the same
75,000-token context configuration, prompts, document extraction plan, concurrency,
and 1536-dimensional `text-embedding-ada-002` embeddings. Input text was unchanged:
full RFC 8259 and RFC 4180, and the first 30,000 characters of RFC 9562; 71,291
source characters and 179 chunks per pass. Schema tokens necessarily increase
input usage; this is part of the intervention.

Each pass used a new scope in `localhost/graphrag`; no database was created or
reset. Initial taxonomy row count was six for all passes. Shared taxonomy was not
frozen, and provider routing, caches, and generation variability were not controlled.
These are repeat-input exploratory trials, with only two measurements per variant.
No competing live model tests ran. Download, schema setup, and embedding warmup
are excluded from ingestion timing. No billed-cost comparison was performed.

## Results

All passes completed, embedded every chunk, made seven chat calls with zero
extraction retries, and achieved Recall@3=100% and MRR@3=1.000. Consequently this
trial does **not** demonstrate fewer malformed-output retries: the baseline had
none either. Provider usage reported zero reasoning tokens throughout.

| Order | Variant | Seconds | Input tokens | Cached input | Output tokens | Entities | Non-provenance edges | Required edge matches |
| --- | --- | ---: | ---: | ---: | ---: | ---: | ---: | ---: |
| 1 | Prompt-only | 30.376 | 24,873 | 3,840 | 9,973 | 127 | 126 | 3/6 |
| 2 | Schema | 25.924 | 25,696 | 0 | 7,659 | 107 | 99 | 2/6 |
| 3 | Schema + 120-character target | 27.592 | 26,117 | 0 | 8,564 | 124 | 118 | 2/6 |
| 4 | Schema + 120-character target | 27.534 | 26,117 | 23,808 | 8,751 | 130 | 120 | 2/6 |
| 5 | Schema | 27.111 | 25,696 | 16,128 | 8,665 | 120 | 113 | 1/6 |
| 6 | Prompt-only | 29.664 | 24,873 | 22,784 | 9,612 | 132 | 99 | 3/6 |

Mean output tokens: prompt-only 9,792.5; schema 8,162; compact 8,657.5.
Schema reduced average output by 16.7%, but that is not established as lossless.
The compact trial emitted more total output than plain schema, despite shorter
persisted edge descriptions (about 50–51 characters on average versus 59–62).
All persisted edge descriptions in these runs were already at most 120 characters,
including the baseline. The target was a weak constraint for these descriptions.
Entity descriptions were not included in this length analysis. Graph item counts
also vary, so total output is not determined by description length alone.

## Required edge checklist

Labels were added before live trials, based on the ingested source text, and never
inserted into extraction prompts. All source evidence snippets were present.

| Label | Source | Required representation |
| --- | --- | --- |
| json-utf8 | RFC 8259 §8.1 | JSON → USES/DEPENDS_ON → UTF-8 |
| ecma404-publisher | RFC 8259 §14.1 | ECMA-404 → AUTHORED_BY → Ecma International |
| iana-csv | RFC 4180 §5 | IANA → ESTABLISHES → text/csv |
| abnf-rfc2234 | RFC 4180 §2 and §10 | ABNF → REFERENCES/DEPENDS_ON → RFC 2234 |
| uuid-guid | RFC 9562 §1 | UUID ↔ RELATED_TO ↔ GUID, with name-equivalence description |
| uuid-urn | RFC 9562 §1 | UUID → USES → URN |

This is a small directed-representation checklist, not comprehensive factual
precision/recall. Endpoint aliases are regex-matched; allowed types are explicit.
The publisher label uses the current AUTHORED_BY vocabulary as the expected
standard-to-organization representation; separating publication from authorship
would improve the relation ontology. An unmatched label can reflect alternative
representation, an incorrect direction/type, or omitted information.

Manual inspection found all three cases worth distinguishing:

- The second schema run represented JSON's UTF-8 requirement as
  `RFC 8259 → ESTABLISHES → UTF-8`, with a correct encoding description. The
  information exists, but the required JSON-to-encoding edge is absent.
- Multiple runs emitted `RFC 4180 → REFERENCES → RFC 2234`, with ABNF in the
  description. This does not meet the required ABNF-to-standard representation.
- Compact run 1 and prompt run 2 emitted `Ecma International → AUTHORED_BY →
  ECMA-404`, pointing in the opposite direction from the label.
- Compact run 1 initially passed UUID/GUID because an unrelated storage-caveat
  edge shared those endpoints. The checker was corrected to require a name-
  equivalence description, regression-tested, and applied uniformly to all six
  saved reports. No raw graph, timing, or usage data changed. Final scores above
  use the corrected checker. This illustrates the limits of endpoint-only scoring.

Smoke pass flags and exit codes remain separate from these quality checks. Missing
required matches block accepting the compact variant; a smoke pass is insufficient.

## Validation and next useful experiment

All **63 unit tests pass**, including Azure request serialization, routing of all
three response schemas, preservation of output token limits, and rejection of
reversed, wrong-type, unsupported-evidence, and false-synonym matches. Six live
ingestion/retrieval passes completed. Production defaults were not changed.

Next, define relationship direction and permitted representations explicitly,
then evaluate those instructions on a broader labeled corpus. For example,
AUTHORED_BY should point from work to author, and encoding requirements should
connect the format to the encoding. Distinguish authorship from publication rather
than forcing both into one relation. Include unknown/unanswerable facts and
unsupported-edge checks, not just positive expected edges. Repeat the schema
comparison after representation is stable; only then try a tighter description
budget or fewer redundant output fields.

## Reproduce and inspect

```powershell
dotnet run --project src/FabrCore.Services.GraphRag.EvalConsole -- run --model default --response prompt
dotnet run --project src/FabrCore.Services.GraphRag.EvalConsole -- run --model default --response schema
dotnet run --project src/FabrCore.Services.GraphRag.EvalConsole -- run --model default --response schema --description-chars 120
```

Reverse the order for the second set. Reports live under `artifacts/graphrag-evals/`:

1. `20260905T192302-2326bc37ad74434d8b0b5525ba44bf94`
2. `20260905T192335-7bd9ae1435284863a8fe10308dc76920`
3. `20260905T192403-4a060116faef48c0933032c07f96304b`
4. `20260905T192433-94bb65b2f2d347c791b2d4c03a980065`
5. `20260905T192503-572f67b391954adead9e7a39da323ea4`
6. `20260905T192533-792778b220804dd8bb59082532026668`

Each contains `report.md` and `report.json`, including scope IDs, raw graph
snapshots, per-call token/cache usage, labeled matches, and retrieval evidence.
