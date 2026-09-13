# FabrCore 2.0 retained monitoring

Recording and querying are separate contracts. Implement IAgentMonitorQueryProvider
alongside IAgentMessageMonitor for cloud discovery; implement IAgentMonitorPayloadProvider
for bounded payload retrieval. Disabled, in-memory and custom providers remain supported.
Provider capabilities determine whether a remote viewer can query retained records.

All routes below require administration authentication and start at
`/fabrcoreapi/admin/v1/observability/monitor`:

| Route | Purpose |
| --- | --- |
| GET root | Filtered retained records |
| GET `/health` | Source identity, capture/retention and persistence health |
| GET `/tokens` | Explicitly provider-lifetime token/cost summaries |
| GET `/payload/{sequence}?offset=0&sourceId={sourceId}` | Separate captured payload chunks |

MonitorQuery filters principal, agentHandle, traceId, kind, channel, executionCategory,
from and to before paging. Default limit is 100; maximum 1,000. Pages have byte limits
(default MaxBytes 512 KiB, further bounded by transport). IncludePayload defaults false.
Keep filters and source fixed with an opaque cursor. Explicit gaps require refreshing;
the final continuation can read subsequently appended records. Do not silently restart
pagination and duplicate rows. Payload offsets count UTF-16 characters, chunks are
at most 32,000 characters; follow nextOffset. Supply sourceId so restart/sequence reuse
cannot substitute another record. Missing/redacted/truncated capture cannot be recovered
by requesting a larger export or inferred from a successful empty query.

## SQL configuration

With the operational database configured:

```json
{
  "FabrCore": {
    "Monitoring": {
      "Provider": "sql",
      "RetentionDays": 7,
      "QueueRecords": 10000,
      "QueueBytes": 67108864,
      "BatchSize": 250
    }
  }
}
```

SQL buffering flushes every second, retries with bounded backoff and reports failed
writes/drops. Recording serializes/enqueues without awaiting SQL; a saturated queue
drops data instead of blocking an agent on persistence. Durability starts at flush.
Health exposes source identity, queue depth/bytes, lag, drops, persistence errors and
retention boundaries. Existing LlmCaptureOptions still control payload capture/redaction.
Apply the release's additive monitoring migration to the operations database before
enabling SQL in manual-schema mode. Auto-initialization provisions it. Clearing the
legacy recent in-memory view does not erase retained SQL records; SQL retention owns that.

## Cluster and diagnostic attribution

Query silo-local stores separately and shared stores once per opaque store identity.
Do not deduplicate by host name or assume a shared database from similar configuration.
Show unavailable hosts, gaps and source boundaries alongside results. Default viewers
to manual refresh; optional ten-second polling runs only while visible, at most four
concurrent host requests and one query per host. Cancel hidden-view requests. Existing
SSE/local callbacks remain useful locally; the cloud uses HTTP long polling and queries.

Admin LLM usage has `_admin` channel and admin execution category, actor/session fields
on MonitoredLlmCall, target and trace. Keep diagnostic costs distinguishable from normal
agent work. Diagnostic transcripts belong to the administration session store and are
not ordinary monitor payloads. Read transcripts only through authenticated administration.
Provider-lifetime totals are not an all-time cluster billing ledger; unknown cost is not zero.

Audit is unchanged and distinct from monitor capture. Evidence verification is a separate
query/export workflow. See the [cloud administration guide](https://fabrcore.ai/docs/cloud-administration)
for broker integration and the [release validation report](https://github.com/vulcan365/FabrCore/tree/main/docs/cloud-validation)
for remaining sustained SQL, multi-silo and performance validation.
