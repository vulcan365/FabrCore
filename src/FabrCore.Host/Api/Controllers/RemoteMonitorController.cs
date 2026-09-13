using FabrCore.Core.Monitoring;
using FabrCore.Host.Security;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
namespace FabrCore.Host.Api.Controllers;
[ApiController, Authorize(Policy = FabrCoreAdminAuthenticationDefaults.Policy)]
[Route("fabrcoreapi/admin/v1/observability/monitor")]
public sealed class RemoteMonitorController(IAgentMessageMonitor monitor, Microsoft.Extensions.Options.IOptions<FabrCore.Host.Configuration.RemoteAdministrationOptions> options) : ControllerBase
{
    [HttpGet] public async Task<IActionResult> Query([FromQuery] MonitorQuery query, CancellationToken cancellationToken)
    {
        if (monitor is not IAgentMonitorQueryProvider provider) return StatusCode(501, new { error = "The configured monitor does not support retained queries." });
        query.MaxBytes = Math.Min(query.MaxBytes, options.Value.MaxBodyBytes / 2);
        try { return Ok(await provider.QueryAsync(query, cancellationToken)); }
        catch (ArgumentException ex) { return BadRequest(new { error = ex.Message }); }
    }
    [HttpGet("health")] public async Task<IActionResult> Health(CancellationToken cancellationToken) => monitor is IAgentMonitorQueryProvider provider
        ? Ok(await provider.GetHealthAsync(cancellationToken)) : Ok(new { provider = monitor.GetType().Name, querySupported = false });
    [HttpGet("tokens")] public async Task<IActionResult> Tokens() => Ok(new { scope = "provider-lifetime", items = await monitor.GetAllAgentTokenSummariesAsync() });
    [HttpGet("payload/{sequence:long}")]
    public async Task<IActionResult> Payload(long sequence, int offset = 0, CancellationToken cancellationToken = default)
    {
        if (monitor is not IAgentMonitorPayloadProvider provider) return StatusCode(501);
        var expectedSource = Request.Query["sourceId"].FirstOrDefault();
        if (expectedSource is not null && monitor is IAgentMonitorQueryProvider queryProvider)
        {
            var health = await queryProvider.GetHealthAsync(cancellationToken);
            if (!health.TryGetProperty("sourceId", out var source) || source.GetString() != expectedSource)
                return StatusCode(409, new { error = "Monitor source changed; refresh before retrieving this payload." });
        }
        var count = Math.Clamp(options.Value.MaxBodyBytes / 8, 1, 32000);
        var value = await provider.ReadPayloadAsync(sequence, offset, count, cancellationToken);
        if (expectedSource is not null && monitor is IAgentMonitorQueryProvider finalProvider)
        {
            var health = await finalProvider.GetHealthAsync(cancellationToken);
            if (!health.TryGetProperty("sourceId", out var source) || source.GetString() != expectedSource)
                return StatusCode(409, new { error = "Monitor source changed during payload retrieval; refresh the query." });
        }
        return value is null ? NotFound() : Ok(new { data = value, nextOffset = value.Length == count ? offset + count : (int?)null });
    }
}
