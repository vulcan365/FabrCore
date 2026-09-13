# FabrCore 2.0 cloud evidence export

The authenticated administration surface adds retained trace discovery and immutable
exports, independent of Insights. All paths below start at `/fabrcoreapi/admin/v1`.
Probe capabilities; a custom IVerifiableExecutionStore needs the optional
IVerifiableExecutionQueryProvider interface to advertise discovery.

1. GET `/observability/evidence/traces` discovers retained trace IDs. Keep paging
   and source identity scoped to the queried store; do not combine cursors between hosts.
2. POST `/observability/evidence/{traceId}/exports` creates an actor-owned immutable
   snapshot. Read the manifest's SHA-256 revision, byte count, chunk count and verification.
3. GET `/observability/evidence/exports/{id}/{chunk}` returns each chunk as base64.
   Decode each chunk, concatenate in order and check total bytes and the manifest hash.
   Chunk sizes account for base64 and transport body limits. New records after creation
   do not change that export; create a new export when a later revision is needed.
4. DELETE `/observability/evidence/exports/{id}` releases the stored export.

Preserve records, signatures, public certificates and attestations exactly. Do not
reconstruct evidence by serializing monitor rows, rewriting identifiers or normalizing
signed content. Report verified, unsigned, tampered and incomplete outcomes distinctly;
a valid manifest hash establishes transport integrity of the snapshot, not trusted
signer identity or complete coverage. Inspect verification and trust results separately.

Silo-local stores require separate queries. Shared providers are queried once per opaque
store identity; show unavailable hosts and retention gaps. An export from one store is
not a unified all-store cluster export. SQL stores have a stable shared identity, while
in-memory provider identity changes on restart. Durability follows the configured store.
Signing/capture is still opt-in; SQL alone does not create signed evidence.

Admin execution is attributed to `_admin`, actor, session, target and execution category.
Transcript access remains through operator-owned administration sessions. Explain a
disputed agent response using trace/message/tool records and missing-data statements,
never as a recovery of the model's hidden reasoning.
