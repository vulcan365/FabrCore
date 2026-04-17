using FabrCore.Core;
using FabrCore.Core.Monitoring;
using FabrCore.Host.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace FabrCore.Host.Api.Controllers
{
    /// <summary>
    /// Developer-only diagnostic endpoints. All endpoints require the host to be
    /// running in the Development environment. Do not enable in production without
    /// additional authorization.
    /// </summary>
    [ApiController]
    [Route("fabrcoreapi/[controller]")]
    public class DebugController : Controller
    {
        private readonly IHostEnvironment _environment;
        private readonly IAgentMessageMonitor _monitor;
        private readonly IFabrCoreAgentService _agentService;
        private readonly ILogger<DebugController> _logger;

        public DebugController(
            IHostEnvironment environment,
            IAgentMessageMonitor monitor,
            IFabrCoreAgentService agentService,
            ILogger<DebugController> logger)
        {
            _environment = environment;
            _monitor = monitor;
            _agentService = agentService;
            _logger = logger;
        }

        /// <summary>
        /// Replay a recorded inbound message by re-sending it to the same agent.
        /// Intended for debugging: reproduce an agent's response to a specific
        /// input without re-creating the client state that originally produced it.
        /// </summary>
        [HttpPost("replay")]
        public async Task<IActionResult> Replay([FromBody] ReplayRequest request)
        {
            if (!_environment.IsDevelopment())
            {
                _logger.LogWarning("Rejected /debug/replay call outside development environment ({Env})",
                    _environment.EnvironmentName);
                return StatusCode(403, new { Error = "Replay is only allowed in Development environment" });
            }

            if (request is null || string.IsNullOrWhiteSpace(request.MessageId))
                return BadRequest(new { Error = "messageId is required" });

            var messages = await _monitor.GetMessagesAsync(limit: null);
            var original = messages.FirstOrDefault(m => string.Equals(m.Id, request.MessageId, StringComparison.Ordinal));
            if (original is null)
                return NotFound(new { Error = $"No message with id '{request.MessageId}' in the monitor buffer." });

            if (original.Direction != MessageDirection.Inbound)
                return BadRequest(new { Error = "Only inbound messages can be replayed." });

            if (string.IsNullOrWhiteSpace(original.AgentHandle))
                return BadRequest(new { Error = "Original message has no AgentHandle." });

            // AgentHandle format is "userId:handle". Split to resolve grain key + user id.
            var separatorIndex = original.AgentHandle.IndexOf(':');
            if (separatorIndex <= 0 || separatorIndex >= original.AgentHandle.Length - 1)
                return BadRequest(new { Error = $"AgentHandle '{original.AgentHandle}' is not in the expected 'userId:handle' form." });

            var userId = original.AgentHandle[..separatorIndex];
            var handle = original.AgentHandle[(separatorIndex + 1)..];

            var replay = new AgentMessage
            {
                FromHandle = original.FromHandle,
                ToHandle = original.ToHandle,
                OnBehalfOfHandle = original.OnBehalfOfHandle,
                DeliverToHandle = original.DeliverToHandle,
                Channel = original.Channel,
                Message = request.OverrideMessage ?? original.Message,
                MessageType = original.MessageType,
                Kind = MessageKind.Request,
                DataType = original.DataType,
                Files = original.Files,
                State = original.State,
                Args = original.Args,
            };

            _logger.LogInformation(
                "Replaying message {MessageId} for agent {UserId}:{Handle}",
                original.Id, userId, handle);

            try
            {
                var response = await _agentService.SendAndReceiveMessageAsync(userId, handle, replay);
                return Ok(new
                {
                    ReplayedMessageId = original.Id,
                    UserId = userId,
                    Handle = handle,
                    Response = response,
                });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Replay failed for message {MessageId}", original.Id);
                return StatusCode(500, new { Error = "Replay failed", Message = ex.Message });
            }
        }

        public class ReplayRequest
        {
            public string? MessageId { get; set; }

            /// <summary>Optional override for the message body. Useful for what-if experiments.</summary>
            public string? OverrideMessage { get; set; }
        }
    }
}
