using FabrCore.Core.Monitoring;
using FabrCore.Host.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;

namespace FabrCore.Host.Api.Controllers
{
    /// <summary>
    /// Detailed health view that aggregates agent-registry, session and token
    /// counters into a single JSON payload. Complements the lightweight
    /// <c>/health/live</c> and <c>/health/ready</c> endpoints wired via
    /// <c>MapHealthChecks</c>.
    /// </summary>
    [ApiController]
    [Route("health")]
    public class HealthController : Controller
    {
        private readonly IFabrCoreAgentService _agentService;
        private readonly IAgentMessageMonitor _monitor;
        private readonly ILogger<HealthController> _logger;

        public HealthController(
            IFabrCoreAgentService agentService,
            IAgentMessageMonitor monitor,
            ILogger<HealthController> logger)
        {
            _agentService = agentService;
            _monitor = monitor;
            _logger = logger;
        }

        [HttpGet("")]
        public async Task<IActionResult> Details()
        {
            try
            {
                var stats = await _agentService.GetAgentStatisticsAsync();
                var tokenSummaries = await _monitor.GetAllAgentTokenSummariesAsync();

                long totalInput = 0, totalOutput = 0, totalReasoning = 0, totalCached = 0, totalCalls = 0;
                foreach (var s in tokenSummaries)
                {
                    totalInput += s.TotalInputTokens;
                    totalOutput += s.TotalOutputTokens;
                    totalReasoning += s.TotalReasoningTokens;
                    totalCached += s.TotalCachedInputTokens;
                    totalCalls += s.TotalLlmCalls;
                }

                return Ok(new
                {
                    Status = "Healthy",
                    Timestamp = DateTimeOffset.UtcNow,
                    Uptime = Environment.TickCount64 / 1000,
                    Agents = new
                    {
                        Total = stats.GetValueOrDefault("Total", 0),
                        Active = stats.GetValueOrDefault("Active", 0),
                        Deactivated = stats.GetValueOrDefault("Deactivated", 0)
                    },
                    Llm = new
                    {
                        TrackedAgents = tokenSummaries.Count,
                        TotalCalls = totalCalls,
                        TotalInputTokens = totalInput,
                        TotalOutputTokens = totalOutput,
                        TotalReasoningTokens = totalReasoning,
                        TotalCachedInputTokens = totalCached
                    },
                    CapturePayloads = _monitor.LlmCaptureOptions.CapturePayloads
                });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error computing detailed health view");
                return StatusCode(503, new
                {
                    Status = "Degraded",
                    Error = ex.Message,
                    Timestamp = DateTimeOffset.UtcNow
                });
            }
        }
    }
}
