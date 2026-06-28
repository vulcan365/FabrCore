using FabrCore.Core.VerifiableExecution;
using FabrCore.Host.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using System.Diagnostics;
using System.Reflection;

namespace FabrCore.Host.Services;

public sealed class VerifiableExecutionRecorder : IVerifiableExecutionContext
{
    private readonly IVerifiableExecutionStore _store;
    private readonly IVerifiableExecutionSigner _signer;
    private readonly IVerifiableExecutionVerifier _verifier;
    private readonly VerifiableExecutionOptions _options;
    private readonly ILogger<VerifiableExecutionRecorder> _logger;
    private readonly RuntimeProvenance _runtime;

    public VerifiableExecutionRecorder(
        IVerifiableExecutionStore store,
        IVerifiableExecutionSigner signer,
        IVerifiableExecutionVerifier verifier,
        IOptions<VerifiableExecutionOptions> options,
        ILogger<VerifiableExecutionRecorder> logger)
    {
        _store = store;
        _signer = signer;
        _verifier = verifier;
        _options = options.Value;
        _logger = logger;
        _runtime = BuildRuntimeProvenance();
    }

    public async Task<VerifiableExecutionEnvelope?> RecordAsync(
        VerifiableExecutionRecord record,
        CancellationToken cancellationToken = default)
    {
        if (!_options.Enabled)
        {
            return null;
        }

        var activity = Activity.Current;
        record.TraceId ??= activity?.TraceId.ToHexString() ?? Guid.NewGuid().ToString("N");
        record.SpanId ??= activity?.SpanId.ToHexString();
        record.ParentSpanId ??= activity?.ParentSpanId == default ? null : activity?.ParentSpanId.ToHexString();
        record.SegmentId = string.IsNullOrWhiteSpace(record.AgentHandle) ? "host" : record.AgentHandle!;
        record.Sequence = await _store.GetNextSequenceAsync(record.TraceId, record.SegmentId, cancellationToken);
        record.Runtime = MergeRuntime(record.Runtime);

        var latest = await _store.GetLatestSignatureAsync(record.TraceId, record.SegmentId, cancellationToken);
        var recordDigest = VerifiableExecutionCanonicalizer.DigestHex(
            VerifiableExecutionCanonicalizer.CanonicalRecordBytes(record));

        VerifiableExecutionSignature? signature = null;
        VerifiableExecutionCertificate? certificate = null;

        if (_signer.CanSign)
        {
            var chainedDigest = VerifiableExecutionCanonicalizer.ChainDigest(
                latest?.SignatureDigest,
                recordDigest);
            var signatureBytes = await _signer.SignAsync(chainedDigest, cancellationToken);
            var certDigest = VerifiableExecutionCanonicalizer.DigestBytes(_signer.CertificateChain.FirstOrDefault());

            signature = new VerifiableExecutionSignature
            {
                TraceId = record.TraceId,
                RecordId = record.Id,
                SegmentId = record.SegmentId,
                Sequence = record.Sequence,
                PreviousSignatureDigest = latest?.SignatureDigest,
                RecordDigest = recordDigest,
                SignatureDigest = VerifiableExecutionCanonicalizer.DigestBytes(signatureBytes),
                SignerIdentity = _signer.SignerIdentity,
                SignerIdentityKind = _signer.SignerIdentityKind,
                SignatureAlgorithm = _signer.SignatureAlgorithm,
                CertificateChainDigest = certDigest,
                Signature = signatureBytes
            };

            certificate = new VerifiableExecutionCertificate
            {
                Digest = certDigest,
                DerChain = _signer.CertificateChain.ToList()
            };
        }
        else if (_options.RequireSignerForTrustedExecution)
        {
            _logger.LogWarning("Verifiable execution is enabled but no signer is configured.");
        }

        await _store.AppendRecordAsync(record, signature, certificate, cancellationToken);

        return new VerifiableExecutionEnvelope
        {
            TraceId = record.TraceId,
            RecordId = record.Id,
            PreviousSignatureDigest = signature?.PreviousSignatureDigest,
            CurrentSignatureDigest = signature?.SignatureDigest,
            SignerIdentity = signature?.SignerIdentity,
            SignerIdentityKind = signature?.SignerIdentityKind ?? VerifiableExecutionSignerIdentityKind.None,
            EvidenceBundleHash = recordDigest
        };
    }

    public Task<VerifiableExecutionEnvelope?> RecordExternalEffectAsync(
        ExecutionRecordKind kind,
        string subject,
        IReadOnlyDictionary<string, string?> metadata,
        CancellationToken cancellationToken = default)
    {
        var activity = Activity.Current;
        var record = new VerifiableExecutionRecord
        {
            Kind = kind,
            TraceId = activity?.TraceId.ToHexString(),
            SpanId = activity?.SpanId.ToHexString(),
            ParentSpanId = activity?.ParentSpanId == default ? null : activity?.ParentSpanId.ToHexString(),
            Subject = subject,
            Metadata = metadata.ToDictionary(kvp => kvp.Key, kvp => Redact(kvp.Value), StringComparer.Ordinal)
        };

        return RecordAsync(record, cancellationToken);
    }

    public async Task<VerifiableExecutionVerificationResult> VerifyAsync(
        string traceId,
        CancellationToken cancellationToken = default)
    {
        var bundle = await _store.GetBundleAsync(traceId, cancellationToken);
        return await _verifier.VerifyAsync(bundle, cancellationToken);
    }

    private string? Redact(string? value)
    {
        value = _options.Redact?.Invoke(value) ?? value;
        if (value is not null && value.Length > _options.MaxMetadataValueLength)
        {
            return value[.._options.MaxMetadataValueLength];
        }

        return value;
    }

    private RuntimeProvenance MergeRuntime(RuntimeProvenance recordRuntime)
    {
        recordRuntime.FabrCoreHostVersion ??= _runtime.FabrCoreHostVersion;
        recordRuntime.FabrCoreCoreVersion ??= _runtime.FabrCoreCoreVersion;
        recordRuntime.GitCommit ??= _runtime.GitCommit;
        recordRuntime.ContainerImageDigest ??= _runtime.ContainerImageDigest;
        recordRuntime.DeploymentId ??= _runtime.DeploymentId;
        recordRuntime.NodeName ??= _runtime.NodeName;
        return recordRuntime;
    }

    private static RuntimeProvenance BuildRuntimeProvenance()
    {
        var hostAssembly = typeof(VerifiableExecutionRecorder).Assembly;
        var coreAssembly = typeof(VerifiableExecutionRecord).Assembly;
        return new RuntimeProvenance
        {
            FabrCoreHostVersion = hostAssembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
                ?? hostAssembly.GetName().Version?.ToString(),
            FabrCoreCoreVersion = coreAssembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
                ?? coreAssembly.GetName().Version?.ToString(),
            GitCommit = Environment.GetEnvironmentVariable("GIT_COMMIT")
                ?? Environment.GetEnvironmentVariable("FABRCORE_GIT_COMMIT"),
            ContainerImageDigest = Environment.GetEnvironmentVariable("CONTAINER_IMAGE_DIGEST"),
            DeploymentId = Environment.GetEnvironmentVariable("DEPLOYMENT_ID")
                ?? Environment.GetEnvironmentVariable("HOSTNAME"),
            NodeName = Environment.GetEnvironmentVariable("NODE_NAME")
        };
    }
}
