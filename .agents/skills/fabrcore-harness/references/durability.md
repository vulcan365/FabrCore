# Harness Session Durability

Todos, delegation records, and loop position live in the Microsoft Agent Framework's `AgentSession`
state bag. FabrCore does not persist that bag for ordinary agents, so without the machinery
described here every harness feature would reset on each grain activation — the agent would look
amnesiac while appearing to work.

## What is stored, and where

| | |
|---|---|
| Custom state key | `_harness_session:{threadId}` |
| Archive key on corruption | `_harness_session_corrupt:{threadId}` |
| Envelope | `HarnessSessionSnapshot` — `Version`, `ThreadId`, `SavedUtc`, `Payload` |
| Payload | `AIAgent.SerializeSessionAsync(session)` — `{ conversationId, stateBag }` |
| Written by | `FabrCoreHarnessResult.RunAsync`, on the completion path of every turn |
| Read by | `CreateFabrCoreHarnessAgent`, during `OnInitialize` |

**Conversation history is not in the snapshot.** `FabrCoreChatHistoryProvider` persists messages to
Orleans `MessageThreads`, not the state bag, so the payload holds only harness provider state. Two
consequences: snapshots stay kilobyte-scale, and a snapshot lost to corruption costs todos but never
conversation continuity.

Substituting `options.ChatHistoryProvider` with an in-memory provider reverses this — that provider
*does* write messages into the state bag, and your snapshots will grow with the transcript.

## Lifecycle

```
OnInitialize   -> CreateFabrCoreHarnessAgent
                    -> TryGetStateAsync("_harness_session:main")
                    -> DeserializeSessionAsync(payload)   [or CreateSessionAsync on miss/failure]

OnMessage      -> FabrCoreHarnessResult.RunAsync
                    -> LoopAgent.RunAsync(...)
                    -> finally: SerializeSessionAsync -> SetState -> FlushStateAsync
```

The snapshot is taken in a `finally`, so a run that throws or is cancelled still persists whatever
progress it made. Losing three completed todos because the fourth step failed would make a partial
turn indistinguishable from one that never started.

`SnapshotSessionAsync` never throws. It returns `false` and logs on failure, because it runs on the
completion path of every turn, including turns that are already failing.

## Why there is no grain change

The design proposal in `docs/harness-adoption-plan.md` §5.2 called for a custom-state flush in
`AgentGrain.HandlePrimaryMessage`'s `finally`. That was **not** implemented: it would have added a
whole-blob `WriteStateAsync` per turn for *every* agent with pending state, harness or not.

Instead the harness flushes its own snapshot through `IHarnessSessionStore`, and the pre-existing
deactivation flush (`AgentGrain.OnDeactivateAsync`) remains the backstop. Non-harness agents are
completely unaffected.

The cost is real and worth stating: each harness turn triggers one `MergeCustomStateAsync`, which
rewrites the entire `AgentGrainState` blob — configuration and all message threads included. That is
a fair trade for an agent doing multi-step work. It is a bad trade for a chatty, high-frequency
agent, which should either set `_HarnessSessionPersistence=false` or not use the harness at all.

## Size limits

Checked on every snapshot, against the UTF-8 byte count of the serialized payload:

| Threshold | Constant | Behavior |
|-----------|----------|----------|
| 256 KB | `FabrCoreHarnessResult.SnapshotWarnBytes` | Written, with a warning |
| 1 MB | `FabrCoreHarnessResult.SnapshotMaxBytes` | **Refused**; the previous good snapshot is retained and an error is logged |

A harness session should never approach these — a few hundred todos is still tens of kilobytes. If
you see the warning, something is writing bulk data into the state bag: usually an in-memory chat
history provider, occasionally a custom `AIContextProvider`.

## Corruption handling

An unreadable snapshot is **archived, not deleted**:

1. The raw element is copied to `_harness_session_corrupt:{threadId}`.
2. The live key is removed.
3. A fresh session is created and the run proceeds normally.
4. `FabrCoreHarnessResult.SessionRestored` is `false`.

Harness state resetting is survivable. Destroying the evidence of *why* it reset would make the next
occurrence undiagnosable, which is why the bad payload is kept.

An envelope `Version` mismatch is handled differently: logged, the key removed, a fresh session
created — but **not** archived. Version drift is expected during upgrades, not a defect, and there
is nothing to investigate.

## In-flight delegations cannot survive

`BackgroundAgentRuntimeState` holds live `Task<AgentResponse>` and live child `AgentSession` objects
behind `[JsonIgnore]`. They cannot be serialized, and after any snapshot round-trip the provider
marks every previously-running task `Lost` on its next refresh.

This is upstream framework behavior, not something FabrCore can fix — the work is genuinely gone,
because the `Task` driving it died with the grain activation.

What FabrCore adds is honesty about it:

```csharp
// Count taken during restore, from the snapshot itself.
if (harness.DescribeLostDelegations() is { } note)
{
    text += $"{Environment.NewLine}{Environment.NewLine}{note}";
}
```

| Member | Purpose |
|--------|---------|
| `FabrCoreHarnessResult.DelegationsLostOnRestore` | Count of delegations running when the snapshot was taken |
| `FabrCoreHarnessResult.DescribeLostDelegations()` | A ready-to-append sentence, or `null` when there were none |

The count is read from the snapshot's own JSON, defensively — the provider exposes no reader for
lost tasks. An upstream shape change degrades this to reporting zero, never to a failure.

**Design around it.** Prefer delegations that complete within a turn; the loop is already built to
wait for them. If a delegation is genuinely long-running, model it as a durable request/response
between agents (see **fabrcore-messaging**) rather than a background task the harness must babysit
across a deactivation.

## Interaction with reset, thread-clear, and eviction

| Operation | Effect on harness state |
|-----------|------------------------|
| Blueprint re-apply | **None.** The agent is already tracked; `ForceReconfigure` is forced false. See `references/configuration.md` |
| `POST /agent/create` with `ForceReconfigure: true` | Reconfigures and rebuilds the proxy. The snapshot survives in custom state and is restored by the new `OnInitialize` |
| Agent reset | `OnReset` runs, then **all custom state is cleared** — todos and delegation records go with it. Conversation history is cleared too |
| `ClearThreadAsync(threadId)` | Clears conversation history but **leaves the snapshot**. The agent forgets the conversation while still holding the todo list — call `ClearHarnessSessionAsync()` too if you want both gone |
| Agent eviction | Everything is destroyed |
| `FabrCoreHarnessResult.ClearHarnessSessionAsync()` | Deletes the snapshot and starts a fresh session. Conversation history untouched |

The thread-clear row is the trap. "Clear the conversation" in a UI usually means both:

```csharp
await fabrcoreAgentHost.ClearThreadAsync("main");
await harness.ClearHarnessSessionAsync();
```

## Custom stores

`IHarnessSessionStore` is two methods:

```csharp
public interface IHarnessSessionStore
{
    Task WriteAsync(string key, HarnessSessionSnapshot snapshot);
    Task DeleteAsync(string key);
}
```

`FabrCoreAgentProxy` implements it privately over `SetState` / `RemoveState` / `FlushStateAsync`.
Supply your own only when hosting a harness outside an agent grain — a console tool, a background
service, a test. On the `AsFabrCoreHarnessAgent` path there is no store at all unless you construct
one, so `IsSessionPersistent` is `false` and nothing is written.

Restore is deliberately *not* on the interface. It needs non-throwing reads and the ability to
archive an unreadable payload intact, both of which are proxy concerns.
