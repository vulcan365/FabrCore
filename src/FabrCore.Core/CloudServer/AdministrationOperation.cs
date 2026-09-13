namespace FabrCore.Core.CloudServer;
public sealed class AdministrationOperationRequest
{
    public string Kind { get; set; } = "";
    public string? AgentHandle { get; set; }
    public AgentManagementRequest? Management { get; set; }
    public List<AgentConfiguration> Configurations { get; set; } = [];
    public AgentMessage? Message { get; set; }
    public EventMessage? Event { get; set; }
    public string? BlueprintName { get; set; }
    public BlueprintDeploymentRequest? Deployment { get; set; }
}
public sealed class AdministrationOperation
{
    public string Id { get; set; } = "";
    public string Status { get; set; } = "running";
    public string RequestDigest { get; set; } = "";
    public DateTimeOffset StartedAt { get; set; }
    public DateTimeOffset? CompletedAt { get; set; }
    public string? ResultJson { get; set; }
    public string? Error { get; set; }
}
