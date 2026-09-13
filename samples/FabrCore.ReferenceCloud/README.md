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

For production, replace the queue and receipt dictionary with durable storage,
bind leases to a host, implement authenticated operator permissions and cluster
routing, and preserve uncertain execution across restart. See
[the administration protocol](../../docs/cloud-administration.md).
