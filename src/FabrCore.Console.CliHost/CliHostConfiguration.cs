using FabrCore.Core;

namespace FabrCore.Console.CliHost;

public class CliHostConfiguration
{
    public string? Handle { get; set; }
    public int? Port { get; set; }
    public List<AgentConfiguration> Agents { get; set; } = new();
}
