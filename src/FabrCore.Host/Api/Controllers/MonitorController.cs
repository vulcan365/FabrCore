using FabrCore.Core.Monitoring;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace FabrCore.Host.Api.Controllers
{
    /// <summary>
    /// Exposes <see cref="IAgentMessageMonitor"/> state over REST so operators and
    /// dashboards can see message flow, event flow, LLM calls and per-agent token
    /// usage without having to inject the monitor into a custom UI.
    /// </summary>
    [ApiController]
    [Route("fabrcoreapi/[controller]")]
    public class MonitorController : Controller
    {
        // Defensive ceiling so a malformed client can't ask for the whole buffer at once.
        private const int DefaultLimit = 100;
        private const int MaxLimit = 1000;

        private readonly IAgentMessageMonitor _monitor;
        private readonly IHostEnvironment _environment;
        private readonly ILogger<MonitorController> _logger;
        private readonly ITokenCostCalculator? _costCalculator;

        public MonitorController(
            IAgentMessageMonitor monitor,
            IHostEnvironment environment,
            ILogger<MonitorController> logger,
            ITokenCostCalculator? costCalculator = null)
        {
            _monitor = monitor;
            _environment = environment;
            _logger = logger;
            _costCalculator = costCalculator;
        }

        [HttpGet("messages")]
        public async Task<IActionResult> GetMessages(
            [FromQuery] string? agentHandle = null,
            [FromQuery] int? limit = null,
            [FromQuery] DateTimeOffset? since = null,
            [FromQuery] string? direction = null)
        {
            var effectiveLimit = ClampLimit(limit);
            // Pull extra up front so server-side filters (since, direction) still leave
            // us with a useful page after trimming.
            var pullLimit = (since.HasValue || !string.IsNullOrEmpty(direction))
                ? Math.Min(effectiveLimit * 4, MaxLimit)
                : effectiveLimit;

            var messages = await _monitor.GetMessagesAsync(agentHandle, pullLimit);

            IEnumerable<MonitoredMessage> filtered = messages;
            if (since.HasValue)
                filtered = filtered.Where(m => m.Timestamp >= since.Value);
            if (!string.IsNullOrEmpty(direction) && Enum.TryParse<MessageDirection>(direction, ignoreCase: true, out var d))
                filtered = filtered.Where(m => m.Direction == d);

            var page = filtered.Take(effectiveLimit).ToList();
            return Ok(new { Count = page.Count, Limit = effectiveLimit, Messages = page });
        }

        [HttpGet("events")]
        public async Task<IActionResult> GetEvents(
            [FromQuery] string? agentHandle = null,
            [FromQuery] int? limit = null,
            [FromQuery] DateTimeOffset? since = null)
        {
            var effectiveLimit = ClampLimit(limit);
            var pullLimit = since.HasValue ? Math.Min(effectiveLimit * 4, MaxLimit) : effectiveLimit;

            var events = await _monitor.GetEventsAsync(agentHandle, pullLimit);

            IEnumerable<MonitoredEvent> filtered = events;
            if (since.HasValue)
                filtered = filtered.Where(e => e.Timestamp >= since.Value);

            var page = filtered.Take(effectiveLimit).ToList();
            return Ok(new { Count = page.Count, Limit = effectiveLimit, Events = page });
        }

        [HttpGet("llm-calls")]
        public async Task<IActionResult> GetLlmCalls(
            [FromQuery] string? agentHandle = null,
            [FromQuery] int? limit = null,
            [FromQuery] DateTimeOffset? since = null,
            [FromQuery] bool? failedOnly = null)
        {
            var effectiveLimit = ClampLimit(limit);
            var pullLimit = (since.HasValue || failedOnly == true)
                ? Math.Min(effectiveLimit * 4, MaxLimit)
                : effectiveLimit;

            var calls = await _monitor.GetLlmCallsAsync(agentHandle, pullLimit);

            IEnumerable<MonitoredLlmCall> filtered = calls;
            if (since.HasValue)
                filtered = filtered.Where(c => c.Timestamp >= since.Value);
            if (failedOnly == true)
                filtered = filtered.Where(c => !string.IsNullOrEmpty(c.ErrorMessage));

            var page = filtered.Take(effectiveLimit).ToList();
            return Ok(new
            {
                Count = page.Count,
                Limit = effectiveLimit,
                PayloadsCaptured = _monitor.LlmCaptureOptions.CapturePayloads,
                Calls = page
            });
        }

        [HttpGet("tokens")]
        public async Task<IActionResult> GetAllTokenSummaries()
        {
            var summaries = await _monitor.GetAllAgentTokenSummariesAsync();

            long totalIn = 0, totalOut = 0, totalReasoning = 0, totalCached = 0, totalCalls = 0, totalMessages = 0;
            foreach (var s in summaries)
            {
                totalIn += s.TotalInputTokens;
                totalOut += s.TotalOutputTokens;
                totalReasoning += s.TotalReasoningTokens;
                totalCached += s.TotalCachedInputTokens;
                totalCalls += s.TotalLlmCalls;
                totalMessages += s.TotalMessages;
            }

            return Ok(new
            {
                AgentCount = summaries.Count,
                Totals = new
                {
                    InputTokens = totalIn,
                    OutputTokens = totalOut,
                    ReasoningTokens = totalReasoning,
                    CachedInputTokens = totalCached,
                    LlmCalls = totalCalls,
                    Messages = totalMessages
                },
                Agents = summaries
            });
        }

        /// <summary>
        /// Cost attribution per agent, computed from captured LLM calls. Requires
        /// an <see cref="ITokenCostCalculator"/> registered in DI (default reads
        /// from configuration under <c>FabrCore:ModelPricing</c>).
        /// </summary>
        [HttpGet("costs")]
        public async Task<IActionResult> GetCosts()
        {
            if (_costCalculator is null)
            {
                return Ok(new
                {
                    PricingConfigured = false,
                    Message = "No ITokenCostCalculator is registered.",
                });
            }

            // Walk the recent LLM call buffer — cost must be attributed per (agent, model)
            // pair because token summaries don't track model mix.
            var calls = await _monitor.GetLlmCallsAsync(null, MaxLimit);

            var perAgentPerModel = calls
                .Where(c => !string.IsNullOrEmpty(c.AgentHandle))
                .GroupBy(c => (Agent: c.AgentHandle!, Model: c.Model ?? "(unknown)"))
                .Select(g => new
                {
                    Agent = g.Key.Agent,
                    Model = g.Key.Model,
                    LlmCalls = g.Count(),
                    InputTokens = g.Sum(c => c.InputTokens),
                    OutputTokens = g.Sum(c => c.OutputTokens),
                    CachedInputTokens = g.Sum(c => c.CachedInputTokens),
                    ReasoningTokens = g.Sum(c => c.ReasoningTokens),
                    CostUsd = _costCalculator.EstimateUsd(
                        g.Key.Model,
                        g.Sum(c => c.InputTokens),
                        g.Sum(c => c.OutputTokens),
                        g.Sum(c => c.CachedInputTokens),
                        g.Sum(c => c.ReasoningTokens)),
                })
                .ToList();

            var perAgent = perAgentPerModel
                .GroupBy(x => x.Agent)
                .Select(g => new
                {
                    Agent = g.Key,
                    TotalCostUsd = g.Sum(x => x.CostUsd ?? 0m),
                    HasUnpricedCalls = g.Any(x => x.CostUsd is null),
                    Models = g.ToList(),
                })
                .ToList();

            return Ok(new
            {
                PricingConfigured = true,
                SampleWindow = $"last {calls.Count} LLM calls",
                TotalCostUsd = perAgent.Sum(a => a.TotalCostUsd),
                Agents = perAgent,
            });
        }

        [HttpGet("tokens/{agentHandle}")]
        public async Task<IActionResult> GetTokenSummary(string agentHandle)
        {
            if (string.IsNullOrWhiteSpace(agentHandle))
                return BadRequest(new { Error = "agentHandle is required" });

            var summary = await _monitor.GetAgentTokenSummaryAsync(agentHandle);
            if (summary is null)
                return NotFound(new { Message = $"No token summary for agent '{agentHandle}'" });

            return Ok(summary);
        }

        /// <summary>
        /// Flat list of recent tool/function calls extracted from captured LLM calls.
        /// Only populated when <c>LlmCaptureOptions.CapturePayloads</c> is true, since
        /// tool calls are stored inside the captured response payload.
        /// </summary>
        [HttpGet("tool-calls")]
        public async Task<IActionResult> GetToolCalls(
            [FromQuery] string? agentHandle = null,
            [FromQuery] string? toolName = null,
            [FromQuery] DateTimeOffset? since = null,
            [FromQuery] int? limit = null)
        {
            if (!_monitor.LlmCaptureOptions.CapturePayloads)
            {
                return Ok(new
                {
                    Count = 0,
                    PayloadsCaptured = false,
                    Message = "Tool call bodies are only stored when LlmCaptureOptions.CapturePayloads is true.",
                    ToolCalls = Array.Empty<object>(),
                });
            }

            var effectiveLimit = ClampLimit(limit);
            var calls = await _monitor.GetLlmCallsAsync(agentHandle, MaxLimit);

            IEnumerable<MonitoredLlmCall> filtered = calls;
            if (since.HasValue)
                filtered = filtered.Where(c => c.Timestamp >= since.Value);

            var flattened = filtered
                .Where(c => c.ToolCalls is { Count: > 0 })
                .SelectMany(c => c.ToolCalls!.Select(t => new
                {
                    c.Timestamp,
                    c.AgentHandle,
                    c.Model,
                    ParentLlmCallId = c.Id,
                    c.ParentMessageId,
                    ToolName = t.Name,
                    t.CallId,
                    t.Arguments,
                    t.Truncated,
                    LlmDurationMs = c.DurationMs,
                    LlmErrorMessage = c.ErrorMessage,
                }))
                .Where(t => string.IsNullOrEmpty(toolName)
                    || string.Equals(t.ToolName, toolName, StringComparison.OrdinalIgnoreCase))
                .Take(effectiveLimit)
                .ToList();

            return Ok(new
            {
                Count = flattened.Count,
                PayloadsCaptured = true,
                ToolCalls = flattened,
            });
        }

        /// <summary>
        /// Recent errors across agents, aggregated from failed LLM calls in the
        /// monitor buffer. Answers "what broke in the last N minutes" without
        /// tailing logs.
        /// </summary>
        [HttpGet("errors")]
        public async Task<IActionResult> GetErrors(
            [FromQuery] string? agentHandle = null,
            [FromQuery] DateTimeOffset? since = null,
            [FromQuery] int? limit = null)
        {
            var effectiveLimit = ClampLimit(limit);
            // Pull a wider slice so the failure filter still yields meaningful totals.
            var calls = await _monitor.GetLlmCallsAsync(agentHandle, MaxLimit);

            IEnumerable<MonitoredLlmCall> failed = calls.Where(c => !string.IsNullOrEmpty(c.ErrorMessage));
            if (since.HasValue)
                failed = failed.Where(c => c.Timestamp >= since.Value);

            var failedList = failed.ToList();

            var byAgent = failedList
                .GroupBy(c => c.AgentHandle ?? "(unknown)")
                .ToDictionary(g => g.Key, g => g.Count());

            var byModel = failedList
                .Where(c => !string.IsNullOrEmpty(c.Model))
                .GroupBy(c => c.Model!)
                .ToDictionary(g => g.Key, g => g.Count());

            var recent = failedList
                .Take(effectiveLimit)
                .Select(c => new
                {
                    c.Timestamp,
                    c.AgentHandle,
                    c.Model,
                    c.OriginContext,
                    c.DurationMs,
                    c.ErrorMessage,
                    c.ParentMessageId,
                })
                .ToList();

            return Ok(new
            {
                Since = since,
                TotalErrors = failedList.Count,
                ByAgent = byAgent,
                ByModel = byModel,
                Recent = recent,
            });
        }

        /// <summary>
        /// Wipes in-memory monitor buffers. Guarded to non-production environments to
        /// avoid accidental data loss during investigations.
        /// </summary>
        [HttpPost("clear")]
        public async Task<IActionResult> Clear()
        {
            if (!_environment.IsDevelopment())
            {
                _logger.LogWarning("Rejected /monitor/clear call outside development environment ({Env})",
                    _environment.EnvironmentName);
                return StatusCode(403, new { Error = "Clear is only allowed in Development environment" });
            }

            await _monitor.ClearAsync();
            _logger.LogInformation("Agent message monitor cleared");
            return Ok(new { Message = "Monitor buffers cleared" });
        }

        /// <summary>
        /// Inspect current LLM capture configuration.
        /// </summary>
        [HttpGet("config")]
        public IActionResult GetConfig()
        {
            var opts = _monitor.LlmCaptureOptions;
            return Ok(new
            {
                opts.Enabled,
                opts.CapturePayloads,
                opts.MaxBufferedCalls,
                opts.MaxPayloadChars,
                opts.MaxToolArgsChars,
            });
        }

        /// <summary>
        /// Mutate LLM capture configuration at runtime. Accepts only the fields
        /// the caller wants to change — unspecified fields are preserved.
        /// Guarded to Development by default; production can promote this later.
        /// </summary>
        [HttpPost("config")]
        public IActionResult SetConfig([FromBody] MonitorConfigUpdate update)
        {
            if (!_environment.IsDevelopment())
            {
                _logger.LogWarning("Rejected /monitor/config write outside development environment ({Env})",
                    _environment.EnvironmentName);
                return StatusCode(403, new { Error = "Runtime config writes are only allowed in Development environment" });
            }

            var opts = _monitor.LlmCaptureOptions;
            if (update.Enabled.HasValue) opts.Enabled = update.Enabled.Value;
            if (update.CapturePayloads.HasValue) opts.CapturePayloads = update.CapturePayloads.Value;
            if (update.MaxBufferedCalls.HasValue && update.MaxBufferedCalls.Value > 0)
                opts.MaxBufferedCalls = update.MaxBufferedCalls.Value;
            if (update.MaxPayloadChars.HasValue && update.MaxPayloadChars.Value >= 0)
                opts.MaxPayloadChars = update.MaxPayloadChars.Value;
            if (update.MaxToolArgsChars.HasValue && update.MaxToolArgsChars.Value >= 0)
                opts.MaxToolArgsChars = update.MaxToolArgsChars.Value;

            _logger.LogInformation(
                "Monitor LLM capture config updated (Enabled={Enabled}, CapturePayloads={CapturePayloads})",
                opts.Enabled, opts.CapturePayloads);

            return Ok(new
            {
                opts.Enabled,
                opts.CapturePayloads,
                opts.MaxBufferedCalls,
                opts.MaxPayloadChars,
                opts.MaxToolArgsChars,
            });
        }

        public class MonitorConfigUpdate
        {
            public bool? Enabled { get; set; }
            public bool? CapturePayloads { get; set; }
            public int? MaxBufferedCalls { get; set; }
            public int? MaxPayloadChars { get; set; }
            public int? MaxToolArgsChars { get; set; }
        }

        private static int ClampLimit(int? limit)
        {
            if (!limit.HasValue) return DefaultLimit;
            if (limit.Value <= 0) return DefaultLimit;
            if (limit.Value > MaxLimit) return MaxLimit;
            return limit.Value;
        }
    }
}
