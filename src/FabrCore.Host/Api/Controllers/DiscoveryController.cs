using FabrCore.Sdk;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;

namespace FabrCore.Host.Api.Controllers
{
    [ApiController]
    [Route("fabrcoreapi/[controller]")]
    public class DiscoveryController : Controller
    {
        private readonly IFabrCoreRegistry _registry;
        private readonly ILogger<DiscoveryController> _logger;

        public DiscoveryController(IFabrCoreRegistry registry, ILogger<DiscoveryController> logger)
        {
            _registry = registry;
            _logger = logger;
        }

        [HttpGet]
        public IActionResult Get()
        {
            try
            {
                var result = new
                {
                    agents = _registry.GetAgentTypes(),
                    plugins = _registry.GetPlugins(),
                    tools = _registry.GetTools()
                };

                return Ok(result);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error retrieving discovery information");
                return StatusCode(500, new { Error = "Failed to retrieve discovery information", Message = ex.Message });
            }
        }
    }
}
