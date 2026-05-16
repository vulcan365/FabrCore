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
    MidTurnCompactionFailed = 3
}

public sealed class FabrCoreRunStoppedException : Exception
{
    public FabrCoreRunStoppedException(
        RunStopReason reason,
        string message,
        long actualPromptInputTokens,
        long turnCumulativeInputTokens,
        int llmCalls,
        int checkpointCount)
        : base(message)
    {
        Reason = reason;
        ActualPromptInputTokens = actualPromptInputTokens;
        TurnCumulativeInputTokens = turnCumulativeInputTokens;
        LlmCalls = llmCalls;
        CheckpointCount = checkpointCount;
    }

    public RunStopReason Reason { get; }
    public long ActualPromptInputTokens { get; }
    public long TurnCumulativeInputTokens { get; }
    public int LlmCalls { get; }
    public int CheckpointCount { get; }
}

public sealed record ChatRunSafetyConfig
{
    public bool MidTurnCompactionEnabled { get; init; }
    public int PerTurnMaxInputTokens { get; init; }
    public int MaxPromptInputTokens { get; init; }
    public string RunawayBudgetBehavior { get; init; } = "StopWithDiagnostic";

    public bool StopWithDiagnostic =>
        string.IsNullOrWhiteSpace(RunawayBudgetBehavior)
        || string.Equals(RunawayBudgetBehavior, "StopWithDiagnostic", StringComparison.OrdinalIgnoreCase);
}

/// <summary>
/// Tracks prompt-size and cumulative-token guardrails for one agent turn.
/// </summary>
public sealed class ChatRunSafetyScope : IDisposable
{
    private static readonly AsyncLocal<ChatRunSafetyScope?> CurrentScope = new();

    private readonly IAgentMessageMonitor? _monitor;
    private readonly ILogger? _logger;

    private FabrCoreChatHistoryProvider? _provider;
    private CompactionConfig? _compactionConfig;
    private CompactionService? _compactionService;
    private string? _modelConfigName;
    private bool _isCompacting;
    private bool _checkpointAttemptedThisPrompt;

    private long _turnCumulativeInputTokens;
    private long _actualPromptInputTokens;
    private long _maxPromptInputTokensPerCall;
    private int _llmCalls;
    private int _checkpointCount;

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
    public int CheckpointCount => Volatile.Read(ref _checkpointCount);
    public bool IsCompacting => _isCompacting;

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

    public void RegisterCheckpointProvider(
        FabrCoreChatHistoryProvider provider,
        CompactionConfig compactionConfig,
        CompactionService? compactionService,
        string modelConfigName)
    {
        _provider = provider;
        _compactionConfig = compactionConfig;
        _compactionService = compactionService;
        _modelConfigName = modelConfigName;
        _checkpointAttemptedThisPrompt = false;
    }

