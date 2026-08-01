namespace FabrCore.Core.CloudServer;

/// <summary>
/// One admin HTTP request delivered to a cluster over the outbound-only Cloud Server v2
/// connect channel.
/// </summary>
public sealed class CloudAdminCommand
{
    public string CommandId { get; set; } = string.Empty;
    public string Method { get; set; } = "GET";
    public string PathAndQuery { get; set; } = string.Empty;
    public Dictionary<string, string[]> Headers { get; set; } =
        new(StringComparer.OrdinalIgnoreCase);
    public byte[]? Body { get; set; }
    public DateTimeOffset ExpiresAt { get; set; }
    public string? TargetHostInstanceId { get; set; }
    public string LeaseToken { get; set; } = string.Empty;
    public int Attempt { get; set; } = 1;
}

/// <summary>Result returned by a cluster after executing a connect-channel command locally.</summary>
public sealed class CloudAdminCommandResponse
{
    public string CommandId { get; set; } = string.Empty;
    public int StatusCode { get; set; }
    public Dictionary<string, string[]> Headers { get; set; } =
        new(StringComparer.OrdinalIgnoreCase);
    public byte[]? Body { get; set; }
    public string? Error { get; set; }
    public string LeaseToken { get; set; } = string.Empty;
}
