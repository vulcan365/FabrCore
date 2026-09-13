# GraphRAG ingestion architecture and performance review

Reviewed September 5, 2026. Findings are from source inspection and deterministic
tests, not production profiling. Production speedup is not yet measured.

## Assessment

FabrCore follows a legitimate graph-augmented retrieval design: retain source text
and embeddings, extract entities and typed relationships with an LLM, resolve
identities, then persist scoped evidence. SQL VECTOR storage does not require
changing to adopt a better extraction pipeline. The extraction contract and work
scheduling deserve attention before a database replacement.

Microsoft Standard GraphRAG also extracts entities and relationships from text
units, but collects descriptions when merging repeated entities and relationships.
It then summarizes those descriptions. FabrCore's combined extraction avoids some
of those extra LLM passes, but its current merge loses evidence.
[Microsoft dataflow](https://microsoft.github.io/graphrag/index/default_dataflow/).

Microsoft FastGraphRAG uses noun phrases and co-occurrence relationships to reduce
LLM work. Its documentation explicitly describes a noisier graph and recommends
standard extraction when high-fidelity entities and graph exploration matter.
Co-occurrence cannot replace a factual DEPENDS_ON or AUTHORED_BY edge.
[Indexing methods](https://microsoft.github.io/graphrag/index/methods/).

LazyGraphRAG defers LLM work until query time and builds a concept/co-occurrence
index. This is an architectural alternative for question answering, but does not
fulfill a requirement for fully enriched factual nodes and edges at ingestion.
Its published cost results are workload-specific, not a FabrCore speed estimate.
[Microsoft Research](https://www.microsoft.com/en-us/research/blog/lazygraphrag-setting-a-new-standard-for-quality-and-cost/).

## Baseline findings in KnowledgeIngestionService

These findings describe the original reviewed pipeline. The follow-up implementation
below addresses source windows, repeated taxonomy work, and incomplete extraction;
identity/evidence merging and automatic artifact versioning remain open.

| Finding | Consequence | Proposed direction |
| --- | --- | --- |
| Retrieval chunks target 500 characters with 100 characters of overlap; the same chunks feed extraction | Repeated overlap text enters prompts; extraction boundaries are tied to retrieval granularity | Independently build extraction windows from source sections, preserving source offsets and mapping to retrieval chunks |
| Every extraction batch receives all domains/categories and returns classification | Repeated taxonomy tokens and output; first non-null batch classification wins | Retrieve a small candidate taxonomy set and classify once per document, allowing multiple categories when appropriate |
| Prompt requests ALL meaningful entities with broad types and prose descriptions | Large output and extraction of concepts that may never support retrieval | Define source-specific extraction profiles and compact, evidence-backed output; retain coverage for required types |
| Entities merge by name only; later batches overwrite earlier descriptions | Same-name different-type entities collide and earlier facts disappear | Merge by type and normalized identity, preserve mentions/evidence, resolve aliases carefully |
| Relationships are deduplicated by source/target/type, retaining the first | Later supporting descriptions are discarded | Aggregate source evidence without an LLM; summarize only where needed |
| Extraction output lacks chunk/span references | Graph evidence is linked to the document, but cannot precisely identify the supporting passage | Emit source span IDs for each entity mention and relationship |
| Retry depth 2 can produce 7 calls for each original batch | A larger prompt may cost more once split retries occur | Track truncation and output sizes; benchmark batch size and output budget together |
| Exhausted extraction errors can return partial/empty results and still complete ingestion | Reduced latency or call count can conceal missing graph coverage | Track explicit graph extraction completeness separately from successful chunk indexing |
| Reuse checks content and instruction hashes, not extraction model/schema version | A changed extraction pipeline can leave old results marked reusable | Version extraction artifacts and include version/model/prompt in cache invalidation |
| Changed documents regenerate all embeddings/extraction | Minor revisions repeat expensive work | Persist per-window extraction and embedding artifacts keyed by scope, content hash, model, prompt/schema version, and relevant classification context |

The code already provides parallel chat batches, bounded chat concurrency,
document reuse for identical content/instructions, batched SQL writes, and short
write transactions after all network operations. These should be retained.
SQL orphan cleanup and taxonomy resolution still contain per-item round trips;
profile their contribution before changing transaction and provenance semantics.

## Implemented improvements

- Embedding batches execute concurrently instead of serially, under the existing
  MaxEmbeddingConcurrency setting.
- A service-wide semaphore bounds batch and fallback calls across documents and
  chunk/entity embedding stages. This is per service instance, not distributed
  provider rate limiting.
- Identical strings within an embedding operation share a vector generation;
  original ordering and every output position are retained.
- Partial remote batches preserve successful vectors and retry only missing ones.
- Remote single-item calls now receive cancellation; queued work is cancellable.
  Local IEmbeddings calls cannot be interrupted through the current interface.

No SQL schema/vector changes, extraction omissions, or graph feature reductions
were introduced. These improvements mainly help documents with multiple embedding
batches, repeated text, or partial remote responses. A document already fitting in
one successful embedding batch will not gain batch parallelism.

## Follow-up implementation

The default document plan now builds lossless source sections targeting 2000
characters and batches up to 8 sections, also subject to the existing estimated
input-token budget. Retrieval chunking and SQL vectors are unchanged. Unlike the
retrieval splitter, source sections preserve punctuation, whitespace, and Unicode
surrogate pairs exactly and do not prepend overlap text.

Short documents retain a single combined LLM call. Long documents use compact
graph-only calls and one concurrent document classifier. Classification samples
up to 32 evenly distributed sections and 8000 source characters; it does not see
the entire long document. Graph extraction sees all source text. The full taxonomy
is still supplied to the classifier; candidate retrieval is future work.

This is a token-volume optimization, not a universal call-count reduction: long
documents add one classifier call. A deterministic 100-paragraph comparison
verifies lower total prompt character volume without increasing calls for that
specific fixture. It does not measure provider tokenization, latency, or quality.
Large taxonomy inventories and low context budgets still require tuning.

The document plan refuses completion when a graph retry tree has failed leaves,
the classifier has no usable result, or an enabled extraction model is unavailable.
Both the embedding and extraction tasks are observed before Phase 1 exits. A
failure occurs before graph writes and follows the existing Failed document path.
Empty graph arrays are valid; missing arrays, filtered output, and truncated output
are not accepted as complete graph extraction. Classification is attempted once;
a classification failure requires retrying the document.

UseDocumentExtractionPlan=false preserves the legacy path for comparisons, including
its best-effort failure behavior. MaxChunksPerExtractionBatch applies to that legacy
path; the new path uses MaxSectionsPerExtractionBatch. ForceReingestion on the service
request supports explicit rebuilds after configuration changes while retaining the
document ID. Automatic model/prompt/schema cache invalidation is still future work.

Evaluate factual edge recall near extraction batch boundaries and category accuracy
for long mixed-topic documents. Removing overlap and sampling classification can
change results even though every source character is preserved for graph extraction.

## Recommended next design

1. Parse source structure and stable identifiers deterministically. Build retrieval
   chunks and independent extraction sections with explicit source positions.
2. Embed every retrieval chunk in batches. Reuse unchanged scoped artifacts.
3. Extract typed entities and factual edges together with a compact schema from
   each extraction section. Use a small model only after evaluating its quality;
   escalate failed or ambiguous sections rather than every section.
4. Resolve names/types/aliases and aggregate evidence deterministically. A local
   entity recognizer can supply candidates, but should not silently exclude
   business concepts or relationships that require semantic interpretation.
5. Classify once per document against retrieved domain/category candidates.
   Reuse deterministic source mappings where trustworthy and allow an unknown or
   new-category outcome. For short documents, include this in the single extraction
   call; for long documents, classify from section results in one bounded step.
6. Write vectors, nodes, edges, and provenance in short transactions. Expose
   separate searchable and graph-complete states if background enrichment is used.
   Background work reduces time to first search, not total extraction work.

This is a proposed evolution, not a claim that these changes are implemented or
universally superior. Avoid switching to noun co-occurrence if typed factual edges
are part of the product contract.

## Measurement and acceptance

Use a representative corpus of emails, short documents, long documents, repeated
boilerplate, and small revisions. Use separate test scopes: identical completed
documents short-circuit ingestion and invalidate a timing comparison.

Collect existing grag.IngestionMetric fields: DurationMs, LlmExtractionMs,
ChunkEmbeddingMs, EntityEmbeddingMs, SqlWriteMs, ChatCallCount, ChatInputTokens,
ChatOutputTokens, ExtractionRetryCount, ExtractionTruncationCount, and graph counts.
Report p50/p95 latency, calls/tokens per document, provider throttling, and throughput.
Phase timings overlap; do not sum them to infer wall-clock latency.

Compare the existing extraction baseline with independent section windows and
single document classification. Test chunk limits 16/32/64 with appropriate output
budgets; no single value is optimal across document types and models. A smaller
retry budget must not be accepted merely because it drops failed graph sections.

Manually label required entities, typed relationships, categories, and supporting
passages for a sample. Measure precision/recall and downstream retrieval quality;
entity count alone is not a quality metric. Include same-name different-type
entities, aliases, contradictions, and scope isolation.

Validation of the implemented changes: all 55 GraphRAG unit tests pass, including
new tests for parallel batches/shared limits, duplicate ordering, queued
cancellation/permit reuse, out-of-order partial remote responses, lossless source
sections, classification call isolation, single-call short documents, partial retry
tree rejection, missing-model behavior, and comparative prompt volume. A forced
rebuild SQL integration test was added but cannot run without database credentials.
Live SQL and provider latency/quality evaluations have not been run.
