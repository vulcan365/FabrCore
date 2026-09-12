using FabrCore.Core.Blueprints;
using FabrCore.Host.Security;
using FabrCore.Services.Contracts.Capabilities;
using FabrCore.Services.GraphRag.Administration;
using FabrCore.Services.Memory.Administration;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using FabrCore.Host.Configuration;

namespace FabrCore.Host.Api.Controllers;

[ApiController]
[Authorize(Policy = FabrCoreAdminAuthenticationDefaults.Policy)]
[Route("fabrcoreapi/capabilities")]
public sealed class CapabilityController(
    IServiceProvider services,
    IEnumerable<IBlueprintExpander> blueprintExpanders,
    IOptions<RemoteAdministrationOptions> remoteAdministrationOptions) : ControllerBase
{
    [HttpGet]
    public IActionResult Get()
    {
        return Ok(FabrCore.Host.Services.ClusterCapabilityFactory.Create(services.GetRequiredService<FabrCore.Core.FabrCoreFeatureState>(), blueprintExpanders, remoteAdministrationOptions.Value));
    }
}
