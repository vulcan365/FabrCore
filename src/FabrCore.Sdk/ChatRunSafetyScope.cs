using FabrCore.Core.Monitoring;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using System.Text.Json;

namespace FabrCore.Sdk;

public enum RunStopReason
{
    None = 0,
    PromptTooLarge = 1,
    TurnBudgetExceeded = 2,

    /// <summary>
    /// No longer produced. Mid-turn history compaction was retired in favour of context compaction,
    /// which bounds every call in the tool loop without rewriting the persisted thread mid-run.
    /// </summary>
    /// <remarks>
    /// Retained so stop reasons recorded on historical messages still parse.
    /// </remarks>
    [Obsolete("Mid-turn history compaction was retired; this reason is never produced. See ContextCompaction.")]
    MidTurnCompactionFailed = 3
}

public sealed class FabrCoreRunStoppedException : Exception
{
    public FabrCoreRunStoppedException(
        RunStopReason reason,
        string message,
        long actualPromptInputTokens,
        long turnCumulativeInputTokens,
        int llmCalls)
        : base(message)
    {
        Reason = reason;
        ActualPromptInputTokens = actualPromptInputTokens;
        TurnCumulativeInputTokens = turnCumulativeInputTokens;
        LlmCalls = llmCalls;
    }

    public RunStopReason Reason { get; }
    public long ActualPromptInputTokens { get; }
    public long TurnCumulativeInputTokens { get; }
    public int LlmCalls { get; }
}

public sealed record ChatRunSafetyConfig
{
    public int PerTurnMaxInputTokens { get; init; }
    public int MaxPromptInputTokens { get; init; }
    public string RunawayBudgetBehavior { get; init; } = "StopWithDiagnostic";

    public bool StopWithDiagnostic =>
        string.IsNullOrWhiteSpace(RunawayBudgetBehavior)
        || string.Equals(RunawayBudgetBehavior, "StopWithDiagnostic", StringComparison.OrdinalIgnoreCase);
}

/// <summary>
/// Rung 5 of the compaction ladder: the budget stop. Tracks prompt-size and cumulative-token guardrails
/// for one agent turn and aborts the run rather than letting it overspend.
/// </summary>
/// <remarks>
/// <para>
/// This scope no longer compacts anything. Bounding what a single call sees is layer 1's job
/// (<see cref="ContextCompaction"/>, which runs before every call in the tool loop and is free and
/// reversible); bounding what is persisted is layer 2's job (<see cref="CompactionService"/>, between
/// turns). Run safety is what remains when both have already done their work and the run is still too
/// expensive — at that point the correct action is to stop with a diagnostic, not to rewrite history
/// mid-run underneath a live context index.
/// </para>
/// <para>
/// <see cref="IsCompacting"/> is still honoured: layer 2's summarization LLM call runs inside this scope
/// and must not be charged against the turn budget it is trying to protect.
/// </para>
/// </remarks>
public sealed class ChatRunSafetyScope : IDisposable
{
    private static readonly AsyncLocal<ChatRunSafetyScope?> CurrentScope = new();

    private readonly IAgentMessageMonitor? _monitor;
    private readonly ILogger? _logger;

    private int _compactionDepth;

    private long _turnCumulativeInputTokens;
    private long _actualPromptInputTokens;
    private long _maxPromptInputTokensPerCall;
    private int _llmCalls;

    private ChatRunSafetyScope(
        string? agentHandle,
        string? parentMessageId,
        string? traceId,
        ChatRunSafetyConfig config,
        IAgentMessageMonitor? monitor,
        ILogger? logger)
    {
        AgentHandle = agentHandle;
        ParentMessageId = parentMessageId;
        TraceId = traceId;
        Config = config;
        _monitor = monitor;
        _logger = logger;
    }

    public static ChatRunSafetyScope? Current => CurrentScope.Value;

