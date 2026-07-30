using System.Text;

namespace FabrCore.Surface.Identity;

/// <summary>
/// Canonical normalization for Surface principal ids. Principal ids become FabrCore handle
/// owner prefixes ("principal:agent"), so they must stay within the handle-safe alphabet.
/// </summary>
public static class SurfacePrincipalId
{
    /// <summary>
    /// Trims and lowercases the value, keeps [a-z0-9-_.], and replaces every other
    /// character with '-'. Returns null when the input is null or whitespace.
    /// </summary>
    public static string? Normalize(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var trimmed = value.Trim();
        var builder = new StringBuilder(trimmed.Length);

        foreach (var character in trimmed)
        {
            if (char.IsAsciiLetterOrDigit(character) || character is '-' or '_' or '.')
            {
                builder.Append(char.ToLowerInvariant(character));
            }
            else
            {
                builder.Append('-');
            }
        }

        return builder.ToString();
    }
}
