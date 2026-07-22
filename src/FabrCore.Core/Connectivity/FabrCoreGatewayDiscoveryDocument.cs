namespace FabrCore.Core.Connectivity;

/// <summary>
/// Provider-neutral description of an Orleans cluster and its currently advertised gateways.
/// </summary>
public sealed class FabrCoreGatewayDiscoveryDocument
{
    /// <summary>The only discovery document version currently supported.</summary>
    public const int CurrentVersion = 1;

    /// <summary>Gets or sets the discovery document contract version.</summary>
    public int Version { get; set; } = CurrentVersion;

    /// <summary>Gets or sets the Orleans cluster identifier.</summary>
    public string ClusterId { get; set; } = string.Empty;

    /// <summary>Gets or sets the Orleans service identifier.</summary>
    public string ServiceId { get; set; } = string.Empty;

    /// <summary>Gets or sets the active Orleans gateway URIs.</summary>
    public List<string> Gateways { get; set; } = [];

    /// <summary>Gets or sets the recommended gateway refresh interval, in seconds.</summary>
    public int RefreshPeriodSeconds { get; set; }

    /// <summary>Gets or sets whether clients must use Orleans transport TLS.</summary>
    public bool RequireOrleansTls { get; set; }
}
