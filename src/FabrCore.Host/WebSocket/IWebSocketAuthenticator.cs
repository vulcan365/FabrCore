using Microsoft.AspNetCore.Http;

namespace FabrCore.Host.WebSocket
{
    /// <summary>
    /// Pluggable authenticator invoked before a WebSocket upgrade is accepted.
    /// The default implementation redeems the single-use ticket supplied as a
    /// <c>fabrcore.ticket.*</c> WebSocket subprotocol.
    /// </summary>
    public interface IWebSocketAuthenticator
    {
        /// <summary>
        /// Inspect the upgrade request. Return a result indicating whether the
        /// connection is allowed and, if so, which principal handle to associate with
        /// the session.
        /// </summary>
        Task<WebSocketAuthResult> AuthenticateAsync(HttpContext context);
    }

    /// <summary>
    /// Outcome of <see cref="IWebSocketAuthenticator.AuthenticateAsync"/>.
    /// </summary>
    public sealed record WebSocketAuthResult(bool Allowed, string? UserHandle, string? Reason)
    {
        public static WebSocketAuthResult Deny(string reason) => new(false, null, reason);
        public static WebSocketAuthResult Allow(string userHandle) => new(true, userHandle, null);
    }
}
