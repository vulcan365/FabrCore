using FabrCore.Core.Connectivity;
using FabrCore.Host.Configuration;
using FabrCore.Host.Services;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using Orleans.Configuration;

namespace FabrCore.Host.Api.Controllers;

/// <summary>Exposes provider-neutral Orleans gateway discovery.</summary>
[ApiController]
[Route("fabrcoreapi/cluster")]
public sealed class ClusterGatewayController : ControllerBase
{
    private readonly IOptionsMonitor<GatewayDiscoveryOptions> _discoveryOptions;
    private readonly IOptions<ClusterOptions> _clusterOptions;
    private readonly IGatewayDiscoverySource _gatewaySource;

    public ClusterGatewayController(
        IOptionsMonitor<GatewayDiscoveryOptions> discoveryOptions,
        IOptions<ClusterOptions> clusterOptions,
        IGatewayDiscoverySource gatewaySource)
    {
        _discoveryOptions = discoveryOptions;
        _clusterOptions = clusterOptions;
        _gatewaySource = gatewaySource;
    }

    [HttpGet("gateways")]
    [ProducesResponseType<FabrCoreGatewayDiscoveryDocument>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status503ServiceUnavailable)]
    public IActionResult GetGateways()
    {
        var options = _discoveryOptions.CurrentValue;
        var gateways = _gatewaySource.GetGateways();
        if (gateways.Count == 0)
        {
            return Problem(
                statusCode: StatusCodes.Status503ServiceUnavailable,
                title: "No Orleans gateways are currently available.");
        }

        var cluster = _clusterOptions.Value;
        return Ok(new FabrCoreGatewayDiscoveryDocument
        {
            ClusterId = cluster.ClusterId,
            ServiceId = cluster.ServiceId,
            Gateways = gateways.Select(static uri => uri.AbsoluteUri).ToList(),
            RefreshPeriodSeconds = checked((int)Math.Ceiling(options.RefreshPeriod.TotalSeconds)),
            RequireOrleansTls = options.RequireOrleansTls
        });
    }
}
