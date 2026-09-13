namespace FabrCore.Services.GraphRag.Administration.Models;

public sealed class AdminDomainDto
{
    public Guid DomainId { get; set; }
    public string Name { get; set; } = "";
    public string? Description { get; set; }
    public double PriorityWeight { get; set; } = 1.0;
    public string? Metadata { get; set; }
    public DateTime CreatedAt { get; set; }
    public int CategoryCount { get; set; }
    public int EntityCount { get; set; }
}
