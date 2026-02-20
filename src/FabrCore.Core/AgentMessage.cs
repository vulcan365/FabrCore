using Orleans;

namespace FabrCore.Core
{
    /// <summary>
    /// Indicates the kind of message being sent.
    /// </summary>
    public enum MessageKind
    {
        /// <summary>
        /// A request message that expects a response.
        /// </summary>
        Request = 0,

        /// <summary>
        /// A fire-and-forget message that does not expect a response.
        /// </summary>
        OneWay = 1,

        /// <summary>
        /// A response to a previous request message.
        /// </summary>
        Response = 2
    }

    public class AgentMessage
    {
        public string Id { get; set; } = Guid.NewGuid().ToString();

        public string? ToHandle { get; set; }

        public string? FromHandle { get; set; }

        public string? OnBehalfOfHandle { get; set; }

        public string? DeliverToHandle { get; set; }

        public string? Channel { get; set; }

        public string? MessageType { get; set; }

        public string? Message { get; set; }

        public MessageKind Kind { get; set; } = MessageKind.Request;

        public string? DataType { get; set; }

        public byte[]? Data { get; set; }

        public List<string> Files = new List<string>();

        public Dictionary<string, string>? State { get; set; } = new Dictionary<string, string>();
        public Dictionary<string, string>? Args { get; set; } = new Dictionary<string, string>();

        public string? TraceId { get; set; } = Guid.NewGuid().ToString();

        public AgentMessage Response()
        {
            var message = new AgentMessage
            {
                Id = Guid.NewGuid().ToString(),
                ToHandle = FromHandle,
                FromHandle = ToHandle,
                Channel = Channel,
                Kind = MessageKind.Response,
                State = State != null ? new Dictionary<string, string>(State) : new Dictionary<string, string>(),
                TraceId = TraceId
            };

            if (Kind == MessageKind.Response && !string.IsNullOrEmpty(DeliverToHandle))
            {
                message.ToHandle = DeliverToHandle;
            }

            else if (Kind == MessageKind.Response && !string.IsNullOrEmpty(OnBehalfOfHandle))
            {
                message.DeliverToHandle = OnBehalfOfHandle;
            }

            return message;
        }
    }

    [GenerateSerializer]
    internal struct AgentMessageSurrogate
    {
        public AgentMessageSurrogate()
        {
        }

        [Id(0)]
        public string Id { get; set; } = Guid.NewGuid().ToString();

        [Id(1)]
        public string? ToHandle { get; set; }

        [Id(2)]
        public string? FromHandle { get; set; }

        [Id(3)]
        public string? OnBehalfOfHandle { get; set; }

        [Id(4)]
        public string? DeliverToHandle { get; set; }

        [Id(5)]
        public string? Channel { get; set; }

        [Id(6)]
        public string? MessageType { get; set; }

        [Id(7)]
        public string? Message { get; set; }

        [Id(8)]
        public MessageKind Kind { get; set; } = MessageKind.Request;

        [Id(9)]
        public string? DataType { get; set; }

        [Id(10)]
        public byte[]? Data { get; set; }

        [Id(11)]
        public List<string> Files = new List<string>();

        [Id(12)]
        public Dictionary<string, string>? State { get; set; } = new Dictionary<string, string>();

        [Id(13)]
        public Dictionary<string, string>? Args { get; set; } = new Dictionary<string, string>();


        [Id(14)]
        public string? TraceId { get; set; } = Guid.NewGuid().ToString();

    }

    [RegisterConverter]
    internal sealed class AgentMessageSurrogateConverter : IConverter<AgentMessage, AgentMessageSurrogate>
    {
        public AgentMessageSurrogateConverter()
        {
        }

        public AgentMessage ConvertFromSurrogate(in AgentMessageSurrogate surrogate)
        {
            return new AgentMessage
            {
                Id = surrogate.Id,
                MessageType = surrogate.MessageType,
                Kind = surrogate.Kind,
                ToHandle = surrogate.ToHandle,
                FromHandle = surrogate.FromHandle,
                OnBehalfOfHandle = surrogate.OnBehalfOfHandle,
                DeliverToHandle = surrogate.DeliverToHandle,
                Channel = surrogate.Channel,
                Message = surrogate.Message,
                DataType = surrogate.DataType,
                Data = surrogate.Data,
                Files = surrogate.Files,
                State = surrogate.State,
                Args = surrogate.Args,
                TraceId = surrogate.TraceId
            };
        }

        public AgentMessageSurrogate ConvertToSurrogate(in AgentMessage value)
        {
            return new AgentMessageSurrogate
            {
                Id = value.Id,
                MessageType = value.MessageType,
                Kind = value.Kind,
                ToHandle = value.ToHandle,
                FromHandle = value.FromHandle,
                OnBehalfOfHandle = value.OnBehalfOfHandle,
                DeliverToHandle = value.DeliverToHandle,
                Channel = value.Channel,
                Message = value.Message,
                DataType = value.DataType,
                Data = value.Data,
                Files = value.Files,
                State = value.State,
                Args = value.Args,
                TraceId = value.TraceId
            };
        }
    }
}
