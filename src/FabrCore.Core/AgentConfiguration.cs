using Orleans;

namespace FabrCore.Core
{
    public class AgentConfiguration
    {
        public string? Handle { get; set; }
        public string? AgentType { get; set; }
        public string? Models { get; set; }
        public List<string> Streams { get; set; } = new();
        public string? SystemPrompt { get; set; }
        public string? Description { get; set; }
        public Dictionary<string, string> Args { get; set; } = new();
        public List<string> Plugins { get; set; } = new();
        public List<string> Tools { get; set; } = new();
        public bool ForceReconfigure { get; set; }

    }

    [GenerateSerializer]
    internal struct AgentConfigurationSurrogate
    {
        public AgentConfigurationSurrogate() { }

        [Id(0)]
        public string? Handle { get; set; }
        [Id(1)]
        public string? AgentType { get; set; }
        [Id(2)]
        public string? Models { get; set; }
        [Id(3)]
        public List<string> Streams { get; set; } = new();
        [Id(4)]
        public string? SystemPrompt { get; set; }
        [Id(9)]
        public string? Description { get; set; }
        [Id(5)]
        public Dictionary<string, string> Args { get; set; } = new();
        [Id(6)]
        public List<string> Plugins { get; set; } = new();
        [Id(7)]
        public List<string> Tools { get; set; } = new();
        [Id(8)]
        public bool ForceReconfigure { get; set; }

    }


    [RegisterConverter]
    internal sealed class FabrCoreAgentConfigurationSurrogateConverter : IConverter<AgentConfiguration, AgentConfigurationSurrogate>
    {
        public FabrCoreAgentConfigurationSurrogateConverter()
        {
        }

        public AgentConfiguration ConvertFromSurrogate(in AgentConfigurationSurrogate surrogate)
        {
            return new AgentConfiguration
            {
                Handle = surrogate.Handle,
                AgentType = surrogate.AgentType,
                Models = surrogate.Models,
                SystemPrompt = surrogate.SystemPrompt,
                Description = surrogate.Description,
                Streams = surrogate.Streams,
                Args = surrogate.Args,
                Plugins = surrogate.Plugins,
                Tools = surrogate.Tools,
                ForceReconfigure = surrogate.ForceReconfigure
            };
        }

        public AgentConfigurationSurrogate ConvertToSurrogate(in AgentConfiguration value)
        {
            return new AgentConfigurationSurrogate
            {
                Handle = value.Handle,
                AgentType = value.AgentType,
                Models = value.Models,
                Streams = value.Streams,
                SystemPrompt = value.SystemPrompt,
                Description = value.Description,
                Args = value.Args,
                Plugins = value.Plugins,
                Tools = value.Tools,
                ForceReconfigure = value.ForceReconfigure
            };
        }
    }
}
