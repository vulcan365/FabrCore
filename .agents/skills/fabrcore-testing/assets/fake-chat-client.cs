using Microsoft.Extensions.AI;

namespace FabrCore.Tests.Infrastructure;

/// <summary>
/// A fake IChatClient that returns predetermined responses for testing.
/// Supports sequential responses for multi-call scenarios (e.g., effort analysis then actual response).
/// </summary>
public class FakeChatClient : IChatClient
{
    private readonly Func<IEnumerable<ChatMessage>, ChatResponse> _responseFactory;
    private int _callCount;

    public FakeChatClient(Func<IEnumerable<ChatMessage>, ChatResponse> responseFactory)
    {
        _responseFactory = responseFactory;
    }

    /// <summary>Number of times GetResponseAsync has been called.</summary>
    public int CallCount => _callCount;

    /// <summary>All messages received across all calls.</summary>
    public List<IEnumerable<ChatMessage>> ReceivedMessages { get; } = new();

    public Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> chatMessages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ReceivedMessages.Add(chatMessages);
        Interlocked.Increment(ref _callCount);
        return Task.FromResult(_responseFactory(chatMessages));
    }

    public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> chatMessages,
        ChatOptions? options = null,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var response = await GetResponseAsync(chatMessages, options, cancellationToken);
        var text = response.Text ?? "";
        yield return new ChatResponseUpdate(ChatRole.Assistant, text);
    }

    public void Dispose() { }

    public object? GetService(Type serviceType, object? serviceKey = null) => null;

    // --- Factory methods ---

    /// <summary>Creates a FakeChatClient that always returns the given text.</summary>
    public static FakeChatClient WithTextResponse(string text)
    {
        return new FakeChatClient(_ => new ChatResponse(new ChatMessage(ChatRole.Assistant, text)));
    }

    /// <summary>Creates a FakeChatClient that always returns the given JSON string.</summary>
    public static FakeChatClient WithJsonResponse(string json)
    {
        return WithTextResponse(json);
    }

    /// <summary>
    /// Creates a FakeChatClient that returns different responses on each call.
    /// Useful for agents that make multiple LLM calls per message (e.g., analysis then response).
    /// </summary>
    public static FakeChatClient WithSequentialResponses(params string[] responses)
    {
        var index = 0;
        return new FakeChatClient(_ =>
        {
            var text = index < responses.Length ? responses[index] : responses[^1];
            Interlocked.Increment(ref index);
            return new ChatResponse(new ChatMessage(ChatRole.Assistant, text));
        });
    }
}
