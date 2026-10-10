using FabrCore.Core.CloudServer;
using FabrCore.Host.A2A;
using FabrCore.Host.Configuration;
using FabrCore.Host.Security;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace FabrCore.Host.Api.Controllers;

/// <summary>
/// Reports how this host's external channels are configured, for management consoles. The host
/// serves the A2A status here; add-ons map their own routes under the same prefix.
/// </summary>
[ApiController]
[Authorize(Policy = FabrCoreAdminAuthenticationDefaults.Policy)]
[Route("fabrcoreapi/admin/v1/integrations")]
public sealed class IntegrationsAdminController(IServiceProvider services) : ControllerBase
{
    /// <summary>
    /// A2A configuration, published agents, and posture findings. Always answers, including when
    /// A2A is disabled, so a console can render one state instead of interpreting a 404.
    /// </summary>
    [HttpGet(IntegrationAdministration.A2A)]
    public async Task<ActionResult<A2AIntegrationStatus>> GetA2A(CancellationToken cancellationToken)
    {
        if (Request.Headers.ContainsKey("X-FabrCore-Admin-Target") ||
            Request.Headers.ContainsKey("x-user") ||
            Request.Headers.ContainsKey("x-user-handle"))
        {
            return BadRequest(new { Error = "This route describes the host and accepts no target principal." });
        }

        var options = services.GetService<IOptions<A2AOptions>>()?.Value ?? new A2AOptions();
        // The catalog and task store are registered only while A2A is enabled.
        var catalog = services.GetService<IA2AAgentCatalog>();
        var agents = options.Enabled && catalog is not null ? await catalog.ListAsync(cancellationToken) : [];
        return Ok(A2AIntegrationStatusBuilder.Build(options, agents, services.GetService<IA2ATaskStore>()?.GetType()));
    }
}
