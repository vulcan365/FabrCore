using FabrCore.Core;
using FabrCore.Core.Acl;
using FabrCore.Core.Monitoring;
using FabrCore.Host.Services;
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
        private readonly IAclProvider _aclProvider;

        public MonitorController(
            IAgentMessageMonitor monitor,
            IHostEnvironment environment,
            ILogger<MonitorController> logger,
            IAclProvider aclProvider,
            ITokenCostCalculator? costCalculator = null)
        {
            _monitor = monitor;
            _environment = environment;
            _logger = logger;
            _aclProvider = aclProvider;
            _costCalculator = costCalculator;
        }

        [HttpGet("messages")]
        public async Task<IActionResult> GetMessages(
            [FromHeader(Name = "x-user-handle")] string userHandle,
            [FromQuery] string? agentHandle = null,
            [FromQuery] int? limit = null,
            [FromQuery] DateTimeOffset? since = null,
            [FromQuery] string? direction = null)
        {
            var authorization = await AuthorizeMonitorReadAsync(userHandle, agentHandle);
            if (authorization is not null) return authorization;

            var effectiveLimit = ClampLimit(limit);
            // Pull extra up front so server-side filters (since, direction) still leave
            // us with a useful page after trimming.
            var pullLimit = string.IsNullOrWhiteSpace(agentHandle)
                ? MaxLimit
                : (since.HasValue || !string.IsNullOrEmpty(direction))
                ? Math.Min(effectiveLimit * 4, MaxLimit)
                : effectiveLimit;

            var messages = await _monitor.GetMessagesAsync(agentHandle, pullLimit);

            IEnumerable<MonitoredMessage> filtered = string.IsNullOrWhiteSpace(agentHandle)
                ? await FilterMessagesAsync(userHandle, messages)
                : messages;
            if (since.HasValue)
                filtered = filtered.Where(m => m.Timestamp >= since.Value);
            if (!string.IsNullOrEmpty(direction) && Enum.TryParse<MessageDirection>(direction, ignoreCase: true, out var d))
                filtered = filtered.Where(m => m.Direction == d);

            var page = filtered.Take(effectiveLimit).ToList();
            return Ok(new { Count = page.Count, Limit = effectiveLimit, Messages = page });
        }

        [HttpGet("events")]
        public async Task<IActionResult> GetEvents(
            [FromHeader(Name = "x-user-handle")] string userHandle,
            [FromQuery] string? agentHandle = null,
            [FromQuery] int? limit = null,
            [FromQuery] DateTimeOffset? since = null)
        {
            var authorization = await AuthorizeMonitorReadAsync(userHandle, agentHandle);
            if (authorization is not null) return authorization;

            var effectiveLimit = ClampLimit(limit);
            var pullLimit = string.IsNullOrWhiteSpace(agentHandle)
                ? MaxLimit
                : since.HasValue ? Math.Min(effectiveLimit * 4, MaxLimit) : effectiveLimit;

            var events = await _monitor.GetEventsAsync(agentHandle, pullLimit);

            IEnumerable<MonitoredEvent> filtered = string.IsNullOrWhiteSpace(agentHandle)
                ? await FilterEventsAsync(userHandle, events)
                : events;
            if (since.HasValue)
                filtered = filtered.Where(e => e.Timestamp >= since.Value);

            var page = filtered.Take(effectiveLimit).ToList();
            return Ok(new { Count = page.Count, Limit = effectiveLimit, Events = page });
        }

        [HttpGet("llm-calls")]
        public async Task<IActionResult> GetLlmCalls(
            [FromHeader(Name = "x-user-handle")] string userHandle,
            [FromQuery] string? agentHandle = null,
            [FromQuery] int? limit = null,
            [FromQuery] DateTimeOffset? since = null,
            [FromQuery] bool? failedOnly = null)
        {
            var authorization = await AuthorizeMonitorReadAsync(userHandle, agentHandle);
            if (authorization is not null) return authorization;

            var effectiveLimit = ClampLimit(limit);
            var pullLimit = string.IsNullOrWhiteSpace(agentHandle)
                ? MaxLimit
                : (since.HasValue || failedOnly == true)
                ? Math.Min(effectiveLimit * 4, MaxLimit)
                : effectiveLimit;

            var calls = await _monitor.GetLlmCallsAsync(agentHandle, pullLimit);

            IEnumerable<MonitoredLlmCall> filtered = string.IsNullOrWhiteSpace(agentHandle)
                ? await FilterLlmCallsAsync(userHandle, calls)
                : calls;
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
        public async Task<IActionResult> GetAllTokenSummaries([FromHeader(Name = "x-user-handle")] string userHandle)
        {
            var authorization = RequireUser(userHandle);
            if (authorization is not null) return authorization;

            var summaries = await _monitor.GetAllAgentTokenSummariesAsync();
            summaries = await FilterTokenSummariesAsync(userHandle, summaries);

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
        public async Task<IActionResult> GetCosts([FromHeader(Name = "x-user-handle")] string userHandle)
        {
            var authorization = RequireUser(userHandle);
            if (authorization is not null) return authorization;

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
            calls = await FilterLlmCallsAsync(userHandle, calls);

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
        public async Task<IActionResult> GetTokenSummary(
            [FromHeader(Name = "x-user-handle")] string userHandle,
            string agentHandle)
        {
            if (string.IsNullOrWhiteSpace(agentHandle))
                return BadRequest(new { Error = "agentHandle is required" });

            var authorization = await AuthorizeMonitorReadAsync(userHandle, agentHandle);
            if (authorization is not null) return authorization;

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
            [FromHeader(Name = "x-user-handle")] string userHandle,
            [FromQuery] string? agentHandle = null,
            [FromQuery] string? toolName = null,
            [FromQuery] DateTimeOffset? since = null,
            [FromQuery] int? limit = null)
        {
            var authorization = await AuthorizeMonitorReadAsync(userHandle, agentHandle);
            if (authorization is not null) return authorization;

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

            IEnumerable<MonitoredLlmCall> filtered = string.IsNullOrWhiteSpace(agentHandle)
                ? await FilterLlmCallsAsync(userHandle, calls)
                : calls;
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
            [FromHeader(Name = "x-user-handle")] string userHandle,
            [FromQuery] string? agentHandle = null,
            [FromQuery] DateTimeOffset? since = null,
            [FromQuery] int? limit = null)
        {
            var authorization = await AuthorizeMonitorReadAsync(userHandle, agentHandle);
            if (authorization is not null) return authorization;

            var effectiveLimit = ClampLimit(limit);
            // Pull a wider slice so the failure filter still yields meaningful totals.
            var calls = await _monitor.GetLlmCallsAsync(agentHandle, MaxLimit);

            IEnumerable<MonitoredLlmCall> visibleCalls = string.IsNullOrWhiteSpace(agentHandle)
                ? await FilterLlmCallsAsync(userHandle, calls)
                : calls;
            IEnumerable<MonitoredLlmCall> failed = visibleCalls.Where(c => !string.IsNullOrEmpty(c.ErrorMessage));
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
        public IActionResult GetConfig([FromHeader(Name = "x-user-handle")] string userHandle)
        {
            var authorization = RequireUser(userHandle);
            if (authorization is not null) return authorization;

            var opts = _monitor.LlmCaptureOptions;
            return Ok(new
            {
                RecordingAvailable = IsRecordingAvailable,
                MonitorProvider = _monitor.GetType().Name,
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
        public IActionResult SetConfig(
            [FromHeader(Name = "x-user-handle")] string userHandle,
            [FromBody] MonitorConfigUpdate update)
        {
            var authorization = RequireUser(userHandle);
            if (authorization is not null) return authorization;

            if (!IsRecordingAvailable)
            {
                return StatusCode(409, new
                {
                    Error = "Agent message monitoring is not enabled on this host.",
                    Message = "Configure FabrCoreServerOptions.UseInMemoryAgentMessageMonitor() or UseAgentMessageMonitor<T>() before changing capture settings."
                });
            }

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
                RecordingAvailable = IsRecordingAvailable,
                MonitorProvider = _monitor.GetType().Name,
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

        private bool IsRecordingAvailable => _monitor is not NullAgentMessageMonitor;

        private static int ClampLimit(int? limit)
        {
            if (!limit.HasValue) return DefaultLimit;
            if (limit.Value <= 0) return DefaultLimit;
            if (limit.Value > MaxLimit) return MaxLimit;
            return limit.Value;
        }

        private IActionResult? RequireUser(string? userHandle)
        {
            return string.IsNullOrWhiteSpace(userHandle)
                ? BadRequest(new { Error = "x-user-handle header is required for monitor access" })
                : null;
        }

        private async Task<IActionResult?> AuthorizeMonitorReadAsync(string? userHandle, string? agentHandle)
        {
            var userResult = RequireUser(userHandle);
            if (userResult is not null)
            {
                return userResult;
            }

            if (string.IsNullOrWhiteSpace(agentHandle))
            {
                return null;
            }

            return await CanReadAgentAsync(userHandle!, agentHandle)
                ? null
                : StatusCode(403, new { Error = $"Access denied: '{userHandle}' cannot Read monitor data for '{agentHandle}'." });
        }

        private async Task<bool> CanReadAgentAsync(string userHandle, string? agentHandle)
        {
            if (string.IsNullOrWhiteSpace(agentHandle))
            {
                return false;
            }

            var (targetUserHandle, targetAgentHandle) = HandleUtilities.ParseHandle(agentHandle);
            if (string.IsNullOrWhiteSpace(targetUserHandle) || string.IsNullOrWhiteSpace(targetAgentHandle))
            {
                return false;
            }

            if (string.Equals(userHandle, targetUserHandle, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            var result = await _aclProvider.EvaluateAsync(userHandle, targetUserHandle, targetAgentHandle, AclPermission.Read);
            return result.Allowed;
        }

        private async Task<List<MonitoredMessage>> FilterMessagesAsync(string userHandle, IEnumerable<MonitoredMessage> messages)
        {
            var visible = new List<MonitoredMessage>();
            var cache = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);

            foreach (var message in messages)
            {
                if (await AnyReadableHandleAsync(userHandle, MessageHandles(message), cache))
                {
                    visible.Add(message);
                }
            }

            return visible;
        }

        private async Task<List<MonitoredEvent>> FilterEventsAsync(string userHandle, IEnumerable<MonitoredEvent> events)
        {
            var visible = new List<MonitoredEvent>();
            var cache = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);

            foreach (var monitoredEvent in events)
            {
                if (await AnyReadableHandleAsync(userHandle, [monitoredEvent.AgentHandle], cache))
                {
                    visible.Add(monitoredEvent);
                }
            }

            return visible;
        }

        private async Task<List<MonitoredLlmCall>> FilterLlmCallsAsync(string userHandle, IEnumerable<MonitoredLlmCall> calls)
        {
            var visible = new List<MonitoredLlmCall>();
            var cache = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);

            foreach (var call in calls)
            {
                if (await AnyReadableHandleAsync(userHandle, [call.AgentHandle], cache))
                {
                    visible.Add(call);
                }
            }

            return visible;
        }

        private async Task<List<AgentTokenSummary>> FilterTokenSummariesAsync(
            string userHandle,
            IEnumerable<AgentTokenSummary> summaries)
        {
            var visible = new List<AgentTokenSummary>();
            var cache = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);

            foreach (var summary in summaries)
            {
                if (await AnyReadableHandleAsync(userHandle, [summary.AgentHandle], cache))
                {
                    visible.Add(summary);
                }
            }

            return visible;
        }

        private async Task<bool> AnyReadableHandleAsync(
            string userHandle,
            IEnumerable<string?> handles,
            Dictionary<string, bool> cache)
        {
            foreach (var handle in handles)
            {
                if (string.IsNullOrWhiteSpace(handle))
                {
                    continue;
                }

                if (!cache.TryGetValue(handle, out var allowed))
                {
                    allowed = await CanReadAgentAsync(userHandle, handle);
                    cache[handle] = allowed;
                }

                if (allowed)
                {
                    return true;
                }
            }

            return false;
        }

        private static IEnumerable<string?> MessageHandles(MonitoredMessage message)
        {
            yield return message.AgentHandle;
            yield return message.ToHandle;
            yield return message.FromHandle;
            yield return message.DeliverToHandle;
            yield return message.OnBehalfOfHandle;
        }
    }
}
