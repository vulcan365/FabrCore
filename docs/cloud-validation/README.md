# Cloud management validation — September 12, 2026

Implementation and protocol: [cloud administration](../cloud-administration.md).
Independent server fixture: [FabrCore.ReferenceCloud](../../samples/FabrCore.ReferenceCloud/README.md).

## Completed checks

| Check | Result |
| --- | --- |
| Release solution build and public API/Orleans contract checks | Passed, no warnings or errors |
| Nine NuGet packages, version `2.0.0-cloudvalidation.4` | Built into a temporary local feed; not published |
| Fresh package-only consumers | Startup, readiness, discovery, storage, typed blueprint summaries and diagnostic-session creation/deletion passed |
| Host offline suite | 395 passed, no skips |
| Host SQL suite | 32 passed, no skips, isolated SQL Server 2025 databases |
| SDK suite | 134 passed |
| Orleans client suite | 17 passed |
| WebSocket client suite | 4 passed |
| Insights with SQL | 92 passed, no skips |
| Insights application build after view changes | Passed |
| Independent reference server | Built; live command/lease/response/header round trip passed |
| Documentation entry-point links and whitespace checks | Passed |

Focused real-host tests exercise authenticated operator isolation, concurrent
normal/admin turns, rejection of overlapping admin turns and reset, submission
deduplication, unchanged production state/history, stale management revisions,
mutation-free blueprint previews, deployment replay receipts, inactive principal
discovery and conditional enforcement changes. Evidence export tests verify an
immutable snapshot while new records arrive and chunks below a 1 KiB transport
limit. SDK tests cover fork-copy isolation, repeated persistence and cancellation
guard cleanup. SQL tests include 1,105 retained monitor records across pages and
provider restart, shared store identity and non-waiting queue saturation.

The SQL run found and corrected a DDL concatenation error. Insights protocol
validation found and corrected a lease-query isolation-level problem and updated
the fixture to supply the required host identity and lease token.

## Performance measurement and limits

[Raw benchmark result](benchmark.json) records a synthetic single-silo run with
16 echo agents, a 10,000-record monitor and one authenticated 100-record query
per second. It uses warmup followed by baseline/read/baseline windows, with no
model calls. The measurement showed no regression; differences between windows
are not evidence that reads improve performance. Short windows and a developer
machine do not establish a statistically reliable five-percent bound.

Production certification still requires the planned multi-silo load/failover
matrix, SQL outage and sustained-retention stress, and diagnostic evaluation
with the deployment's actual models and capture settings. These results must
not be presented as that certification. Evidence exports identify their source
scope; a verified export does not prove that every cluster record was captured.

Current UI drift reporting compares the saved blueprint with its last deployment
revision. Independent edits to runtime agent configuration remain inspectable
through configuration reads and require a separate comparison. Partial retries
report their selected scope rather than asserting a full deployment succeeded.

Existing release edits were preserved. No packages were published and no release
deployment was performed.
