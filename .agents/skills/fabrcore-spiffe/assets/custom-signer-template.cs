using FabrCore.Core.VerifiableExecution;

public sealed class MyVerifiableExecutionSigner : IVerifiableExecutionSigner
{
    public bool CanSign => true;
    public string? SignerIdentity => "kms://my-key-id-or-enterprise-identity";
    public VerifiableExecutionSignerIdentityKind SignerIdentityKind => VerifiableExecutionSignerIdentityKind.Kms;
    public string? SignatureAlgorithm => "RS256";
    public IReadOnlyList<byte[]> CertificateChain => _certificateChain;

    private readonly IReadOnlyList<byte[]> _certificateChain;

    public MyVerifiableExecutionSigner(/* inject KMS/HSM/certificate client */)
    {
        _certificateChain = LoadCertificateChain();
    }

    public async Task<byte[]> SignAsync(byte[] digest, CancellationToken cancellationToken = default)
    {
        // Sign the supplied SHA-256 chain digest with the configured key.
        // Return raw signature bytes. Do not hash the digest again unless your provider requires it.
        return await SignWithExternalKeyAsync(digest, cancellationToken);
    }

    private static IReadOnlyList<byte[]> LoadCertificateChain()
    {
        // Return DER-encoded certificates or an empty list for public-key-only providers.
        return Array.Empty<byte[]>();
    }

    private static Task<byte[]> SignWithExternalKeyAsync(byte[] digest, CancellationToken cancellationToken)
        => throw new NotImplementedException();
}
