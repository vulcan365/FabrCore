#pragma warning disable MAAI001 // Harness providers (LoopAgent, BackgroundAgentsProvider, loop evaluators) are for evaluation purposes only and may change.
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;

namespace FabrCore.Sdk;

/// <summary>
/// A composed harness agent together with its session and the plumbing that keeps that session durable.
/// </summary>
/// <remarks>
/// Returned by <c>FabrCoreAgentProxy.CreateFabrCoreHarnessAgent</c>. Run through
/// <see cref="RunAsync(string, AgentRunOptions?, CancellationToken)"/> rather than calling
/// <see cref="Agent"/> directly — the wrapper snapshots the session afterwards, which is what carries
/// todos across user turns and grain deactivation.
/// </remarks>
public sealed class FabrCoreHarnessResult
{
    /// <summary>Snapshots above this size are written but logged as a warning.</summary>
    public const int SnapshotWarnBytes = 256 * 1024;

    /// <summary>Snapshots above this size are refused; the last good snapshot is kept instead.</summary>
    public const int SnapshotMaxBytes = 1024 * 1024;

    private readonly IHarnessSessionStore? store;
    private readonly string stateKey;
    private readonly ILogger? logger;
    private readonly string agentHandle;

    internal FabrCoreHarnessResult(
        FabrCoreHarnessAgent agent,
        AgentSession session,
        string threadId,
        string agentHandle,
        FabrCoreChatHistoryProvider? chatHistoryProvider = null,
        IHarnessSessionStore? store = null,
        bool sessionRestored = false,
        int delegationsLostOnRestore = 0,
        ILogger? logger = null)
    {
        Agent = agent;
        Session = session;
        ThreadId = threadId;
        ChatHistoryProvider = chatHistoryProvider;
        SessionRestored = sessionRestored;
        DelegationsLostOnRestore = delegationsLostOnRestore;

        this.agentHandle = agentHandle;
        this.store = store;
        this.logger = logger;
        stateKey = HarnessSessionSnapshot.KeyFor(threadId);
    }

    /// <summary>The composed harness agent.</summary>
    public FabrCoreHarnessAgent Agent { get; }

    /// <summary>The live session. Replaced by <see cref="ClearHarnessSessionAsync"/>.</summary>
    public AgentSession Session { get; private set; }

    /// <summary>The conversation thread this harness runs on.</summary>
    public string ThreadId { get; }

    /// <summary>The Orleans-backed history provider, when one was supplied.</summary>
    public FabrCoreChatHistoryProvider? ChatHistoryProvider { get; }

    /// <summary>The todo provider, or <see langword="null"/> when todos are disabled.</summary>
    public TodoProvider? Todos => Agent.Todos;

    /// <summary>The background-agent provider, or <see langword="null"/> when none were configured.</summary>
    public BackgroundAgentsProvider? BackgroundAgents => Agent.BackgroundAgents;

    /// <summary>True when this harness resumed a persisted session rather than starting fresh.</summary>
    public bool SessionRestored { get; }

    /// <summary>
    /// How many delegations were mid-flight when the restored snapshot was taken. Those are unrecoverable —
    /// the provider marks them <c>Lost</c> — so they are counted here to be reported rather than silently dropped.
    /// </summary>
    public int DelegationsLostOnRestore { get; }

    /// <summary>
    /// True when session snapshots are being persisted. False means harness state lives only for the
    /// lifetime of this agent instance.
    /// </summary>
    public bool IsSessionPersistent => store is not null;

    /// <summary>Runs the harness on a single user message, then snapshots the session.</summary>
    public Task<AgentResponse> RunAsync(
        string message,
        AgentRunOptions? options = null,
        CancellationToken cancellationToken = default) =>
        RunAsync([new ChatMessage(ChatRole.User, message)], options, cancellationToken);

    /// <summary>Runs the harness, then snapshots the session.</summary>
    /// <remarks>
    /// The snapshot is taken even when the run throws or is cancelled — partial progress (todos added,
    /// steps completed) is worth keeping, and losing it would make a failed turn look like it never started.
    /// </remarks>
    public async Task<AgentResponse> RunAsync(
        IEnumerable<ChatMessage> messages,
        AgentRunOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        try
        {
            return await Agent.RunAsync(messages, Session, options, cancellationToken);
        }
        finally
        {
            // Deliberately not passing cancellationToken: a cancelled run still deserves its state persisted.
            await SnapshotSessionAsync();
        }
    }

