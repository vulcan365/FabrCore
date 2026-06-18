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

    /// <summary>
    /// Request for ensuring a blueprint-defined set of agents exists for a user.
    /// </summary>
    public class AgentBlueprintRequest
    {
        public string? Name { get; set; }
        public string? Version { get; set; }
        public List<AgentConfiguration> Agents { get; set; } = new();
    }

    /// <summary>
    /// Response for blueprint agent ensure processing.
    /// </summary>
    public class AgentBlueprintResponse
    {
        public string? Name { get; set; }
        public string? Version { get; set; }
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
            [FromHeader(Name = "x-user-handle")] string userHandle,
            [FromBody] List<AgentConfiguration> configs,
            [FromQuery] HealthDetailLevel detailLevel = HealthDetailLevel.Basic)
        {
            if (configs == null || !configs.Any())
            {
                return BadRequest("Request body must contain a list of configurations.");
            }

            var results = await _agentService.ConfigureAgentsAsync(userHandle, configs, detailLevel);

            var response = new CreateAgentsResponse
            {
                TotalRequested = configs.Count,
                SuccessCount = results.Count(r => r.State == HealthState.Healthy),
                FailureCount = results.Count(r => r.State != HealthState.Healthy),
                Results = results
            };

            return Ok(response);
        }

        [HttpPost("blueprint")]
        public async Task<IActionResult> PostBlueprint(
            [FromHeader(Name = "x-user-handle")] string userHandle,
            [FromBody] AgentBlueprintRequest request,
            [FromQuery] HealthDetailLevel detailLevel = HealthDetailLevel.Basic)
        {
            if (string.IsNullOrWhiteSpace(userHandle))
            {
                return BadRequest("x-user-handle header is required.");
            }

            if (request == null || request.Agents == null || !request.Agents.Any())
            {
                return BadRequest("Request body must contain an agents list.");
            }

            try
            {
                var results = await _agentService.EnsureAgentsAsync(userHandle, request.Agents, detailLevel);

                var response = new AgentBlueprintResponse
                {
                    Name = request.Name,
                    Version = request.Version,
                    TotalRequested = request.Agents.Count,
                    SuccessCount = results.Count(r => r.State == HealthState.Healthy),
                    FailureCount = results.Count(r => r.State != HealthState.Healthy),
                    Results = results
                };

                return Ok(response);
            }
            catch (ArgumentException ex)
            {
                return BadRequest(ex.Message);
            }
        }

        [HttpGet("health/{handle}")]
        public async Task<IActionResult> GetHealth(
            [FromHeader(Name = "x-user-handle")] string userHandle,
            [FromRoute] string handle,
            [FromQuery] HealthDetailLevel detailLevel = HealthDetailLevel.Basic)
        {
            try
            {
                var health = await _agentService.GetHealthAsync(userHandle, handle, detailLevel);
                return Ok(health);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error getting health for agent {userHandle}:{Handle}", userHandle, handle);
                return StatusCode(500, new { Error = "Failed to get agent health", Message = ex.Message });
            }
        }

        [HttpDelete("{handle}")]
        public async Task<IActionResult> DeleteAgent(
            [FromHeader(Name = "x-user-handle")] string userHandle,
            [FromRoute] string handle)
        {
            if (string.IsNullOrWhiteSpace(userHandle))
            {
                return BadRequest("x-user-handle header is required.");
            }

            if (string.IsNullOrWhiteSpace(handle))
            {
                return BadRequest("Agent handle is required.");
            }

            try
            {
                var result = await _agentService.EvictAgentAsync(userHandle, handle);
                return Ok(result);
            }
            catch (InvalidOperationException ex) when (ex.Message.Contains("actively processing", StringComparison.OrdinalIgnoreCase))
            {
                _logger.LogWarning(ex, "Agent eviction conflicted because agent is active: {UserHandle}:{Handle}",
                    userHandle, handle);
                return Conflict(new { Error = "Agent is actively processing a message", Message = ex.Message });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error evicting agent {UserHandle}:{Handle}", userHandle, handle);
                return StatusCode(500, new { Error = "Failed to evict agent", Message = ex.Message });
            }
        }

        [HttpPost("chat/{handle}")]
        public async Task<IActionResult> PostChat([FromHeader(Name = "x-user-handle")] string userHandle, [FromRoute] string handle, [FromBody] string message)
        {
            if (string.IsNullOrEmpty(message))
            {
                return BadRequest("Request body cannot be empty.");
            }

            var response = await _agentService.SendAndReceiveMessageAsync(userHandle, handle, message);
            return Ok(response);
        }

        [HttpPost("event/{handle}")]
        public async Task<IActionResult> PostEvent(
            [FromHeader(Name = "x-user-handle")] string userHandle,
            [FromRoute] string handle,
            [FromBody] EventMessage message)
        {
            try
            {
                await _agentService.SendEventAsync(userHandle, handle, message);
                return Accepted();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error sending event to agent {userHandle}:{Handle}", userHandle, handle);
                return StatusCode(500, new { Error = "Failed to send event", Message = ex.Message });
            }
        }
    }
}
