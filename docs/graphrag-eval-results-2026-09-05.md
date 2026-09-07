# Initial GraphRAG console evaluations — September 5, 2026

The new `FabrCore.Services.GraphRag.EvalConsole` ran successfully as a standalone
console process. It initialized schema migrations inside the existing
`localhost/graphrag` database and verified native SQL VECTOR distance operations.
No web server was used. Connection settings are in the console project's local
appsettings file. The SDK uses the sample application's configured Azure models:
`default` (`gpt-5.4-mini`) and `embeddings` (`text-embedding-ada-002`, 1536 dimensions).

## Corpus and measurements

The manifest downloads RFC Editor's plain-text JSON, CSV, and UUID documents.
JSON and CSV were ingested in full; UUID ingestion used the first 30,000 characters.
Total input: 71,291 source characters, yielding 179 retrieval chunks in each pass.
Source hashes, URLs, and excerpt lengths are recorded in each JSON report.

| Pipeline | Ingestion wall time | Chat calls | Input tokens | Output tokens | Recall@3 | MRR@3 |
| --- | ---: | ---: | ---: | ---: | ---: | ---: |
| Legacy | 37.4 s | 7 | 27,403 | 16,926 | 100% | 1.000 |
| Document plan | 30.8 s | 7 | 24,779 | 10,588 | 100% | 1.000 |

On this initial comparison, ingestion wall time decreased approximately 18%,
input tokens 10%, and output tokens 37%, with the same call count. Both passes
passed chunk embedding coverage, expected entity-name, non-provenance edge,
taxonomy, and scoped retrieval smoke checks. These gates do not establish equal
entity/relationship precision or recall. Extracted graph counts differed.

An earlier standalone document-plan pass completed in 33.1 seconds and passed
the same checks. Latency variability is already visible at this small sample size.

The comparison ran legacy first against a database with existing taxonomy. Initial
taxonomy rows were 4 for legacy and 5 for document-plan. The existing live test suite
also ran during part of this comparison, so this is an exploratory result rather
than an isolated performance benchmark. For stronger conclusions, run repeated,
alternating comparisons without concurrent provider workloads and label graph facts.

## Local artifacts

- Initial run: `artifacts/graphrag-evals/20260905T185618-1969d6e329894d149e88c3edd2d78021/report.json`
- Comparison: `artifacts/graphrag-evals/20260905T185707-f573742bdd004f7282dfae6be8b63e8f/report.json`

Each directory also contains a readable Markdown report. Data remains in fresh
`eval:graphrag:*` scopes for inspection; the console does not remove shared taxonomy
or other runs' data. Artifacts and downloaded corpus files are ignored by Git.

## Validation

- 59 unit tests passed, including console argument and report-gate checks.
- 10 SQL integration tests passed against the supplied database.
- Both existing live model evaluation tests passed.
- One legacy-schema migration sandbox test is intentionally skipped because it
  requires creating a separate database. It now requires explicit opt-in before
  any CREATE DATABASE attempt; the supplied account should leave it disabled.

See [the console README](../src/FabrCore.Services.GraphRag.EvalConsole/README.md)
for setup, commands, configuration, corpus customization, and gate definitions.
