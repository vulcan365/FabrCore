using Orleans;

namespace FabrCore.Core
{
    public enum McpTransportType
    {
        Stdio,
        Http
    }

    public class McpServerConfig
    {
        public string? Name { get; set; }
        public McpTransportType TransportType { get; set; } = McpTransportType.Stdio;
        public string? Command { get; set; }
        public List<string> Arguments { get; set; } = new();
        public Dictionary<string, string> Env { get; set; } = new();
        public string? Url { get; set; }
        public Dictionary<string, string> Headers { get; set; } = new();
    }

    [GenerateSerializer]
    internal struct McpServerConfigSurrogate
    {
        public McpServerConfigSurrogate() { }

        [Id(0)]
        public string? Name { get; set; }
        [Id(1)]
        public McpTransportType TransportType { get; set; } = McpTransportType.Stdio;
        [Id(2)]
        public string? Command { get; set; }
        [Id(3)]
        public List<string> Arguments { get; set; } = new();
        [Id(4)]
        public Dictionary<string, string> Env { get; set; } = new();
        [Id(5)]
        public string? Url { get; set; }
        [Id(6)]
        public Dictionary<string, string> Headers { get; set; } = new();
    }

    [RegisterConverter]
    internal sealed class McpServerConfigSurrogateConverter : IConverter<McpServerConfig, McpServerConfigSurrogate>
    {
        public McpServerConfig ConvertFromSurrogate(in McpServerConfigSurrogate surrogate)
        {
            return new McpServerConfig
            {
                Name = surrogate.Name,
                TransportType = surrogate.TransportType,
                Command = surrogate.Command,
                Arguments = surrogate.Arguments,
                Env = surrogate.Env,
                Url = surrogate.Url,
                Headers = surrogate.Headers
            };
        }

        public McpServerConfigSurrogate ConvertToSurrogate(in McpServerConfig value)
        {
            return new McpServerConfigSurrogate
            {
                Name = value.Name,
                TransportType = value.TransportType,
                Command = value.Command,
                Arguments = value.Arguments,
                Env = value.Env,
                Url = value.Url,
                Headers = value.Headers
            };
        }
    }
}
