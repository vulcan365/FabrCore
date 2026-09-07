# GraphRAG GPT-5.6-luna experiment — September 5, 2026

## Decision

Keep `gpt-5.4-mini` as the extraction default for ingestion latency. The existing
Azure resource accepted the `gpt-5.6-luna` deployment (availability probe returned
`gpt-5.6-luna-2026-07-09`), and two complete ingestion passes succeeded. Luna
averaged 41.223 seconds, versus 28.453 seconds for the immediately following mini
control: approximately 45% slower. This is exploratory evidence from a small corpus.

## Method

Ran Luna twice, then mini once, sequentially without competing live API tests.
All used reasoning effort `none`, the same 75,000-token context configuration,
document extraction plan, concurrency, and `text-embedding-ada-002` embeddings.
Inputs were the same full JSON/CSV RFCs and first 30,000 characters of the UUID
RFC: 71,291 source characters and 179 chunks per pass. Each pass used a fresh
scope in `localhost/graphrag`. Shared taxonomy started at six rows in both Luna
passes and was not reset. Setup, download, and embedding warmup are outside timing.

The local, gitignored `artifacts/graphrag-models/luna.json` clones the existing
configuration and adds the `graphrag-luna` alias. It retains the configured Azure
endpoint and credentials; it must not be committed. No production default or
library code changed for this trial.

```powershell
dotnet run --project src/FabrCore.Services.GraphRag.EvalConsole --no-build -- run --models C:/repos/FabrCore/artifacts/graphrag-models/luna.json --model graphrag-luna --iterations 2
dotnet run --project src/FabrCore.Services.GraphRag.EvalConsole --no-build -- run --model default
```

## Results

| Model/pass | Ingestion seconds | Calls | Input tokens | Cached input tokens | Output tokens | Retries | Entities | Non-provenance edges |
| --- | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: |
| Luna 1 | 40.224 | 7 | 24,869 | 0 | 9,498 | 0 | 117 | 114 |
| Luna 2 | 42.221 | 7 | 24,869 | 24,848 | 9,945 | 0 | 129 | 117 |
| Mini control | 28.453 | 7 | 24,869 | 20,992 | 9,641 | 0 | 124 | 110 |

Both Luna passes reported zero reasoning tokens. The repeat pass reused over
99.9% of input tokens through the provider cache, yet took slightly longer. This
shows why cache hits alone do not establish an ingestion speed improvement:
response generation, service variability, and other work remain. Similar output
token counts here also distinguish Luna from the earlier nano experiment, where
nano generated substantially more text. Earlier mini runs averaged 29.433 seconds;
earlier nano runs averaged 96.739 seconds.

Every pass completed, embedded all 179 chunks, and passed entity-name, edge,
taxonomy, scope-isolation, and retrieval smoke gates. Recall@3 was 100% and MRR@3
was 1.000. These chunk-retrieval checks do not measure factual graph equivalence.
For example, Luna's first JSON graph includes `Ecma International --AUTHORED_BY-->
ECMA-262`, while its description says ECMA-262 is published by Ecma International.
The edge direction appears reversed under the ordinary meaning of AUTHORED_BY.
This is an observed semantic issue, not a measured error rate or a claim that
mini avoids it. Explicit relation-direction definitions and labeled graph facts
should accompany subsequent performance experiments.

## Interpretation and next step

Luna works in the current Azure-backed console pipeline, but this trial does not
support replacing mini to improve speed. Retain it as an evaluation candidate.
Test schema-enforced JSON with mini next, then compact output with labeled fact
coverage and relation-direction checks. Schema enforcement can prevent malformed
JSON; it cannot establish semantic correctness.

OpenAI documents Luna's structured-output support and reasoning controls in its
[model documentation](https://developers.openai.com/api/docs/models/gpt-5.6-luna).
The deployment availability and cache numbers here are actual Azure observations.
No Azure billed-cost comparison was performed. Two Luna runs and one mini control
are not a statistical benchmark; cache state and provider routing were not controlled.

## Raw reports

- Luna: `artifacts/graphrag-evals/20260905T191431-cfec0fc551234b93945da53be9292ebf/report.json`
- Mini control: `artifacts/graphrag-evals/20260905T191602-0439c96aa8894c008762aaf42f800f41/report.json`
- Earlier mini/nano comparison: [Model and cache experiment](graphrag-model-cache-experiment-2026-09-05.md)

Reports retain per-document phase timings, cache usage, graph snapshots, and
retrieval evidence. Validation for this configuration-only experiment was the
three successful live evaluation passes; unit tests were not rerun.
