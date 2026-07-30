namespace FabrCore.SampleApp.Crm;

public sealed record Customer
{
    public required string Id { get; init; }
    public required string Name { get; set; }
    public required string Segment { get; set; }
    public required string Status { get; set; }
    public required string Owner { get; set; }
    public decimal AnnualRevenue { get; set; }
    public required string Notes { get; set; }
    public DateTime UpdatedUtc { get; set; }
}
