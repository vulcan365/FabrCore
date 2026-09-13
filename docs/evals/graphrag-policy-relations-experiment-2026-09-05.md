# Policy relationship vocabulary experiment, 2026-09-05

The constrained policy variant retained 8/9 original typed facts in both runs versus
7/9 and 6/9 for controls. It averaged 19.984s versus 17.923s (11.5% longer), made the
same seven LLM calls, and still omitted a labeled recommendation. Keep it opt-in;
this is a small-sample quality signal, not a performance win or complete quality gate.

## Code

`GraphRag:Ingestion:UsePolicyRelations` (default false), selected in the console by
`--relations policy --response schema`, adds an 18-type enum to graph and combined
response schemas. Generic actor/action/object direction and obligation instructions
accompany it. The existing from/type/to/description representation and SQL VECTOR
storage remain unchanged. It does not add a second extraction pass or LLM repair.
Taxonomy-only prompts/schemas are unchanged by this option. The flag requires schema
responses and cannot combine with UseExtractionRelationGuidance. Guidance and flag
participate in extraction-cache identity. Unchanged sources still require an explicit
rebuild to apply different extraction settings.

The vocabulary is ESTABLISHES, USES, PART_OF, PUBLISHED_BY, AUTHORED_BY, REFERENCES,
REQUIRES, RECOMMENDS, PROHIBITS, RESPONSIBLE_FOR, IMPLEMENTS, REPORTS_TO, FUNDS,
GOVERNS, DEFINES, DEPENDS_ON, ALIAS_OF and RELATED_TO. It is policy-specific; a closed
vocabulary may be inappropriate for other corpora. Schema conformance does not ensure
correct selection of a relationship type or retention of a required fact.

## Fixed corpus and labels

corpus-policy-v2.json preserves all v1 source URLs, full content, instructions, retrieval
questions and original nine fact labels. Two positive labels were added before running:
agencies must use machine-readable/open formats, and agencies should prioritize
non-proprietary open formats. A selected negative check rejects mandatory-use wording
for the latter recommendation. All evidence text was verified against the cached full
sources with whitespace normalization. Labels are not included in extraction prompts.

There are now 11 positive and one negative check. Names, direction, type and selected
description qualifiers must match; alternative representations are diagnostics and
receive no credit. Negative-check success alone does not establish precision: omission
of the positive edge passes the negative check. These regex checks do not fully assess
conditions, exceptions, polarity, or every responsibility in the documents.

## Live method and results

Four fresh-scope passes in order current, policy, policy, current on localhost/graphrag.
Mini, full policy corpus (59,152 characters), two concurrent documents, shared LLM limit
four, strict schema, eight sections per extraction batch, taxonomy-names current and
endpoint aliases off. Result, taxonomy and embedding caches were off. No database
creation, cleanup or taxonomy migration was performed. Batch wall time excludes
warmup/download/schema setup and final graph/retrieval validation.

| Variant | Corpus seconds | Original facts | All positive facts | Negative checks | Output tokens |
| --- | ---: | ---: | ---: | ---: | ---: |
| current | 16.835 | 7/9 | 8/11 | 1/1 | 11818 |
| policy | 21.014 | 8/9 | 8/11 | 1/1 | 12529 |
| policy | 18.954 | 8/9 | 9/11 | 1/1 | 13029 |
| current | 19.010 | 6/9 | 6/11 | 1/1 | 11022 |

Every pass completed all three documents, retained 163 embedded chunks and returned
the correct document at rank one for all three queries. All passed smoke gates, used
seven LLM calls and peaked at four simultaneous calls. No SQL/provider failures were
reported. Policy output averaged 12,779 tokens versus 11,420 for control (11.9% more).
This experiment neither reduced calls nor output volume.

## What improved and what failed

Both policy runs retained Federal Government ESTABLISHES Data.gov and M-16-21
PUBLISHED_BY OMB, both absent as matching typed facts in both controls. One policy
run missed the common-metadata USES edge; the other missed Project Open Data USES
GitHub. All labels and scoring stayed fixed throughout the matrix.

The first policy run emitted `agencies REQUIRES common core metadata` and `agencies
REQUIRES machine-readable and open formats`. Their descriptions preserved the must-use
requirement, but their predicate did not follow the requested actor USES resource
contract. These were retained as failed typed checks, not reclassified as successes.
The second policy run used USES correctly for both required resources.

None of the four runs retained the separately labeled non-proprietary-format
recommendation. A raw memo-response inspection found no relationship mentioning
non-proprietary wording. The negative check passed without demonstrating preservation
of that recommendation. This is a substantive coverage gap alongside type ambiguity.

Two observations per variant are insufficient for statistical adoption or a quality
non-regression claim. Provider prompt-cache inputs were 0, 2,304, 11,776 and 12,544
respectively; shared taxonomy and provider timing remain potential order effects.
The reversed order helps but is not an identical-response replay or isolated prompt
component experiment. We changed enum and guidance together and cannot attribute the
observed coverage gain to either independently.

## Validation and next decision

94 unit tests passed, including live-service test doubles confirming enum/guidance
routing to combined and graph calls only, no added calls, and obligation checks that
reject reversed actors and upgraded recommendation wording. Four live passes completed
with unchanged chunk/retrieval smoke success. Production defaults remain unchanged.

Continue with mini and two concurrent documents. A useful follow-up is to separate
obligation strength into its own response field, leaving relationship type to express
the action (such as USES). That could reduce REQUIRES-versus-USES ambiguity, but needs
fresh tests for qualifier persistence and the currently missing recommendation. Do not
weaken labels or accept lower output volume when required facts disappear.

## Reproduction and artifacts

Run VARIANT as current, policy, policy, current:

```powershell
dotnet run --project src/FabrCore.Services.GraphRag.EvalConsole -- run --model graphrag --manifest src/FabrCore.Services.GraphRag.EvalConsole/corpus-policy-v2.json --max-chars 0 --response schema --sections-per-batch 8 --document-concurrency 2 --taxonomy-names current --relations VARIANT --endpoint-aliases off --result-cache off --taxonomy-cache off --embedding-cache off --reingest fresh --iterations 1
```

Summary: artifacts/graphrag-evals/policy-relations-experiment-2026-09-05.json.
Full report.json and report.md under artifacts/graphrag-evals:

- `20260906T024049-479b1b3b3bb740688affbfeb99e6b604` (current)
- `20260906T024109-7346cb42c3da4b209745003f880d4c34` (policy)
- `20260906T024133-88fc925d8f124932aba8f4c324cecad0` (policy)
- `20260906T024154-e92ac3ee0b8043ec8bf5e96104980800` (current)
