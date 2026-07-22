using FabrCore.Core.Connectivity;
using System.Net;

namespace FabrCore.Client.Orleans;

internal static class GatewayDiscoveryDocumentValidator
{
    public static IReadOnlyList<Uri> Validate(
        FabrCoreGatewayDiscoveryDocument? document,
        bool allowInsecureOrleansTransport)
    {
        if (document is null)
        {
            throw new FabrCoreGatewayDiscoveryException("The FabrCore Host returned an empty gateway discovery document.");
        }

        if (document.Version != FabrCoreGatewayDiscoveryDocument.CurrentVersion)
        {
            throw new FabrCoreGatewayDiscoveryException(
                $"Unsupported FabrCore gateway discovery version '{document.Version}'. Expected '{FabrCoreGatewayDiscoveryDocument.CurrentVersion}'.");
        }

        if (string.IsNullOrWhiteSpace(document.ClusterId))
        {
            throw new FabrCoreGatewayDiscoveryException("The gateway discovery document is missing clusterId.");
        }

        if (string.IsNullOrWhiteSpace(document.ServiceId))
        {
            throw new FabrCoreGatewayDiscoveryException("The gateway discovery document is missing serviceId.");
        }

        if (document.RefreshPeriodSeconds <= 0)
        {
            throw new FabrCoreGatewayDiscoveryException("The gateway discovery refreshPeriodSeconds must be greater than zero.");
        }

        if (!document.RequireOrleansTls && !allowInsecureOrleansTransport)
        {
            throw new FabrCoreGatewayDiscoveryException(
                "The FabrCore Host does not require Orleans TLS, but this client disallows insecure Orleans transport. " +
                "Enable Host Orleans TLS or explicitly set AllowInsecureOrleansTransport for a trusted development environment.");
        }

        if (document.Gateways is null || document.Gateways.Count == 0)
        {
            throw new FabrCoreGatewayDiscoveryException("The gateway discovery document does not contain any Orleans gateways.");
        }

        var gateways = new List<Uri>(document.Gateways.Count);
        foreach (var value in document.Gateways)
        {
            if (!Uri.TryCreate(value, UriKind.Absolute, out var gateway) ||
                !string.Equals(gateway.Scheme, "gwy.tcp", StringComparison.OrdinalIgnoreCase) ||
                string.IsNullOrWhiteSpace(gateway.Host) ||
                gateway.Port is <= 0 or > 65535 ||
                gateway.AbsolutePath != "/0" ||
                !string.IsNullOrEmpty(gateway.UserInfo) ||
                !string.IsNullOrEmpty(gateway.Query) ||
                !string.IsNullOrEmpty(gateway.Fragment) ||
                (IPAddress.TryParse(gateway.Host, out var address) &&
                    (address.Equals(IPAddress.Any) || address.Equals(IPAddress.IPv6Any))))
            {
                throw new FabrCoreGatewayDiscoveryException(
                    $"Gateway discovery returned malformed Orleans gateway URI '{value}'. Expected gwy.tcp://host:port/0.");
            }

            gateways.Add(gateway);
        }

        return gateways.Distinct().ToArray();
    }
}
