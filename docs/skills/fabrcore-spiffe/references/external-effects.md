# External Effects Reference

## Principle

SPIFFE does not record DB updates or API calls. It identifies/signs the workload that produced evidence.

For plugins/tools that call external systems, FabrCore can prove only:

```text
plugin/tool was invoked -> returned/failed
```

unless the plugin/tool records external effects through an attested wrapper, manual evidence API, or external audit/outbox integration.

## Plugin/Tool Responsibilities

Plugin/tool authors do not implement SPIFFE. They may use `IVerifiableExecutionContext` to record important side effects:

```csharp
private IVerifiableExecutionContext? _evidence;

public Task InitializeAsync(AgentConfiguration config, IServiceProvider serviceProvider)
{
    _evidence = serviceProvider.GetService<IVerifiableExecutionContext>();
    return Task.CompletedTask;
}
```

Record external effects after the effect is known:

```csharp
await _evidence!.RecordExternalEffectAsync(
    ExecutionRecordKind.ExternalDbEffect,
    "orders:update-status",
    new Dictionary<string, string?>
    {
        ["db.system"] = "sqlserver",
        ["db.name"] = "orders",
        ["db.table"] = "Orders",
        ["operation"] = "UPDATE",
        ["row_key_hash"] = Hash(orderId),
        ["command_hash"] = Hash(commandText),
        ["parameter_hash"] = Hash(redactedParametersJson),
        ["affected_rows"] = affectedRows.ToString(),
        ["transaction_id"] = txId,
        ["rowversion"] = rowVersion
    });
```

## DB Effects

Recommended evidence metadata:

- `db.system`
- `db.name`
- `db.schema`
- `db.table`
- `operation`
- `row_key_hash`
- `command_hash`
- `parameter_hash`
- `affected_rows`
- `transaction_id`
- `rowversion`
- `commit_lsn` when available
- `error.type` and `error.message_hash` on failure

Avoid storing raw SQL with secrets or raw customer values. Store command hashes and redacted/hashed parameters.

## Transactional Outbox Pattern

Best pattern for DB writes:

1. Begin DB transaction.
2. Execute business update.
3. Write an evidence/outbox row in the same transaction with trace id, operation, row key hash, affected row count, command hash, and transaction marker.
4. Commit.
5. Record or export the outbox/evidence marker into `IVerifiableExecutionStore`.

Without a transactional link, FabrCore can prove the plugin claimed an effect, but cannot independently prove the database committed it.

## HTTP/API Effects

Recommended metadata:

- `http.method`
- `url.host`
- `url.path_template`
- `request_body_hash`
- `response_body_hash`
- `status_code`
- `duration_ms`
- `retry_count`
- `idempotency_key`
- `correlation_id`
- `error.type`

Do not store authorization headers or raw bodies unless explicitly safe and redacted.

## Storage Effects

Recommended metadata:

- `storage.provider`
- `container`
- `object_key_hash`
- `operation` (`put`, `delete`, `copy`, etc.)
- `content_hash`
- `etag`
- `version_id`
- `size_bytes`

## Unreported Effects

If a plugin/tool performs direct DB/API/storage work outside an attested wrapper and does not call `IVerifiableExecutionContext`, mark the tool/plugin call as signed but `ExternalEffectsUnverified` in UI/reporting. Do not overclaim.

## Testing

Test these cases:

- successful DB update with evidence
- rollback does not leave misleading success evidence
- failed command records error evidence
- redaction removes secrets
- command/parameter hash is deterministic
- missing manual evidence is visible as unverified external effect
