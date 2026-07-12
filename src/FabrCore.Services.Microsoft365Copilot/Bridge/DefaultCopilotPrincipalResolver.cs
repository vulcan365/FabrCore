using System.IdentityModel.Tokens.Jwt;
using Microsoft.Agents.Builder;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace FabrCore.Services.Microsoft365Copilot;

/// <summary>
/// Default principal mapping: prefers the Entra identity that Teams / Microsoft 365 Copilot stamp
/// on the activity (<c>From.AadObjectId</c>), optionally combined with the tenant id or replaced
/// by the UPN from the SSO token, per <see cref="CopilotPrincipalOptions.Strategy"/>.
/// </summary>
internal sealed class DefaultCopilotPrincipalResolver : ICopilotPrincipalResolver
{
    private readonly Microsoft365CopilotOptions _options;
    private readonly ILogger<DefaultCopilotPrincipalResolver> _logger;

    public DefaultCopilotPrincipalResolver(
        IOptions<Microsoft365CopilotOptions> options,
        ILogger<DefaultCopilotPrincipalResolver> logger)
    {
        _options = options.Value;
        _logger = logger;
    }

    public ValueTask<string?> ResolvePrincipalHandleAsync(
        ITurnContext turnContext,
        string? userAccessToken,
        CancellationToken cancellationToken)
    {
        var activity = turnContext.Activity;
        var objectId = activity.From?.AadObjectId ?? GetTokenClaim(userAccessToken, "oid");
        var tenantId = activity.From?.TenantId
            ?? activity.Conversation?.TenantId
            ?? GetTokenClaim(userAccessToken, "tid");

        string? handle = _options.Principal.Strategy switch
        {
            CopilotPrincipalStrategy.EntraObjectId => objectId,
            CopilotPrincipalStrategy.TenantAndObjectId =>
                objectId is null ? null : $"{tenantId ?? "unknown-tenant"}-{objectId}",
            CopilotPrincipalStrategy.UserPrincipalName =>
                GetTokenClaim(userAccessToken, "upn")
                ?? GetTokenClaim(userAccessToken, "preferred_username")
                ?? objectId,
            CopilotPrincipalStrategy.ChannelUserId => ChannelUserHandle(turnContext),
            _ => objectId,
        };

        // No Entra identity on the activity — typical for the Agents Playground and test
        // channels. Only fall back to the raw channel user id when explicitly allowed or when
        // channel token validation is off (local development).
        if (handle is null)
        {
            if (_options.Principal.AllowChannelIdFallback || !_options.TokenValidation.Enabled)
            {
                handle = ChannelUserHandle(turnContext);
            }
            else
            {
                _logger.LogWarning(
                    "Rejected activity {ActivityId} on channel {ChannelId}: no Entra identity present and channel-id fallback is disabled.",
                    activity.Id, activity.ChannelId?.ToString());
                return ValueTask.FromResult<string?>(null);
            }
        }

        if (handle is null)
        {
            return ValueTask.FromResult<string?>(null);
        }

        if (!string.IsNullOrEmpty(_options.Principal.Prefix))
        {
            handle = _options.Principal.Prefix + handle;
        }

        return ValueTask.FromResult<string?>(CopilotHandleSanitizer.SanitizePrincipalHandle(handle));
    }

    private static string? ChannelUserHandle(ITurnContext turnContext)
    {
        var activity = turnContext.Activity;
        var userId = activity.From?.Id;
        if (string.IsNullOrWhiteSpace(userId))
        {
            return null;
        }

        var channel = activity.ChannelId?.ToString() ?? "unknown";
        return $"{channel}-{userId}";
    }

    private static string? GetTokenClaim(string? accessToken, string claimType)
    {
        if (string.IsNullOrWhiteSpace(accessToken))
        {
            return null;
        }

        try
        {
            // The token was obtained through the Bot Framework token service over an
            // authenticated channel; it is read (not trusted for authorization) purely to
            // derive a stable principal identifier.
            var token = new JwtSecurityTokenHandler().ReadJwtToken(accessToken);
            return token.Claims.FirstOrDefault(c => c.Type == claimType)?.Value;
        }
        catch
        {
            return null;
        }
    }
}
