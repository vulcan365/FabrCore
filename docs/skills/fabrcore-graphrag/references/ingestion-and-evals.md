# Ingestion and evaluation reference

Published snapshot: 2026-09-07. Use this reference for ingestion configuration,
performance diagnosis, cache integration, or resuming evaluations. It describes
the current implementation; experimental availability is not a production endorsement.

## Pipeline and readiness

The document plan is enabled by default. Retrieval chunks (500 characters with
100-character overlap) are separate from non-overlapping extraction sections
(2,000 characters by default). Do not optimize LLM batching by changing retrieval
chunk size without independently checking retrieval quality.

A short document uses combined graph/taxonomy extraction. Longer documents use
graph batches and a separate sampled taxonomy classification. Chunk/document
embeddings and extraction overlap; extracted entities are embedded before graph
writes. Enabled document-plan extraction that exhausts its retry path fails rather
than treating partial extraction as a complete graph. Inspect the returned status.

Persistence requires resolvable endpoint names. Raw relationship counts can exceed
persisted facts. Current merging is not a complete assertion/provenance conflict
model: shared endpoints/type can collapse different descriptions or qualifiers.
Domains/categories are shared taxonomy, even across fresh evaluation scopes.

## Actual defaults

All keys below are under `GraphRag:Ingestion` unless noted. The minimal bundled
appsettings asset is valid but intentionally does not enumerate every setting.

| Key | Default | Meaning |
| --- | --- | --- |
| EnableExtraction | true | Set false explicitly for source/chunk ingestion without LLM graph extraction |
| UseDocumentExtractionPlan | true | Separate extraction sections from retrieval chunks |
| ExtractionSectionSizeChars | 2000 | Extraction section size; clamped to 256–16000 |
| MaxSectionsPerExtractionBatch | 8 | Document-plan batch limit; 1–256 |
| MaxChunksPerExtractionBatch | 32 | Legacy extraction path limit |
| MaxConcurrentChatCalls | 4 | Shared across documents on one ingestion instance; 1–16 |
| MaxEmbeddingConcurrency | 4 | Shared embedding limit; 1–16 |
| EmbeddingBatchSize | 128 | Provider input batch size; 1–512 |
| MaxExtractionRetryDepth | 2 | Bounded split retry depth; 0–4 |
| EmailExtractedEntityLimit | 60 | Email entity cap; 1–100 |
| UseExtractionJsonSchema | false | API-enforced JSON schema; requires local chat-client service |
| ExtractionDescriptionTargetChars | 0 | No compact-description target; a positive target requires schema |
| ExtractionInputTokenBudget | unset | Resolved from model configuration or service fallback |
| ExtractionMaxOutputTokens | unset | Optional output limit, not a speed guarantee |

`extractionModelName` selects an explicit alias. If omitted, aliases `graphrag` and
then `default` are tried. Omitting it does not turn off extraction. Resolve the
consumer's actual deployment configuration rather than assuming an alias denotes
the same model everywhere. Schema extraction is unsupported over the Host API
fallback, which does not carry response schemas.

## Scheduling and measured shortlist

In the local evaluations, mini (`graphrag` mapped to `gpt-5.4-mini`) was the useful
model choice. Nano and Luna did not justify replacement. Eight sections per batch
remained the preferred tested setting. Schema responses reduced formatting risk,
but do not establish entity/edge correctness.

Two concurrent documents reduced policy corpus batch time from about 25.5s to
16.9s in a same-session comparison; three added little. This is an exploratory
throughput result, not a universal latency guarantee or automatic production setting.
Use bounded workers sharing the registered singleton. Do not construct one ingestion
service per document, which multiplies request limits. A process-level singleton
does not impose a cross-process/provider-wide limit.

The bundled worker is a legacy serial/unbounded-queue example, not the tested bulk
ingestion implementation. Adapt its API call to `KnowledgeIngestionRequest`, choose
bounded queue capacity/backpressure for the consumer, and add cancellation/status
handling when using it as a starting point. No production `DocumentConcurrency`
configuration key is currently supplied; `--document-concurrency` belongs to the
eval console. Keep SQL graph and `VECTOR(1536)` storage.

## Three different kinds of reuse

1. **Unchanged-document reuse:** same source identity, content and instructions can
   return `Reused=true`. `ForceReingestion=true` deliberately bypasses that check.
   Changing a model or processing flag alone does not automatically rebuild content.
2. **Extraction-result reuse:** `UseExtractionResultCache=true` enables the optional
   process-local `ExtractionResultCache` (256 entries, 30-minute TTL by default).
   It stores validated local extraction results. `CacheTaxonomyResponses=true`
   extends reuse to combined/classifier responses with exact taxonomy context and
   only takes effect when the result cache is enabled. The service uses a supplied
   singleton cache or creates its own instance. Host API extraction is not this cache path.
3. **Embedding reuse:** `CachedEmbeddings` wraps an existing `IEmbeddings` with an
   `EmbeddingResultCache` (8192 vectors, 30-minute TTL by default). The caller must
   supply an explicit database/tenant/scope namespace, model identity and dimensions.
   Do not globally decorate multiple scopes using one arbitrary scope value. There
   is no automatic production `UseEmbeddingCache` config switch.

