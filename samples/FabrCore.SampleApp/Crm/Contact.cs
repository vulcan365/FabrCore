namespace FabrCore.SampleApp.Crm;

public sealed record Contact
{
    public required string Id { get; init; }
    public required string CustomerId { get; set; }
    public required string FullName { get; set; }
    public required string Title { get; set; }
    public required string Email { get; set; }
    public required string Phone { get; set; }
    public bool Primary { get; set; }
}
