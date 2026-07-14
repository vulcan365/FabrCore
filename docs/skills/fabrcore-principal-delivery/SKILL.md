---
name: fabrcore-principal-delivery
description: >
  Build, configure, test, or troubleshoot FabrCore durable agent-to-principal delivery when no
  user turn or live observer is active. Covers SendToUserAsync, PrincipalDeliveryTarget,
  IPrincipalMessageRelay, IPrincipalContextStore, IPrincipalMessageDeliveryCompletion, provider
  endpoint registration, bounded relay workers, durable outbox leases/retries/dead letters,
  at-least-once semantics, and Microsoft 365 Copilot proactive delivery. Use for "proactive
  messaging", "out-of-turn message", "send to user later", "background agent notification",
  "principal delivery", "durable outbox", "delivery relay", SMS/email/mobile push/web push/Slack/
  webhook provider packages, relay queue saturation, endpoint refresh, or M365 proactive sends.
---

# FabrCore Principal Delivery

Use FabrCore's provider-neutral principal-delivery pipeline for messages produced when a principal
has no live client observer:

```text
agent -> PrincipalGrain -> durable outbox -> channel relay -> provider
                                      ^            |
                                      + completion-+
```

Keep Core, Host, SDK, and agent code provider-neutral. Put provider payload mapping, credentials,
API calls, and retry classification in the provider package.

## Choose the workflow

- **Send from an agent:** use `SendToUserAsync`; see Agent API below.
- **Build SMS, email, push, Slack, or webhook delivery:** read
  [references/principal-delivery-reference.md](references/principal-delivery-reference.md), then
  start from [assets/webhook-relay.cs](assets/webhook-relay.cs).
- **Enable Microsoft 365 Copilot/Teams:** use the `fabrcore-microsoft365copilot` skill for bot and
  manifest setup; use this skill for the generic durability model and agent API.
- **Diagnose stuck or duplicated work:** inspect pending/outbox state, lease timestamps, endpoint
  eligibility, relay queue acceptance, completion callbacks, and the recovery reminder in that
  order.
- **Change Core durability behavior:** preserve rolling-upgrade serializer IDs and add state-machine
  tests for leases, restart recovery, ordering, expiration, and dead-letter retention.

## Agent API

Call the protected helpers on `FabrCoreAgentProxy`:

```csharp
// Let Core choose the most recently active eligible endpoint across installed relays.
await SendToUserAsync("Report ready");

// Select a provider and provider-owned endpoint explicitly.
await SendToUserAsync(
    "Your code is 123456",
    messageType: "verification.code",
    target: new PrincipalDeliveryTarget("sms", "verified-phone-1"));

// Preserve a structured AgentMessage for provider-specific mapping.
await SendToUserAsync(new AgentMessage
{
    MessageType = "report.ready",
    DataType = "application/vnd.contoso.report+json",
    Data = JsonSerializer.SerializeToUtf8Bytes(report)
});
```

The agent must have a principal-qualified handle. The helper sets the owning principal as
`ToHandle` and forces `MessageKind.OneWay`. Do not call provider APIs from the agent.

## Provider implementation workflow

1. Register verified endpoint metadata through `IPrincipalContextStore` under a versioned,
   provider-owned key such as `sms:endpoints:v1`. Keep credentials and tokens in secret storage.
2. Implement `IPrincipalMessageRelay.ResolveAsync`. Return `NotApplicable` for unsupported
   payloads, `Unavailable` for a supported channel with no usable endpoint, and `Available` with
   channel, endpoint ID, and `LastActiveUtc`.
3. Implement `TryEnqueueAsync` with a bounded local queue and `TryWrite`-style behavior. Return
   `false` when full; never wait for network I/O in the grain call.
4. Perform provider I/O on background workers. Preserve per-principal ordering and pass
   `DeliveryId` as an idempotency key when the provider supports it.
5. Report `Delivered`, `RetryableFailure`, `EndpointUnavailable`, or `PermanentFailure` through
   `IPrincipalMessageDeliveryCompletion`.
6. Register exactly one owner for the channel with
   `services.AddPrincipalMessageRelay<TRelay>()`.

## Durability invariants

- Persist pending work before resolution and atomically move resolved work to the outbox.
- Lease only the oldest outbox entry for a principal; never lease multiple entries concurrently.
- Treat `NotApplicable` messages as skippable. Treat a supported-but-`Unavailable` message as an
  ordering barrier.
- Return `false` on relay saturation so Core retains the durable entry.
- Keep accepted entries until completion, expiration, or dead-lettering.
- Assume at-least-once delivery. A crash after provider acceptance but before completion can
  produce a duplicate.
- Use the one-minute Orleans reminder as crash recovery; do not move provider network I/O into the
  grain.

## Microsoft 365 proactive delivery

Proactive delivery is disabled by default. Enable only after the normal M365 turn bridge works:

```json
"Microsoft365Copilot": {
  "Proactive": {
    "Enabled": true,
    "AllowedConversationTypes": [ "personal" ]
  }
}
```

The provider stores up to eight versioned conversation endpoints, defaults to personal scope,
supports text and valid Adaptive Cards, and marks stale endpoints unavailable until an eligible
inbound turn refreshes them. Never put conversation references or Microsoft SDK types in Core.

## Verification

Run targeted tests after contract, Host, SDK, or M365 changes:

```powershell
dotnet build src\FabrCore.sln
dotnet test src\FabrCore.Host.Tests\FabrCore.Host.Tests.csproj
dotnet test src\FabrCore.Sdk.Tests\FabrCore.Sdk.Tests.csproj
dotnet test src\FabrCore.Services.Microsoft365Copilot.Tests\FabrCore.Services.Microsoft365Copilot.Tests.csproj
```

Cover explicit routing, newest-endpoint fallback, zero-relay compatibility, context limits,
observer precedence, queue saturation, leases, retries, persisted-state recovery, endpoint
refresh, dead letters, payload rejection, and retry classification.

## Key implementation files

- `src/FabrCore.Core/PrincipalMessageDelivery.cs` — public provider-neutral contracts
- `src/FabrCore.Core/AgentMessage.cs` — `DeliveryTarget` serialization
- `src/FabrCore.Host/Grains/PrincipalGrain.cs` — pending/outbox orchestration
- `src/FabrCore.Host/Services/PrincipalDeliveryStateMachine.cs` — durable transitions
- `src/FabrCore.Host/Services/PrincipalMessageDeliveryServices.cs` — context/completion facades and dispatcher
- `src/FabrCore.Sdk/FabrCoreAgentProxy.cs` — agent helpers
- `src/FabrCore.Services.Microsoft365Copilot/Bridge/CopilotConversationContext.cs` — endpoint capture
- `src/FabrCore.Services.Microsoft365Copilot/Bridge/CopilotPrincipalMessageRelay.cs` — M365 relay/workers
