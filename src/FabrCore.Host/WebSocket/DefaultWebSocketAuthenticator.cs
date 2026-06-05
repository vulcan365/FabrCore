using FabrCore.Host.Configuration;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Options;

namespace FabrCore.Host.WebSocket
{
    /// <summary>
    /// Default authenticator. Reads the user handle from the
    /// <c>x-fabrcore-userhandle</c> header or <c>userhandle</c> query parameter, and (optionally)
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

            string? userHandle = null;
            if (context.Request.Headers.TryGetValue("x-fabrcore-userhandle", out var userHandleValues))
            {
                userHandle = userHandleValues.FirstOrDefault();
            }
            else if (context.Request.Query.TryGetValue("userhandle", out var queryValues))
            {
                userHandle = queryValues.FirstOrDefault();
            }

            if (string.IsNullOrWhiteSpace(userHandle))
            {
                return Task.FromResult(WebSocketAuthResult.Deny(
                    "Missing required user handle. Provide via x-fabrcore-userhandle header or userhandle query parameter."));
            }

            return Task.FromResult(WebSocketAuthResult.Allow(userHandle));
        }
    }
}
