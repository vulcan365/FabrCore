using System.Text;

namespace FabrCore.Services.Microsoft365Copilot;

/// <summary>
/// Normalizes external identifiers into safe FabrCore handle fragments. FabrCore handles use
/// <c>:</c> as the principal/agent separator, so external ids (UPNs, Teams conversation ids like
/// <c>19:abc@thread.v2</c>) must never leak one into a handle.
/// </summary>
internal static class CopilotHandleSanitizer
{
    private const int MaxLength = 96;

    /// <summary>Sanitizes a value for use as a FabrCore principal handle.</summary>
    public static string SanitizePrincipalHandle(string value) => Sanitize(value);

    /// <summary>Sanitizes a value for use as (part of) an agent handle.</summary>
    public static string SanitizeAgentHandleFragment(string value) => Sanitize(value);

    private static string Sanitize(string value)
    {
        var builder = new StringBuilder(Math.Min(value.Length, MaxLength));
        var lastWasDash = false;

        foreach (var ch in value)
        {
            if (builder.Length >= MaxLength)
            {
                break;
            }

            if (char.IsAsciiLetterOrDigit(ch) || ch is '-' or '_' or '.' or '@')
            {
                builder.Append(char.ToLowerInvariant(ch));
                lastWasDash = false;
            }
            else if (!lastWasDash)
            {
                builder.Append('-');
                lastWasDash = true;
            }
        }

        return builder.ToString().Trim('-');
    }
}
