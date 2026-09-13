using FabrCore.Core.Blueprints;
namespace FabrCore.Core.CloudServer;
public sealed class BlueprintDeploymentRequest
{
    public string OperationId { get; set; } = "";
    public string Mode { get; set; } = "ensure";
    public string Revision { get; set; } = "";
    public string ExpansionDigest { get; set; } = "";
    public List<string> AgentHandles { get; set; } = [];
}
public sealed class BlueprintSummary
{
    public string Name { get; set; } = "";
    public string? Description { get; set; }
    public string? Version { get; set; }
    public string Revision { get; set; } = "";
    public string? DeployedRevision { get; set; }
    public string? DeploymentStatus { get; set; }
    public string? LastDeploymentId { get; set; }
    public bool? DefinitionDrift { get; set; }
}
public sealed class BlueprintPreview
{
    public string Revision { get; set; } = "";
    public string ExpansionDigest { get; set; } = "";
    public List<AgentConfiguration> Agents { get; set; } = [];
}
