using System.Text.Json;
using FabrCore.Core;
using Microsoft.Extensions.AI;

namespace FabrCore.Sdk.Tests.Infrastructure;

/// <summary>
/// Scriptable chat client. Returns the queued responses in order, then falls back to a terminal text
/// response, which is what lets a test drive an agent through an exact tool-calling sequence.
/// </summary>
internal sealed class FakeChatClient : IChatClient
{
    private readonly string responseText;
    private readonly Queue<ChatResponse> scripted = new();

    private FakeChatClient(string responseText)
    {
        this.responseText = responseText;
    }

    /// <summary>Requests seen by the client, in order. Useful for asserting on composed instructions.</summary>
    public List<List<ChatMessage>> Requests { get; } = [];

    /// <summary>The <see cref="ChatOptions"/> supplied with each request, in order.</summary>
    public List<ChatOptions?> RequestOptions { get; } = [];

    /// <summary>How many times the model was called.</summary>
    public int CallCount => Requests.Count;

    public static FakeChatClient WithTextResponse(string responseText) => new(responseText);

    /// <summary>Returns each supplied response in turn, then falls back to a terminal text response.</summary>
    public static FakeChatClient Scripted(params ChatResponse[] responses)
    {
        var client = new FakeChatClient("Done.");
        foreach (var response in responses)
        {
            client.scripted.Enqueue(response);
        }

        return client;
    }

    public static ChatResponse Text(string text)
        => new(new ChatMessage(ChatRole.Assistant, text));

    public static ChatResponse ToolCall(string callId, string name, string argumentsJson)
    {
        var arguments = JsonSerializer
            .Deserialize<Dictionary<string, JsonElement>>(argumentsJson)!
            .ToDictionary(pair => pair.Key, pair => (object?)pair.Value);

        return new ChatResponse(new ChatMessage(ChatRole.Assistant, [new FunctionCallContent(callId, name, arguments)]));
    }

    /// <summary>The instructions and message text of a single request, flattened for substring assertions.</summary>
    public string PromptAt(int index) => string.Join(
        "\n",
        [RequestOptions[index]?.Instructions ?? string.Empty, .. Requests[index].Select(message => message.Text)]);

    public Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> chatMessages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        Requests.Add([.. chatMessages]);
        RequestOptions.Add(options);

        return Task.FromResult(scripted.Count > 0
            ? scripted.Dequeue()
            : new ChatResponse(new ChatMessage(ChatRole.Assistant, responseText)));
    }

    public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> chatMessages,
        ChatOptions? options = null,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var response = await GetResponseAsync(chatMessages, options, cancellationToken);
        yield return new ChatResponseUpdate(ChatRole.Assistant, response.Text ?? string.Empty);
    }

    public object? GetService(Type serviceType, object? serviceKey = null) => null;

    public void Dispose()
    {
    }
}

/// <summary>
/// In-memory agent host. Custom state survives across proxy instances built on the same host, which is how
/// grain deactivation and reactivation is simulated.
/// </summary>
internal sealed class FakeAgentHost : IFabrCoreAgentHost
{
    public FakeAgentHost(string handle)
    {
        Handle = handle;
    }

    public string Handle { get; }

    public List<AgentMessage> SentMessages { get; } = [];

    /// <summary>Durable custom state. Deliberately shared across proxy instances built on this host.</summary>
    public Dictionary<string, JsonElement> CustomState { get; } = [];

    /// <summary>Requests observed by <see cref="SendAndReceiveMessage"/>, in order.</summary>
    public List<AgentMessage> ReceivedRequests { get; } = [];

