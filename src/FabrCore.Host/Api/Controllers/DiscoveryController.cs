using FabrCore.Host.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;

namespace FabrCore.Host.Api.Controllers
{
    [ApiController]
    [Route("fabrcoreapi/[controller]")]
    public class DiscoveryController : Controller
    {
        private readonly IFabrCoreAgentService _agentService;
        private readonly ILogger<DiscoveryController> _logger;

        public DiscoveryController(IFabrCoreAgentService agentService, ILogger<DiscoveryController> logger)
        {
            _agentService = agentService;
            _logger = logger;
        }

        [HttpGet]
        public IActionResult Get()
        {
            try
            {
                var result = new
                {
                    agents = _agentService.GetAgentTypes(),
                    plugins = _agentService.GetPlugins(),
                    tools = _agentService.GetTools()
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
