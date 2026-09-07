# FabrCore.Services.GraphRag.EvalConsole

A standalone .NET 10 console application for real SQL VECTOR, embedding, graph
extraction, and retrieval evaluations. Blazor and a FabrCore web host are not
required. The app uses the existing GraphRAG services and FabrCore SDK model client
with a local model-configuration resolver.

## Configuration

`appsettings.json` contains ingestion defaults. `appsettings.local.json` contains
the local database connection and model-file path and is ignored by Git. Both are
copied to the build output. Environment variables override both files.

The current local file targets the existing `localhost/graphrag` database as
`graphrag365`. The supplied account only needs access to that database; the console
does not create or drop databases. Initialization runs the package's idempotent
schema migrations and a native SQL VECTOR distance probe.

For another machine, create `appsettings.local.json` alongside the project:

```json
{
  "ConnectionStrings": {
    "GraphRagTestDb": "Server=localhost;Database=graphrag;User Id=graphrag365;Password=YOUR_PASSWORD;Encrypt=True;TrustServerCertificate=True"
  },
  "Eval": {
    "ModelConfigurationPath": "C:/repos/FabrCore/samples/FabrCore.SampleApp/fabrcore.json",
    "ExtractionModel": "default"
  }
}
```

The model file uses FabrCore's `ModelConfigurations` and `ApiKeys` format. It needs
an extraction model and an `embeddings` model producing **1536-dimensional vectors**.
No keys or connection-string passwords are written to evaluation reports.
Relative model paths and command-line paths resolve from the working directory.

## Run from the repository root

```powershell
# Initialize the existing database; no model credentials required
dotnet run --project src/FabrCore.Services.GraphRag.EvalConsole -- init

# Download/cache the public text corpus; no SQL or model calls
dotnet run --project src/FabrCore.Services.GraphRag.EvalConsole -- fetch

# Default live evaluation, using the new document extraction pipeline
dotnet run --project src/FabrCore.Services.GraphRag.EvalConsole -- run

# Compare legacy extraction with the document plan; alternate order between iterations
dotnet run --project src/FabrCore.Services.GraphRag.EvalConsole -- compare --iterations 2

# Embedding/retrieval baseline, without graph extraction
dotnet run --project src/FabrCore.Services.GraphRag.EvalConsole -- run --mode vector

# Evaluate another configured model or complete source documents
dotnet run --project src/FabrCore.Services.GraphRag.EvalConsole -- run --model email-graph --max-chars 0
```

Use `--help` for all options. `run` is the default command. Ingestion is sequential
across documents; the library controls concurrent extraction and embedding calls
within each document. Compare modes share identical content, instructions, model,
and embedding settings. This compares extraction plans, not the pre-optimization
embedding implementation.

## Corpus

The bundled `corpus.json` downloads native UTF-8 text from RFC Editor:

