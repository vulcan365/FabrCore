# Government policy corpus ingestion - 2026-09-05

Tested three complete government policy documents, totaling **59,152 characters**.
Both fresh-scope passes completed and stored **163 embedded retrieval chunks**.
Ingestion took **26.287 and 29.013 seconds** (27.650-second mean), with seven LLM
calls per pass. Retrieval passed, but only **4/9 and 5/9** selected typed facts were
retained. This is a usable new evaluation corpus, not evidence that graph quality is solved.

## Documents and provenance

| Document | Characters | Retrieval chunks |
| --- | ---: | ---: |
| M-13-13 Open Data Policy memo | 44,406 | 123 |
| Project Open Data Governance | 6,397 | 18 |
| CISA open-source policy | 8,349 | 22 |

Source Markdown was downloaded directly, without conversion, and ingested in full,
including front matter and inline HTML. The sources are pinned to repository commits:

- [Open Data Policy memo](https://github.com/project-open-data/project-open-data.github.io/blob/f136070aa9fea595277f6ebd1cd66f57ff504dfd/policy-memo.md).
- [Project Open Data Governance](https://github.com/project-open-data/project-open-data.github.io/blob/f136070aa9fea595277f6ebd1cd66f57ff504dfd/governance.md).
- [CISA open-source policy](https://github.com/cisagov/development-guide/blob/c22ca838ba50b8f5e7dc44b79ca0515c48497a6a/open-source-policy/policy.md).

These are historical/repository snapshots selected for repeatable ingestion tests.
The evaluation does not establish their current policy applicability. Source hashes
and exact URLs are retained in the report and corpus manifest.

## Setup

Existing localhost/graphrag database; gpt-5.4-mini via graphrag; schema-enforced JSON;
2,000-character extraction sections; section batch limit eight; existing concurrency
four and 1536-dimensional text-embedding-ada-002 embeddings. All application extraction,
taxonomy and embedding caches and experimental endpoint-alias resolution were disabled.
Two passes use separate fresh scopes. Provider prompt caching remains observable.

Added optional ExtractionInstructions to corpus entries and recorded effective instructions
in each document result. The new corpus requests policies, laws, organizations, programs,
roles and explicit responsibilities, preserving direction and obligation strength. Existing
RFC manifests retain their prior technical-standards guidance. This is a content-and-guidance
change, so speed and factual scores should not be compared directly with RFC benchmarks.

The nine positive labels and three retrieval questions were fixed before calling the model.
Each label's evidence was checked in the pinned source first and remained present in both
runs. Labels are not passed to the model. There are no selected negative labels in this
initial policy set. Only the metadata-use fact explicitly checks requirement language in
the edge description; this is not comprehensive evaluation of policy modality or exceptions.

## Results

| Pass | Seconds | LLM calls | Input tokens | Output tokens | Provider cached input | Entities | Persisted edges | Required facts |
| --- | ---: | ---: | ---: | ---: | ---: | ---: | ---: | --- |
| 1 | 26.287 | 7 | 18,840 | 11,129 | 0 | 147 | 113 | 4/9 |
| 2 | 29.013 | 7 | 18,949 | 11,572 | 6,144 | 135 | 102 | 5/9 |

Every document completed. Each pass had Recall@3=100%, MRR@3=1.0, all 163 chunks
embedded and actual measured chat calls matching SQL ChatCallCount. These smoke gates
do not require all labeled facts, so both runs pass despite missing relationships.

| Required fact | Pass 1 | Pass 2 |
| --- | --- | --- |
| Federal Government established Data.gov | Missing typed edge | Missing |
| Federal CIO tasked with establishing an inter-agency working group | Matching raw edge, missing endpoint entity | Retained |
| Agencies required to use common core metadata | Missing | Missing |
| OMB is part of the Executive Office of the President | Retained | Retained |
| OSTP is part of the Executive Office of the President | Retained | Retained |
| Project Open Data uses GitHub | Retained | Alternative representation |
| CISA uses FOSS | Retained | Retained |
| CISA/default license uses CC0 | Alternative predicate | Retained |
| OMB published M-16-21 | Alternative predicate | Alternative predicate |

The model wrote that the Federal Government launched Data.gov but attached USES rather
than ESTABLISHES in pass 1. The license/CC0 description said uses while the predicate was
REFERENCES. Both runs emitted AUTHORED_BY for a description saying M-16-21 was published
by OMB. These mirror the predicate-contract problems found in the RFC corpus; a plausible
edge description is not sufficient for a correctly typed graph.

## New taxonomy findings

All six classifications explicitly selected Engineering in the raw model response; SQL
did not substitute that domain. This suggests strong influence from the existing taxonomy,
although some software policy could reasonably sit under that broad domain. It warrants
a separate test of selecting a new policy/governance domain instead of forcing broad reuse.

The first memo created Open Data Policy and Information Governance. The governance document
then returned Open Data Policy and Information Governance (in Engineering) as the category
name. The combined extraction prompt displays categories with this parenthesized domain
annotation, and the model copied it into the name. The second variant was persisted and
reused. Taxonomy row count therefore rose from seven at pass 1 start to nine at pass 2 start.
This demonstrates a category display-label leak and duplicate variant; no existing rows
were renamed or deleted during this experiment. Fresh scopes do not isolate shared taxonomy.

A useful next experiment is structured taxonomy options with separate name and domain
fields plus exact reuse validation, followed by testing appropriate new-domain selection.
Do not silently strip parenthetical text from arbitrary real category names.

## Reproduction and artifacts

```powershell
dotnet run --project src/FabrCore.Services.GraphRag.EvalConsole -- run --model graphrag --manifest src/FabrCore.Services.GraphRag.EvalConsole/corpus-policy-v1.json --max-chars 0 --response schema --sections-per-batch 8 --endpoint-aliases off --result-cache off --taxonomy-cache off --embedding-cache off --reingest fresh --iterations 2
```

- Manifest: src/FabrCore.Services.GraphRag.EvalConsole/corpus-policy-v1.json.
- JSON/Markdown report: artifacts/graphrag-evals/20260906T012005-b7872ba2ead047c69f7c35eb96c9188b.
- Source Markdown cache: artifacts/graphrag-corpus (URL-hashed filenames).
- Per-document report data includes source hashes, extraction instructions, raw responses,
  graph snapshots, chunk-vector fingerprints, factual checks and stage traces.

Build succeeded with no warnings, **87 unit tests passed**, and scoped whitespace checks
passed. No production extraction defaults or database schema were changed. The UTC report
ID is September 6; the local experiment date is September 5.
