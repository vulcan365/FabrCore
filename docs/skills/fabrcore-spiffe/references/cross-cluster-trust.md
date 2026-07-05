# Cross-Cluster Trust Reference

## Flow

Cross-cluster `AgentMessage` trust has two layers:

1. Authentication/authorization: can this remote cluster call this endpoint/agent?
2. Evidence verification: does the attached envelope or fetched bundle verify?

Typical flow:

```text
Cluster A signs outbound AgentMessage evidence
Cluster A sends AgentMessage + VerifiableExecutionEnvelope
Cluster B authenticates Cluster A transport identity
Cluster B authorizes remote FromHandle/ToHandle/action
Cluster B verifies evidence envelope/bundle according to policy
Cluster B runs target agent
Cluster B signs response evidence
Cluster A verifies response links to original request
```

## AgentMessage Payload

Do not put the full bundle on every message. Put compact proof pointers on `AgentMessage.VerifiableExecution`:

- trace id
- current record id
- current signature digest
- previous signature digest
- signer identity and kind
- bundle hash/URI when available
- lineage chunks when propagation scope is `Lineage`

Fetch/export full bundles separately.

## Receiving Cluster Policy

Cluster B should verify:

- transport caller identity is authenticated
- caller identity chains to a configured trust root
- `SignerIdentity` in evidence matches the authenticated caller or an allowed delegated signer
- signed record message fields match the actual `AgentMessage`
- `FromHandle` / remote user namespace is allowed
- target local `ToHandle` is allowed
- message type/action is allowed
- evidence chain verifies or policy allows unsigned legacy mode

Example policy shape:

```json
{
  "remoteCluster": "customer-a-prod",
  "trustedSigner": "spiffe://customer-a/ns/prod/app/fabrcore-host",
  "allowedFromHandles": ["customer-a:*"],
  "allowedTargetHandles": ["shared:research-agent"],
  "allowedMessageTypes": ["request"]
}
```

## Sending Cluster Response Verification

Cluster A should verify:

- responder identity is trusted
- response trace id matches original request
- response evidence links to the outbound request record
- response payload hash matches the received response
- responder identity is authorized to speak for the claimed agent handle
- any propagated lineage verifies according to policy

## Offline Verification

For auditors and external systems, prefer:

```text
evidence bundle + trust roots/public keys -> local verifier -> verification result
```

Do not require broad API access to the source cluster. A narrow bundle export endpoint or object-store URI is enough.

## SPIFFE Value

SPIFFE matters most here because it gives the remote verifier a standard workload identity:

```text
spiffe://trust-domain/ns/prod/app/fabrcore-host
```

It still does not identify dynamic FabrCore agents. The signed record must bind the workload identity to the FabrCore `principalHandle:agentHandle` claim.
