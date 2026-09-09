# Bounded body previews before selection

Follow-up: [body-position testing](memory-body-position-experiment.md) shows that moving
facts past the 160-character window breaks that configuration; larger prefixes only help
when they actually cover the evidence.

This experiment targets the opaque-header failure: relevant bodies enter the candidate
pool, but the selector lacks enough header information to identify them reliably.

The opt-in `Retrieval.SelectionPreviewCharacters` setting accepts 1..512 characters per
candidate; zero remains the default. It requires semantic candidates. After exclusions and
preferred-type filtering, the service divides a 4,096-character body-preview budget across
the remaining candidates and appends each prefix to a copy of its description. Stored
headers remain unchanged. SQL loads only primary-chunk prefixes for matching, non-cold
entities in the exact scope; it accepts at most 200 candidate IDs. A custom store may
implement the optional preview capability or explicitly reject unsupported use.

The budget caps **preview body characters**, not total prompt tokens, existing headers,
or separators. Full selected bodies are still loaded after selection. Preview mode adds
one SQL read when candidates exist, with no additional selection or embedding call.

## Design and reproduction

Completed opaque-header report:
`artifacts/memory-headers-opaque/20260907T162631-2ff527aaa3514bf08a04051ce33865fc/report.json`.
Completed topic-header report:
`artifacts/memory-headers-topic/20260907T162726-5432c10098984ada90817fa7dbc14838/report.json`.

| Three repetitions | Opaque, no preview | Opaque, 160 chars | Topic, no preview | Topic, 160 chars |
| --- | ---: | ---: | ---: | ---: |
| Exact checks | 5/21 | **21/21** | 21/21 | 21/21 |
| Selection calls | 21 | 21 | 21 | 21 |
| Gross input tokens | 22,083 | 30,546 | 18,321 | 26,784 |
| Output tokens | 410 | 396 | 396 | 396 |
| Cached input tokens | 0 | 16,640 | 0 | 0 |

Previews recover the generic-header failures and preserve abstention, but increase gross
selection input by 38.3%. On topic-only headers they add 46.2% input with no measured quality
gain. Each variant uses 21 query embeddings. Preview variants add a bounded SQL prefix read
per recall; failure-diagnostic embeddings are separate. Provider cache differences prevent
equating gross-token ratios with priced cost ratios.

Decision: retain previews as an opt-in tool for poor headers. Do not replace useful concise
headers or enable previews globally based on this fixture. Automatic vague-header detection
and selective activation are not implemented. Existing baseline registrations and defaults
remain unchanged.

Validation: all **114 Memory tests passed**. A real-SQL test verifies a long body's prefix
is truncated without exposing a cold entity or another scope, and stored metadata remains
unchanged. A service test offers 200 candidates and verifies the shared preview budget is
enforced even if a custom store returns overlong text; original headers are not mutated.

The contemporary two-iteration harness comparison passed **34/34 in both variants**, with
no comparison regressions and 31 chat calls each. Input increased from 33,807 to 36,139
(6.9%); output was 597 versus 533 and cached input 19,584 in both. This reinforces keeping
previews optional when existing headers already support accurate retrieval. Reports:

- `artifacts/memory-preview-harness-0/20260907T162907-f21aa0ed7c11491c9f46245319fe9181/report.json`
- `artifacts/memory-preview-harness-160/20260907T162944-34e412b3e0234a4bb9d200f029039128/report.json`

Both conditions compare hybrid 8+8 without previews against hybrid 8+8 with 160-character
prefixes, using identical stored data within each run. Three repetitions alternate variant
order. Compact IDs and minimal selection are on; verification and graph expansion are off.
The quality gate targets the preview-enabled variant and preserves failed controls.

```powershell
dotnet run --project src/FabrCore.Services.Memory.EvalConsole -- sparse-headers --iterations 3 --header-mode opaque --selection-preview 160
dotnet run --project src/FabrCore.Services.Memory.EvalConsole -- sparse-headers --iterations 3 --header-mode topic --selection-preview 160
```

Ordinary `run` mode also accepts `--selection-preview 160` alongside semantic retrieval.
The report version is `sparse-headers-v2`, with per-variant preview settings. Failed-case
diagnostics still inspect candidate coverage, not the augmented preview text.

These are authored synthetic regression fixtures, used to develop this option. Their target
bodies are short and the answer occurs near the beginning; a 160-character prefix includes
the entire target fact. They do not validate answers beyond the prefix, multiple relevant
chunks, extraction, automatic detection of vague headers, or independently labeled workloads.
A separate SQL test uses a long body to verify truncation. Previews must not be interpreted
as a universal fix for poor headers or as an established token-saving optimization.
No production default or registered baseline is promoted.
