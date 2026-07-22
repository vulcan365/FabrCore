using FabrCore.Host.Configuration;
using Microsoft.Extensions.Options;
using Orleans.Configuration;
using Orleans.Runtime;

namespace FabrCore.Host.Services;

public interface IGatewayDiscoverySource
{
    IReadOnlyList<Uri> GetGateways();
}

internal sealed class GatewayDiscoverySource : IGatewayDiscoverySource
{
    private readonly IClusterMembershipService _membershipService;
    private readonly IOptionsMonitor<GatewayDiscoveryOptions> _discoveryOptions;
    private readonly IOptions<EndpointOptions> _endpointOptions;

    public GatewayDiscoverySource(
        IClusterMembershipService membershipService,
        IOptionsMonitor<GatewayDiscoveryOptions> discoveryOptions,
        IOptions<EndpointOptions> endpointOptions)
    {
        _membershipService = membershipService;
        _discoveryOptions = discoveryOptions;
        _endpointOptions = endpointOptions;
    }

    public IReadOnlyList<Uri> GetGateways()
    {
        return SelectGateways(
            _discoveryOptions.CurrentValue.AdvertisedGateways,
            _membershipService.CurrentSnapshot.Members.Values,
            _endpointOptions.Value.GatewayPort);
    }

    internal static IReadOnlyList<Uri> SelectGateways(
        IReadOnlyCollection<string> configured,
        IEnumerable<ClusterMember> members,
        int gatewayPort)
    {
        if (configured.Count > 0)
        {
            return configured
                .Select(value => GatewayUriUtilities.TryParse(value, out var uri) ? uri : null)
                .Where(static uri => uri is not null)
                .Cast<Uri>()
                .Distinct()
                .ToArray();
        }

        return DeriveActiveGateways(members, gatewayPort);
    }

    internal static IReadOnlyList<Uri> DeriveActiveGateways(
        IEnumerable<ClusterMember> members,
        int gatewayPort)
    {
        if (gatewayPort is <= 0 or > 65535)
        {
            return [];
        }

        return members
            .Where(static member =>
                member.Status == SiloStatus.Active &&
                GatewayUriUtilities.IsUsableAddress(member.SiloAddress.Endpoint.Address))
            .Select(member => GatewayUriUtilities.Create(member.SiloAddress.Endpoint.Address, gatewayPort))
            .Distinct()
            .OrderBy(static uri => uri.AbsoluteUri, StringComparer.Ordinal)
            .ToArray();
    }
}