    /// <summary>Runs the harness in streaming mode, then snapshots the session once enumeration completes.</summary>
    public async IAsyncEnumerable<AgentResponseUpdate> RunStreamingAsync(
        IEnumerable<ChatMessage> messages,
        AgentRunOptions? options = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        try
        {
            await foreach (var update in Agent.RunStreamingAsync(messages, Session, options, cancellationToken))
            {
                yield return update;
            }
        }
        finally
        {
            await SnapshotSessionAsync();
        }
    }

    /// <summary>Todo items not yet completed. Empty when todos are disabled.</summary>
    public async Task<IReadOnlyList<TodoItem>> GetRemainingTodosAsync(CancellationToken cancellationToken = default)
        => Todos is null ? [] : await Todos.GetRemainingTodosAsync(Session, cancellationToken);

    /// <summary>Every todo item, complete or not. Empty when todos are disabled.</summary>
    public async Task<IReadOnlyList<TodoItem>> GetAllTodosAsync(CancellationToken cancellationToken = default)
        => Todos is null ? [] : await Todos.GetAllTodosAsync(Session, cancellationToken);

    /// <summary>Delegations still running right now. Empty when no background agents are configured.</summary>
    public IReadOnlyList<BackgroundTaskInfo> GetRunningDelegations()
        => BackgroundAgents?.GetIncompleteTasks(Session) ?? [];

    /// <summary>
    /// A one-line note about delegations lost to the last restore, or <see langword="null"/> when there were
    /// none. Append it to a response so the loss is visible to whoever asked for the work.
    /// </summary>
    public string? DescribeLostDelegations() => DelegationsLostOnRestore switch
    {
        0 => null,
        1 => "1 delegation that was still running before this agent restarted could not be recovered and was not completed.",
        var n => $"{n} delegations that were still running before this agent restarted could not be recovered and were not completed."
    };

    /// <summary>
    /// Serializes the session and writes it to durable storage.
    /// </summary>
    /// <returns>
    /// True when a snapshot was persisted. False when persistence is disabled, the snapshot exceeded
    /// <see cref="SnapshotMaxBytes"/>, or the write failed — all three are logged, never thrown, because this
    /// runs on the completion path of every turn including failed ones.
    /// </returns>
    public async Task<bool> SnapshotSessionAsync()
    {
        if (store is null)
        {
            return false;
        }

        try
        {
            var payload = await Agent.SerializeSessionAsync(Session);
            var bytes = Encoding.UTF8.GetByteCount(payload.GetRawText());

            if (bytes > SnapshotMaxBytes)
            {
                logger?.LogError(
                    "Harness session snapshot refused: {Bytes} bytes exceeds the {Limit} byte limit - Handle: {Handle}, ThreadId: {ThreadId}. The previous snapshot is retained.",
                    bytes, SnapshotMaxBytes, agentHandle, ThreadId);
                return false;
            }

            if (bytes > SnapshotWarnBytes)
            {
                logger?.LogWarning(
                    "Harness session snapshot is large: {Bytes} bytes - Handle: {Handle}, ThreadId: {ThreadId}. Every write rewrites the whole grain state blob.",
                    bytes, agentHandle, ThreadId);
            }

            await store.WriteAsync(stateKey, new HarnessSessionSnapshot
            {
                Version = HarnessSessionSnapshot.CurrentVersion,
                ThreadId = ThreadId,
                SavedUtc = DateTimeOffset.UtcNow,
                Payload = payload
            });

            logger?.LogDebug(
                "Harness session snapshot saved - Handle: {Handle}, ThreadId: {ThreadId}, Bytes: {Bytes}",
                agentHandle, ThreadId, bytes);

            return true;
        }
        catch (Exception ex)
        {
            logger?.LogError(
                ex,
                "Failed to snapshot harness session - Handle: {Handle}, ThreadId: {ThreadId}. Harness state may reset on the next activation.",
                agentHandle, ThreadId);
            return false;
        }
    }

    /// <summary>
    /// Discards the persisted snapshot and starts a fresh session: no todos, no delegation records, no
    /// standing harness state. Conversation history is untouched — clear that separately if that is what
    /// you meant.
    /// </summary>
    public async Task ClearHarnessSessionAsync(CancellationToken cancellationToken = default)
    {
        if (store is not null)
        {
            try
            {
                await store.DeleteAsync(stateKey);
            }
            catch (Exception ex)
            {
                logger?.LogError(
                    ex,
                    "Failed to delete harness session snapshot - Handle: {Handle}, ThreadId: {ThreadId}",
                    agentHandle, ThreadId);
            }
        }

        Session = await Agent.CreateSessionAsync(cancellationToken);

        logger?.LogInformation(
            "Harness session cleared - Handle: {Handle}, ThreadId: {ThreadId}",
            agentHandle, ThreadId);
    }
}
