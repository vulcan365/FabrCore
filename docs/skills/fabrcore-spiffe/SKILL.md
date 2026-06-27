---
name: fabrcore-spiffe
description: >
  Implement, configure, troubleshoot, or explain FabrCore verifiable execution, signed evidence chains,
  optional SPIFFE/SVID workload identity, signer providers, trust bundles, evidence stores, cross-cluster
  verification, runtime provenance, and attested plugin/tool external effects. Triggers on: "verifiable execution",
  "verified execution", "SPIFFE", "SVID", "trust bundle", "signed execution", "evidence bundle",
  "IVerifiableExecutionStore", "IVerifiableExecutionSigner", "IVerifiableExecutionContext",
  "UseVerifiableExecution", "LocalCertificateVerifiableExecutionSigner", "ExternalDbEffect",
  "attested side effect", "tamper evidence", "execution provenance".
---

# FabrCore Verifiable Execution and SPIFFE

Use this skill when working on the verifiable execution feature in FabrCore. The feature is **verifiable agent execution**; SPIFFE is only one optional production identity/signing backend.

## Core Mental Model

- Verifiable execution records what happened and signs a chained timeline.
- SPIFFE identifies the workload that signs evidence; it does not identify individual FabrCore agents and does not observe DB/API side effects by itself.
- FabrCore agent identity remains `userHandle:agentHandle` plus `AgentConfiguration.AgentType` / `[AgentAlias]`.
- The signed record binds FabrCore identity (`UserHandle`, `AgentHandle`, `AgentType`) to workload signer identity (`IVerifiableExecutionSigner.SignerIdentity`).
- Default value path: unsigned/off -> local certificate signer -> customer cert/KMS/HSM -> SPIFFE/SVID for cross-cluster or zero-trust deployments.

## Four Configuration Levels

Use this ladder when explaining setup, designing docs, or implementing configuration:

1. **Default: off** — existing monitoring only. No verifiable execution records, no signer, no trust bundles.
2. **Easy: local signed execution** — enable verifiable execution with `UseLocalCertificateVerifiableExecutionSigner()` and the default in-memory/development store. Good for demos, local development, and proving tamper-evident chains.
3. **Production: cert/KMS signed execution** — use a durable `IVerifiableExecutionStore` plus a customer-managed certificate, KMS, HSM, or enterprise PKI signer. This is the normal customer production path.
4. **Enterprise/cross-cluster: SPIFFE trust** — use SPIFFE/SVID signer and trust bundles when independent workload identity, cross-cluster verification, service mesh, Dapr-style interoperability, or zero-trust infrastructure matters.

Do not collapse levels 2-4 into "turn on SPIFFE." SPIFFE is the advanced trust backend, not the base product feature.

## Current Implementation Surface

- Core contracts: `src/FabrCore.Core/VerifiableExecution/*`
- Message/event envelopes: `AgentMessage.VerifiableExecution`, `EventMessage.VerifiableExecution`
- Event tracing: `EventMessage.TraceId`, `SpanId`, `ParentSpanId`
- Host configuration: `FabrCoreServerOptions.UseVerifiableExecution(...)`, `UseVerifiableExecutionStore<T>()`, `UseVerifiableExecutionSigner<T>()`, `UseLocalCertificateVerifiableExecutionSigner()`
- Built-in host providers: `InMemoryVerifiableExecutionStore`, `NullVerifiableExecutionSigner`, `LocalCertificateVerifiableExecutionSigner`, `VerifiableExecutionVerifier`, `VerifiableExecutionRecorder`
- API endpoints: `/fabrcoreapi/monitor/verifiable-execution/operations/{traceId}`, `/bundle`, `/verify`
- Monitor links: `VerifiableExecutionId`, `SignatureDigest`, `VerificationStatus` on monitored messages/events/LLM calls

## Reference Routing

Read only the references needed for the task:

- `references/architecture.md` — use for explaining the model, record kinds, chain semantics, runtime provenance, what SPIFFE does and does not do.
- `references/setup.md` — use for server configuration, signer/store provider registration, local/cert/KMS/SPIFFE deployment shapes, API routes.
- `references/external-effects.md` — use for plugin/tool DB/API/storage side effects and how to record `ExternalDbEffect`, `ExternalHttpCall`, `ExternalStorageEffect`.
- `references/cross-cluster-trust.md` — use for cross-cluster `AgentMessage` flows, evidence envelopes, trust bundles, and authorization policy.
- `references/pitfalls.md` — use before implementing security-sensitive changes or debugging verification failures.

## Assets

Copy or adapt assets when implementing examples:

- `assets/appsettings-verifiable-execution.json` — config shape for local/cert/SPIFFE style deployments.
- `assets/sql-evidence-store-schema.sql` — starter schema for a SQL append-only evidence store provider.
- `assets/custom-signer-template.cs` — template for an `IVerifiableExecutionSigner`.
- `assets/external-effect-plugin-template.cs` — plugin pattern for recording DB/API effects through `IVerifiableExecutionContext`.

## Implementation Rules

- Keep verifiable execution provider-neutral. Do not make SPIFFE mandatory for normal FabrCore hosts.
- Do not use Orleans entity storage for high-volume evidence by default; prefer `IVerifiableExecutionStore` with SQL/event-log/object-store providers.
- Do not store raw secrets or full payloads in evidence. Store hashes and redacted metadata unless an explicit secure-capture policy is configured.
- Do not claim DB/API effects are independently proven unless the effect was recorded through an attested wrapper, manual evidence API, database trigger/audit table, or transactional outbox.
- Do not treat monitor data as authoritative evidence. Monitor rows link to evidence records; the evidence store and signatures are the trust source.
- Keep agent authorization in FabrCore ACL/policy. SPIFFE proves workload identity, not agent-handle authorization.

## Validation

For implementation changes, run:

```powershell
dotnet build src\FabrCore.sln
dotnet test src\FabrCore.sln
```

For security-sensitive changes, add or update tests for signed verification, unsigned mode, tampering, missing signatures, wrong signer identity, and external-effect recording.
