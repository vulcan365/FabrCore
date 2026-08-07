using System.Security.Claims;

namespace FabrCore.Host.WebSocket;

public interface IWebSocketPrincipalResolver
{
    string? Resolve(ClaimsPrincipal principal);
}

public sealed class DefaultWebSocketPrincipalResolver : IWebSocketPrincipalResolver
{
    public string? Resolve(ClaimsPrincipal principal)
    {
        if (principal.Identity?.IsAuthenticated != true)
            return null;

        var value = new[]
        {
            principal.FindFirstValue(ClaimTypes.NameIdentifier),
            principal.FindFirstValue("oid"),
            principal.FindFirstValue("sub"),
        }.FirstOrDefault(x => !string.IsNullOrWhiteSpace(x));
        return Normalize(value);
    }

    internal static string? Normalize(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;
        var normalized = new string(value.Trim().Select(c =>
            char.IsAsciiLetterOrDigit(c) || c is '-' or '_' or '.' ? char.ToLowerInvariant(c) : '-').ToArray());
        return normalized.Length == 0 ? null : normalized;
    }
}
