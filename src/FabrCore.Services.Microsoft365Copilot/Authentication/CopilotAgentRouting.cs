using System.Security.Claims;
using Microsoft.AspNetCore.Http;

namespace FabrCore.Services.Microsoft365Copilot;

/// <summary>
/// Ties a request on a messaging route to the agent that owns the route. The authentication
/// scheme accepts a token for any exposed bot; this is what stops a token issued for one bot
/// from being replayed against another agent's route.
/// </summary>
internal static class CopilotAgentRouting
{
    /// <summary>
    /// Checks the request's token was issued for <paramref name="agent"/>'s bot and names the
    /// agent on the request identity, which is what reaches the turn. Returns false when the
    /// token belongs to a different bot.
    /// </summary>
    public static bool TryBind(HttpContext context, CopilotAgent agent, Microsoft365CopilotOptions options)
    {
        var identity = context.User.Identity as ClaimsIdentity;
        var audience = identity?.FindFirst("aud")?.Value;

        if (options.TokenValidation.Enabled && !IsAudienceFor(agent, audience, options))
        {
            return false;
        }

        if (identity is null)
        {
            identity = new ClaimsIdentity();
            context.User = new ClaimsPrincipal(identity);
        }

        foreach (var stale in identity.FindAll(CopilotAgentCatalog.AgentClaimType).ToList())
        {
            identity.RemoveClaim(stale);
        }

        identity.AddClaim(new Claim(CopilotAgentCatalog.AgentClaimType, agent.Name));

        // Anonymous local development carries no token, so nothing says which bot to answer as.
        // Naming the audience lets the Agents SDK pick this agent's connection all the same.
        if (audience is null && !string.IsNullOrWhiteSpace(agent.ClientId))
        {
            identity.AddClaim(new Claim("aud", agent.ClientId));
        }

        return true;
    }

    private static bool IsAudienceFor(CopilotAgent agent, string? audience, Microsoft365CopilotOptions options)
    {
        if (string.IsNullOrWhiteSpace(audience))
        {
            return false;
        }

        return string.Equals(audience, agent.ClientId, StringComparison.OrdinalIgnoreCase)
            || options.TokenValidation.Audiences.Contains(audience, StringComparer.OrdinalIgnoreCase);
    }
}
