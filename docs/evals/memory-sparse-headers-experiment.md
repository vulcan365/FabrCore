# Recall with brief and opaque headers

Follow-up: [bounded body previews](memory-selection-preview-experiment.md) recover the
opaque-header failures, with measurable token overhead when applied to useful topic headers.

This experiment tests whether the ordinary and hybrid 8+8 retrieval paths need answers in
the memory headers. The earlier fixtures used full content as descriptions, which makes
relevance selection unusually easy.

The five-fact fixture is run in three header conditions:

- **Rich:** original topical title and answer-bearing full-content description.
- **Topic:** original topical title and a short topic description, omitting every answer value.
- **Opaque:** titles contain only `Stored memory` and an arbitrary GUID; every description is `Details retained in the memory body.`

Both the target memories and 240 distractors receive the same header treatment. Full bodies,
taxonomy labels, embedding input text (original topical title plus full body), question wording
and expected sets remain fixed. Each condition gets a fresh isolated SQL scope. All five
targets are verified outside the newest-200 header window. Every primary chunk is read back.
Each condition compares ordinary and hybrid 8+8 over three repetitions with alternating
variant order. Compact IDs and minimal selection are on; verification and graph expansion
are off. The exact-set score also checks loaded content and source provenance.

The topic condition tests brief useful headers. The opaque condition is a deliberately
adversarial limit case, not an estimate of typical production data. It retains informative
body embeddings while hiding relevance information from the header selector. Similarity
alone is not treated as a sufficient relevance decision in these paths.

## Reproduce

Rich control:
`artifacts/memory-headers-rich/20260907T161824-7664829834ca4f0a8f2991098e76c781/report.json`.
Topic-only:
`artifacts/memory-headers-topic/20260907T161920-53b6daf55f4b40cc8df1b0f03cfdd93f/report.json`.

| Three repetitions | Rich ordinary | Topic ordinary | Rich hybrid | Topic hybrid |
| --- | ---: | ---: | ---: | ---: |
| Exact checks | 21/21 | 21/21 | 21/21 | 21/21 |
| Selection calls | 21 | 21 | 21 | 21 |
| Gross input tokens | 22,608 | 18,303 | 22,626 | 18,321 |
| Output tokens | 396 | 396 | 396 | 396 |
| Cached input tokens | 0 | 0 | 0 | 0 |

Brief topic headers preserve every labeled answer while reducing selection input by about
19% in both strategies. Each strategy still uses one query embedding per recall. Full
content and provenance checks confirm the body is loaded correctly. This supports keeping
useful topic descriptions concise; it does not validate an automatic header-shortening
algorithm, which was not changed or evaluated here.

Corrected opaque report:
`artifacts/memory-headers-opaque/20260907T162152-3f2a77091ce74dca884759e7d63ce775/report.json`.
This run completed but failed the quality gate. Ordinary recall passed **5/21** and hybrid
**6/21**. All three five-fact requests happened to return the exact required set in both
strategies, but single-fact selection was unreliable. Both strategies returned no memory
on 13 known-answer requests. Ordinary recall also returned evidence for two of three
unknown-answer requests; hybrid correctly abstained on those three requests.

Repeated candidate diagnostics found no expected IDs missing before selection on failed
known-answer cases. The limiting factor here is the opaque header selection, unlike the
candidate-coverage failures in earlier experiments. Opaque input tokens were 22,263 for
ordinary and 22,287 for hybrid, with 21 calls each (output 406 and 410). Arbitrary identifiers
consume tokens, so opaque headers offer no useful efficiency win. Diagnostic embeddings
remain separate from recall measurements.

Decision: preserve concise **topical** descriptions rather than replace them with generic
labels. Header selection does not require answer values, but it needs usable relevance
information. For genuinely vague legacy headers, a future experiment should compare bounded
body previews with description repair; neither was implemented in this run. This fixture
does not establish that all automatically shortened descriptions will retain sufficient
information. No production retrieval code, defaults or baseline registrations changed.
All **112 Memory tests passed**, and the build and whitespace checks passed.

The initial opaque attempt at
`artifacts/memory-headers-opaque/20260907T162018-32f92748bdd9448bb7830ed2fda2b428/report.json`
is retained with status `failed`. Giving every same-type entity the identical title violated
the existing unique scope/name/type index during seeding. No quality checks from that attempt
are included. The corrected opaque fixture uses arbitrary GUIDs solely to satisfy uniqueness.

```powershell
dotnet run --project src/FabrCore.Services.Memory.EvalConsole -- sparse-headers --iterations 3 --header-mode rich
dotnet run --project src/FabrCore.Services.Memory.EvalConsole -- sparse-headers --iterations 3 --header-mode topic
dotnet run --project src/FabrCore.Services.Memory.EvalConsole -- sparse-headers --iterations 3 --header-mode opaque
```

Reports use version `sparse-headers-v1` and record the header mode, strategy, binary hash,
raw model responses and separate candidate diagnostics on failures. Fixtures preserve both
original embedding titles and actual stored titles/descriptions. The gate requires every
hybrid check to pass; ordinary-control failures remain visible. Initial data is directly
seeded, so this does not test automatic description generation, extraction or compaction.
These are authored synthetic fixtures, not an independently curated external benchmark.
Production defaults and registered baselines remain unchanged.
