using FabrCore.Core.VerifiableExecution;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace FabrCore.Host.Services;

public sealed class NullVerifiableExecutionSigner : IVerifiableExecutionSigner
{
    public bool CanSign => false;
    public string? SignerIdentity => null;
    public VerifiableExecutionSignerIdentityKind SignerIdentityKind => VerifiableExecutionSignerIdentityKind.None;
    public string? SignatureAlgorithm => null;
    public IReadOnlyList<byte[]> CertificateChain => Array.Empty<byte[]>();
    public Task<byte[]> SignAsync(byte[] digest, CancellationToken cancellationToken = default)
        => Task.FromResult(Array.Empty<byte>());
}

public sealed class LocalCertificateVerifiableExecutionSigner : IVerifiableExecutionSigner, IDisposable
{
    private readonly RSA _key;
    private readonly X509Certificate2 _certificate;

    public LocalCertificateVerifiableExecutionSigner()
    {
        _key = RSA.Create(2048);
        var request = new CertificateRequest(
            "CN=FabrCore Local Verifiable Execution",
            _key,
            HashAlgorithmName.SHA256,
            RSASignaturePadding.Pkcs1);

        _certificate = request.CreateSelfSigned(
            DateTimeOffset.UtcNow.AddMinutes(-5),
            DateTimeOffset.UtcNow.AddYears(2));
    }

    public bool CanSign => true;
    public string? SignerIdentity => "fabrcore://local-host";
    public VerifiableExecutionSignerIdentityKind SignerIdentityKind => VerifiableExecutionSignerIdentityKind.LocalCertificate;
    public string? SignatureAlgorithm => "RS256";
    public IReadOnlyList<byte[]> CertificateChain => new[] { _certificate.Export(X509ContentType.Cert) };

    public Task<byte[]> SignAsync(byte[] digest, CancellationToken cancellationToken = default)
        => Task.FromResult(_key.SignHash(digest, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1));

    public void Dispose()
    {
        _certificate.Dispose();
        _key.Dispose();
    }
}
