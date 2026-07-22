namespace FabrCore.Client.Orleans;

/// <summary>Configures provider-neutral FabrCore Orleans client connectivity.</summary>
public sealed class FabrCoreOrleansClientOptions
{
    public const string FabrCoreHostUrlConfigurationKey = "FabrCoreHostUrl";
    public const string DefaultGatewayDiscoveryPath = "/fabrcoreapi/cluster/gateways";

    /// <summary>Gets or sets the base URL of the FabrCore Host.</summary>
    public string? FabrCoreHostUrl { get; set; }

    /// <summary>Gets or sets the Host gateway-discovery path.</summary>
    public string GatewayDiscoveryPath { get; set; } = DefaultGatewayDiscoveryPath;

    /// <summary>
    /// Gets or sets whether a discovery document which does not require Orleans TLS is
    /// accepted. Defaults to false and should only be enabled on trusted development networks.
    /// </summary>
    public bool AllowInsecureOrleansTransport { get; set; }

    internal Uri GetHostUri()
    {
        if (!Uri.TryCreate(FabrCoreHostUrl, UriKind.Absolute, out var hostUri) ||
            (hostUri.Scheme != Uri.UriSchemeHttp && hostUri.Scheme != Uri.UriSchemeHttps))
        {
            throw new InvalidOperationException(
                $"{FabrCoreHostUrlConfigurationKey} must be an absolute HTTP or HTTPS URL.");
        }

        return hostUri;
    }

    internal Uri GetDiscoveryUri()
    {
        if (string.IsNullOrWhiteSpace(GatewayDiscoveryPath))
        {
            throw new InvalidOperationException("GatewayDiscoveryPath cannot be empty.");
        }

        var host = GetHostUri().AbsoluteUri.TrimEnd('/');
        var path = GatewayDiscoveryPath.TrimStart('/');
        return new Uri($"{host}/{path}", UriKind.Absolute);
    }
}
