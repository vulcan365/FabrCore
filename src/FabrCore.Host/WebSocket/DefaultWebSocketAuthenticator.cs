using FabrCore.Host.Configuration;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Options;

namespace FabrCore.Host.WebSocket
{
    /// <summary>
    /// Default backward-compatible authenticator. Reads the user handle from the
    /// <c>x-fabrcore-userid</c> header or <c>userid</c> query parameter, and (optionally)
    /// enforces a configured <c>Origin</c> allowlist. Does NOT validate an identity —
    /// replace in production with an implementation that verifies a bearer token
    /// or session cookie.
    /// </summary>
    public sealed class DefaultWebSocketAuthenticator : IWebSocketAuthenticator
    {
        private readonly FabrCoreHostOptions _options;

        public DefaultWebSocketAuthenticator(IOptions<FabrCoreHostOptions> options)
        {
            _options = options.Value;
        }

        public Task<WebSocketAuthResult> AuthenticateAsync(HttpContext context)
        {
            // Origin allowlist — only enforced when configured. Empty list = allow all.
            if (_options.AllowedWebSocketOrigins is { Count: > 0 } allowed)
            {
                var origin = context.Request.Headers["Origin"].FirstOrDefault();
                if (!string.IsNullOrEmpty(origin) && !allowed.Contains(origin, StringComparer.OrdinalIgnoreCase))
                {
                    return Task.FromResult(WebSocketAuthResult.Deny(
                        $"Origin '{origin}' is not in the allow-list."));
                }
            }

            string? userId = null;
            if (context.Request.Headers.TryGetValue("x-fabrcore-userid", out var userIdValues))
            {
                userId = userIdValues.FirstOrDefault();
            }
            else if (context.Request.Query.TryGetValue("userid", out var queryValues))
            {
                userId = queryValues.FirstOrDefault();
            }

            if (string.IsNullOrWhiteSpace(userId))
            {
                return Task.FromResult(WebSocketAuthResult.Deny(
                    "Missing required user ID. Provide via x-fabrcore-userid header or userid query parameter."));
            }

            return Task.FromResult(WebSocketAuthResult.Allow(userId));
        }
    }
}
