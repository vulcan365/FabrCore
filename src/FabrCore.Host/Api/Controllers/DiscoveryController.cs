using Fabr.Sdk;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;

namespace Fabr.Host.Api.Controllers
{
    [ApiController]
    [Route("fabrapi/[controller]")]
    public class DiscoveryController : Controller
    {
        private readonly IFabrRegistry _registry;
        private readonly ILogger<DiscoveryController> _logger;

        public DiscoveryController(IFabrRegistry registry, ILogger<DiscoveryController> logger)
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
