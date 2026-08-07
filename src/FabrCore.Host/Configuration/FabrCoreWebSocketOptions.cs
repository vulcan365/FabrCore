namespace FabrCore.Host.Configuration;

public sealed class FabrCoreWebSocketOptions
{
    public const string SectionName = "FabrCore:WebSocket";

    public TimeSpan TicketLifetime { get; set; } = TimeSpan.FromSeconds(30);
    public int TicketRegistryShards { get; set; } = 32;
    public int MaxTicketsPerShard { get; set; } = 4096;
    public int MaxConcurrentRequests { get; set; } = 8;
    public TimeSpan RequestTimeout { get; set; } = TimeSpan.FromMinutes(5);
    public TimeSpan HelloTimeout { get; set; } = TimeSpan.FromSeconds(15);
    public TimeSpan DeliveryRetention { get; set; } = TimeSpan.FromHours(24);
    public int MaxDeliveriesPerPrincipal { get; set; } = 10_000;
    public int MaxClientsPerPrincipal { get; set; } = 16;
    public TimeSpan InactiveClientExpiration { get; set; } = TimeSpan.FromHours(24);
    public bool AllowDevelopmentPrincipalSelection { get; set; }
}
