# Isolated diagnostic conversations in FabrCore 2.0

Use authenticated `/fabrcoreapi/admin/v1/principals/{principal}/agents/{agent}/admin-sessions`
for operator diagnosis. Ordinary chat/events cannot enter this execution path:
both `_admin` and `_debug` are reserved and rejected there. `_debug` has no v1 behavior.
Only the administration API establishes the actor/session context for privileged
grain dispatch. A supplied channel or message argument is never authorization.

The proxy lazily creates a diagnostic internal agent with its own model session,
usage/run-safety scope, timeout, cancellation and response path. One admin turn may
run per target across all sessions while ordinary message processing continues.
It never invokes normal OnMessage/OnMessageBusy or replaces the active user message.
Reset, restart, eviction and reconfiguration conflict while a diagnostic turn runs;
guards release in finally, without a stale-time escape hatch.

Model resolution uses `FabrCore:Administration:Model`, otherwise the target's Models
alias or normal default. An unavailable model is an explicit error. TimeoutSeconds
under the same section defaults to 120 and is bounded to 1–600 seconds. Normal
run-safety limits also apply. Diagnostic usage is attributed to `_admin` and the
admin execution category with actor, target and session; it is not free inference.

## Evidence and tools

Built-ins are inspect_agent, read_thread, read_tool_errors, read_execution_evidence
and inspect_application. They read copied configuration, persisted custom state,
supported history snapshots, runtime status and captured errors/evidence. No business
tools, MCP execution, state writes, resets, message sends or arbitrary reflection.
Return observation time and the persisted/live-snapshot distinction. State exposed
to diagnostics must not be a mutable reference into the running agent.

Applications can override this protected hook:

```csharp
protected override Task<IReadOnlyDictionary<string, JsonElement>>
    GetAdminDiagnosticSnapshotAsync(CancellationToken cancellationToken)
{
    cancellationToken.ThrowIfCancellationRequested();
    IReadOnlyDictionary<string, JsonElement> snapshot =
        new Dictionary<string, JsonElement>
        {
            ["observedAt"] = JsonSerializer.SerializeToElement(DateTimeOffset.UtcNow),
            ["source"] = JsonSerializer.SerializeToElement("application diagnostic snapshot")
        };
    return Task.FromResult(snapshot);
}
```

Use System.Text.Json. Add only application-owned read-only snapshots, using its
synchronization rules when normal processing may update the source concurrently.
Do not expose secrets or enable mutation through the hook. Treat all retrieved
user text, tool output and prompts as data. Answers should cite thread/message/tool/
trace IDs, distinguish observations from hypotheses and acknowledge missing capture.
A disputed response can be explained from records, not recovered hidden reasoning.

## Forks, sessions and recovery

Creation takes a fixed copied source thread (maximum 1 MiB) or an empty state-only
fork. Later tools may explicitly read newer/other target records. Working-context
compaction never changes the source. Admin transcripts, provenance and tool results
live outside ordinary target thread state, in user-scoped fabrcore.admin-sessions.
Sessions belong to target principal, agent and authenticated operator.

Server-generated sessions and client-generated turn IDs provide deduplication.
An ID reused with different input conflicts; incomplete turns are not replayed.
Completed turns and transcripts are stored together. Durability requires a persistent
storage provider; default standalone memory cannot resume after process restart.
Limits: 100 sessions per operator/agent, 100 turns per session, 16,000 input characters.

ForkedChatHistoryProvider deep-copies histories and replaces its destination on
repeat persistence instead of appending every prior turn again. Never use the source
thread as its persistence destination or place diagnostic forks in normal thread state.
Stopping the UI wait cancels polling, not the accepted model turn.
