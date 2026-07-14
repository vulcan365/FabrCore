# Principal delivery reference

## Contents

- [Public contracts](#public-contracts)
- [Resolution and ordering](#resolution-and-ordering)
- [Context storage](#context-storage)
- [Outbox lifecycle](#outbox-lifecycle)
- [Provider failure mapping](#provider-failure-mapping)
- [Microsoft 365 provider](#microsoft-365-provider)
- [Testing checklist](#testing-checklist)

## Public contracts

`AgentMessage.DeliveryTarget` is optional:

```csharp
public sealed class PrincipalDeliveryTarget
{
    public string? Channel { get; set; }
    public string? EndpointId { get; set; }
}
```

An explicit endpoint requires a channel. Channel identifiers are provider-neutral strings such as
`sms`, `email`, `webpush`, `webhook`, or `m365copilot`.

Provider packages implement:

```csharp
public interface IPrincipalMessageRelay
{
    string Channel { get; }

    ValueTask<PrincipalMessageRelayResolution> ResolveAsync(
        string principalHandle,
        AgentMessage message,
        PrincipalDeliveryTarget? target,
        IReadOnlyDictionary<string, string> principalContext,
        CancellationToken cancellationToken = default);

    ValueTask<bool> TryEnqueueAsync(
        PrincipalMessageDelivery delivery,
        CancellationToken cancellationToken = default);
}
```

Use `IPrincipalContextStore` to register endpoint metadata and
`IPrincipalMessageDeliveryCompletion` to report worker results. `PrincipalMessageDelivery`
contains `DeliveryId`, principal handle, `AgentMessage`, selected channel/endpoint, a context
snapshot, and attempt number.

Do not expose provider types through these contracts. Store provider serialization inside a
provider-owned context value.

## Resolution and ordering

For an explicit target, Core calls only the matching channel relay. Without a target, Core asks all
installed relays and chooses the available endpoint with the greatest `LastActiveUtc`; channel name
breaks timestamp ties deterministically.

Relay resolution statuses mean:

- `NotApplicable`: this relay cannot map the payload. Core may inspect later pending messages.
- `Unavailable`: the relay applies but currently has no usable endpoint. This is an ordering
  barrier for the principal.
- `Available`: freeze the selected channel and endpoint into a durable outbox entry.

Only one relay may own a channel. Duplicate channel registrations fail when the dispatcher is
constructed because a channel alone must identify the relay that later receives the frozen work.

A live observer handles newly arriving messages directly. Once a message has moved to the outbox,
it remains on the relay path even if an observer subsequently connects.

## Context storage

Context values are bounded:

| Limit | Value |
|---|---:|
| Entries per principal | 64 |
| Key length | 128 characters |
| One value | 128 KiB UTF-8 |
| All keys and values | 256 KiB UTF-8 |

Use a versioned key such as `mobilepush:installations:v1`. Store endpoint IDs, provider routing
metadata, eligibility, and last-active timestamps. Never store API keys, OAuth access/refresh
tokens, signing secrets, or other provider credentials.

Updating context persists immediately, re-evaluates pending messages when no observer is active,
and wakes outbox entries waiting for endpoint refresh.

## Outbox lifecycle

Default Host settings:

```json
"FabrCore": {
  "PrincipalDelivery": {
    "LeaseDuration": "00:02:00",
    "RecoveryReminderPeriod": "00:01:00",
    "MaxDeliveryAge": "1.00:00:00",
    "DeadLetterRetention": "7.00:00:00",
    "MaxDeadLetters": 100
  }
}
```

The actual configuration section is `FabrCore:PrincipalDelivery`; context limits bind from
`FabrCore:PrincipalContext`.

Lifecycle:

1. Persist the message in `PendingMessages` before relay resolution.
2. Atomically remove an available message from pending and append it to `DeliveryOutbox`.
3. Lease only outbox index zero for two minutes, persist the lease, and call `TryEnqueueAsync`.
4. If enqueue returns false or throws, clear the lease and keep the entry for reminder recovery.
5. On success, remove the entry and dispatch the next one.
6. On retryable failure, clear the lease and set `AvailableAfterUtc`.
7. On endpoint unavailable, wait for context refresh or message expiration.
8. On permanent failure or 24-hour expiration, move the entry to bounded dead-letter history.

The one-minute Orleans reminder recovers expired leases after silo/process failure. A provider may
still accept work immediately before a crash or timeout, so exactly-once delivery is not promised.

Metrics use meter `FabrCore.Host.PrincipalGrain`, counter
`fabrcore.principal.delivery.operations`, and bounded `status`/`channel` tags. Do not add principal
handles or endpoint IDs as metric tags.

## Provider failure mapping

Map provider outcomes consistently:

| Provider result | Completion |
|---|---|
| Accepted/success | `Delivered()` |
| Rate limit, 5xx, network, timeout | `RetryableFailure(retryAfter, error)` |
| Deleted installation, stale conversation, gone subscription | `EndpointUnavailable(...)` |
| Unsupported mapping, invalid payload, permanent 4xx | `PermanentFailure(error)` |

Honor provider retry hints. Keep provider-local retry duration shorter than the Core lease; report a
durable retry when a long delay would risk holding the lease. Use `DeliveryId` as an idempotency key
where supported.

## Microsoft 365 provider

All `Conversation`, `ConversationReference`, claims, `IChannelAdapter`, and
`Proactive.SendActivityAsync` usage stays in `FabrCore.Services.Microsoft365Copilot`.

Endpoint registry:

- Context key: `m365copilot:proactive:endpoints:v1`
- Inbound arg: `Microsoft365Copilot:DeliveryEndpointId`
- Stable endpoint hash inputs: channel, tenant, conversation ID
- Default allowed conversation type: `personal`
- Default retained endpoints: 8, newest first
- Stored claims are allowlisted identity/routing claims, not user access tokens

Sender defaults:

- Disabled unless `Microsoft365Copilot:Proactive:Enabled` is true
- 3 provider attempts
- 2-second exponential backoff
- 30-second send timeout
- 4 worker shards
- 64 bounded queue entries per shard

Supported payloads are nonblank plain text and valid FabrCore surface Adaptive Cards. Reject system
messages, blank content, foreign channel targets, `ui.action`, and unsupported `ui.*` payloads.

## Testing checklist

Generic tests should use fake SMS, webhook, and rejecting relays so provider neutrality is explicit.
Cover:

- explicit channel/endpoint routing
- newest active endpoint across providers
- `NotApplicable` skipping and `Unavailable` ordering barriers
- zero-relay pending compatibility
- context entry/key/value/total limits
- live-observer precedence and persisted pending flushes
- bounded queue saturation returning false without drops
- lease acquisition, expiry, and persisted restart recovery
- retryable, endpoint-unavailable, delivered, and permanent transitions
- 24-hour expiration and seven-day/100-entry dead letters
- old-state surrogate defaults and additive serializer IDs

M365 tests should additionally cover personal-scope filtering, stable/multiple conversation
serialization, anonymous/authenticated allowlisted claims, endpoint refresh, text/cards, malformed
cards, 429/5xx/network/timeout retries, permanent 4xx, queue saturation, and opt-in DI registration.
