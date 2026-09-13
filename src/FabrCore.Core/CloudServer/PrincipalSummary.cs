namespace FabrCore.Core.CloudServer;

public sealed class PrincipalSummary
{
    public string Handle { get; set; } = "";
    public string? DisplayName { get; set; }
    public string? Description { get; set; }
    public bool Registered { get; set; }
    public bool RuntimeDiscovered { get; set; }
    public AgentStatus? RuntimeStatus { get; set; }
    public bool IsSystem { get; set; }
}
