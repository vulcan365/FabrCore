using FabrCore.Core;
using FabrCore.Core.Streaming;
using Orleans;
using Orleans.Streams;

namespace FabrCore.Host.Streaming
{
    /// <summary>
    /// Factory and extension methods for creating Orleans streams with consistent naming.
    /// </summary>
    public static class StreamFactory
    {
        /// <summary>
        /// Gets a stream from the cluster client using a StreamName.
        /// </summary>
        public static IAsyncStream<T> GetStream<T>(this IClusterClient client, StreamName streamName)
        {
            var provider = client.GetStreamProvider(streamName.Provider);
            var streamId = StreamId.Create(streamName.Namespace, streamName.Handle);
            return provider.GetStream<T>(streamId);
        }

        /// <summary>
        /// Gets a stream from a grain using a StreamName.
        /// </summary>
        public static IAsyncStream<T> GetStream<T>(this Grain grain, StreamName streamName)
        {
            var provider = grain.GetStreamProvider(streamName.Provider);
            var streamId = StreamId.Create(streamName.Namespace, streamName.Handle);
            return provider.GetStream<T>(streamId);
        }

        /// <summary>
        /// Gets the AgentChat stream for a specific handle from a cluster client.
        /// </summary>
        public static IAsyncStream<AgentMessage> GetAgentChatStream(this IClusterClient client, string handle)
            => client.GetStream<AgentMessage>(StreamName.ForAgentChat(handle));

        /// <summary>
        /// Gets the AgentEvent stream for a specific handle from a cluster client.
        /// </summary>
        public static IAsyncStream<AgentMessage> GetAgentEventStream(this IClusterClient client, string handle)
            => client.GetStream<AgentMessage>(StreamName.ForAgentEvent(handle));

        /// <summary>
        /// Gets the AgentChat stream for a specific handle from a grain.
        /// </summary>
        public static IAsyncStream<AgentMessage> GetAgentChatStream(this Grain grain, string handle)
            => grain.GetStream<AgentMessage>(StreamName.ForAgentChat(handle));

        /// <summary>
        /// Gets the AgentEvent stream for a specific handle from a grain.
        /// </summary>
        public static IAsyncStream<AgentMessage> GetAgentEventStream(this Grain grain, string handle)
            => grain.GetStream<AgentMessage>(StreamName.ForAgentEvent(handle));
    }
}
