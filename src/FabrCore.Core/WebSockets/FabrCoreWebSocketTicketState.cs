namespace FabrCore.Core.WebSockets;

[GenerateSerializer]
public sealed class FabrCoreWebSocketTicketEntry
{
    [Id(0)] public string PrincipalHandle { get; set; } = string.Empty;
    [Id(1)] public DateTimeOffset ExpiresAt { get; set; }
    [Id(2)] public DateTimeOffset IssuedAt { get; set; }
}

[GenerateSerializer]
public sealed class FabrCoreWebSocketTicketState
{
    [Id(0)] public Dictionary<string, FabrCoreWebSocketTicketEntry> Tickets { get; set; } = new(StringComparer.Ordinal);
}
