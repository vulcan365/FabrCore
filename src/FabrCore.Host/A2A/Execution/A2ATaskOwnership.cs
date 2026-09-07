using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace FabrCore.Host.A2A;

internal static class A2ATaskOwnership
{
    // Server-owned metadata persists with snapshots, including custom task stores.
    public const string Key = "fabrcore.owner";
    public static string Fingerprint(string agent, string principal, string? caller)
        => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
            JsonSerializer.Serialize(new[] { agent, principal, caller }))));
}
