using FabrCore.Host.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace FabrCore.Host.Api.Controllers
{
    [ApiController]
    [Route("fabrcoreapi/[controller]")]
    public class DiagnosticsController : Controller
    {
        private readonly IFabrCoreAgentService _agentService;
        private readonly ILogger<DiagnosticsController> _logger;

        public DiagnosticsController(IFabrCoreAgentService agentService, ILogger<DiagnosticsController> logger)
        {
            _agentService = agentService;
            _logger = logger;
        }

        [HttpGet("agents")]
        public async Task<IActionResult> GetAllAgents([FromQuery] string? status = null)
        {
            try
            {
                var agents = await _agentService.GetAgentsAsync(status);

                _logger.LogInformation("Retrieved {Count} agents with status filter: {Status}",
                    agents.Count, status ?? "all");

                return Ok(new
                {
                    Count = agents.Count,
                    Agents = agents
                });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error retrieving agents with status: {Status}", status);
                return StatusCode(500, new { Error = "Failed to retrieve agents", Message = ex.Message });
            }
        }

        [HttpGet("agents/{key}")]
        public async Task<IActionResult> GetAgent(string key)
        {
            try
            {
                var agent = await _agentService.GetAgentInfoAsync(key);

                if (agent == null)
                {
                    _logger.LogWarning("Agent not found in registry: {Key}", key);
                    return NotFound(new { Message = $"Agent '{key}' not found in registry" });
                }

                _logger.LogInformation("Retrieved agent info for: {Key}", key);
                return Ok(agent);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error retrieving agent: {Key}", key);
                return StatusCode(500, new { Error = "Failed to retrieve agent", Message = ex.Message });
            }
        }

        [HttpGet("principals")]
        public async Task<IActionResult> GetAllPrincipals([FromQuery] string? status = null)
        {
            try
            {
                var principals = await _agentService.GetPrincipalsAsync(status);

                _logger.LogInformation("Retrieved {Count} principals with status filter: {Status}",
                    principals.Count, status ?? "all");

                return Ok(new
                {
                    Count = principals.Count,
                    Principals = principals
                });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error retrieving principals with status: {Status}", status);
                return StatusCode(500, new { Error = "Failed to retrieve principals", Message = ex.Message });
            }
        }

        [HttpGet("principals/{handle}")]
        public async Task<IActionResult> GetPrincipal(string handle)
        {
            try
            {
                var principal = await _agentService.GetPrincipalInfoAsync(handle);

                if (principal == null)
                {
                    _logger.LogWarning("Principal not found in registry: {Handle}", handle);
                    return NotFound(new { Message = $"Principal '{handle}' not found in registry" });
                }

                _logger.LogInformation("Retrieved principal info for: {Handle}", handle);
                return Ok(principal);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error retrieving principal: {Handle}", handle);
                return StatusCode(500, new { Error = "Failed to retrieve principal", Message = ex.Message });
            }
        }

        [HttpGet("agents/statistics")]
        public async Task<IActionResult> GetStatistics()
        {
            try
            {
                var stats = await _agentService.GetAgentStatisticsAsync();

                _logger.LogInformation("Retrieved agent statistics - Total: {Total}, Active: {Active}, Deactivated: {Deactivated}",
                    stats.GetValueOrDefault("Total", 0),
                    stats.GetValueOrDefault("Active", 0),
                    stats.GetValueOrDefault("Deactivated", 0));

                return Ok(stats);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error retrieving agent statistics");
                return StatusCode(500, new { Error = "Failed to retrieve statistics", Message = ex.Message });
            }
        }

        [HttpPost("agents/purge")]
        public async Task<IActionResult> PurgeOldAgents([FromQuery] int olderThanHours = 24)
        {
            try
            {
                if (olderThanHours <= 0)
                {
                    return BadRequest(new { Error = "olderThanHours must be greater than 0" });
                }

                var purgedCount = await _agentService.PurgeDeactivatedAgentsAsync(
                    TimeSpan.FromHours(olderThanHours));

                _logger.LogInformation("Purged {Count} agents older than {Hours} hours",
                    purgedCount, olderThanHours);

                return Ok(new
                {
                    PurgedCount = purgedCount,
                    Message = $"Purged {purgedCount} agents deactivated more than {olderThanHours} hours ago"
                });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error purging old agents");
                return StatusCode(500, new { Error = "Failed to purge agents", Message = ex.Message });
            }
        }
    }
}
