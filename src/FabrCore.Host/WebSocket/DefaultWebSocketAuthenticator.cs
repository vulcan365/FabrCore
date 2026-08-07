using FabrCore.Core.Acl;
using FabrCore.Core.Auditing;
using FabrCore.Core.WebSockets;
using FabrCore.Host.Configuration;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Hosting;

namespace FabrCore.Host.WebSocket;

/// <summary>Authenticates v2 upgrades with a short-lived, single-use ticket.</summary>
public sealed class DefaultWebSocketAuthenticator(
    IOptions<FabrCoreHostOptions> hostOptions,
    IOptions<FabrCoreWebSocketOptions> webSocketOptions,
    IOptions<FabrCoreAclOptions> aclOptions,
    IWebHostEnvironment environment,
    IWebSocketTicketService ticketService,
    IAuditProvider auditProvider) : IWebSocketAuthenticator
{
    public async Task<WebSocketAuthResult> AuthenticateAsync(HttpContext context)
    {
        var protocols = context.WebSockets.WebSocketRequestedProtocols;
        if (!protocols.Contains(FabrCoreWebSocketProtocol.Subprotocol, StringComparer.Ordinal))
            return WebSocketAuthResult.Deny($"The '{FabrCoreWebSocketProtocol.Subprotocol}' subprotocol is required.");

        var origin = context.Request.Headers.Origin.FirstOrDefault();
        if (!string.IsNullOrWhiteSpace(origin) &&
            !hostOptions.Value.AllowedWebSocketOrigins.Contains(origin, StringComparer.OrdinalIgnoreCase))
            return WebSocketAuthResult.Deny("The browser Origin is not allowed.");

        var ticketProtocols = protocols.Where(x =>
            x.StartsWith(FabrCoreWebSocketProtocol.TicketSubprotocolPrefix, StringComparison.Ordinal)).ToArray();
        if (ticketProtocols.Length > 1)
            return WebSocketAuthResult.Deny("Exactly one WebSocket ticket subprotocol is allowed.");
        var ticketProtocol = ticketProtocols.SingleOrDefault();
        string? principal = null;
        if (ticketProtocol is not null)
        {
            var token = ticketProtocol[FabrCoreWebSocketProtocol.TicketSubprotocolPrefix.Length..];
            if (!string.IsNullOrWhiteSpace(token))
                principal = await ticketService.RedeemAsync(token);
        }
        else if (environment.IsDevelopment() && webSocketOptions.Value.AllowDevelopmentPrincipalSelection)
        {
            var selected = context.Request.Headers["x-fabrcore-userhandle"].FirstOrDefault()
                ?? context.Request.Query["userhandle"].FirstOrDefault();
            principal = DefaultWebSocketPrincipalResolver.Normalize(selected);
        }

        if (principal is null)
        {
            await AuditAsync(null, AuditOutcome.Denied, "Ticket is missing, expired, invalid, or already used.");
            return WebSocketAuthResult.Deny("A valid single-use WebSocket ticket is required.");
        }

        if (string.Equals(principal, aclOptions.Value.SystemPrincipal, StringComparison.OrdinalIgnoreCase))
        {
            await AuditAsync(principal, AuditOutcome.Denied, "The System principal cannot open an external WebSocket.");
            return WebSocketAuthResult.Deny("The System principal is forbidden.");
        }

        await AuditAsync(principal, AuditOutcome.Success, "WebSocket ticket redeemed.");
        return WebSocketAuthResult.Allow(principal);
    }

    private Task AuditAsync(string? principal, AuditOutcome outcome, string reason) => auditProvider.RecordAsync(new AuditEvent
    {
        Category = AuditCategory.WebSocketSecurity,
        Outcome = outcome,
        SubjectPrincipal = principal,
        Permission = "websocket.ticket.redeem",
        Reason = reason,
        WasEnforced = outcome == AuditOutcome.Denied,
    });
}