- [RFC 8259: JSON](https://www.rfc-editor.org/rfc/rfc8259.txt)
- [RFC 4180: CSV](https://www.rfc-editor.org/rfc/rfc4180.txt)
- [RFC 9562: UUIDs](https://www.rfc-editor.org/rfc/rfc9562.txt)

No document conversion is performed. The cache retains each complete download,
including its original copyright notices. Default ingestion uses the first 30,000
characters per source to bound the initial experiment. This includes the complete
JSON and CSV documents but only a prefix of the UUID document. Use `--max-chars 0`
for full documents. Reports identify the source URL, full-source hash, ingested
hash, and both lengths so excerpt runs cannot be mistaken for full-corpus runs.

Use `--manifest PATH` for another JSON manifest with HTTPS `.txt`/`.md` URLs,
unique filenames, retrieval questions, expected entity-name fragments, and evidence
terms. Cached source bytes are reused across comparisons. Use a new `--cache` path
to refresh downloads and record their new hashes.

## Reports and gates

Each pass creates a fresh `eval:graphrag:*` scope, avoiding unchanged-document
short circuits. Data is retained for inspection. The app never deletes existing
scope data or shared taxonomy. JSON/Markdown report checkpoints are written under
`artifacts/graphrag-evals/<run-id>/` after each document and query.

Reports include:

- Per-document wall time and persisted phase timings, chat calls/tokens, retries,
  embeddings and SQL batch counts.
- Per-chat-call provider latency, input/output tokens, cached input tokens, reasoning
  tokens, and error type. Missing provider usage remains null (unknown), not zero.
  These measurements observe the existing provider cache; no application response
  cache is added. Cached input is part of total input tokens, not additional usage.
- Chunk embedding coverage, expected entity-name hits, actual non-provenance edges,
  domain/category names, and edge descriptions.
- Expected-document rank among the first three chunks, Recall@3, truncated MRR@3,
  retrieved-scope checks, and raw retrieval results. Evidence-term hits are diagnostic.

Passing requires every ingestion to complete, every chunk to have an embedding,
and all non-vector runs to contain expected entity-name fragments, a real
non-provenance edge, and taxonomy assignments. Retrieval requires Recall@3=100%,
MRR@3>=0.70, and no returned row outside the requested scope. These are functional
smoke gates, **not** a labeled entity/relationship precision or category-accuracy
benchmark. The app does not use an LLM judge. The existing test suite separately
exercises scope isolation using forbidden documents.

The taxonomy inventory is global, even across new scopes. Reports record its size
at each pass start; later passes may benefit from earlier taxonomy creation.
Repeated runs and alternating order help expose variability but do not eliminate
provider, prompt-cache, or database-cache effects. Avoid other model workloads
when collecting controlled latency measurements. Download, migration, and initial
embedding warmup time are outside reported ingestion time; overlapping phase
durations must not be summed. Failed ingestion may have no persisted token metrics.
The per-call console telemetry can retain usage from successful calls even when
document extraction later fails. Streaming calls are not used by this ingestion
path and are not measured by the evaluator's wrapper.

Exit codes: `0` all gates passed, `1` infrastructure/configuration failure,
`2` evaluation gates failed, `130` cancellation. Interrupted reports retain
`Completed=false`; scope data and checkpoints remain available.

## Existing tests

### Structured-response experiments

Keep model, corpus, prompts, and concurrency fixed while varying response format:

```powershell
dotnet run --project src/FabrCore.Services.GraphRag.EvalConsole -- run --model default --response prompt
dotnet run --project src/FabrCore.Services.GraphRag.EvalConsole -- run --model default --response schema
dotnet run --project src/FabrCore.Services.GraphRag.EvalConsole -- run --model default --response schema --description-chars 120
```

Repeat in reverse order to assess variability. `--description-chars` adds a soft
description-length target in schema metadata; it does not truncate output or
enforce a hard character limit. Reports record both experiment settings. The
console defaults to prompt-only responses and no description target independently
of the service configuration, making these CLI comparisons explicit.

The bundled corpus now includes six `ExpectedEdges` labels with source evidence,
endpoint patterns, allowed relationship types, and direction. These checks are
reported separately from existing smoke gates and exit codes. A failed match may
indicate a missing fact, reversed direction, an unexpected type, or an alternative
representation; inspect the retained edges before drawing a quality conclusion.
This small coverage checklist is not overall graph precision/recall. Do not accept
a compact variant while required labeled facts are missing. UUID/GUID equivalence
is the only symmetric label; the others require the specified direction.

Relationship convention trials use `--relations current|defined` (default
`current`). The versioned `corpus-relations-v2.json` fixes the publisher label to
`PUBLISHED_BY`, checks RFC 4180's ABNF reference as a citation, accepts explicit
`ALIAS_OF` equivalence, and adds a forbidden central-registration dependency for
UUIDs. Previous labels remain in `corpus.json` for reproducibility. Do not compare
scores across these label versions as if they used the same quality definition.

```powershell
dotnet run --project src/FabrCore.Services.GraphRag.EvalConsole -- run --model default --response schema --relations defined --manifest src/FabrCore.Services.GraphRag.EvalConsole/corpus-relations-v2.json
dotnet run --project src/FabrCore.Services.GraphRag.EvalConsole -- run --model default --response schema --relations defined --manifest src/FabrCore.Services.GraphRag.EvalConsole/corpus-relations-holdout.json
```

The holdout manifest contains RFC 1952 (GZIP). Repeat with `--relations current`
for controls. Reports separate positive matches and selected forbidden-edge checks.
`OtherRepresentations` retains endpoint or description candidates for manual review;
these candidates do not count as passing facts. A forbidden check requires its
supporting source evidence and absence of the specified edge, not absence of all
hallucinations. These remain diagnostics separate from smoke gate exit codes.

### Evidence validation experiment

The evaluator now defaults to the dedicated `graphrag` mini alias. Experimental
schema, relation, compact-description, and evidence options remain disabled by
default. Use `--model default` explicitly only when comparing the general alias.

```powershell
dotnet run --project src/FabrCore.Services.GraphRag.EvalConsole -- run --response schema --evidence strict --manifest src/FabrCore.Services.GraphRag.EvalConsole/corpus-relations-v2.json
```

Compare with `--evidence off` while keeping other settings fixed. Strict evidence
requires `run --mode document` and schema responses. Failures produce exit code 2;
they are not successful faster ingestions. No repair calls are added for evidence
failures. Retained per-call usage includes work from failed documents.

Eval reports now capture raw provider extraction responses (including evidence
text) for inspection. These contain document-derived content. Production ingestion
does not persist this additional response snapshot. Full-document evidence checks
in the report are diagnostic; ingestion checks the actual batch and merged graph.
Quote presence alone cannot prove that a fact is true or its direction is correct.

### Source-ID and endpoint-repair experiment

```powershell
dotnet run --project src/FabrCore.Services.GraphRag.EvalConsole -- run --response schema --evidence spans --repair off --manifest src/FabrCore.Services.GraphRag.EvalConsole/corpus-relations-v2.json
dotnet run --project src/FabrCore.Services.GraphRag.EvalConsole -- run --response schema --evidence spans --repair once --manifest src/FabrCore.Services.GraphRag.EvalConsole/corpus-relations-v2.json
```

Use `--evidence off --repair off` as the schema control. Repair is limited to one
call per document, after merging; it cannot edit relationships. Source-ID-only
ingestion fails if endpoints remain unresolved. Invalid source IDs or conflicting
asymmetric edges also fail. Failed attempts are not successful faster ingestions.

Reports retain provider schema names to count repair calls and a source-span
catalog (`Id`, `Start`, `Length`) per document. Offsets and lengths use .NET UTF-16
string positions in the ingested text identified by the corpus hash. Identical
sections may share an ID at multiple positions. IDs identify sections, not an
exact supporting sentence. Console ID diagnostics check document membership;
ingestion enforces the stricter actual-batch membership check.

Per-response missing-endpoint diagnostics describe original responses; a later
repair or another batch may supply those entities. Inspect completed graph
snapshots and status when assessing whether a defect remains after repair.
Repair schema v2 records unsupported names explicitly in `unresolvedNames` and
fails when that list is nonempty. Provider schema names in reports distinguish v2
from the initial v1 trial, which could accept unsupported placeholders as entities.
Source schema v2 also enumerates allowed IDs for the actual batch, preventing the
mistyped IDs observed with the initial free-string source-ID schema. A valid ID
still does not prove the relationship is supported by that section.

### Running existing tests

GraphRAG tests discover this project's local settings file when environment
overrides are absent. Both SQL integration tests and live evals therefore use the
same existing database and model file:

```powershell
dotnet run --project src/FabrCore.Services.GraphRag.Tests -- --filter "TestCategory=Integration"
dotnet run --project src/FabrCore.Services.GraphRag.Tests -- --filter "TestCategory=Evaluation"
```

The legacy-schema M005 migration sandbox is skipped unless explicitly enabled
with `FABRCORE_GRAPHRAG_ALLOW_DATABASE_CREATION=true` and a suitable account.
Leave that disabled for the supplied database-owner account. Normal initialization
and migration-idempotency coverage runs in `graphrag` without server permissions.

### Exact extraction cache experiment

```powershell
dotnet run --project src/FabrCore.Services.GraphRag.EvalConsole -- run --model graphrag --manifest src/FabrCore.Services.GraphRag.EvalConsole/corpus-relations-v2.json --response schema --result-cache on --reingest edit --iterations 4
```

Repeat with `--result-cache off` for the control. Pass 1 is cold, pass 2 repeats the
original content, pass 3 replaces the final character with a space (or newline if it
was a space), and pass 4 repeats that modified content. This synthetic one-character
mutation preserves length; it is not an evaluation of arbitrary document edits.
`--reingest repeat` leaves content unchanged throughout. Both modes force rebuilding
within one run-specific scope. The default `fresh` mode retains isolated passes.
Each document records its actual content SHA-256, cache hits, measured provider calls,
persisted graph snapshot and factual checks. The corpus section describes original inputs.
Only graph-only batches are cached; taxonomy and combined CSV extraction remain live.
All embeddings are still generated. Cached raw responses are not listed as new provider
calls, so assess their content through persisted graph snapshots and factual checks.

### Embedding cache experiment

Add `--embedding-cache on` to the four-pass extraction-cache experiment above; repeat
with `--embedding-cache off`, holding `--result-cache on` in both arms. The report
records SDK embedding calls, input counts, character counts, elapsed call time and
cache hits. Warmup and retrieval-query requests are excluded from ingestion metrics.
SDK-internal HTTP retries are not individually counted. Per-call elapsed times can
overlap and must not be interpreted as total ingestion wall time.

For an embedding-only comparison, use `--mode vector --result-cache off --reingest edit
--iterations 4` with each embedding-cache setting. This isolates vector ingestion time;
it does not evaluate graph extraction quality. Both modes retain SQL vector-search checks.

`ChunkVectors` fingerprints each persisted chunk's content and SQL vector text
serialization. Repeat passes compare unchanged content at the same chunk index. Cache-on
runs fail the smoke gate if those vector hashes change. Fingerprints are serialization
hashes, not a comparison of raw SQL binary storage. Entity-vector cloning and preservation
are covered by unit tests; the live snapshots currently fingerprint retrieval chunk vectors.

### Taxonomy-context cache experiment

Add `--taxonomy-cache on` to the full-pipeline repeat experiment, holding both
`--result-cache on` and `--embedding-cache on` fixed. The control uses
`--taxonomy-cache off`. This caches the short-document combined graph/classification
response and the long-document classification response when their entire context is
unchanged. Extraction cache hits now include these response types. The report records
`TaxonomyCache` so earlier graph-only cache runs remain distinguishable.

Source content, caller instructions, model/configuration, schema settings, domain and
category names/descriptions and category-domain mappings participate in invalidation.
Classification keys include all source sections, even when the prompt samples only
part of them. The first pass can change taxonomy, which causes a legitimate miss on
later passes until the relevant context is stable. It does not bypass document skip,
SQL writes or the normal taxonomy persistence rules.

### Cold-ingestion batch-size experiment

`--sections-per-batch N` overrides the document pipeline's extraction section limit
(1-256; default is the configured value, normally 8). The console rejects this option
outside `run --mode document`. Reports store the effective `SectionsPerBatch`; older
reports without that field must not be assigned an assumed batch size.

```powershell
dotnet run --project src/FabrCore.Services.GraphRag.EvalConsole -- run --model graphrag --manifest src/FabrCore.Services.GraphRag.EvalConsole/corpus-relations-v2.json --response schema --result-cache off --taxonomy-cache off --embedding-cache off --reingest fresh --sections-per-batch 8
```

Compare 8, 4, 16 and then 16, 4, 8 to reduce simple run-order bias. Source sections
remain 2,000 characters, retrieval chunks stay unchanged, and the existing input-token
budget can still split a batch earlier than the section limit. A document fitting in
one batch uses combined extraction/classification; larger documents classify separately.
Consequently changing the limit also exercises that existing routing behavior. Provider
prompt caching is measured but is not disabled by these application cache switches.
Retain required typed factual-edge checks, not just entity counts and retrieval smoke
checks, when deciding whether to adopt a faster configuration.

### Trace missing factual relationships

```powershell
dotnet run --project src/FabrCore.Services.GraphRag.EvalConsole -- trace --input artifacts/graphrag-evals/batch-size-experiment-2026-09-05.json --manifest src/FabrCore.Services.GraphRag.EvalConsole/corpus-relations-v3.json --output artifacts/graphrag-fact-trace-v3
```

Input can be one report.json or a comparison array containing run IDs whose directories
are siblings of that array. The trace command reads captured responses and persisted
graph snapshots, verifies the corpus hash, and makes no LLM calls or database writes.
Missing corpus files can be downloaded through the existing corpus loader. Changed-content
reports require their exact source; reports without captured responses cannot be replayed.
Results distinguish absent required edges, alternative representations, missing endpoint
names and raw matches missing from the graph. Expected endpoint entity inventories are
included. Candidates are diagnostics, not automatic factual credit. Output identifies
run, iteration and scope. Raw-response ordering is completion order and does not replay
production batch-order deduplication; alias predictions do not simulate SQL persistence.

`corpus-relations-v3.json` tightens format/identifier names so JSON parser, JSON generator,
UUID registries, UUID examples and IANA email addresses cannot stand in for the intended
entities. It also corrects CSV source-section references. The required predicates remain
unchanged. Original v2 manifests and reports are preserved; compare versions by rescoring
the same snapshots, not by treating their scores as interchangeable.

`--endpoint-aliases on` enables conservative deterministic endpoint resolution before
persistence (default off). It recognizes a unique explicit parenthesized initialism only
when the expansion's capital letters agree. It does not infer plural/singular aliases,
perform fuzzy matching or create entities. Exact entity names win; ambiguous aliases stay
unresolved. The production flag is `GraphRag:Ingestion:ResolveExtractionEndpointAliases`.
This post-extraction option adds no LLM calls and does not alter prompts or cached raw
responses. Normal unchanged-document skipping still applies; force rebuilding to apply a
new processing setting to existing content. New live reports include FactTraces.

### Government policy corpus

`corpus-policy-v1.json` contains complete source Markdown from pinned commits of
Project Open Data (the M-13-13 memo and governance rules) and CISA's open-source policy.
Use `--max-chars 0` to ingest all 59,152 characters rather than the default 30,000-character
prefix. No PDF/HTML conversion is needed. Source Markdown, including its front matter
and inline HTML, is ingested unchanged. These are reproducible source snapshots, not
an assertion about current policy applicability.

```powershell
dotnet run --project src/FabrCore.Services.GraphRag.EvalConsole -- run --model graphrag --manifest src/FabrCore.Services.GraphRag.EvalConsole/corpus-policy-v1.json --max-chars 0 --response schema --sections-per-batch 8 --endpoint-aliases off --result-cache off --taxonomy-cache off --embedding-cache off --reingest fresh --iterations 2
```

Corpus entries can now optionally specify `ExtractionInstructions`. The console uses
those instructions for that document and records them in DocumentResult. Existing RFC
manifests retain the previous standards/technology guidance. This policy corpus asks for
policies, laws, organizations, programs, roles and explicit responsibilities, preserving
actors, direction and obligation strength. Labels and instructions are fixed before runs;
labels are not included in the extraction prompt. Nine positive typed-fact checks and
three retrieval questions accompany this initial corpus. Only the common-metadata fact
has an explicit required-language description check; the suite does not fully evaluate
policy exceptions, timing or obligation strength, and has no selected negative edge labels.

### Document concurrency experiment

`--document-concurrency N` (1-16, default 1) bounds overlapping document ingestions.
All documents in a pass use the same ingestion service, scope, and shared chat/embedding
semaphores. This does not raise `MaxConcurrentChatCalls` (configured as 4).
Result and embedding caches must be off above concurrency 1 because their existing
aggregate hit counters cannot attribute hits to overlapping documents.

Reports record `IngestionWallMs` for batch elapsed time and `PeakChatConcurrency` for
observed provider chat overlap. Per-document `WallMs` includes contention for shared
limits; summing those durations is not batch wall time. Warmup, corpus download,
schema setup, final graph validation and retrieval are excluded. Chat/embedding samples
belong to their document's async flow. Graph snapshots and fixed fact checks run after
all document writes settle, including in the sequential control. Completed ingestion
results are checkpointed if a later worker fails or the run is canceled.

Example: add `--document-concurrency 2` to the policy corpus command above.

### Structured taxonomy names

`--taxonomy-names structured` enables an opt-in taxonomy experiment (default `current`).
The combined extraction prompt serializes existing names, domain membership and
descriptions as separate JSON fields. Combined and classifier response schemas explain
that a reused name must match a supplied name value. An unknown name marked
`isNew=false` skips that document's taxonomy assignment, logs a warning, and retains
its entities/edges without an extra LLM call. It does not rename/delete existing taxonomy,
remove legitimate parenthetical text, or prevent explicitly proposed new names.

Production configuration: `GraphRag:Ingestion:UseStructuredTaxonomyNames` (default false).
The flag participates in extraction-cache identity; normal unchanged-document skipping
still applies, so force rebuilding is needed to apply it to already ingested content.

### Policy relationship vocabulary

`--relations policy --response schema` enables the opt-in
`GraphRag:Ingestion:UsePolicyRelations` experiment. It adds an 18-type enum to
combined/graph relationship schemas and actor/action/object, direction and obligation
instructions. It uses the existing from/type/to/description fields and SQL edge model;
no additional extraction pass or schema migration is introduced. The taxonomy-only
request is unchanged. Do not combine this flag with UseExtractionRelationGuidance.
The policy flag and guidance participate in extraction-cache identity.

`corpus-policy-v2.json` uses the same complete documents, instructions, retrieval
questions and original nine labels as v1. It adds two positive obligation labels and
one selected negative check for upgrading a recommendation to a requirement. Use v2
for both control and policy runs. Labels are not included in prompts. Selected negative
checks and description regexes do not establish general obligation accuracy; absent
edges can pass a negative check while failing positive coverage. This vocabulary is
policy-specific and remains disabled by default for general ingestion.

### Separate policy obligation experiment

`--relations policy-obligation --response schema` adds a required obligation enum
(required/recommended/permitted/prohibited/none) to each graph relationship. It keeps
USES as an action type and removes REQUIRES/RECOMMENDS from the action enum. Explicit
prohibition must use both PROHIBITS and obligation=prohibited; invalid or missing
metadata is rejected through the existing extraction retry/failure behavior.

Production flags are UsePolicyRelations=true and UsePolicyObligations=true under
GraphRag:Ingestion; both remain opt-in. This variant adds guidance/schema output, not a
second extraction pass. It may still incur existing retries on invalid output.

For this experiment only, SQL Description stores the source description followed by
`[GraphRAG obligation v1: value]` on a new line. There is no database migration or new
queryable obligation column. Other readers may display the suffix; this is not yet a
production storage contract. Eval Graph edges decode that suffix into a separate
Obligation property and exclude it from Description used by fixed factual checks.
Consequently the tag alone cannot satisfy a required-wording test. Raw extraction JSON
and persisted qualifier values remain available for separate audits. Existing graph
merging still keys by endpoints/type, so conflicting qualifiers across sources are not
independent assertions; that needs separate design before production adoption.
