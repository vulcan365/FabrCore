# Setup Reference

## Defaults

Verifiable execution is off by default. Existing monitoring keeps working.

```csharp
builder.AddFabrCoreServer(new FabrCoreServerOptions
{
    AdditionalAssemblies = [typeof(MyAgent).Assembly]
});
```

## The Four Setup Levels

Use this rollout ladder:

| Level | Name | What it means | Use when |
|---|---|---|---|
| 0 | Default: off | No verifiable execution records are written. Existing monitor behavior is unchanged. | Customers do not need tamper-evident evidence yet |
| 1 | Easy: local signed execution | Enable verifiable execution with a local self-signed certificate signer and default/dev store. | Local development, demos, first pilots, feature validation |
| 2 | Production: cert/KMS signed execution | Durable evidence store plus customer-managed cert, KMS, HSM, or enterprise PKI signer. | Normal production audit/provenance requirements |
| 3 | Enterprise/cross-cluster: SPIFFE trust | SPIFFE/SVID signer, trust bundles, and remote identity policy. | Cross-cluster, multi-team, service mesh, Dapr-style, zero-trust, or independent external verification |

SPIFFE belongs at level 3. Do not require it for levels 1 or 2.

## Easy Local Signing

Use this for demos, local development, and first customer pilots.

```csharp
builder.AddFabrCoreServer(new FabrCoreServerOptions
{
    AdditionalAssemblies = [typeof(MyAgent).Assembly]
}
.UseVerifiableExecution()
.UseLocalCertificateVerifiableExecutionSigner());
```

This uses:

- `InMemoryVerifiableExecutionStore`
- `LocalCertificateVerifiableExecutionSigner`
- `VerifiableExecutionRecorder`
- `VerifiableExecutionVerifier`

It is tamper-evident for the life of the process/store, but not a production trust root.

## Unsigned Evidence Mode

Use this only for observability/prototyping.

```csharp
builder.AddFabrCoreServer(new FabrCoreServerOptions
{
    AdditionalAssemblies = [typeof(MyAgent).Assembly]
}
.UseVerifiableExecution());
```

Because the default signer is `NullVerifiableExecutionSigner`, bundles verify as `Unsigned`.

## Custom Store

Use a SQL/event-log/object-store implementation when evidence must survive restarts and support audit/export.

```csharp
builder.AddFabrCoreServer(new FabrCoreServerOptions
{
    AdditionalAssemblies = [typeof(MyAgent).Assembly]
}
.UseVerifiableExecution()
.UseVerifiableExecutionStore<SqlVerifiableExecutionStore>()
.UseLocalCertificateVerifiableExecutionSigner());
```

Implement `IVerifiableExecutionStore`:

- `AppendRecordAsync`
- `AddAttestationAsync`
- `GetBundleAsync`
- `GetLatestSignatureAsync`
- `GetNextSequenceAsync`

Provider rules:

- Append records; do not mutate prior rows.
- Enforce unique `(TraceId, SegmentId, Sequence)`.
- Store certificates/chains by digest.
- Return records/signatures in deterministic sequence order.
- Keep evidence storage separate from Orleans entity storage for high-volume production capture.

## Custom Signer

Use a customer certificate, KMS, HSM, or enterprise PKI.

```csharp
builder.AddFabrCoreServer(new FabrCoreServerOptions
{
    AdditionalAssemblies = [typeof(MyAgent).Assembly]
}
.UseVerifiableExecution()
.UseVerifiableExecutionSigner<MyKmsSigner>());
```

Implement `IVerifiableExecutionSigner`:

- `CanSign`
- `SignerIdentity`
- `SignerIdentityKind`
- `SignatureAlgorithm`
- `CertificateChain`
- `SignAsync(byte[] digest)`

Use `assets/custom-signer-template.cs` as the starting point.

## SPIFFE/SVID Shape

SPIFFE should be implemented as a signer provider, not as a required core dependency.

Expected behavior for a future `SpiffeVerifiableExecutionSigner`:

- Load X.509 SVID from the local SPIFFE Workload API.
- Expose `SignerIdentityKind = Spiffe`.
- Expose `SignerIdentity = spiffe://...` from the SVID URI SAN.
- Return DER certificate chain from the SVID.
- Sign chain digests with the private key associated with the SVID.
- Refresh certificates before expiry.
- Fail closed only when `RequireSignerForTrustedExecution` or cross-cluster trust policy requires it.

## Options

`VerifiableExecutionOptions`:

- `Enabled`
- `DefaultPropagationScope`
- `RequireSignerForTrustedExecution`
- `RequireSpiffeForDaprParity`
- `RequireAttestedExternalEffects`
- `CapturePayloadBytes`
- `FailOnVerificationError`
- `MaxMetadataValueLength`
- `Redact`

Recommended production defaults:

```csharp
.UseVerifiableExecution(v =>
{
    v.Enabled = true;
    v.DefaultPropagationScope = ExecutionPropagationScope.Lineage;
    v.RequireSignerForTrustedExecution = true;
    v.CapturePayloadBytes = false;
    v.FailOnVerificationError = true;
    v.Redact = value => RedactSecrets(value);
})
```

## REST API

Routes:

- `GET /fabrcoreapi/monitor/verifiable-execution/operations/{traceId}`
- `GET /fabrcoreapi/monitor/verifiable-execution/operations/{traceId}/bundle`
- `GET /fabrcoreapi/monitor/verifiable-execution/operations/{traceId}/verify`
- `POST /fabrcoreapi/monitor/verifiable-execution/operations/{traceId}/verify`

External verifiers should prefer fetching the bundle and verifying locally against their own trust roots. The source cluster's API is convenient for evidence retrieval, not the final authority.
