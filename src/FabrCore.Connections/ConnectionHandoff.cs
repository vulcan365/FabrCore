using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace FabrCore.Connections;

public sealed record ConnectionHandoffChallenge(string Id, string PublicKey, DateTimeOffset ExpiresAt);
public sealed record ConnectionHandoffEnvelope(string Id, string WrappedKey, string Nonce, string Ciphertext, string Tag);

/// <summary>Encrypted inside the client before passing through a cloud administration broker.</summary>
public sealed class ConnectionHandoffPayload
{
    public string UserProof { get; set; } = "";
    public string Operation { get; set; } = "";
    public string? RedirectUri { get; set; }
    public string? State { get; set; }
    public string? AuthorizationCode { get; set; }
    public string? Assertion { get; set; }
    public override string ToString() => "[protected connection handoff]";
}

public static class ConnectionHandoff
{
    public static ConnectionHandoffEnvelope Encrypt(ConnectionHandoffChallenge challenge, ConnectionHandoffPayload payload)
    {
        if (challenge.ExpiresAt <= DateTimeOffset.UtcNow) throw new ConnectionException("invalid-transaction", "The handoff challenge has expired.");
        var plaintext = JsonSerializer.SerializeToUtf8Bytes(payload, JsonSerializerOptions.Web);
        if (plaintext.Length > 64 * 1024) throw new ArgumentException("The handoff exceeds the supported size.", nameof(payload));
        var key = RandomNumberGenerator.GetBytes(32);
        try
        {
            using var rsa = RSA.Create();
            rsa.ImportSubjectPublicKeyInfo(Convert.FromBase64String(challenge.PublicKey), out _);
            var nonce = RandomNumberGenerator.GetBytes(12); var tag = new byte[16]; var ciphertext = new byte[plaintext.Length];
            using var aes = new AesGcm(key, 16);
            aes.Encrypt(nonce, plaintext, ciphertext, tag, Encoding.UTF8.GetBytes(challenge.Id));
            return new(challenge.Id, Convert.ToBase64String(rsa.Encrypt(key, RSAEncryptionPadding.OaepSHA256)),
                Convert.ToBase64String(nonce), Convert.ToBase64String(ciphertext), Convert.ToBase64String(tag));
        }
        finally { CryptographicOperations.ZeroMemory(key); CryptographicOperations.ZeroMemory(plaintext); }
    }
}
