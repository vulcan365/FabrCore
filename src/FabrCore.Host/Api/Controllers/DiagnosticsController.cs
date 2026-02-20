using Fabr.Core;
using Fabr.Core.Interfaces;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using Orleans;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace Fabr.Host.Api.Controllers
{
    [ApiController]
    [Route("fabrapi/[controller]")]
    public class DiagnosticsController : Controller
    {
        private readonly IClusterClient clusterClient;
        private readonly ILogger<DiagnosticsController> logger;

        public DiagnosticsController(IClusterClient clusterClient, ILogger<DiagnosticsController> logger)
        {
            this.clusterClient = clusterClient;
            this.logger = logger;
        }

        [HttpGet("agents")]
        public async Task<IActionResult> GetAllAgents([FromQuery] string? status = null)
        {
            try
            {
                var registry = clusterClient.GetGrain<IAgentManagementGrain>(0);

                List<AgentInfo> agents = status?.ToLower() switch
                {
                    "active" => await registry.GetActiveAgents(),
                    "deactivated" => await registry.GetDeactivatedAgents(),
                    _ => await registry.GetAllAgents()
                };

                logger.LogInformation("Retrieved {Count} agents with status filter: {Status}",
                    agents.Count, status ?? "all");

                return Ok(new
                {
                    Count = agents.Count,
                    Agents = agents
                });
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Error retrieving agents with status: {Status}", status);
                return StatusCode(500, new { Error = "Failed to retrieve agents", Message = ex.Message });
            }
        }

        [HttpGet("agents/{key}")]
        public async Task<IActionResult> GetAgent(string key)
        {
            try
            {
                var registry = clusterClient.GetGrain<IAgentManagementGrain>(0);
                var agent = await registry.GetAgentInfo(key);

                if (agent == null)
                {
                    logger.LogWarning("Agent not found in registry: {Key}", key);
                    return NotFound(new { Message = $"Agent '{key}' not found in registry" });
                }

                logger.LogInformation("Retrieved agent info for: {Key}", key);
                return Ok(agent);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Error retrieving agent: {Key}", key);
                return StatusCode(500, new { Error = "Failed to retrieve agent", Message = ex.Message });
            }
        }

        [HttpGet("agents/statistics")]
        public async Task<IActionResult> GetStatistics()
        {
            try
            {
                var registry = clusterClient.GetGrain<IAgentManagementGrain>(0);
                var stats = await registry.GetAgentStatistics();

                logger.LogInformation("Retrieved agent statistics - Total: {Total}, Active: {Active}, Deactivated: {Deactivated}",
                    stats.GetValueOrDefault("Total", 0),
                    stats.GetValueOrDefault("Active", 0),
                    stats.GetValueOrDefault("Deactivated", 0));

                return Ok(stats);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Error retrieving agent statistics");
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

                var registry = clusterClient.GetGrain<IAgentManagementGrain>(0);
                var purgedCount = await registry.PurgeDeactivatedAgentsOlderThan(
                    TimeSpan.FromHours(olderThanHours));

                logger.LogInformation("Purged {Count} agents older than {Hours} hours",
                    purgedCount, olderThanHours);

                return Ok(new
                {
                    PurgedCount = purgedCount,
                    Message = $"Purged {purgedCount} agents deactivated more than {olderThanHours} hours ago"
                });
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Error purging old agents");
                return StatusCode(500, new { Error = "Failed to purge agents", Message = ex.Message });
            }
        }
    }
}