    public string? AgentHandle { get; }
    public string? ParentMessageId { get; }
    public string? TraceId { get; }
    public ChatRunSafetyConfig Config { get; }
    public RunStopReason StopReason { get; private set; }
    public long TurnCumulativeInputTokens => Interlocked.Read(ref _turnCumulativeInputTokens);
    public long ActualPromptInputTokens => Interlocked.Read(ref _actualPromptInputTokens);
    public long MaxPromptInputTokensPerCall => Interlocked.Read(ref _maxPromptInputTokensPerCall);
    public int LlmCalls => Volatile.Read(ref _llmCalls);

    /// <summary>
    /// True while a history-compaction LLM call is in flight. Calls made in this window bypass the
    /// budget guards — compaction exists to reduce spend and must never be aborted by the spend limit.
    /// </summary>
    public bool IsCompacting => Volatile.Read(ref _compactionDepth) > 0;

    public static ChatRunSafetyScope Begin(
        string? agentHandle,
        string? parentMessageId,
        string? traceId,
        ChatRunSafetyConfig config,
        IAgentMessageMonitor? monitor,
        ILogger? logger)
    {
        var scope = new ChatRunSafetyScope(agentHandle, parentMessageId, traceId, config, monitor, logger);
        CurrentScope.Value = scope;
        return scope;
    }

    /// <summary>
    /// Marks the enclosed work as history compaction, exempting its LLM calls from the budget guards.
    /// Re-entrant; the exemption lifts when the outermost scope is disposed.
    /// </summary>
    public IDisposable BeginHistoryCompaction() => new HistoryCompactionScope(this);

    public async Task<ChatRunSafetyCallInfo> PrepareCallAsync(
        IReadOnlyList<ChatMessage> requestMessages,
        bool streaming,
        CancellationToken cancellationToken)
    {
        var actualPromptTokens = EstimateTokens(requestMessages);
        Interlocked.Exchange(ref _actualPromptInputTokens, actualPromptTokens);
        UpdateMax(ref _maxPromptInputTokensPerCall, actualPromptTokens);

        await RecordDiagnosticAsync("pre-call-check", new Dictionary<string, string>
        {
            ["streaming"] = streaming.ToString(),
            ["actual_prompt_input_tokens"] = actualPromptTokens.ToString(),
            ["turn_cumulative_input_tokens"] = TurnCumulativeInputTokens.ToString(),
            ["max_prompt_input_tokens"] = Config.MaxPromptInputTokens.ToString(),
            ["per_turn_max_input_tokens"] = Config.PerTurnMaxInputTokens.ToString()
        });

        if (Config.MaxPromptInputTokens > 0 && actualPromptTokens > Config.MaxPromptInputTokens)
        {
            StopReason = RunStopReason.PromptTooLarge;
            await RecordDiagnosticAsync("prompt-too-large", new Dictionary<string, string>
            {
                ["actual_prompt_input_tokens"] = actualPromptTokens.ToString(),
                ["max_prompt_input_tokens"] = Config.MaxPromptInputTokens.ToString()
            });

            if (Config.StopWithDiagnostic)
                ThrowStopped(RunStopReason.PromptTooLarge, actualPromptTokens);
        }

        var projectedTurnTokens = TurnCumulativeInputTokens + actualPromptTokens;
        if (Config.PerTurnMaxInputTokens > 0 && projectedTurnTokens > Config.PerTurnMaxInputTokens)
        {
            StopReason = RunStopReason.TurnBudgetExceeded;
            await RecordDiagnosticAsync("turn-budget-exceeded", new Dictionary<string, string>
            {
                ["actual_prompt_input_tokens"] = actualPromptTokens.ToString(),
                ["turn_cumulative_input_tokens"] = TurnCumulativeInputTokens.ToString(),
                ["projected_turn_input_tokens"] = projectedTurnTokens.ToString(),
                ["per_turn_max_input_tokens"] = Config.PerTurnMaxInputTokens.ToString(),
                ["llm_calls"] = LlmCalls.ToString()
            });

            if (Config.StopWithDiagnostic)
                ThrowStopped(RunStopReason.TurnBudgetExceeded, actualPromptTokens);
        }

        return new ChatRunSafetyCallInfo(
            actualPromptTokens,
            TurnCumulativeInputTokens,
            MaxPromptInputTokensPerCall);
    }

