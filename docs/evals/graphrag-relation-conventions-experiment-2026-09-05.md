# GraphRAG relationship convention experiment — September 5, 2026

## Outcome

Explicit relationship conventions are available as an opt-in experiment, but are
not reliable enough to enable by default. On the original three-document corpus,
current instructions averaged **26.923 seconds** and the defined conventions
averaged **25.302 seconds** (about 6% faster). Consistent required facts improved
only from **2/6, 2/6** to **3/6, 2/6**. Missing facts remain, and one defined run
emitted both the correct and reversed registration edge.

A new GZIP document completed in **5.347 seconds** with defined conventions versus
**9.908 seconds** for the control, with both required facts preserved. This single
pair also reduced entities from 42 to 26 and edges from 41 to 22; two checked facts
cannot establish that the rest of the reduction was safe. The second GZIP pass
benefited from an existing taxonomy entry. Do not treat this as a general 46%
speedup or a lossless optimization.

Production defaults remain unchanged.

## What changed

`GraphRag:Ingestion:UseExtractionRelationGuidance` defaults to false. Enabling it
prepends generic conventions to graph and combined extraction calls, defining:

- The direction of authorship, signing, publication, dependencies, use, components,
  and registration/establishment relationships.
- `PUBLISHED_BY` separately from `AUTHORED_BY`, and explicit `ALIAS_OF` relationships.
- Technical subjects as endpoints instead of using the source document as a
  substitute; conditions and optionality stay in descriptions.
- Citations as citations, without inferring dependencies from them.
- Consistent names and rejection of negated or speculative positive edges.

The rules contain no corpus-specific names or expected answers. They leave the
standalone taxonomy prompt unchanged, reserve space in the extraction input
budget, and add no calls. Existing graph data is not rewritten. The evaluator
exposes `--relations current|defined` and records the variant.

The new `corpus-relations-v2.json` keeps the same source text but corrects two
earlier label assumptions: publisher edges require `PUBLISHED_BY`, and the ABNF
reference is checked as RFC 4180 citing RFC 2234. It accepts explicit alias edges
and adds a negative check against a UUID dependency on central registration.
The previous manifest remains intact. Scores across label versions are not
directly comparable.

The evaluator now retains alternative endpoint/description candidates without
counting them as correct. It separates positive coverage from consistency:
`Passed` indicates the expected fact was found (or a selected forbidden edge was
absent), while `Consistent` also requires no reversed edge of the expected type.
Neither measure is comprehensive factual precision/recall. Smoke gate exit codes
remain separate from this quality checklist.

## Trial design and results

All runs used `default` / `gpt-5.4-mini`, reasoning disabled, strict JSON schemas,
no compact-description target, the document extraction plan, and the same
embedding and concurrency settings. Original-corpus order was current → defined
→ defined → current. Every pass used a fresh scope in `localhost/graphrag`.

The original corpus remained 71,291 characters and 179 retrieval chunks. The
additional document was the full 25,037-character
[GZIP specification, RFC 1952](https://www.rfc-editor.org/rfc/rfc1952.txt), producing
62 chunks. Its two labels check GZIP's use of DEFLATE and CRC32. The prompt was not
tuned against GZIP output. Only one pass per variant was collected on this document.

| Order | Corpus / variant | Seconds | Input tokens | Cached input | Output tokens | Entities | Edges | Positive facts | Consistent facts |
| --- | --- | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: |
| 1 | Original / current | 26.429 | 25,696 | 23,808 | 8,619 | 115 | 113 | 2/6 | 2/6 |
| 2 | Original / defined | 28.026 | 27,661 | 4,608 | 7,864 | 104 | 100 | 4/6 | 3/6 |
| 3 | Original / defined | 22.577 | 27,661 | 17,664 | 7,195 | 92 | 94 | 2/6 | 2/6 |
| 4 | Original / current | 27.417 | 25,696 | 23,808 | 7,847 | 109 | 94 | 2/6 | 2/6 |
| 5 | GZIP / current | 9.908 | 8,906 | 0 | 3,626 | 42 | 41 | 2/2 | 2/2 |
| 6 | GZIP / defined | 5.347 | 9,723 | 0 | 2,021 | 26 | 22 | 2/2 | 2/2 |

Edges exclude provenance links. All passes embedded every chunk and achieved
Recall@3=100% and MRR@3=1.000. Original passes each made seven chat calls; GZIP
passes each made three. The selected negative check passed in all four original
passes. There were no extraction retries. Setup, download, and embedding warmup
are outside ingestion timing. No competing live model tests ran.

Provider/cache variability was not controlled, and taxonomy was shared: initial
row count was six for the original passes and first GZIP pass, seven for the
second GZIP pass. These small exploratory samples do not establish statistical
significance or broad quality equivalence.

## What the quality review found

The first defined pass emitted both `IANA → ESTABLISHES → text/csv` and
`text/csv → ESTABLISHES → IANA`. The positive match remains visible, but the
consistency check rejects it. Direction diagnostics were added after this was
observed, regression-tested, and applied uniformly to all six saved reports.
Raw graphs, timing, and usage did not change.

Manual review also found a checker false negative: `GUID → ALIAS_OF → UUID`, with
the description "another name for", expressed the expected equivalence. The
description matcher was broadened to recognize that wording, "alternate name",
and "alias", regression-tested against the manifest, and reapplied uniformly.
The second defined run therefore has two consistent facts, not the initial one.
This scoring correction is documented in the saved reports.

Both defined runs still omitted the required ECMA-404 publisher edge. The UUID
namespace relationship was represented through the source specification or a
citation instead of the required direct UUID-to-URN edge. Those candidates are
retained for inspection but are not silently promoted to matches. The second
defined run also lacked the required IANA registration and ABNF citation edges.

Adding more prompt rules alone has not stabilized extraction. A useful next
experiment is source-evidence-backed relation extraction with deterministic
checks for contradictory directions and missing endpoints, followed by repair
only for flagged output. Measure the extra tokens/calls alongside factual
coverage; do not assume a repair pass improves end-to-end performance.

## Validation and reproduction

All **66 unit tests pass**, including graph/combined/classifier routing, selected
forbidden facts, reversed-edge conflicts, alternative-representation diagnostics,
and alias wording. Six live ingestion/retrieval passes completed.

```powershell
dotnet run --project src/FabrCore.Services.GraphRag.EvalConsole -- run --model default --response schema --relations defined --manifest src/FabrCore.Services.GraphRag.EvalConsole/corpus-relations-v2.json
dotnet run --project src/FabrCore.Services.GraphRag.EvalConsole -- run --model default --response schema --relations defined --manifest src/FabrCore.Services.GraphRag.EvalConsole/corpus-relations-holdout.json
```

Use `--relations current` for controls. Each report directory below contains
`report.md` and `report.json`, under `artifacts/graphrag-evals/`:

1. `20260905T193520-d17191ba0c2646849d2bac714ff34c4a`
2. `20260905T193549-3c65b018f5384c25b0f4d9013bbb3f65`
3. `20260905T193619-e200ec6120a548f3b3cdd1a09e725813`
4. `20260905T193644-34b4a6bba54e426abc8a5ea162bbcf17`
5. `20260905T193714-de4ed6aa3b0e491b873e2bfc98ea35be`
6. `20260905T193726-847314e74aee48b39bc18a98ce2bee73`
