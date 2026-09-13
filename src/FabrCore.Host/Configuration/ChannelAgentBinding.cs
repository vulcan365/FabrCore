using Microsoft.Extensions.Configuration;

namespace FabrCore.Host.Configuration;

/// <summary>A single agent definition referenced by multiple external channels.</summary>
public sealed class ChannelAgentBinding
{
    public string Handle { get; set; } = "assistant";
    public string AgentType { get; set; } = string.Empty;
    public string Models { get; set; } = "default";
    public string? SystemPrompt { get; set; }
    public List<string> Plugins { get; set; } = [];
    public List<string> Tools { get; set; } = [];
    public Dictionary<string, string> Args { get; set; } = new();

    public static ChannelAgentBinding Read(IConfiguration configuration, string name)
    {
        var section = configuration.GetSection("AgentBindings").GetSection(name);
        var binding = section.Get<ChannelAgentBinding>();
        if (binding is null || string.IsNullOrWhiteSpace(binding.AgentType)
            || string.IsNullOrWhiteSpace(binding.Handle) || binding.Handle.Contains(':'))
            throw new InvalidOperationException($"AgentBindings:{name} requires an AgentType and a bare Handle.");
        return binding;
    }
}
