using FabrCore.Core;
using FabrCore.Core.Streaming;
using Microsoft.Extensions.Logging;
using Orleans;
using Orleans.Streams;
using System.Diagnostics;
using System.Diagnostics.Metrics;

namespace FabrCore.Client
{
    /// <summary>
    /// Sends messages directly to agents via Orleans streams without using ClientContext or ClientGrain infrastructure.
    /// All messages are automatically marked as OneWay since there is no mechanism to receive responses.
    /// </summary>
    public class DirectMessageSender : IDirectMessageSender
    {
        private static readonly ActivitySource ActivitySource = new("FabrCore.Client.DirectMessageSender");
        private static readonly Meter Meter = new("FabrCore.Client.DirectMessageSender");

        private static readonly Counter<long> MessagesSentCounter = Meter.CreateCounter<long>(
            "fabrcore.direct.messages.sent",
            description: "Number of direct messages sent");

        private static readonly Counter<long> EventsSentCounter = Meter.CreateCounter<long>(
            "fabrcore.direct.events.sent",
            description: "Number of direct events sent");

        private static readonly Counter<long> ErrorCounter = Meter.CreateCounter<long>(
            "fabrcore.direct.errors",
            description: "Number of errors encountered");

        private readonly IClusterClient _clusterClient;
        private readonly ILogger<DirectMessageSender> _logger;

        public DirectMessageSender(IClusterClient clusterClient, ILogger<DirectMessageSender> logger)
        {
            _clusterClient = clusterClient ?? throw new ArgumentNullException(nameof(clusterClient));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        }

        /// <inheritdoc />
        public async Task SendMessageAsync(AgentMessage message)
        {
            if (message == null)
                throw new ArgumentNullException(nameof(message));

            if (string.IsNullOrEmpty(message.ToHandle))
                throw new ArgumentException("ToHandle must be set on the message", nameof(message));

            using var activity = ActivitySource.StartActivity("SendDirectMessage", ActivityKind.Producer);
            activity?.SetTag("message.from", message.FromHandle);
            activity?.SetTag("message.to", message.ToHandle);
            activity?.SetTag("stream.namespace", StreamConstants.AgentChatNamespace);

            // Force OneWay - no response possible with direct sending
            message.Kind = MessageKind.OneWay;

            _logger.LogDebug("Sending direct message to {ToHandle} from {FromHandle}",
                message.ToHandle, message.FromHandle);

            try
            {
                var stream = GetAgentChatStream(message.ToHandle);
                await stream.OnNextAsync(message);

                _logger.LogDebug("Direct message sent successfully to {ToHandle}", message.ToHandle);

                MessagesSentCounter.Add(1,
                    new KeyValuePair<string, object?>("message.to", message.ToHandle),
                    new KeyValuePair<string, object?>("message.from", message.FromHandle));

                activity?.SetStatus(ActivityStatusCode.Ok);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error sending direct message to {ToHandle}", message.ToHandle);
                activity?.SetStatus(ActivityStatusCode.Error, ex.Message);
                activity?.AddException(ex);

                ErrorCounter.Add(1,
                    new KeyValuePair<string, object?>("error.type", "send_message_failed"),
                    new KeyValuePair<string, object?>("message.to", message.ToHandle));

                throw;
            }
        }

        /// <inheritdoc />
        public async Task SendEventAsync(AgentMessage message, string? streamName = null)
        {
            if (message == null)
                throw new ArgumentNullException(nameof(message));

            if (streamName == null && string.IsNullOrEmpty(message.ToHandle))
                throw new ArgumentException("ToHandle must be set on the message (unless using streamName)", nameof(message));

            using var activity = ActivitySource.StartActivity("SendDirectEvent", ActivityKind.Producer);
            activity?.SetTag("message.from", message.FromHandle);
            activity?.SetTag("message.to", message.ToHandle);
            activity?.SetTag("stream.namespace", StreamConstants.AgentEventNamespace);

            // Force OneWay - no response possible with direct sending
            message.Kind = MessageKind.OneWay;

            try
            {
                if (streamName != null)
                {
                    activity?.SetTag("stream.name", streamName);

                    _logger.LogDebug("Sending direct event to named stream {StreamName} from {FromHandle}",
                        streamName, message.FromHandle);

                    var stream = GetAgentEventStream(streamName);
                    await stream.OnNextAsync(message);

                    _logger.LogDebug("Direct event sent successfully to named stream {StreamName}", streamName);
                }
                else
                {
                    _logger.LogDebug("Sending direct event to {ToHandle} from {FromHandle}",
                        message.ToHandle, message.FromHandle);

                    var stream = GetAgentEventStream(message.ToHandle!);
                    await stream.OnNextAsync(message);

                    _logger.LogDebug("Direct event sent successfully to {ToHandle}", message.ToHandle);
                }

                EventsSentCounter.Add(1,
                    new KeyValuePair<string, object?>("message.to", message.ToHandle),
                    new KeyValuePair<string, object?>("message.from", message.FromHandle));

                activity?.SetStatus(ActivityStatusCode.Ok);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error sending direct event to {ToHandle}, StreamName: {StreamName}",
                    message.ToHandle, streamName);
                activity?.SetStatus(ActivityStatusCode.Error, ex.Message);
                activity?.AddException(ex);

                ErrorCounter.Add(1,
                    new KeyValuePair<string, object?>("error.type", "send_event_failed"),
                    new KeyValuePair<string, object?>("message.to", message.ToHandle));

                throw;
            }
        }

        private IAsyncStream<AgentMessage> GetAgentChatStream(string handle)
        {
            var streamName = StreamName.ForAgentChat(handle);
            var provider = _clusterClient.GetStreamProvider(streamName.Provider);
            var streamId = StreamId.Create(streamName.Namespace, streamName.Handle);
            return provider.GetStream<AgentMessage>(streamId);
        }

        private IAsyncStream<AgentMessage> GetAgentEventStream(string handle)
        {
            var streamName = StreamName.ForAgentEvent(handle);
            var provider = _clusterClient.GetStreamProvider(streamName.Provider);
            var streamId = StreamId.Create(streamName.Namespace, streamName.Handle);
            return provider.GetStream<AgentMessage>(streamId);
        }
    }
}
