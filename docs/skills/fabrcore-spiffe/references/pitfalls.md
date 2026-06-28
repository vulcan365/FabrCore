# Pitfalls Reference

## Do Not Make SPIFFE Mandatory

Most FabrCore users want proof, not SPIFFE. Keep SPIFFE optional. Support local cert, enterprise cert, KMS/HSM, and unsigned observability modes.

The intended config ladder is:

```text
Default: off
Easy: local signed execution
Production: cert/KMS signed execution
Enterprise/cross-cluster: SPIFFE trust
```

Do not describe level 1 or level 2 as "SPIFFE setup." That makes the feature sound harder than it needs to be.

## Do Not Confuse Identities

- `userHandle:agentHandle` identifies a FabrCore agent.
- `[AgentAlias]` / `AgentConfiguration.AgentType` identifies code/config type.
- SPIFFE identifies a workload/process/pod/service.

If all agents run in one host, SPIFFE cannot distinguish one agent from another. The host signs claims about agent identity; FabrCore ACL enforces agent access.

## Do Not Trust Monitor Rows as Evidence

`IAgentMessageMonitor` is an operational view. It can link to `VerifiableExecutionId` and `SignatureDigest`, but the evidence store and signatures are the trust source.

## Do Not Store Secrets

Evidence often lasts longer than logs. Hash or redact:

- API keys
- auth headers
- connection strings
- raw prompts with sensitive data
- PII/customer values
- SQL parameters

Prefer hashes plus safe descriptors.

## Do Not Overclaim External Effects

A signed plugin call proves the plugin call record was signed. It does not prove the DB commit happened unless DB/API/storage effect evidence is recorded and linked.

## Watch Chain Segments

Sequence and previous signature digest are per trace id and segment id. If multiple agents write into the same trace, keep segment ids stable and deterministic. For current host behavior, agent/client handle is a practical segment id.

## Beware Fire-and-Forget Events

Events have no response. Record:

- `EventPublished`
- `EventDelivered`
- `EventHandled`
- `Error` when handler fails

Do not use only `EventPublished`; it proves the event was emitted but not that a subscriber handled it.

## Do Not Break W3C Trace Context

Use `StampFromActivity` and `StartIngressActivity`. Do not hand-generate trace/span ids. `TraceId` is the operation id; invalid values break tracing and verification correlation.

## Key Rotation

Signatures must remain verifiable after rotation:

- store certificate/public key chain by digest
- include signer identity and algorithm on each signature
- retain old trust roots/public keys for the evidence retention period
- timestamp records/signatures

## Clock and Expiry

Certificate expiry affects trust interpretation. A verifier may need signing time and certificate validity-at-signing, not just validity-at-verification.

## Payload Size

Do not propagate full bundles through `AgentMessage`. Use compact envelopes and bundle fetch/export APIs.

## Testing Checklist

Add tests for:

- no records -> `UnverifiableLegacy`
- records without signatures -> `Unsigned`
- valid local certificate chain -> `SignedCustomIdentity`
- tampered record metadata/payload -> `Tampered`
- deleted/reordered record -> chain mismatch
- missing certificate -> verification failure
- wrong signer/trust root -> verification failure
- event publish/delivery/handled lineage
- external DB effect success/failure/rollback
