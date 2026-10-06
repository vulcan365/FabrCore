using System.Runtime.CompilerServices;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;

namespace FabrCore.Sdk;

/// <summary>
/// Recovers a call the provider rejected because the prompt did not fit the model's context: records
/// what the rejection taught us, trims tool output harder than layer 1 would, and retries once.
/// </summary>
/// <remarks>
/// <para>
/// Sits outside <see cref="TokenTrackingChatClient"/>, so the rejected attempt and the retry are each
/// a fully monitored, guarded call. A second rejection — or a request with nothing left to trim —
/// ends the run with <see cref="FabrCoreRunStoppedException"/> rather than a raw provider error.
/// </para>
/// <para>
/// Only tool results are excerpted; user text, instructions and assistant prose are never touched,
/// and stored history is not changed. Anything that is not recognizably a context overflow passes
/// through untouched.
/// </para>
/// </remarks>
internal sealed class ContextOverflowRecoveryChatClient(
    IChatClient innerClient,
    ModelContextLimits limits,
    ILogger? logger = null) : DelegatingChatClient(innerClient)
{
    // Empty updates precede the first content on a streaming response; this many are held back so a
    // rejection reported in-band can still be retried before anything reaches the caller.
    private const int MaxHeldUpdates = 8;

    public override async Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        var request = messages as IReadOnlyList<ChatMessage> ?? messages.ToList();

        ContextOverflowInfo overflow;
        try
        {
            var response = await base.GetResponseAsync(request, options, cancellationToken);
            if (!ContextOverflow.TryClassify(response, out overflow))
                return response;
        }
        catch (Exception ex) when (ContextOverflow.TryClassify(ex, out overflow))
        {
            logger?.LogDebug(ex, "Provider rejected a request for '{ModelConfig}' as a context overflow", limits.ModelConfigurationName);
        }

        var retry = await PrepareRetryAsync(request, options, overflow, cancellationToken);

        try
        {
            var response = await base.GetResponseAsync(retry, options, cancellationToken);
            if (!ContextOverflow.TryClassify(response, out overflow))
                return response;
        }
        catch (Exception ex) when (ContextOverflow.TryClassify(ex, out overflow))
        {
            logger?.LogDebug(ex, "Provider rejected the trimmed retry for '{ModelConfig}'", limits.ModelConfigurationName);
        }

        throw Stopped(overflow, ChatRunSafetyScope.EstimateTokens(retry, options), "it was still too large after trimming tool output");
    }

    public override async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var request = messages as IReadOnlyList<ChatMessage> ?? messages.ToList();

        for (var attempt = 0; ; attempt++)
        {
            var held = new List<ChatResponseUpdate>();
            ContextOverflowInfo? overflow = null;
            var enumerator = base.GetStreamingResponseAsync(request, options, cancellationToken)
                .GetAsyncEnumerator(cancellationToken);
            try
            {
                // Phase 1: nothing has reached the caller yet, so a rejection can still be retried.
                var open = true;
                while (overflow is null && held.Count <= MaxHeldUpdates)
                {
                    // A yield cannot sit inside a try with a catch, so only the advance is guarded.
                    try
                    {
                        open = await enumerator.MoveNextAsync();
                    }
                    catch (Exception ex) when (held.Count == 0 && ContextOverflow.TryClassify(ex, out var thrown))
                    {
                        logger?.LogDebug(ex, "Provider rejected a streaming request for '{ModelConfig}' as a context overflow", limits.ModelConfigurationName);
                        overflow = thrown;
                        break;
                    }

                    if (!open)
                        break;

                    var update = enumerator.Current;
                    if (ContextOverflow.TryClassify(update, out var reported))
                    {
                        overflow = reported;
                        break;
                    }

                    held.Add(update);
                    if (update.Contents.Count > 0)
                        break;
                }

                if (overflow is null)
                {
                    // Phase 2: an ordinary response. Release what was held and pass the rest through.
                    foreach (var update in held)
                        yield return update;

                    while (open && await enumerator.MoveNextAsync())
                        yield return enumerator.Current;

                    yield break;
                }
            }
            finally
            {
                await enumerator.DisposeAsync();
            }

            if (attempt > 0)
                throw Stopped(overflow.Value, ChatRunSafetyScope.EstimateTokens(request, options), "it was still too large after trimming tool output");

            request = await PrepareRetryAsync(request, options, overflow.Value, cancellationToken);
        }
    }

    private async Task<IReadOnlyList<ChatMessage>> PrepareRetryAsync(
        IReadOnlyList<ChatMessage> request,
        ChatOptions? options,
        ContextOverflowInfo overflow,
        CancellationToken cancellationToken)
    {
        var estimate = ChatRunSafetyScope.EstimateTokens(request, options);
        var configuredWindow = limits.Configured.MaxContextWindowTokens;
        var capped = limits.Record(overflow, estimate);

        if (overflow.LimitTokens is { } stated)
        {
            if (capped)
            {
                logger?.LogWarning(
                    "The provider rejected a request for model configuration '{ModelConfig}' and stated a context limit of {Stated} tokens, below the configured ContextWindowTokens of {Configured}. " +
                    "Compaction now works against {Stated}. Correct ContextWindowTokens on the model configuration to make this permanent.",
                    limits.ModelConfigurationName, stated, configuredWindow, stated);
            }
            else if (configuredWindow <= 0)
            {
                logger?.LogWarning(
                    "The provider rejected a request for model configuration '{ModelConfig}' and stated a context limit of {Stated} tokens, but no ContextWindowTokens is configured, so context compaction cannot keep later requests inside it. " +
                    "Set ContextWindowTokens to {Stated} on the model configuration.",
                    limits.ModelConfigurationName, stated, stated);
            }
        }

        // Aim below where it failed. When the provider gave both its limit and the size it measured,
        // scale that limit into our estimate's units; either way take at least a fifth off.
        var reserve = Math.Max(limits.Configured.StatedOutputTokens, options?.MaxOutputTokens ?? 0);
        var target = estimate * 8 / 10;
        if (overflow is { LimitTokens: > 0, RequestTokens: > 0 })
        {
            var scaled = (long)((overflow.LimitTokens.Value - (long)reserve) * 0.9 * estimate / overflow.RequestTokens.Value);
            target = Math.Min(target, scaled);
        }

        // The retry still passes the local guard, which now sits at the stated limit.
        if (limits.Effective(forTrim: false).MaxContextWindowTokens is > 0 and var window)
            target = Math.Min(target, (long)window - reserve);

        // The strategy measures messages only; instructions and tool schemas ride along untrimmed.
        var fixedTokens = options is null ? 0 : ChatRunSafetyScope.EstimateTokens([], options);
        var trimmed = target > fixedTokens
            ? await ContextCompaction.TrimToTargetAsync(request, target - fixedTokens, cancellationToken)
            : request;

        var trimmedEstimate = ChatRunSafetyScope.EstimateTokens(trimmed, options);
        if (trimmedEstimate >= estimate)
            throw Stopped(overflow, estimate, "it holds no tool output left to trim");

        logger?.LogInformation(
            "Retrying a request for '{ModelConfig}' the provider rejected as a context overflow: ~{Before} → ~{After} estimated tokens after trimming tool output",
            limits.ModelConfigurationName, estimate, trimmedEstimate);

        return trimmed;
    }

    private FabrCoreRunStoppedException Stopped(ContextOverflowInfo overflow, long promptEstimate, string why)
    {
        var limit = overflow.LimitTokens is { } stated ? $" ({stated} tokens)" : string.Empty;
        var scope = ChatRunSafetyScope.Current;

        logger?.LogWarning(
            "Stopping the run: the provider rejected a request for '{ModelConfig}' as larger than its context{Limit} and {Why}. Provider message: {Message}",
            limits.ModelConfigurationName, limit, why, overflow.Message);

        return new FabrCoreRunStoppedException(
            RunStopReason.PromptTooLarge,
            $"The provider rejected the request for model configuration '{limits.ModelConfigurationName}' as larger than the model's context{limit}, and {why}. " +
            "Protected context was retained. Start a new conversation or reduce the request; if this model's ContextWindowTokens is set too high, correct it.",
            promptEstimate,
            scope?.TurnCumulativeInputTokens ?? 0,
            scope?.LlmCalls ?? 0);
    }
}
