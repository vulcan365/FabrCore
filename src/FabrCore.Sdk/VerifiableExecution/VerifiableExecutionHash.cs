using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace FabrCore.Sdk.VerifiableExecution;

public static class VerifiableExecutionHash
{
    public static string HashText(string? value)
        => HashBytes(value is null ? Array.Empty<byte>() : Encoding.UTF8.GetBytes(value));

    public static string HashBytes(byte[]? value)
        => Convert.ToHexString(SHA256.HashData(value ?? Array.Empty<byte>())).ToLowerInvariant();

    public static string HashObject<T>(T value)
        => HashText(JsonSerializer.Serialize(value));
}
