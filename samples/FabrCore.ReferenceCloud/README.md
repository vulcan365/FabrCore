# Independent reference cloud server

This single-process fixture uses only FabrCore.Core and ASP.NET Core. It has no
Insights identity, package or storage dependency. It is a conformance example,
not a production broker: the bounded queue and receipts are in memory.

Set `REFERENCE_CLUSTER_KEY`, `REFERENCE_OPERATOR_KEY`, `REFERENCE_CLUSTER_ID`,
`REFERENCE_ENVIRONMENT`, and `REFERENCE_CONFIGURATION_FILE` (an absolute path to
a valid CloudConfigurationEnvelope JSON file). Use different random credentials.
Run `dotnet run --project samples/FabrCore.ReferenceCloud --urls http://localhost:5099`.
Configure the test host's CloudServer URL/key/cluster/environment to match.

Authenticate operator requests with the operator bearer credential:

1. POST `/operator/commands` with `{"method":"GET","pathAndQuery":"/fabrcoreapi/admin/v1/capabilities"}`.
2. GET the returned operation URL until the host's response arrives.
3. Verify status, headers and base64 body. Repeat with ACL/blueprint pages,
   conditional writes and persistent management-operation submissions.
4. Disconnect the host and confirm a pending command becomes `incomplete` after
   expiry. It must not be replayed. Check that a wrong lease token cannot complete it.
5. DELETE consumed receipts. The fixture bounds retained receipts to 1,000.

## Configuration state, preview, and adoption

Hosts advertising `configuration-state: 1` send the open `CloudConfigurationState`
contract in heartbeats. This sample retains the latest report per process, rejects
scope mismatches and malformed reports, redacts secrets, and preserves reports on
legacy heartbeats. It caps retained host identities at 1,000; restart clears them.

With the operator bearer credential:

- GET `/operator/configuration/reports` to inspect the retained reports and receive times.
- Queue a GET command for `/fabrcoreapi/admin/v1/settings/state` to inspect a fresh report.
- Queue a POST command for `/fabrcoreapi/admin/v1/settings/preview` with the complete
  candidate flat settings dictionary encoded as JSON bytes in `CloudAdminCommand.Body`.
  In the command wire JSON, `body` is base64 (the standard JSON encoding for `byte[]`).
  `Content-Type: application/json` is supplied by the fixture. Read the eventual command
  response and decode its body. Preview changes no configuration.
- POST `/operator/configuration/draft` with
  `{"hostInstanceId":"<reported instance>","keys":["FabrCore:Orleans:ClusteringMode"]}`.
  The response contains `baseRevision` and a flat `settings` draft preserving existing
  desired keys. It never modifies the configuration file or removes code ownership.
  Production servers must validate and publish this draft with a revision check.

The sample's generic command queue is for a **single connected host**; do not use it
to target one of several connected hosts. A production broker must route and bind
commands to the selected instance. Retained report storage itself distinguishes hosts.

See the complete [server-neutral configuration-state contract](../../docs/cloud-configuration-state-protocol.md).
`ConfigurationReportStore.cs` uses only FabrCore.Core and the BCL; it is also compiled
directly into the OSS conformance tests, with no Insights reference.

For production, replace the queue and receipt dictionary with durable storage,
bind leases to a host, implement authenticated operator permissions and cluster
routing, and preserve uncertain execution across restart. See
[the administration protocol](../../docs/cloud-administration.md).