    public void RecordCompletedCall(long inputTokens, long fallbackPromptEstimate)
    {
        var consumed = inputTokens > 0 ? inputTokens : fallbackPromptEstimate;
        if (consumed > 0)
            Interlocked.Add(ref _turnCumulativeInputTokens, consumed);

        Interlocked.Increment(ref _llmCalls);
    }

    public void ApplyTo(Dictionary<string, string> args)
    {
        if (ActualPromptInputTokens > 0)
            args["_actual_prompt_input_tokens"] = ActualPromptInputTokens.ToString();
        if (TurnCumulativeInputTokens > 0)
            args["_turn_cumulative_input_tokens"] = TurnCumulativeInputTokens.ToString();
        if (MaxPromptInputTokensPerCall > 0)
            args["_max_prompt_input_tokens_per_call"] = MaxPromptInputTokensPerCall.ToString();
        if (StopReason != RunStopReason.None)
            args["_fabrcore_run_stop_reason"] = StopReason.ToString();
    }

    private void ThrowStopped(RunStopReason reason, long actualPromptTokens)
    {
        throw new FabrCoreRunStoppedException(
            reason,
            $"FabrCore stopped the agent run before the next LLM call: {reason}.",
            actualPromptTokens,
            TurnCumulativeInputTokens,
            LlmCalls);
    }

    private Task RecordDiagnosticAsync(string type, Dictionary<string, string> args)
    {
        if (_monitor is null)
            return Task.CompletedTask;

        args["parent_message_id"] = ParentMessageId ?? "";

        return _monitor.RecordEventAsync(new MonitoredEvent
        {
            AgentHandle = AgentHandle,
            Type = $"run-safety.{type}",
            Source = "FabrCore.Sdk",
            Subject = ParentMessageId,
            Args = args,
            EventTime = DateTimeOffset.UtcNow,
            TraceId = TraceId
        });
    }

    public void Dispose()
    {
        CurrentScope.Value = null;
    }

    public static long EstimateTokens(IEnumerable<ChatMessage> messages)
    {
        long chars = 0;
        foreach (var message in messages)
        {
            chars += message.Role.Value?.Length ?? 0;
            chars += message.AuthorName?.Length ?? 0;

            foreach (var content in message.Contents)
                chars += EstimateContentChars(content);
        }

        return Math.Max(1, chars / 4);
    }

    private static long EstimateContentChars(AIContent content)
    {
        return content switch
        {
            TextContent text => text.Text?.Length ?? 0,
            FunctionCallContent call => (call.Name?.Length ?? 0) + SafeSerializedLength(call.Arguments),
            FunctionResultContent result => result.Result?.ToString()?.Length ?? 0,
            UsageContent => 0,
            _ => SafeSerializedLength(content)
        };
    }

    private static int SafeSerializedLength(object? value)
    {
        if (value is null)
            return 0;

        try
        {
            return JsonSerializer.Serialize(value).Length;
        }
        catch
        {
            return value.ToString()?.Length ?? 0;
        }
    }

    private static void UpdateMax(ref long target, long candidate)
    {
        long current;
        while (candidate > (current = Interlocked.Read(ref target)))
        {
            if (Interlocked.CompareExchange(ref target, candidate, current) == current)
                break;
        }
    }

    private sealed class HistoryCompactionScope : IDisposable
    {
        private readonly ChatRunSafetyScope _owner;
        private bool _disposed;

        public HistoryCompactionScope(ChatRunSafetyScope owner)
        {
            _owner = owner;
            Interlocked.Increment(ref owner._compactionDepth);
        }

        public void Dispose()
        {
            if (_disposed)
                return;

            _disposed = true;
            Interlocked.Decrement(ref _owner._compactionDepth);
        }
    }
}

public sealed record ChatRunSafetyCallInfo(
    long ActualPromptInputTokens,
    long TurnCumulativeInputTokens,
    long MaxPromptInputTokensPerCall);
