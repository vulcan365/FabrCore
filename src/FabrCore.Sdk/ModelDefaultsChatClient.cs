using FabrCore.Core;
using Microsoft.Extensions.AI;

namespace FabrCore.Sdk;

/// <summary>
/// Applies model-level inference defaults without overriding explicit per-call options.
/// </summary>
internal sealed class ModelDefaultsChatClient : DelegatingChatClient
{
    private readonly int? _maxOutputTokens;
    private readonly ReasoningEffort? _reasoningEffort;

    private ModelDefaultsChatClient(
        IChatClient innerClient,
        int? maxOutputTokens,
        ReasoningEffort? reasoningEffort)
        : base(innerClient)
    {
        _maxOutputTokens = maxOutputTokens;
        _reasoningEffort = reasoningEffort;
    }

    public static IChatClient Apply(IChatClient innerClient, ModelConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(innerClient);
        ArgumentNullException.ThrowIfNull(configuration);

        var reasoningEffort = ParseReasoningEffort(configuration);
        if (configuration.MaxOutputTokens is null && reasoningEffort is null)
        {
            return innerClient;
        }

        return new ModelDefaultsChatClient(
            innerClient,
            configuration.MaxOutputTokens,
            reasoningEffort);
    }

    public static void ValidateConfiguration(ModelConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        _ = ParseReasoningEffort(configuration);
    }

    public override Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default)
        => base.GetResponseAsync(messages, ApplyDefaults(options), cancellationToken);

    public override IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default)
        => base.GetStreamingResponseAsync(messages, ApplyDefaults(options), cancellationToken);

    private ChatOptions ApplyDefaults(ChatOptions? options)
    {
        var configured = options?.Clone() ?? new ChatOptions();
        configured.MaxOutputTokens ??= _maxOutputTokens;

        if (configured.Reasoning is { } existingReasoning)
        {
            configured.Reasoning = new ReasoningOptions
            {
                Effort = existingReasoning.Effort,
                Output = existingReasoning.Output
            };
        }

        if (_reasoningEffort is { } defaultEffort && configured.Reasoning?.Effort is null)
        {
            var reasoning = configured.Reasoning ?? new ReasoningOptions();
            reasoning.Effort = defaultEffort;
            configured.Reasoning = reasoning;
        }

        return configured;
    }

    private static ReasoningEffort? ParseReasoningEffort(ModelConfiguration configuration)
    {
        if (string.IsNullOrWhiteSpace(configuration.ReasoningEffort))
        {
            return null;
        }

        return configuration.ReasoningEffort.Trim().ToLowerInvariant() switch
        {
            "none" => ReasoningEffort.None,
            "low" => ReasoningEffort.Low,
            "medium" => ReasoningEffort.Medium,
            "high" => ReasoningEffort.High,
            "xhigh" or "extrahigh" => ReasoningEffort.ExtraHigh,
            _ => throw new InvalidOperationException(
                $"Model configuration '{configuration.Name}' has unsupported ReasoningEffort " +
                $"'{configuration.ReasoningEffort}'. Supported values are: none, low, medium, high, xhigh, ExtraHigh.")
        };
    }
}
