using System.Text;
using FabrCore.Host.Configuration;

namespace FabrCore.Host.Services;

internal static class PrincipalContextValues
{
    public static bool Apply(
        Dictionary<string, string> context,
        string key,
        string? value,
        PrincipalContextOptions options)
    {
        ValidateKey(key, options);

        if (value is null)
        {
            return context.Remove(key);
        }

        var valueBytes = Encoding.UTF8.GetByteCount(value);
        if (valueBytes > options.MaxValueBytes)
        {
            throw new ArgumentException(
                $"Principal context value exceeds the {options.MaxValueBytes}-byte limit.",
                nameof(value));
        }

        if (context.TryGetValue(key, out var existing) &&
            string.Equals(existing, value, StringComparison.Ordinal))
        {
            return false;
        }

        if (!context.ContainsKey(key) && context.Count >= options.MaxEntries)
        {
            throw new InvalidOperationException(
                $"Principal context cannot contain more than {options.MaxEntries} entries.");
        }

        var totalBytes = context
            .Where(entry => !string.Equals(entry.Key, key, StringComparison.Ordinal))
            .Sum(entry => Encoding.UTF8.GetByteCount(entry.Key) + Encoding.UTF8.GetByteCount(entry.Value));
        totalBytes += Encoding.UTF8.GetByteCount(key) + valueBytes;

        if (totalBytes > options.MaxTotalBytes)
        {
            throw new InvalidOperationException(
                $"Principal context exceeds the {options.MaxTotalBytes}-byte total limit.");
        }

        context[key] = value;
        return true;
    }

    public static void ValidateKey(string key, PrincipalContextOptions options)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        if (key.Length > options.MaxKeyLength)
        {
            throw new ArgumentException(
                $"Principal context keys cannot exceed {options.MaxKeyLength} characters.",
                nameof(key));
        }
    }
}
