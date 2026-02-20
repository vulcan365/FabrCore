using FabrCore.Core;
using FabrCore.Core.Interfaces;
using FabrCore.Host.Streaming;
using FabrCore.Sdk;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using Orleans;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
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
        private readonly IClusterClient clusterClient;
        private readonly ILogger<AgentController> logger;

        public AgentController(IClusterClient clusterClient, ILogger<AgentController> logger)
        {
            this.clusterClient = clusterClient;
            this.logger = logger;
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

            var results = new List<AgentHealthStatus>();

            foreach (var config in configs)
            {
                try
                {
                    var proxy = clusterClient.GetGrain<IAgentGrain>($"{userId}:{config.Handle}");
                    var health = await proxy.ConfigureAgent(config, detailLevel: detailLevel);
                    results.Add(health);
                }
                catch (Exception ex)
                {
                    logger.LogError(ex, "Error creating agent for user {UserId} with handle {Handle}",
                        userId, $"{userId}:{config.Handle}");

                    // Add unhealthy status for failed agent
                    results.Add(new AgentHealthStatus
                    {
                        Handle = $"{userId}:{config.Handle}",
                        State = HealthState.Unhealthy,
                        Timestamp = DateTime.UtcNow,
                        IsConfigured = false,
                        Message = $"Failed to create agent: {ex.Message}"
                    });
                }
            }

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
                var key = $"{userId}:{handle}";
                var proxy = clusterClient.GetGrain<IAgentGrain>(key);
                var health = await proxy.GetHealth(detailLevel);
                return Ok(health);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Error getting health for agent {UserId}:{Handle}", userId, handle);
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

            var key = $"{userId}:{handle}";
            var proxy = clusterClient.GetGrain<IAgentGrain>(key);
            var response = await proxy.OnMessage(new AgentMessage { ToHandle = $"{userId}:{handle}", FromHandle = "AgentController", Message = message });

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
                if (streamName != null)
                {
                    // Named event stream — publish directly, no handle prefixing
                    var stream = clusterClient.GetAgentEventStream(streamName);
                    await stream.OnNextAsync(message);

                    logger.LogDebug("Event published to named stream {StreamName} for user {UserId}", streamName, userId);
                }
                else
                {
                    // Default agent event stream
                    var key = $"{userId}:{handle}";
                    var stream = clusterClient.GetAgentEventStream(key);
                    await stream.OnNextAsync(message);

                    logger.LogDebug("Event published to agent {Handle} for user {UserId}", handle, userId);
                }

                return Accepted();
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Error sending event to agent {UserId}:{Handle}", userId, handle);
                return StatusCode(500, new { Error = "Failed to send event", Message = ex.Message });
            }
        }
    }
}
