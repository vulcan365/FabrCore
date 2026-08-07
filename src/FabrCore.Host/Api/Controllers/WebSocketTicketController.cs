using FabrCore.Core.Acl;
using FabrCore.Core.Auditing;
using FabrCore.Core.WebSockets;
using FabrCore.Host.WebSocket;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;

namespace FabrCore.Host.Api.Controllers;

[ApiController]
[Authorize]
[Route("fabrcoreapi/ws/ticket")]
public sealed class WebSocketTicketController(
    IWebSocketPrincipalResolver principalResolver,
    IWebSocketTicketService ticketService,
    IOptions<FabrCoreAclOptions> aclOptions,
    IAuditProvider auditProvider) : ControllerBase
{
    [HttpPost]
    public async Task<ActionResult<FabrCoreWebSocketTicketResponse>> Issue()
    {
        var principal = principalResolver.Resolve(User);
        if (principal is null)
            return Unauthorized(new { error = "An authenticated stable principal claim is required." });
        if (string.Equals(principal, aclOptions.Value.SystemPrincipal, StringComparison.OrdinalIgnoreCase))
        {
            await auditProvider.RecordAsync(new AuditEvent
            {
                Category = AuditCategory.WebSocketSecurity,
                Outcome = AuditOutcome.Denied,
                SubjectPrincipal = principal,
                Permission = "websocket.ticket.issue",
                Reason = "The System principal cannot be used by an external WebSocket client.",
                WasEnforced = true,
            });
            return Forbid();
        }

        var ticket = await ticketService.IssueAsync(principal);
        await auditProvider.RecordAsync(new AuditEvent
        {
            Category = AuditCategory.WebSocketSecurity,
            Outcome = AuditOutcome.Success,
            SubjectPrincipal = principal,
            Permission = "websocket.ticket.issue",
            Reason = "Single-use WebSocket ticket issued.",
        });
        return Ok(ticket);
    }
}