Cached chat-client instances are not cached completions. Provider prompt caching is
separate from all three mechanisms and still generates output. Caches are in-memory,
bounded, and do not establish durable or cross-instance reuse/coalescing.

Fully cached forced repeat rebuilding reached approximately 0.54s with zero LLM and
embedding calls in the RFC experiments. This is not cold-ingestion performance;
representative edits and cache/concurrency interactions still need evaluation.

## Experimental options: all disabled by default

| Key | Contract / limitation |
| --- | --- |
| ResolveExtractionEndpointAliases | Resolves unique explicit parenthetical initialisms; no fuzzy matching or entity invention |
| UseStructuredTaxonomyNames | Separates taxonomy name metadata; unknown names marked as reused skip taxonomy assignment while retaining graph extraction; existing duplicates are not cleaned |
| UseExtractionRelationGuidance | Additional generic relation conventions; not a demonstrated adoption win |
| UseExtractionEvidence | Requires schema + document plan, excludes source-span mode; quote evidence does not itself establish semantic correctness |
| UseExtractionSourceSpans | Requires schema + document plan; uses source IDs |
| RepairExtractionEndpoints | Requires source spans; bounded endpoint repair, not targeted repair of arbitrary invalid relationships |
| UsePolicyRelations | Requires schema and excludes generic relation guidance; policy-specific action enum and instructions |
| UsePolicyObligations | Requires policy relations; separate qualifier field with parser consistency checks; not recommended for the fast path |

Policy vocabulary improved selected fact coverage but increased latency in small
trials. Separate obligation fields caused contradictory CISA output and split retries:
about 46.4s versus 28.8s for same-day policy controls, nine rather than seven calls.
All 313 retained qualifiers round-tripped, but required facts were still missing.

Obligation storage currently appends `[GraphRAG obligation v1: value]` to Description.
There is no new obligation column or complete per-source assertion model. Eval
read-back separates this suffix and excludes it from factual description checks.
Other readers may display it. Do not promote this experimental envelope as the
recommended production schema.

## Evaluation runner and paused checkpoint

`FabrCore.Services.GraphRag.EvalConsole` works without Blazor. It uses local
appsettings/model configuration and an existing SQL database. Credentials and API
keys do not belong in a published skill. The repository's paused local run used
`localhost/graphrag`; settings/artifacts may be ignored by Git and unavailable to
other consumers. Do not create a database or globally clear shared taxonomy as an
incidental eval step.

Example from the repository root, using explicit control settings:

```powershell
dotnet run --project src/FabrCore.Services.GraphRag.EvalConsole -- run --model graphrag --manifest src/FabrCore.Services.GraphRag.EvalConsole/corpus-policy-v2.json --max-chars 0 --response schema --sections-per-batch 8 --document-concurrency 2 --taxonomy-names current --relations current --endpoint-aliases off --result-cache off --taxonomy-cache off --embedding-cache off --reingest fresh --iterations 1
```

The full policy corpus has 59,152 characters and 163 retrieval chunks. V2 preserves
the original nine facts and adds two positive obligation labels and one negative
check. Use `--max-chars 0`; otherwise the default prefix truncates the long memo.
The fixed Markdown sources are historical snapshots, not current-policy assertions.

Read `IngestionWallMs` for parallel batch time, not the sum of document durations.
Per-document telemetry is isolated by async flow; final graph checks run after
writers settle. The console rejects enabled result/embedding caches with document
concurrency above one until hit attribution is improved. Build before `--no-build`
and avoid building while a live eval holds DLLs open on Windows.

A successful exit checks completion/embeddings/retrieval smoke behavior, **not all
required facts**. Inspect `FactChecks`, `FactTraces`, raw responses and persisted
edges. Do not weaken labels or count generated metadata as source evidence.
Negative checks can pass simply because the positive edge disappeared.

Evaluations were paused on 2026-09-07 after 97 passing unit tests and the obligation
comparison. No targeted assertion-repair implementation or final production load
validation is complete. Remaining work, in order:

1. Compare bounded repair of invalid assertions with full split/regeneration,
   preserving valid work and required facts; begin with captured failing responses.
2. Test realistic edits, cache freshness/isolation, and concurrent cache attribution.
3. Trace missing facts and conflicting qualifiers; evaluate taxonomy using isolated
   known candidates rather than resetting the shared database.
4. Validate a small shortlist on larger held-out technical/business/policy documents
   with precision, factual recall, obligation and exception checks.
5. Test mixed-document sustained load, tail latency, SQL overlap/races and recovery.
6. Run final regressions, select defaults explicitly, and document remaining limits.

In the source repository, [eval-status.md](../../../eval-status.md) holds the exact
run IDs, incomplete-control exclusion, commands and full handoff. It is supplementary:
the integration guidance here is usable when the skill is distributed alone. The
reported test count is a checkpoint, not a claim that tests ran during skill publication.
