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
            throw new ArgumentException("Surface direct messages require fully-qualified owner:agent handles.", nameof(message));
        }

        message.Kind = MessageKind.OneWay;

        var stream = GetAgentChatStream(message.ToHandle);
        await stream.OnNextAsync(message);
        logger.LogDebug("Sent Surface direct message to {ToHandle}.", message.ToHandle);
    }

    public async Task SendEventAsync(EventMessage message, string? streamName = null)
    {
        ArgumentNullException.ThrowIfNull(message);

        if (streamName == null && string.IsNullOrWhiteSpace(message.Channel))
        {
            throw new ArgumentException("Channel must be set unless streamName is provided.", nameof(message));
        }

        if (streamName == null && !message.Channel!.Contains(':'))
        {
            throw new ArgumentException("Surface direct events require fully-qualified owner:agent handles.", nameof(message));
        }

        var stream = GetAgentEventStream(streamName ?? message.Channel!);
        await stream.OnNextAsync(message);
        logger.LogDebug("Sent Surface direct event to {Target}.", streamName ?? message.Channel);
    }

    private IAsyncStream<AgentMessage> GetAgentChatStream(string handle)
    {
        var streamName = StreamName.ForAgentChat(handle);
        var provider = clusterClient.GetStreamProvider(streamName.Provider);
        var streamId = StreamId.Create(streamName.Namespace, streamName.Handle);
        return provider.GetStream<AgentMessage>(streamId);
    }

    private IAsyncStream<EventMessage> GetAgentEventStream(string handle)
    {
        var streamName = StreamName.ForAgentEvent(handle);
        var provider = clusterClient.GetStreamProvider(streamName.Provider);
        var streamId = StreamId.Create(streamName.Namespace, streamName.Handle);
        return provider.GetStream<EventMessage>(streamId);
    }
}
