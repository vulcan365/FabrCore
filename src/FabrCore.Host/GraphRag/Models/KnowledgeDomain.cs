namespace FabrCore.Services.GraphRag.Models;

internal class KnowledgeDomain
{
    public Guid DomainId { get; set; }
    public string Name { get; set; } = "";
    public string? Description { get; set; }
    public double PriorityWeight { get; set; } = 1.0;
    public string? Metadata { get; set; }
    public DateTime CreatedAt { get; set; }
}