    public async Task<ChatRunSafetyCallInfo> PrepareCallAsync(
        IReadOnlyList<ChatMessage> requestMessages,
        bool streaming,
        CancellationToken cancellationToken)
    {
        var actualPromptTokens = EstimateTokens(requestMessages);
        Interlocked.Exchange(ref _actualPromptInputTokens, actualPromptTokens);
        UpdateMax(ref _maxPromptInputTokensPerCall, actualPromptTokens);

        var threshold = GetCompactionThreshold();
        await RecordDiagnosticAsync("pre-call-check", new Dictionary<string, string>
        {
            ["streaming"] = streaming.ToString(),
            ["actual_prompt_input_tokens"] = actualPromptTokens.ToString(),
            ["turn_cumulative_input_tokens"] = TurnCumulativeInputTokens.ToString(),
            ["max_prompt_input_tokens"] = Config.MaxPromptInputTokens.ToString(),
            ["per_turn_max_input_tokens"] = Config.PerTurnMaxInputTokens.ToString(),
            ["compaction_threshold_tokens"] = threshold.ToString()
        });

        if (Config.MidTurnCompactionEnabled
            && threshold > 0
            && actualPromptTokens > threshold
            && !_checkpointAttemptedThisPrompt)
        {
            await TryCheckpointAsync(actualPromptTokens, threshold, cancellationToken);
            _checkpointAttemptedThisPrompt = true;
        }

        if (Config.MaxPromptInputTokens > 0 && actualPromptTokens > Config.MaxPromptInputTokens)
        {
            StopReason = RunStopReason.PromptTooLarge;
            await RecordDiagnosticAsync("prompt-too-large", new Dictionary<string, string>
            {
                ["actual_prompt_input_tokens"] = actualPromptTokens.ToString(),
                ["max_prompt_input_tokens"] = Config.MaxPromptInputTokens.ToString(),
                ["checkpoint_count"] = CheckpointCount.ToString()
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
        _checkpointAttemptedThisPrompt = false;
    }

    public void ApplyTo(Dictionary<string, string> args)
    {
        if (ActualPromptInputTokens > 0)
            args["_actual_prompt_input_tokens"] = ActualPromptInputTokens.ToString();
        if (TurnCumulativeInputTokens > 0)
            args["_turn_cumulative_input_tokens"] = TurnCumulativeInputTokens.ToString();
        if (MaxPromptInputTokensPerCall > 0)
            args["_max_prompt_input_tokens_per_call"] = MaxPromptInputTokensPerCall.ToString();
        if (CheckpointCount > 0)
            args["_fabrcore_checkpoint_count"] = CheckpointCount.ToString();
        if (StopReason != RunStopReason.None)
            args["_fabrcore_run_stop_reason"] = StopReason.ToString();
    }

    private async Task TryCheckpointAsync(long actualPromptTokens, int threshold, CancellationToken cancellationToken)
    {
        if (_provider is null || _compactionConfig is null || _compactionService is null || string.IsNullOrWhiteSpace(_modelConfigName))
            return;

        await RecordDiagnosticAsync("mid-turn-compaction-started", new Dictionary<string, string>
        {
            ["actual_prompt_input_tokens"] = actualPromptTokens.ToString(),
            ["compaction_threshold_tokens"] = threshold.ToString(),
            ["thread_id"] = _provider.ThreadId
        });

        _isCompacting = true;
        try
        {
            var result = await _compactionService.CompactIfNeededAsync(
                _provider,
                _compactionConfig,
                _modelConfigName,
                ct: cancellationToken);

            if (result.WasCompacted)
                Interlocked.Increment(ref _checkpointCount);

            await RecordDiagnosticAsync("mid-turn-compaction-completed", new Dictionary<string, string>
            {
                ["was_compacted"] = result.WasCompacted.ToString(),
                ["tokens_before_compaction"] = result.EstimatedTokensBefore.ToString(),
                ["tokens_after_compaction"] = result.EstimatedTokensAfter.ToString(),
                ["messages_before_compaction"] = result.OriginalMessageCount.ToString(),
                ["messages_after_compaction"] = result.CompactedMessageCount.ToString(),
                ["checkpoint_count"] = CheckpointCount.ToString(),
                ["thread_id"] = _provider.ThreadId
            });
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            StopReason = RunStopReason.MidTurnCompactionFailed;
            _logger?.LogWarning(ex, "Mid-turn compaction checkpoint failed for {AgentHandle}", AgentHandle);
            await RecordDiagnosticAsync("mid-turn-compaction-failed", new Dictionary<string, string>
            {
                ["error"] = ex.Message,
                ["actual_prompt_input_tokens"] = actualPromptTokens.ToString(),
                ["thread_id"] = _provider.ThreadId
            });
        }
        finally
        {
            _isCompacting = false;
        }
    }

    private int GetCompactionThreshold()
    {
        if (_compactionConfig is null || _compactionConfig.MaxContextTokens <= 0)
            return 0;

        return (int)(_compactionConfig.MaxContextTokens * _compactionConfig.Threshold);
    }

    private void ThrowStopped(RunStopReason reason, long actualPromptTokens)
    {
        throw new FabrCoreRunStoppedException(
            reason,
            $"FabrCore stopped the agent run before the next LLM call: {reason}.",
            actualPromptTokens,
            TurnCumulativeInputTokens,
            LlmCalls,
            CheckpointCount);
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
}

public sealed record ChatRunSafetyCallInfo(
    long ActualPromptInputTokens,
    long TurnCumulativeInputTokens,
    long MaxPromptInputTokensPerCall);
