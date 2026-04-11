# LLM Call Capture — Deep Dive

This document covers the internals of the third monitor track: individual LLM request/response capture. It is a companion to the main `fabrcore-agentmonitor/SKILL.md`.

## Goals

The LLM call track answers questions the message track can't:

- **What prompt was actually sent to the model?** (not just "an LLM was called")
- **What did the model return, verbatim?** (not just the agent's post-processed response)
- **Which tool calls did the model emit?**
- **Did a timer or `OnEvent` handler call the LLM, and with what context?**
- **How many individual round-trips did a single `OnMessage` make?**

Messages record agent-level traffic. LLM calls record the layer *below* that — the actual chat-client round-trips.

## The Three Monitor Tracks

| Track | Buffer | Query | Notification | Recorded from |
|-------|--------|-------|--------------|---------------|
| Messages | `_messages` | `GetMessagesAsync` | `OnMessageRecorded` | `AgentGrain.OnMessage` (inbound + outbound), `ClientGrain.OnMessageStream` |
| Events | `_events` | `GetEventsAsync` | `OnEventRecorded` | `AgentGrain.ReceivedEventMessage` (the `OnEvent` stream handler) |
| **LLM calls** | **`_llmCalls`** | **`GetLlmCallsAsync`** | **`OnLlmCallRecorded`** | **`TokenTrackingChatClient.GetResponseAsync` / `GetStreamingResponseAsync`** |

Each buffer is independently bounded — `LlmCaptureOptions.MaxBufferedCalls` defaults to 2000, separate from the 5000 default for messages and events. A burst of LLM calls cannot evict messages, and vice versa.

## Capture Hook — `TokenTrackingChatClient`

Every chat client handed to an agent is wrapped by `TokenTrackingChatClient` (a `Microsoft.Extensions.AI.DelegatingChatClient`) inside `FabrCoreAgentProxy.GetChatClient`:

```csharp
// src/FabrCore.Sdk/FabrCoreAgentProxy.cs
protected async Task<IChatClient> GetChatClient(string name, int networkTimeoutSeconds = 100)
{
    var client = await chatClientService.GetChatClient(name, networkTimeoutSeconds);
    var monitor = serviceProvider.GetService<IAgentMessageMonitor>();
    return new TokenTrackingChatClient(client, fabrcoreAgentHost.GetHandle(), monitor, logger);
}
```

This means **every LLM call made through the standard agent chat-client pipeline is captured automatically**. Agent authors don't call any recording API themselves. Code paths that construct a raw `IChatClient` without going through `GetChatClient` (rare, usually in tests) will not be captured — audit for this if you see missing calls.

## Attribution Precedence

When `TryRecordLlmCall` runs, it resolves `AgentHandle`, `ParentMessageId`, `TraceId`, and `OriginContext` using a fallback chain:

```
1. LlmUsageScope.Current        (set by FabrCoreAgentProxy.InternalOnMessage)
   └── provides: AgentHandle, ParentMessageId, TraceId, OriginContext = "OnMessage:<id>"

2. LlmCallContext.Current       (AsyncLocal, set by harness/callers for non-OnMessage paths)
   └── overrides: OriginContext, and supplies AgentHandle / TraceId if the scope didn't

3. TokenTrackingChatClient._agentHandle  (captured in the constructor)
   └── guarantees a correct AgentHandle; OriginContext defaults to "Background"
```

The precedence rule is subtle: **`LlmCallContext` does not block `LlmUsageScope`; it overlays on top of it.** When both are set (e.g. compaction running inside an `OnMessage`), `LlmCallContext.OriginContext` wins for tagging, while `LlmUsageScope.ParentMessageId` still flows through so the call is correlated to its parent message. This lets compaction calls appear as `OriginContext = "Compaction"` but still link back to the outbound message that triggered them.

### Where the scopes are set

| Scope | Set by | Triggered for |
|-------|--------|---------------|
| `LlmUsageScope` | `FabrCoreAgentProxy.InternalOnMessage` | Any code path that runs inside `OnMessage` (including agent-scheduled timers/reminders, which are dispatched through `InternalOnMessage` by `AgentGrain.SendTimerOrReminderMessage`) |
| `LlmCallContext` (Timer/Reminder) | `AgentGrain.SendTimerOrReminderMessage` | Before dispatching a timer/reminder synthetic message |
| `LlmCallContext` (OnEvent) | `AgentGrain.ReceivedEventMessage` | Before dispatching `fabrcoreAgentProxy.InternalOnEvent(request)` |
| `LlmCallContext` (Compaction) | `CompactionService.SummarizeAsync` | Before building the compaction chat client |
| Agent-author custom | Agent code | Any custom background path (Task.Run, IHostedService, event handlers) |

## Origin Tags — Reference

| `OriginContext` | When it appears |
|---|---|
| `OnMessage:<id>` | Default for every call inside a normal agent message flow |
| `OnEvent:<type>` | LLM call made while processing an `OnEvent` dispatch |
| `Timer:<name>` | LLM call made while processing a timer tick (which also runs inside `OnMessage`) |
| `Reminder:<name>` | LLM call made while processing a reminder tick |
| `Compaction` | LLM call made by `CompactionService` to summarize chat history |
| `Background` | LLM call made with neither an `LlmUsageScope` nor an `LlmCallContext` active |
| Custom | Whatever string you pass to `LlmCallContext.Begin(handle, originContext)` |

## Streaming Aggregation

`GetStreamingResponseAsync` is wrapped differently from the non-streaming path. The wrapper:

1. Materializes the request messages once (only if `CapturePayloads == true`).
2. Accumulates every `ChatResponseUpdate` into a local list (only if `LlmCaptureOptions.Enabled == true`).
3. Yields each update through to the caller unchanged — callers see no latency or buffering.
4. On stream completion:
   - Aggregates `UsageContent` across all updates into a single `UsageDetails`.
   - Rebuilds a synthetic `ChatResponse` via `Microsoft.Extensions.AI`'s `ToChatResponse()` extension so `RequestMessages` / `ResponseMessages` / `ResponseText` / `ToolCalls` can be snapshotted.
   - Records one `MonitoredLlmCall` with `Streaming = true`.

A single streaming call produces exactly one `MonitoredLlmCall`, not one per update.

## Error and Cancellation Handling

- **LLM throws mid-call (non-streaming):** recorded with `ErrorMessage` populated; the exception still propagates to the caller.
- **LLM throws mid-stream:** the `await foreach` wrapper will propagate the exception. The aggregation block after the loop will not run in that case. If you need to capture partial streams on error, add a try/catch around the foreach — today the implementation records the call only on successful stream completion, matching the behavior of the pre-change `LlmUsageScope` integration.
- **Cancellation:** `OperationCanceledException` propagates normally; no partial call is recorded.
- **Fire-and-forget monitor failures:** `RecordLlmCallAsync` is invoked via `_ = monitor.RecordLlmCallAsync(call).ContinueWith(..., OnlyOnFaulted)`. A slow monitor will not block an LLM response, and a throwing monitor will not fail the call — failures are logged via the client's `ILogger`.

## Payload Capture, Redaction, and Size Caps

Payload capture is opt-in because prompts and responses often contain PII, credentials, or customer data.

```csharp
options.UseInMemoryAgentMessageMonitor(capture =>
{
    capture.CapturePayloads = true;
    capture.MaxPayloadChars = 4_000;    // per message text
    capture.MaxToolArgsChars = 2_000;   // per tool call arguments blob
    capture.MaxBufferedCalls = 1_000;   // lower when payloads are large
    capture.Redact = s =>
    {
        // Strip obvious secrets before storage.
        s = Regex.Replace(s, @"sk-[A-Za-z0-9]{16,}", "sk-***");
        s = Regex.Replace(s, @"Bearer\s+[A-Za-z0-9._-]+", "Bearer ***");
        return s;
    };
});
```

The capture pipeline, per string field:

1. **Redact first.** `Redact` is invoked with the raw string. If it throws, the exception is swallowed — redaction must never break capture.
2. **Truncate after.** If the redacted string exceeds `MaxPayloadChars` (or `MaxToolArgsChars` for tool args), it is cut to length and `Truncated = true` is set on the snapshot.

This ordering means the redactor sees the full string, so patterns that might span the boundary are still scrubbed before they are cut.

### What's snapshotted when `CapturePayloads == true`

- `RequestMessages` — every `ChatMessage` passed into the call, with text content concatenated per message, redacted, and truncated.
- `ResponseMessages` — every `ChatMessage` in the response, same treatment.
- `ResponseText` — the response's concatenated text (via `ChatResponse.Text`), redacted and truncated.
- `ToolCalls` — every `FunctionCallContent` in the response, with arguments JSON-serialized, redacted, and truncated to `MaxToolArgsChars`.

Non-text content parts (images, audio, binary) are not captured — `ContentCount` on each snapshot tells you how many content parts were on the original message so you can see when something was present but not stored.

## Zero-Cost When Disabled

When the null monitor is active (or `LlmCaptureOptions.Enabled == false`):

- `TryRecordLlmCall` short-circuits immediately after checking `_monitor` and `_capture`.
- No request materialization happens.
- No update accumulation happens in the streaming path.
- No snapshot allocation happens.

The metadata-only default (`CapturePayloads == false`) has a slightly higher but still small overhead: it builds a `MonitoredLlmCall` struct per call with tokens / model / duration, but skips all the payload walking.

## Correlating LLM Calls with Messages

When a call has `ParentMessageId` set, you can join it back to the outbound `MonitoredMessage` recorded by `AgentGrain.OnMessage`. A typical viewer layout:

```
[14:23:01] OUT  user1:agent -> user1:client  kind=Response  msg=MSG123   (LlmUsage: 2 calls, 450in/120out)
           └── [14:23:01] LLM OnMessage:MSG123  model=gpt-4  350in/90out  streaming=true
           └── [14:23:02] LLM OnMessage:MSG123  model=gpt-4  100in/30out  streaming=false
```

Multiple LLM calls per outbound message is normal — agents often call the LLM more than once (planner + executor, tool loops, compaction, etc.).

## Testing Checklist

When adding or modifying capture behavior, verify:

1. **OnMessage call:** exactly one `MonitoredLlmCall` per LLM round-trip; `OriginContext` starts with `"OnMessage:"`; `ParentMessageId` equals the inbound `MonitoredMessage.Id`; the existing outbound `MonitoredMessage.LlmUsage` is still populated (no regression in the `LlmUsageScope` path).
2. **Streaming call:** `Streaming == true`, tokens aggregated correctly across updates, `ResponseText` is the concatenated stream text.
3. **OnEvent call:** `OriginContext` starts with `"OnEvent:"`, `ParentMessageId == null`.
4. **Timer/Reminder:** `OriginContext` starts with `"Timer:"` / `"Reminder:"`, `AgentHandle` matches the grain handle.
5. **Compaction:** `OriginContext == "Compaction"`, inherits `AgentHandle` / `TraceId` from the surrounding `OnMessage` scope when present.
6. **Metadata-only default:** with `CapturePayloads = false`, `RequestMessages`/`ResponseMessages`/`ResponseText`/`ToolCalls` are all `null`; `Model`/`DurationMs`/`FinishReason`/tokens populated.
7. **Redaction + cap:** with a `Redact` regex and a tight `MaxPayloadChars`, verify secrets are scrubbed and `Truncated` is set on snapshots that hit the cap.
8. **Error path:** a throwing chat client produces a `MonitoredLlmCall` with `ErrorMessage` set and the exception still propagates.
9. **Null monitor:** with no monitor registered (or the null monitor), capture is a no-op with zero allocations on the hot path.

## Known Limitations / Follow-ups

- **Direct `IChatClient` construction is not captured.** Any code that news up a chat client without going through `FabrCoreAgentProxy.GetChatClient` bypasses the wrapper. Audit with a grep for raw `IChatClient` usage.
- **No Host HTTP API yet.** `IAgentMessageMonitor` is consumed in-process. Adding HTTP endpoints on `FabrCore.Host` and client methods on `FabrCoreHostApiClient` for `GetLlmCallsAsync` is a natural follow-up.
- **Partial-stream error capture.** The streaming path does not currently record a `MonitoredLlmCall` if the stream throws mid-enumeration. Wrap the `await foreach` in a try/catch if you want partial captures.
- **Ordering.** Recording is fire-and-forget. A caller reading `GetLlmCallsAsync` immediately after `RunAsync` returns may see the call land a few moments later. Treat the LLM track as eventually consistent.
- **Payload memory.** Even with the default `MaxBufferedCalls = 2000`, full payloads can add up. Tune down for busy agents, or front the monitor with an external store (database, log aggregator) instead of the in-memory implementation.
