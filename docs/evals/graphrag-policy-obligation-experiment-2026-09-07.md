# Separate policy obligations experiment, 2026-09-07

Do not enable this variant on the ingestion fast path. Mean batch time was 46.434s
versus 28.809s for today's completed policy controls (61.2% longer). Both new runs
needed nine chat calls instead of seven because a contradictory obligation caused
CISA extraction to split and retry. Qualifier persistence worked, but the two added
obligation facts were still missing from both new runs.

## Implementation

Console option: `--relations policy-obligation --response schema`.
Production flags: `GraphRag:Ingestion:UsePolicyRelations=true` and
`GraphRag:Ingestion:UsePolicyObligations=true`. Both remain opt-in.

Each graph/combined relationship gets obligation=required/recommended/permitted/
prohibited/none. REQUIRES and RECOMMENDS are removed from the action enum; USES
expresses the action, with its obligation in the new field. Explicit prohibitions
retain PROHIBITS and obligation=prohibited. Missing/unknown values, unsupported
action types and mismatched prohibition fields are rejected by the parser through
the existing bounded retry/failure path. Taxonomy-only output is unchanged.
The flag and new guidance participate in extraction-cache identity and input budgeting.

For the experiment, persistence appends a newline and `[GraphRAG obligation v1: value]`
to the source description. No database migration or separate queryable column was
introduced. Eval SQL read-back decodes the suffix into EdgeSnapshot.Obligation and
removes it from the description used by factual scoring. A required tag therefore
cannot make a required-wording check pass by itself. Other consumers may display the
suffix; this is experimental storage, not a production data contract. Existing merge
keys remain endpoints/type, so conflicting qualifiers across documents are not stored
as independent assertions. That remains a design limitation before production adoption.

## Fixed controls

Same full policy-v2 corpus, 59,152 characters and 163 retrieval chunks, mini alias,
two concurrent documents, shared chat limit four, eight sections per batch, schema
responses, taxonomy-names current, endpoint aliases off, and all application caches
off. The original nine labels and the additional two positive/one negative obligation
checks were unchanged and were never sent to the model. No LLM judge was used.
Batch times exclude setup/warmup and final retrieval/graph verification.

The intended order was policy, policy-obligation, policy-obligation, policy. The last
control finished ingestion but failed a report write before retrieval. It is excluded
from completed comparison statistics and retained as an incomplete artifact. A fresh
replacement policy control was run after fixing bounded report-write retries. This
interruption and replacement are part of the experimental history, not a hidden discard.

| Variant | Batch seconds | Calls | Original facts | All positive facts | Output tokens |
| --- | ---: | ---: | ---: | ---: | ---: |
| policy | 30.402 | 7 | 7/9 | 8/11 | 13511 |
| policy-obligation | 50.536 | 9 | 7/9 | 7/11 | 17966 |
| policy-obligation | 42.331 | 9 | 8/9 | 8/11 | 15491 |
| policy | 27.215 | 7 | 5/9 | 5/11 | 12301 |

Every completed pass retained all 163 embedded chunks, returned the correct document
at rank one for all three queries, and peaked at four simultaneous chat calls. Positive
fact checks remain separate from those smoke gates. The selected negative check passed
in every completed run; this does not prove recommendation fidelity when the positive
recommendation edge is absent.

The new variant averaged 16,728.5 output tokens versus 12,906 for completed controls
(29.6% more), including rejected responses. Neither LLM-call count nor output volume
improved. Provider cached-input tokens were 0 and 18,688 in the new runs, and 0 and
11,776 in completed controls. Two observations per variant with provider timing,
shared taxonomy and a replacement control cannot establish a statistically reliable
quality or latency estimate. Compare against today's controls, not older 18-20s runs.

## Failure analysis and persistence audit

Both CISA combined responses had valid JSON and enum values but contradicted the
cross-field contract. One emitted RELATED_TO with obligation=prohibited for an exception
about government release rights. The other emitted PROHIBITS with obligation=required
while describing a requirement to publish. The parser rejected these; the existing
retry split CISA into two smaller extraction calls. Each run consequently used nine
chat calls. Invalid JSON formatting was not the cause.

All 175 and 138 retained graph-edge contributions in the new runs had decoded qualifiers.
Every one of these 313 descriptions and qualifiers matched a raw response relationship
with the same directed endpoints and type from that document (case-insensitive names).
This demonstrates round-trip preservation of retained assertions, not correctness of
the qualifier or preservation of every proposed edge. Missing endpoints and graph
merging still affect which assertions survive.

Both new runs retained agencies USES common core metadata with obligation=required and
required wording in the source description. Neither passed the mandatory open-formats
label or the non-proprietary-format recommendation label. Their original fact coverage
was 7/9 and 8/9 versus 7/9 and 5/9 for controls, but this small signal is insufficient
for adoption given missing facts and substantial retry cost. We did not relax labels
or count metadata words as evidence to improve these scores.

## Validation and decision

97 unit tests passed. New checks exercise schema-required qualifiers, parsing and
storage round trips for all five values, rejection of missing/invalid/mismatched fields,
and scoring that excludes generated metadata. A Windows file-sharing regression test
also verifies bounded report retries. ReportWriter retries transient IO/access failures
up to four times with short backoff; persistent failures still surface. The observed
UnauthorizedAccessException is consistent with transient file contention, but its exact
external cause was not established.

Four live runs completed embedding/retrieval gates. A fifth run remains incomplete
because of the report-write failure; its partial graph checkpoint must not be interpreted
as a complete quality result or missing stored chunks. Source scopes were retained.

Keep mini, two concurrent documents, and the currently selected production extraction
settings. The new policy-obligation variant remains disabled by default. The next
performance experiment should address retry granularity: whether a bounded repair of
invalid assertions can preserve valid extraction work instead of regenerating a whole
batch. Required facts and qualifier consistency must still be checked; silently dropping
or coercing contradictory edges is not an acceptable speed improvement.

## Reproduction and artifacts

Run VARIANT as policy, policy-obligation, policy-obligation, policy:

```powershell
dotnet run --project src/FabrCore.Services.GraphRag.EvalConsole -- run --model graphrag --manifest src/FabrCore.Services.GraphRag.EvalConsole/corpus-policy-v2.json --max-chars 0 --response schema --sections-per-batch 8 --document-concurrency 2 --taxonomy-names current --relations VARIANT --endpoint-aliases off --result-cache off --taxonomy-cache off --embedding-cache off --reingest fresh --iterations 1
```

Summary: artifacts/graphrag-evals/policy-obligation-experiment-2026-09-07.json.
Full reports under artifacts/graphrag-evals:

- `20260907T132242-640b5a86552e4619a6ac7cc4e35eeae0` (policy; completed)
- `20260907T132316-60320b2e22424eb9838da7f8f222bbd9` (policy-obligation; completed)
- `20260907T132410-da2ce92d5144412aac02083b2b74712f` (policy-obligation; completed)
- `20260907T132455-cd99b8964ceb4099910d365c611b504c` (policy; incomplete, excluded)
- `20260907T132722-b2c67f0f98d04413b6d426bd89fae78e` (policy; completed)
