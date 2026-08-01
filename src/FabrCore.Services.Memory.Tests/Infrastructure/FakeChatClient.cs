using Microsoft.Extensions.AI;

namespace FabrCore.Services.Memory.Tests.Infrastructure;

internal sealed class FakeChatClient : IChatClient
{
    private readonly Func<IReadOnlyList<ChatMessage>, ChatResponse> _responseFactory;
    private int _callCount;

    public FakeChatClient(Func<IReadOnlyList<ChatMessage>, ChatResponse> responseFactory)
    {
        _responseFactory = responseFactory;
    }

    public int CallCount => _callCount;

    public List<IReadOnlyList<ChatMessage>> ReceivedMessages { get; } = [];

    public Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> chatMessages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        var messages = chatMessages.ToList();
        ReceivedMessages.Add(messages);
        Interlocked.Increment(ref _callCount);
        return Task.FromResult(_responseFactory(messages));
    }

    public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> chatMessages,
        ChatOptions? options = null,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var response = await GetResponseAsync(chatMessages, options, cancellationToken);
        yield return new ChatResponseUpdate(ChatRole.Assistant, response.Text ?? "");
    }

    public object? GetService(Type serviceType, object? serviceKey = null) => null;

    public void Dispose()
    {
    }

    public static FakeChatClient WithText(string text) =>
        new(_ => new ChatResponse(new ChatMessage(ChatRole.Assistant, text)));

    public static FakeChatClient WithSequentialResponses(params string[] responses)
    {
        var index = -1;
        return new FakeChatClient(_ =>
        {
            var responseIndex = Math.Min(Interlocked.Increment(ref index), responses.Length - 1);
            return new ChatResponse(new ChatMessage(ChatRole.Assistant, responses[responseIndex]));
        });
    }
}
