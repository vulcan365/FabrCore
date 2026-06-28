using FabrCore.Core.VerifiableExecution;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace FabrCore.Host.Services;

public sealed class VerifiableExecutionVerifier : IVerifiableExecutionVerifier
{
    public Task<VerifiableExecutionVerificationResult> VerifyAsync(
        VerifiableExecutionBundle bundle,
        CancellationToken cancellationToken = default)
    {
        var result = new VerifiableExecutionVerificationResult
        {
            TraceId = bundle.TraceId
        };

        if (bundle.Records.Count == 0)
        {
            result.IsValid = true;
            result.TrustLevel = ExecutionTrustLevel.UnverifiableLegacy;
            result.Message = "No verifiable execution records were found.";
            return Task.FromResult(result);
        }

        if (bundle.Signatures.Count == 0)
        {
            result.IsValid = true;
            result.TrustLevel = ExecutionTrustLevel.Unsigned;
            result.VerifiedRecordCount = bundle.Records.Count;
            result.Message = "Records are present but unsigned.";
            return Task.FromResult(result);
        }

        var signaturesByRecord = bundle.Signatures.ToDictionary(s => s.RecordId ?? string.Empty, StringComparer.Ordinal);
        var previousBySegment = new Dictionary<string, string?>(StringComparer.Ordinal);

        foreach (var record in bundle.Records.OrderBy(r => r.Sequence))
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (!signaturesByRecord.TryGetValue(record.Id, out var signature))
            {
                result.Errors.Add($"Missing signature for record {record.Id}.");
                continue;
            }

            var digest = VerifiableExecutionCanonicalizer.DigestHex(
                VerifiableExecutionCanonicalizer.CanonicalRecordBytes(record));

            if (!string.Equals(digest, signature.RecordDigest, StringComparison.Ordinal))
            {
                result.Errors.Add($"Record digest mismatch for {record.Id}.");
                continue;
            }

            previousBySegment.TryGetValue(record.SegmentId, out var expectedPrevious);
            if (!string.Equals(expectedPrevious, signature.PreviousSignatureDigest, StringComparison.Ordinal))
            {
                result.Errors.Add($"Signature chain mismatch for record {record.Id}.");
                continue;
            }

            if (!VerifySignature(signature, bundle.Certificates))
            {
                result.Errors.Add($"Signature verification failed for record {record.Id}.");
                continue;
            }

            previousBySegment[record.SegmentId] = signature.SignatureDigest;
            result.VerifiedRecordCount++;
            result.VerifiedSignatureCount++;
        }

        result.IsValid = result.Errors.Count == 0;
        result.TrustLevel = result.IsValid
            ? ResolveTrustLevel(bundle.Signatures)
            : ExecutionTrustLevel.Tampered;
        result.Message = result.IsValid
            ? "Verifiable execution chain is valid."
            : "Verifiable execution chain failed verification.";

        return Task.FromResult(result);
    }

    private static ExecutionTrustLevel ResolveTrustLevel(IEnumerable<VerifiableExecutionSignature> signatures)
        => signatures.Any(s => s.SignerIdentityKind == VerifiableExecutionSignerIdentityKind.Spiffe)
            ? ExecutionTrustLevel.SignedSpiffeIdentity
            : ExecutionTrustLevel.SignedCustomIdentity;

    private static bool VerifySignature(VerifiableExecutionSignature signature, List<VerifiableExecutionCertificate> certificates)
    {
        var certificate = certificates.FirstOrDefault(c => string.Equals(c.Digest, signature.CertificateChainDigest, StringComparison.Ordinal));
        var der = certificate?.DerChain.FirstOrDefault();
        if (der is null || signature.Signature.Length == 0)
        {
            return false;
        }

        using var cert = X509CertificateLoader.LoadCertificate(der);
        using var rsa = cert.GetRSAPublicKey();
        if (rsa is null)
        {
            return false;
        }

        var chainDigest = VerifiableExecutionCanonicalizer.ChainDigest(
            signature.PreviousSignatureDigest,
            signature.RecordDigest);

        return rsa.VerifyHash(chainDigest, signature.Signature, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
    }
}