    /// <summary>Per-target reply text. Targets without an entry get an empty response.</summary>
    public Dictionary<string, Func<AgentMessage, string>> Responders { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Per-target artificial latency, used to exercise delegation timeouts.</summary>
    public Dictionary<string, TimeSpan> Delays { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Per-target health results. Handlers may throw to simulate an unreachable agent.</summary>
    public Dictionary<string, Func<AgentHealthStatus>> HealthResponders { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Status messages set during the run, in order.</summary>
    public List<string?> StatusMessages { get; } = [];

    public string GetHandle() => Handle;

    public async Task<AgentMessage> SendAndReceiveMessage(AgentMessage request)
    {
        ReceivedRequests.Add(request);

        if (request.ToHandle is { Length: > 0 } target && Delays.TryGetValue(target, out var delay))
        {
            await Task.Delay(delay);
        }

        var response = request.Response();
        if (request.ToHandle is { Length: > 0 } handle && Responders.TryGetValue(handle, out var responder))
        {
            response.Message = responder(request);
        }

        return response;
    }

    public Task SendMessage(AgentMessage request)
    {
        SentMessages.Add(request);
        return Task.CompletedTask;
    }

    public Task<AgentHealthStatus> GetAgentHealth(string? handle = null, HealthDetailLevel detailLevel = HealthDetailLevel.Detailed)
    {
        var target = handle ?? Handle;

        if (HealthResponders.TryGetValue(target, out var responder))
        {
            return Task.FromResult(responder());
        }

        return Task.FromResult(new AgentHealthStatus
        {
            Handle = target,
            State = HealthState.Healthy,
            Timestamp = DateTime.UtcNow,
            IsConfigured = true
        });
    }

    public Task SendEvent(EventMessage request) => Task.CompletedTask;

    public void RegisterTimer(string timerName, string messageType, string? message, TimeSpan dueTime, TimeSpan period)
    {
    }

    public void UnregisterTimer(string timerName)
    {
    }

    public Task RegisterReminder(string reminderName, string messageType, string? message, TimeSpan dueTime, TimeSpan period)
        => Task.CompletedTask;

    public Task UnregisterReminder(string reminderName) => Task.CompletedTask;

    public FabrCoreChatHistoryProvider GetChatHistoryProvider(string threadId) => throw new NotSupportedException();

    public void TrackChatHistoryProvider(FabrCoreChatHistoryProvider provider)
    {
    }

    public Task<List<StoredChatMessage>> GetThreadMessagesAsync(string threadId)
        => Task.FromResult(new List<StoredChatMessage>());

    public Task AddThreadMessagesAsync(string threadId, IEnumerable<StoredChatMessage> messages)
        => Task.CompletedTask;

    public Task ClearThreadAsync(string threadId) => Task.CompletedTask;

    public Task ReplaceThreadMessagesAsync(string threadId, IEnumerable<StoredChatMessage> messages)
        => Task.CompletedTask;

    // A copy, so a proxy's in-memory cache cannot alias the durable store — the same isolation the grain has.
    public Task<Dictionary<string, JsonElement>> GetCustomStateAsync()
        => Task.FromResult(new Dictionary<string, JsonElement>(CustomState));

    public Task MergeCustomStateAsync(Dictionary<string, JsonElement> changes, IEnumerable<string> deletes)
    {
        foreach (var key in deletes)
        {
            CustomState.Remove(key);
        }

        foreach (var (key, value) in changes)
        {
            CustomState[key] = value;
        }

        return Task.CompletedTask;
    }

    public void SetStatusMessage(string? message) => StatusMessages.Add(message);
}

/// <summary>Chat client service that hands back a fixed fake client for every configuration name.</summary>
internal sealed class FakeChatClientService : IFabrCoreChatClientService
{
    private readonly IChatClient chatClient;

    public FakeChatClientService(IChatClient chatClient)
    {
        this.chatClient = chatClient;
    }

    /// <summary>Configuration names requested, in order.</summary>
    public List<string> RequestedClients { get; } = [];

    public Task<IChatClient> GetChatClient(string name, int networkTimeoutSeconds = 100)
    {
        RequestedClients.Add(name);
        return Task.FromResult(chatClient);
    }

#pragma warning disable MEAI001
    public Task<ISpeechToTextClient> GetAudioClient(string name, int networkTimeoutSeconds = 100)
        => throw new NotSupportedException();
#pragma warning restore MEAI001

    public Task<IEmbeddingGenerator<string, Embedding<float>>> GetEmbeddingsClient(string name)
        => throw new NotSupportedException();

    public Task<ModelConfiguration> GetModelConfigurationAsync(string name)
        => Task.FromResult(new ModelConfiguration
        {
            Name = name,
            Provider = "Test",
            Uri = "http://localhost",
            Model = name,
            ApiKeyAlias = "test"
        });
}
