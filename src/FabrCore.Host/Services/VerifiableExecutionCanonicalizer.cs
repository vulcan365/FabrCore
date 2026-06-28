using FabrCore.Core.VerifiableExecution;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace FabrCore.Host.Services;

internal static class VerifiableExecutionCanonicalizer
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = false
    };

    public static byte[] CanonicalRecordBytes(VerifiableExecutionRecord record)
    {
        var canonical = new
        {
            record.Id,
            record.TraceId,
            record.SpanId,
            record.ParentSpanId,
            record.SegmentId,
            record.Sequence,
            Kind = record.Kind.ToString(),
            Timestamp = record.Timestamp.ToUniversalTime().ToString("O"),
            record.ParentRecordId,
            record.UserHandle,
            record.AgentHandle,
            record.AgentType,
            record.Subject,
            record.PayloadHash,
            Metadata = record.Metadata
                .OrderBy(kvp => kvp.Key, StringComparer.Ordinal)
                .ToDictionary(kvp => kvp.Key, kvp => kvp.Value, StringComparer.Ordinal),
            Runtime = record.Runtime
        };

        return JsonSerializer.SerializeToUtf8Bytes(canonical, JsonOptions);
    }

    public static string DigestHex(byte[] bytes)
        => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

    public static byte[] Digest(byte[] bytes)
        => SHA256.HashData(bytes);

    public static string DigestText(string? text)
        => DigestHex(Encoding.UTF8.GetBytes(text ?? string.Empty));

    public static string DigestBytes(byte[]? bytes)
        => DigestHex(bytes ?? Array.Empty<byte>());

    public static byte[] ChainDigest(string? previousSignatureDigest, string recordDigest)
        => SHA256.HashData(Encoding.UTF8.GetBytes($"{previousSignatureDigest ?? string.Empty}:{recordDigest}"));
}
