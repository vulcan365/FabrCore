using FabrCore.Core.Acl;
using FabrCore.Core.Auditing;
using FabrCore.Host.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace FabrCore.Host.Api.Controllers
{
    /// <summary>
    /// Exposes recorded security audit events (<see cref="IAuditProvider"/>) over REST.
    /// Reads require <c>acl.read.allow</c> or <c>acl.manage.allow</c> (System bypasses).
    /// </summary>
    [ApiController]
    [Route("fabrcoreapi/audit")]
    public class AuditController : ControllerBase
    {
        private const int DefaultLimit = 100;
        private const int MaxLimit = 1000;

        private readonly IAuditProvider _audit;
        private readonly IAclEvaluator _evaluator;
        private readonly IHostEnvironment _environment;
        private readonly ILogger<AuditController> _logger;

        public AuditController(
            IAuditProvider audit,
            IAclEvaluator evaluator,
            IHostEnvironment environment,
            ILogger<AuditController> logger)
        {
            _audit = audit;
            _evaluator = evaluator;
            _environment = environment;
            _logger = logger;
        }

        [HttpGet("events")]
        public async Task<IActionResult> GetEvents(
            [FromHeader(Name = "x-user-handle")] string userHandle,
            [FromQuery] string? category = null,
            [FromQuery] string? outcome = null,
            [FromQuery] string? subject = null,
            [FromQuery] DateTimeOffset? since = null,
            [FromQuery] int? limit = null)
        {
            if (Authorize(userHandle) is { } denied) return denied;

            var query = new AuditQuery
            {
                SubjectPrincipal = subject,
                Since = since,
                Limit = Math.Clamp(limit ?? DefaultLimit, 1, MaxLimit)
            };

            if (!string.IsNullOrWhiteSpace(category))
            {
                if (!Enum.TryParse<AuditCategory>(category, ignoreCase: true, out var parsedCategory))
                    return BadRequest($"Invalid category '{category}'.");
                query.Category = parsedCategory;
            }

            if (!string.IsNullOrWhiteSpace(outcome))
            {
                if (!Enum.TryParse<AuditOutcome>(outcome, ignoreCase: true, out var parsedOutcome))
                    return BadRequest($"Invalid outcome '{outcome}'.");
                query.Outcome = parsedOutcome;
            }

            var events = await _audit.GetEventsAsync(query);
            return Ok(events);
        }

        [HttpGet("config")]
        public IActionResult GetConfig(
            [FromHeader(Name = "x-user-handle")] string userHandle)
        {
            if (Authorize(userHandle) is { } denied) return denied;

            var options = _audit.Options;
            return Ok(new
            {
                ProviderType = _audit.GetType().Name,
                RecordingAvailable = _audit is not NullAuditProvider,
                DefaultLevel = options.DefaultLevel.ToString(),
                Categories = options.Categories.ToDictionary(c => c.Key.ToString(), c => c.Value.ToString()),
                options.MaxBufferedEvents
            });
        }

        /// <summary>Clears recorded audit events. Development environments only.</summary>
        [HttpPost("clear")]
        public async Task<IActionResult> Clear(
            [FromHeader(Name = "x-user-handle")] string userHandle)
        {
            if (Authorize(userHandle) is { } denied) return denied;

            if (!_environment.IsDevelopment())
                return StatusCode(403, new { Error = "Audit clear is only available in Development environments." });

            await _audit.ClearAsync();
            _logger.LogInformation("Audit events cleared by '{UserHandle}'", userHandle);
            return NoContent();
        }

        private IActionResult? Authorize(string userHandle)
        {
            if (string.IsNullOrWhiteSpace(userHandle))
                return BadRequest("x-user-handle header is required.");

            if (_evaluator.CanReadAcl(userHandle).IsAllowed || _evaluator.CanManageAcl(userHandle).IsAllowed)
                return null;

            return StatusCode(403, new { Error = $"Access denied: '{userHandle}' cannot read audit data." });
        }
    }
}
