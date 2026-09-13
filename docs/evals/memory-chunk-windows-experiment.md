# Beginning/end previews for chunk selection

This experiment tests whether splitting the selection budget across a body's beginning and
end can preserve late corrections without hiding other required evidence. It compares the
same policy on two retained fixtures rather than judging it only on late-correction recovery.

## Late-correction results

Report: `artifacts/memory-chunk-windows-late/20260907T191456-00b2865f969a4f20ad0182f16f642969/report.json`.

| Three repetitions | Full body | Prefix 288 | Beginning/end 288 |
| --- | ---: | ---: | ---: |
| Strict evidence checks | 24/24 | 16/24 | **24/24** |
| Known-answer body coverage | 21/21 | 21/21 | 21/21 |
| Unwanted chunks | 0 | 8 | 0 |
| Total selection input tokens | 49,797 | 47,529 | 48,159 |
| Second-stage input, known-answer questions | 22,236 | 19,968 | 20,598 |
| Output tokens | 834 | 850 | 834 |
| Cached input tokens | 0 | 0 | 0 |
| Chat calls | 45 | 45 | 45 |
| Returned body characters | 16,230 | 19,846 | 16,230 |
| Formatted context characters | 34,386 | 38,658 | 34,386 |

The split preview retains all requested bodies and excludes the rejected drafts while using
7.4% less second-stage input on known-answer questions and 3.3% less total selection input.
Both source bodies and formatted context size match full loading. The prefix condition adds
eight unwanted chunks despite retaining all current facts. These findings do not establish
that a split preview works when essential evidence lies between its windows.

## Middle-fact guard results

Report: `artifacts/memory-chunk-windows-middle/20260907T191826-7217b7ca7d58492dabd5f38419b7f7d2/report.json`.

| Three repetitions | Full body | Prefix 288 | Beginning/end 288 |
| --- | ---: | ---: | ---: |
| Strict evidence checks | **24/24** | 24/24 | 13/24 |
| Known-answer body coverage | 21/21 | 21/21 | 10/21 |
| Unwanted chunks | 0 | 0 | 4 |
| Total selection input tokens | 48,984 | 47,526 | 47,857 |
| Second-stage input, known-answer questions | 21,423 | 19,965 | 19,368 |
| Output tokens | 834 | 834 | 886 |
| Cached input tokens | 0 | 0 | 0 |
| Chat calls | 45 | 45 | 46 |
| Returned body characters | 16,230 | 16,230 | 11,716 |
| Formatted context characters | 34,428 | 34,428 | 26,916 |

The split preview misses the owner, recovery objective and complete five-fact evidence in
all three repetitions, and the two-fact evidence in two repetitions. All returned provenance
checks pass, but authentic source content is insufficient when required evidence is absent.
Both other conditions preserve all requested bodies. The reduced context from the split
policy is therefore lost coverage, not a useful efficiency gain. Its extra call is an unknown
question requiring a second selector to abstain; all variants ultimately pass the three unknown
checks. Each variant makes 24 query embeddings.

Across both fixtures, full bodies pass **48/48**, prefixes **40/48**, and split windows **37/48**.
The same window policy succeeds when corrections lie at the end and fails when facts lie in
the omitted middle. These are different synthetic fixtures, so the combined totals describe
this regression suite rather than an external benchmark score.

## Implementation

`Retrieval.ChunkSelectionIncludeTail` is opt-in and requires a selection preview length of at
least two. For a body longer than the budget, half the characters come from its end and the
remainder from its beginning. An odd extra character goes to the beginning. An explicit
middle-omission notice separates the windows. Short bodies are unchanged. The default remains
full bounded bodies; existing prefix behavior is unchanged when the new option is false.

This transformation happens after bounded body loading. The "end" is the end of the loaded
body, which may itself be a prefix of a longer stored chunk. It is not a new fetch of the
stored chunk's true tail. Source truncation and provenance retain their existing meanings.
Selected IDs map back to the same original bounded bodies; neither windowing nor its notice
is written into stored or returned source content. It adds no model, embedding or SQL call.

## Experiment design

```powershell
dotnet run --project src/FabrCore.Services.Memory.EvalConsole -- chunk-windows --iterations 3 --window-fixture late
dotnet run --project src/FabrCore.Services.Memory.EvalConsole -- chunk-windows --iterations 3 --window-fixture middle
```

Each compares full bodies, a 288-character prefix and 144+144-character windows. Policy and
preview order rotate across three repetitions of eight questions. The split condition is
the gate, while failing controls remain in the report. Real SQL, embeddings and gpt-5.4-mini
are used, with hybrid 8+8 entity candidates, eight-chunk pools, compact IDs, minimal selection
and chunk selection. First-stage previews, the redundant-selection shortcut, verification
and graph expansion remain off.

Both fixtures have 241 entities, with five current production fact chunks and three distractor
chunks grouped inside the target entity and 240 newer entities outside it. Target fact bodies
are 450 characters and fit the existing body budget without truncation.

- **Late:** Rejected draft values start near the beginning; their rejection begins at character
  320. Windows include positions 0–143 and 306–449, exposing the complete rejection. The prefix
  ends at 287 and hides it.
- **Middle:** Production facts alternate between offsets zero and 145, with sandbox facts as
  distractors. The windows omit positions 144–305, including the owner and recovery objective.
  The prefix exposes those facts.

Strict success requires complete requested bodies, valid entity/source/chunk provenance and
zero unwanted chunks. Current-body coverage, excess chunks, returned context and measured
selection usage are also retained separately. The target header intentionally describes
all current production facts to isolate body selection. These are small synthetic evidence
tests, not independently labeled external histories or downstream answer-generation tests.

## Validation and limits

All 130 Memory tests pass with none skipped. Extended selector tests cover even and odd window
budgets, bodies exactly equal to the budget, unchanged returned bodies and source truncation
flags, and the existing invalid-ID, abstention and cancellation behavior. Defaults and baseline
registrations are unchanged.

A window policy cannot expose evidence in its omitted span. Corrections can appear in the
middle, and relevant facts can appear after the storage read cap. The omission notice adds
tokens, so fewer body characters do not imply proportional token savings. Any apparent gain
on the late fixture must be considered alongside the middle-fact guard.

## Decision

Keep full bounded bodies as the default. Beginning/end windows repair the late-rejection
fixture but do not preserve evidence located in the middle. The option stays explicitly
opt-in, and both fixtures remain controls for future selection-cost experiments. A useful
general replacement must retain corrective context and middle facts together, not merely
pass the fixture whose important text happens to fall within its windows.

Both runs completed all 72 cases. The middle fixture retains a failed candidate gate rather
than an infrastructure failure. Final whitespace checks passed. No broad harness matrix was
rerun: this experiment validates the new opt-in selector-input transformation through the
service tests and both SQL/model fixtures, without promoting it or changing returned-body
semantics. Existing harness controls remain retained.
