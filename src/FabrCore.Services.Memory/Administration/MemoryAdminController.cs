using System.Diagnostics;
using FabrCore.Core.Acl;
using FabrCore.Services.Memory.Models;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace FabrCore.Services.Memory.Administration;

[ApiController]
[Microsoft.AspNetCore.Authorization.Authorize(Policy = "FabrCoreAdmin")]
[Route("fabrcoreapi/memory/admin/v1")]
public sealed class MemoryAdminController(
    IServiceProvider services,
    ILogger<MemoryAdminController> logger) : ControllerBase
{
    private static readonly AclAction ReadAction = new("memory", "read");
    private static readonly AclAction ManageAction = new("memory", "manage");
    private static readonly string[] Features =
        ["dashboard", "scopes", "memories", "consolidation", "audit"];

    private IMemoryAdminClient Client =>
        services.GetRequiredKeyedService<IMemoryAdminClient>(MemoryAdminClientKeys.Local);

    [HttpGet("capabilities")]
    public Task<IActionResult> GetCapabilities(
        [FromHeader(Name = "x-user-handle")] string? principal,
        CancellationToken ct) =>
        ExecuteAsync(principal, ReadAction, async actor =>
        {
            var capability = await Client.GetCapabilityAsync(ct);
            await Client.GetDashboardStatsAsync(ct);
            return Ok(new MemoryAdminCapability
            {
                Availability = capability.Availability,
                ApiVersion = MemoryAdminCapability.CurrentApiVersion,
                Features = Features,
                Message = capability.Message
            });
        });

    [HttpGet("dashboard")]
    public Task<IActionResult> GetDashboard(
        [FromHeader(Name = "x-user-handle")] string? principal,
        CancellationToken ct) =>
        ExecuteAsync(principal, ReadAction, async _ => Ok(await Client.GetDashboardStatsAsync(ct)));

    [HttpGet("scopes")]
    public Task<IActionResult> ListScopes(
        [FromHeader(Name = "x-user-handle")] string? principal,
        CancellationToken ct) =>
        ExecuteAsync(principal, ReadAction, async _ => Ok(await Client.ListScopesAsync(ct)));

    [HttpPost("scopes/{scopeKey}")]
    public Task<IActionResult> CreateScope(
        [FromHeader(Name = "x-user-handle")] string? principal,
        string scopeKey,
        MemoryScopeCreateRequest request,
        CancellationToken ct) =>
        ExecuteAsync(principal, ManageAction, async actor =>
            Ok(await Client.CreateSharedScopeAsync(scopeKey, request.Description, actor, ct)));

    [HttpDelete("scopes/{scopeKey}")]
    public Task<IActionResult> DeleteScope(
        [FromHeader(Name = "x-user-handle")] string? principal,
        string scopeKey,
        CancellationToken ct) =>
        ExecuteAsync(principal, ManageAction, async actor =>
            Ok(await Client.DeleteScopeAsync(scopeKey, actor, ct)));

    [HttpGet("memories")]
    public Task<IActionResult> ListMemories(
        [FromHeader(Name = "x-user-handle")] string? principal,
        [FromQuery(Name = "scope")] string scope,
        [FromQuery(Name = "type")] MemoryType? type,
        [FromQuery(Name = "temperature")] MemoryTemperature? temperature,
        [FromQuery(Name = "search")] string? search,
        int page = 1,
        int pageSize = 25,
        CancellationToken ct = default) =>
        ExecuteAsync(principal, ReadAction, async _ =>
        {
            ValidateScope(scope);
            ValidatePage(page, pageSize);
            return Ok(await Client.ListMemoriesAsync(
                scope, type, temperature, search, page, pageSize, ct));
        });

    [HttpGet("memories/count")]
    public Task<IActionResult> CountMemories(
        [FromHeader(Name = "x-user-handle")] string? principal,
        [FromQuery(Name = "scope")] string scope,
        [FromQuery(Name = "type")] MemoryType? type,
        [FromQuery(Name = "temperature")] MemoryTemperature? temperature,
        [FromQuery(Name = "search")] string? search,
        CancellationToken ct) =>
        ExecuteAsync(principal, ReadAction, async _ =>
        {
            ValidateScope(scope);
            return Ok(await Client.CountMemoriesAsync(scope, type, temperature, search, ct));
        });

    [HttpGet("memories/{memoryId:guid}")]
    public Task<IActionResult> GetMemory(
        [FromHeader(Name = "x-user-handle")] string? principal,
        Guid memoryId,
        CancellationToken ct) =>
        ExecuteAsync(principal, ReadAction, async _ =>
        {
            var memory = await Client.GetMemoryAsync(memoryId, ct);
            return memory is null
                ? ProblemResult(
                    StatusCodes.Status404NotFound,
                    "Memory not found",
                    "The requested memory was not found.",
                    MemoryAdminProblemCodes.NotFound)
                : Ok(memory);
        });

    [HttpPost("scopes/{scopeKey}/memories")]
    public Task<IActionResult> CreateMemory(
        [FromHeader(Name = "x-user-handle")] string? principal,
        string scopeKey,
        MemoryCreateRequest request,
        CancellationToken ct) =>
        ExecuteAsync(principal, ManageAction, async actor =>
        {
            ValidateScope(scopeKey);
            return Ok(await Client.CreateMemoryAsync(
                scopeKey,
                request.Title,
                request.Type,
                request.Content,
                request.Description,
                request.Temperature,
                request.IsPointInTime,
                request.Metadata,
                actor,
                ct));
        });

    [HttpPut("memories/{memoryId:guid}")]
    public Task<IActionResult> UpdateMemory(
        [FromHeader(Name = "x-user-handle")] string? principal,
        Guid memoryId,
        MemoryUpdateRequest request,
        CancellationToken ct) =>
        ExecuteAsync(principal, ManageAction, async actor =>
            Ok(await Client.UpdateMemoryAsync(
                memoryId,
                request.Title,
                request.Type,
                request.Content,
                request.Description,
                request.Temperature,
                actor,
                ct)));

    [HttpDelete("memories/{memoryId:guid}")]
    public Task<IActionResult> DeleteMemory(
        [FromHeader(Name = "x-user-handle")] string? principal,
        Guid memoryId,
        CancellationToken ct) =>
        ExecuteAsync(principal, ManageAction, async actor =>
            Ok(await Client.DeleteMemoryAsync(memoryId, actor, ct)));

    [HttpPost("scopes/{scopeKey}/consolidate")]
    public Task<IActionResult> ConsolidateScope(
        [FromHeader(Name = "x-user-handle")] string? principal,
        string scopeKey,
        CancellationToken ct) =>
        ExecuteAsync(principal, ManageAction, async actor =>
        {
            ValidateScope(scopeKey);
            return Ok(await Client.ConsolidateScopeAsync(scopeKey, actor, ct));
        });

    [HttpGet("audit")]
    public Task<IActionResult> ListAudit(
        [FromHeader(Name = "x-user-handle")] string? principal,
        [FromQuery(Name = "scope")] string? scope,
        int page = 1,
        int pageSize = 50,
        CancellationToken ct = default) =>
        ExecuteAsync(principal, ReadAction, async _ =>
        {
            ValidatePage(page, pageSize);
            return Ok(await Client.ListAuditEntriesAsync(scope, page, pageSize, ct));
        });

    private async Task<IActionResult> ExecuteAsync(
        string? principal,
        AclAction action,
        Func<string, Task<IActionResult>> operation)
    {
        if (string.IsNullOrWhiteSpace(principal))
        {
            return ProblemResult(
                StatusCodes.Status401Unauthorized,
                "Administration principal required",
                "The x-user-handle header is required.",
                MemoryAdminProblemCodes.Unauthorized);
        }

        var actor = principal.Trim();
        var enforcer = services.GetService<AclEnforcer>();
        if (enforcer is null)
        {
            return ProblemResult(
                StatusCodes.Status503ServiceUnavailable,
                "Authorization unavailable",
                "The FabrCore ACL enforcer is not available on this host.",
                MemoryAdminProblemCodes.Unavailable);
        }

        try
        {
            var subject = new AclSubjectContext(actor, null);
            enforcer.Authorize(in subject, action, "*:*");
        }
        catch (AclDeniedException)
        {
            return ProblemResult(
                StatusCodes.Status403Forbidden,
                "Memory administration denied",
                "The current principal is not authorized for this Memory administration operation.",
                MemoryAdminProblemCodes.Unauthorized);
        }

        try
        {
            return await operation(actor);
        }
        catch (ArgumentException ex)
        {
            return ProblemResult(
                StatusCodes.Status400BadRequest,
                "Invalid Memory request",
                ex.Message,
                MemoryAdminProblemCodes.ValidationFailed);
        }
        catch (InvalidOperationException ex)
        {
            logger.LogError(ex, "Memory administration dependencies are unavailable for {Principal}.", actor);
            return ProblemResult(
                StatusCodes.Status503ServiceUnavailable,
                "Memory administration unavailable",
                "The Memory service is not fully configured or available.",
                MemoryAdminProblemCodes.Unavailable);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Memory administration operation failed for {Principal}.", actor);
            return ProblemResult(
                StatusCodes.Status503ServiceUnavailable,
                "Memory administration unavailable",
                "The Memory service could not complete the operation.",
                MemoryAdminProblemCodes.Unavailable);
        }
    }

    private static void ValidateScope(string scope)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(scope);
    }

    private static void ValidatePage(int page, int pageSize)
    {
        if (page < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(page), "page must be at least 1.");
        }

        if (pageSize is < 1 or > 200)
        {
            throw new ArgumentOutOfRangeException(
                nameof(pageSize), "pageSize must be between 1 and 200.");
        }
    }

    private ObjectResult ProblemResult(int status, string title, string detail, string code)
    {
        var problem = new ProblemDetails
        {
            Status = status,
            Title = title,
            Detail = detail,
            Instance = HttpContext?.Request.Path
        };
        problem.Extensions["code"] = code;
        problem.Extensions["traceId"] =
            Activity.Current?.TraceId.ToString() ?? HttpContext?.TraceIdentifier;
        return new ObjectResult(problem) { StatusCode = status };
    }
}
