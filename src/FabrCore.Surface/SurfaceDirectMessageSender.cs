using FabrCore.Core;
using FabrCore.Core.Streaming;
using Microsoft.Extensions.Logging;
using Orleans;
using Orleans.Streams;

namespace FabrCore.Surface;

public sealed class SurfaceDirectMessageSender : ISurfaceDirectMessageSender
{
    private readonly IClusterClient clusterClient;
    private readonly ILogger<SurfaceDirectMessageSender> logger;

    public SurfaceDirectMessageSender(IClusterClient clusterClient, ILogger<SurfaceDirectMessageSender> logger)
    {
        this.clusterClient = clusterClient;
        this.logger = logger;
    }

    public async Task SendMessageAsync(AgentMessage message)
    {
        ArgumentNullException.ThrowIfNull(message);

        if (string.IsNullOrWhiteSpace(message.ToHandle))
        {
            throw new ArgumentException("ToHandle must be set.", nameof(message));
        }

        if (!message.ToHandle.Contains(':'))
        {
            throw new ArgumentException("Surface direct messages require fully-qualified principal:agent handles.", nameof(message));
        }

        var stream = GetAgentChatStream(message.ToHandle);
        await stream.OnNextAsync(message);
        logger.LogInformation(
            "Sent Surface direct message to {ToHandle} with kind {Kind}.",
            message.ToHandle,
            message.Kind);
    }

    public async Task SendEventAsync(EventMessage message)
    {
        ArgumentNullException.ThrowIfNull(message);

        if (string.IsNullOrWhiteSpace(message.Channel))
        {
            throw new ArgumentException("Channel must be set.", nameof(message));
        }

        if (string.IsNullOrWhiteSpace(message.Namespace) && !message.Channel!.Contains(':'))
        {
            throw new ArgumentException("Surface direct events require fully-qualified principal:agent handles.", nameof(message));
        }

        var stream = GetAgentEventStream(message);
        await stream.OnNextAsync(message);
        logger.LogDebug("Sent Surface direct event to {Namespace}.{Channel}.", message.Namespace, message.Channel);
    }

    private IAsyncStream<AgentMessage> GetAgentChatStream(string handle)
    {
        var streamName = StreamName.ForAgentChat(handle);
        var provider = clusterClient.GetStreamProvider(streamName.Provider);
        var streamId = StreamId.Create(streamName.Namespace, streamName.Handle);
        return provider.GetStream<AgentMessage>(streamId);
    }

    private IAsyncStream<EventMessage> GetAgentEventStream(EventMessage message)
    {
        var streamName = EventStreamSubscription.ToStreamName(message);
        var provider = clusterClient.GetStreamProvider(streamName.Provider);
        var streamId = StreamId.Create(streamName.Namespace, streamName.Handle);
        return provider.GetStream<EventMessage>(streamId);
    }
}
