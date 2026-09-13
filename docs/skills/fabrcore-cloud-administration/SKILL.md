---
name: fabrcore-cloud-administration
description: "Build FabrCore 2.0 cloud management integrations: authenticated administration APIs, outbound HTTP polling, principal/ACL CRUD, blueprint deployment, agent lifecycle, isolated diagnostic sessions, retained monitoring and evidence exports. Use for third-party cloud servers and Insights integrations, not ordinary agent chat or business tools."
metadata:
  author: FabrCore
  version: 2.0.0
  documentation: https://fabrcore.ai/docs/cloud-administration
---

# FabrCore 2.0 cloud administration

These capabilities ship as part of FabrCore 2.0.0. Contracts live in FabrCore.Core,
host services in FabrCore.Host, and the typed HTTP client in FabrCore.Sdk.
Vulcan365 Insights is one implementation; do not introduce its identities,
packages, routes or storage into the vendor-neutral contract.

## Choose the workflow

- For optional connection profiles, Agent ID bindings, external user consent and
  encrypted handoffs, read [fabrcore-connections](../fabrcore-connections/SKILL.md)
  and its [cloud guide](../fabrcore-connections/references/integration.md#cloud-administration-and-encrypted-handoffs).
  Discover `connections` plus `entra-agent-id` / `encrypted-client-handoff` features.
  Use `/fabrcoreapi/admin/v1/principals/{principal}/connections` through the existing
  broker and preserve revisions. Only client-encrypted user authorization envelopes
  may enter durable commands; operator authentication is not user consent.
  Insights manages profiles/grants/disconnects; any client app can own consent UI.

- For authentication, target addressing, capabilities, CRUD, operations and
  diagnostic sessions, read [administration](references/administration.md).
- For cloud enrollment, configuration, heartbeat, connect leases and transport
  failures, read [transport](references/transport.md). Configuration uses v1,
  outbound connect uses v2, and Host administration uses `/fabrcoreapi/admin/v1`.
  These are protocol versions within the 2.0 release.
- For SDK integration, adapt [the client example](assets/administration-client.cs).
  Supply authenticated HTTP or a connect-channel handler; the SDK does not enroll
  a cluster, authenticate operators or create a production command broker.
- For SQL monitoring in manual-schema mode, provision
  [the additive migration](assets/monitoring.sql) in the operational database.

## Integration invariants

1. Discover capabilities before enabling controls. Preserve older APIs and label
   older-host snapshots as partial. `GetPrincipalPageAsync` includes inactive ACL
   registrations; `GetPrincipalsAsync` is the legacy runtime-only discovery call.
2. Authenticate the operator at the cloud server and establish the actor there.
   Never forward a caller-selected admin actor or use a message channel as a grant.
   Preserve conditional headers, command/operation IDs, leases, status, expiry and
   bounded bodies. Never automatically replay a mutation after a lost response.
3. Read the current revision, preview a blueprint without effects, then deploy
   the reviewed revision and expansion digest. Distinguish ensure from update.
   Neither removed definitions nor removed envelope entries delete agents.
4. Diagnostic `_admin` turns use their own execution context and forked store.
   One turn per target may overlap ordinary processing; overlapping admin turns
   and disruptive lifecycle changes conflict. Reject `_admin` and `_debug` on
   ordinary ingress. Keep state mutation and normal test messages in management UI.
5. Read only retained, captured observations. Attribute data to its source, actor,
   session, channel and execution category; report missing capture, retention gaps,
   offline silos and unknown costs. Do not infer hidden reasoning or full cluster
   coverage from one source. Signing is separate from retention and monitoring.
6. Use bounded HTTP polling. Query shared stores once by opaque source identity,
   silo-local stores separately. Default to manual refresh; optional ten-second
   visible-view polling should use at most four concurrent host requests and one
   query per host. Cancel hidden-view polling. Cloud WebSockets and streaming
   diagnostic tokens are deferred.

## Verification and distribution

Exercise stale writes, actor isolation, duplicate submissions, incomplete receipts,
normal/admin overlap, busy lifecycle rejection and immutable source histories.
Test shared/local coverage, cursor invalidation, SQL failure/drop reporting and
byte-preserving evidence exports. Benchmark representative cluster workloads;
the less-than-5% regression goal is not an established production guarantee.
Definition drift compares saved and deployed blueprint revisions; automatic
comparison with every live agent configuration is not implemented.

Copy this entire folder, including references and assets. The independent
[reference cloud server](https://github.com/vulcan365/FabrCore/tree/main/samples/FabrCore.ReferenceCloud)
demonstrates the protocol with a bounded in-memory queue; production brokers need
their own durable queue and operator authentication. The
[release validation report](https://github.com/vulcan365/FabrCore/tree/main/docs/cloud-validation)
records remaining multi-silo, sustained SQL and real-model validation work.
