# Architecture Reference

## Purpose

Verifiable execution upgrades FabrCore monitoring from "the system says this happened" to "this host identity signed this exact sequence of evidence records, and tampering is detectable."

The feature proves integrity and provenance of execution evidence. It does not prove that an LLM answer was correct, that a business decision was wise, or that an external database commit happened unless that external effect is separately attested.

## Separation of Concerns

| Concern | Owner |
|---|---|
| Agent routing identity | FabrCore handles: `userHandle:agentHandle` |
| Agent type identity | `AgentConfiguration.AgentType` / `[AgentAlias]` |
| Workload signer identity | `IVerifiableExecutionSigner` (`LocalCertificate`, `Certificate`, `Kms`, `Spiffe`, `Custom`) |
| Authorization | FabrCore ACL/provider policy |
| Tamper evidence | Chained `VerifiableExecutionRecord` + `VerifiableExecutionSignature` |
| Operational view | `IAgentMessageMonitor` linked to evidence ids |
| Durable evidence storage | `IVerifiableExecutionStore` |

SPIFFE fits only in the workload signer identity row. It is not an agent handle system and should not replace FabrCore routing/ACL.

## Record Chain

Each `VerifiableExecutionRecord` is canonicalized and hashed. If a signer is available, the host signs a chain digest built from:

```text
SHA256(previousSignatureDigest || recordDigest)
```

This makes edit/delete/reorder tampering visible during verification. The verifier recomputes canonical record digests, validates each signature, and checks each record's `PreviousSignatureDigest` against the previous record in that segment.

## Operation and Causal Graph

- `TraceId` is the operation id.
- `SpanId` is the publisher/handler span for the current hop.
- `ParentSpanId` links causal parentage.
- `AgentMessage.VerifiableExecution` and `EventMessage.VerifiableExecution` carry compact evidence pointers and optional lineage, not full bundles.

Full evidence is retrieved from the store/API as a `VerifiableExecutionBundle`.

## Record Kinds

Use these kinds consistently:

| Kind | Meaning |
|---|---|
| `MessageInbound` | Agent/client received an `AgentMessage` |
| `MessageOutbound` | Agent/client emitted an `AgentMessage` response |
| `EventPublished` | Agent/client/plugin/tool published an `EventMessage` |
| `EventDelivered` | Event reached a receiving grain/handler boundary |
| `EventHandled` | `OnEvent` completed successfully |
| `AgentDispatch` | Agent/client dispatched a request or stream message |
| `AgentResponse` | Caller received/linked a response from a callee |
| `LlmCall` | Standard chat client wrapper observed a model call |
| `ToolCall` | Standalone tool invocation evidence |
| `PluginCall` | Plugin method invocation evidence |
| `ExternalHttpCall` | Attested outbound HTTP effect |
| `ExternalDbEffect` | Attested DB command/update effect |
| `ExternalStorageEffect` | Attested storage write/delete effect |
| `ExternalLibraryCall` | Attested local library/business-method call |
| `Error` | Failed handler/effect evidence |
| `Terminal` | Explicit terminal state for an operation/segment |

## Runtime Provenance

Always include provenance where available:

- FabrCore host/core version
- git commit or build id
- container image digest
- deployment id / pod / node
- agent type alias
- agent assembly and version
- agent config hash
- system prompt hash
- model config hash
- plugin/tool assembly and method metadata when recording plugin/tool calls

This answers "which code/config/prompt/model version did this?" SPIFFE does not provide this by itself; it only signs the provenance under a workload identity.

## Trust Levels

| Trust level | Meaning |
|---|---|
| `Unsigned` | Records exist but no signature is attached |
| `SignedCustomIdentity` | Signed by local cert, enterprise cert, KMS, HSM, or custom signer |
| `SignedSpiffeIdentity` | Signed by SPIFFE/SVID identity |
| `Verified` | Generic verified result when a verifier resolves trust independently |
| `Tampered` | Chain/signature/digest mismatch |
| `ConfigurationError` | Required signer/trust material missing |
| `UnverifiableLegacy` | Old monitor data or no evidence records |

## Important Boundary

For a single trusted FabrCore cluster, SPIFFE is usually not the feature customers care about. The value is signed/tamper-evident execution evidence. SPIFFE becomes useful at workload trust boundaries: multi-cluster, multi-team, independent audit, service mesh, Dapr interoperability, or avoiding static signing keys.
