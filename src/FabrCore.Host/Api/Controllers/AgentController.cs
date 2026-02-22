using FabrCore.Core;
using FabrCore.Host.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace FabrCore.Host.Api.Controllers
{
    /// <summary>
    /// Response for batch agent creation.
    /// </summary>
    public class CreateAgentsResponse
    {
        public int TotalRequested { get; set; }
        public int SuccessCount { get; set; }
        public int FailureCount { get; set; }
        public List<AgentHealthStatus> Results { get; set; } = new();
    }

    [ApiController]
    [Route("fabrcoreapi/[controller]")]
    public class AgentController : Controller
    {
        private readonly IFabrCoreAgentService _agentService;
        private readonly ILogger<AgentController> _logger;

        public AgentController(IFabrCoreAgentService agentService, ILogger<AgentController> logger)
        {
            _agentService = agentService;
            _logger = logger;
        }

        [HttpPost("create")]
        public async Task<IActionResult> PostCreate(
            [FromHeader(Name = "x-user")] string userId,
            [FromBody] List<AgentConfiguration> configs,
            [FromQuery] HealthDetailLevel detailLevel = HealthDetailLevel.Basic)
        {
            if (configs == null || !configs.Any())
            {
                return BadRequest("Request body must contain a list of configurations.");
            }

            var results = await _agentService.ConfigureAgentsAsync(userId, configs, detailLevel);

            var response = new CreateAgentsResponse
            {
                TotalRequested = configs.Count,
                SuccessCount = results.Count(r => r.State == HealthState.Healthy),
                FailureCount = results.Count(r => r.State != HealthState.Healthy),
                Results = results
            };

            return Ok(response);
        }

        [HttpGet("health/{handle}")]
        public async Task<IActionResult> GetHealth(
            [FromHeader(Name = "x-user")] string userId,
            [FromRoute] string handle,
            [FromQuery] HealthDetailLevel detailLevel = HealthDetailLevel.Basic)
        {
            try
            {
                var health = await _agentService.GetHealthAsync(userId, handle, detailLevel);
                return Ok(health);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error getting health for agent {UserId}:{Handle}", userId, handle);
                return StatusCode(500, new { Error = "Failed to get agent health", Message = ex.Message });
            }
        }

        [HttpPost("chat/{handle}")]
        public async Task<IActionResult> PostChat([FromHeader(Name = "x-user")] string userId, [FromRoute] string handle, [FromBody] string message)
        {
            if (string.IsNullOrEmpty(message))
            {
                return BadRequest("Request body cannot be empty.");
            }

            var response = await _agentService.SendAndReceiveMessageAsync(userId, handle, message);
            return Ok(response);
        }

        [HttpPost("event/{handle}")]
        public async Task<IActionResult> PostEvent(
            [FromHeader(Name = "x-user")] string userId,
            [FromRoute] string handle,
            [FromBody] AgentMessage message,
            [FromQuery] string? streamName = null)
        {
            try
            {
                await _agentService.SendEventAsync(userId, handle, message, streamName);
                return Accepted();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error sending event to agent {UserId}:{Handle}", userId, handle);
                return StatusCode(500, new { Error = "Failed to send event", Message = ex.Message });
            }
        }
    }
}
