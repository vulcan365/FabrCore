# Orleans 10.3 adoption

FabrCore uses Orleans 10.3.1. The ADO.NET streaming provider is published separately as
`Microsoft.Orleans.Streaming.AdoNet` **10.3.1-alpha.1**. Its preview dependency is pinned;
SQL-backed streams are opt-in and memory streams remain the default.

## Persisted JSON compatibility

The default Orleans JSON storage serializer now checks `$type` metadata against its
type manifest. FabrCore keeps this protection enabled. Agent and principal state use
registered surrogate converters, which supply their type metadata.

FabrCore also installs a `JsonElement` converter for custom agent JSON, preserving nested
objects, arrays, and primitive values through the Newtonsoft storage serializer. Previously
written data which already lost its original JSON content cannot be reconstructed by this fix.

`OrleansStorageCompatibilityTests` verifies representative legacy-shaped JSON containing
agent configuration, conversation history, tracked agents, and pending messages. It also
verifies rejection of unknown application types and successful reads after explicit registration.
These synthetic fixtures do not replace a restore test using a copy of each deployment's
existing SQL/Azure state and application-specific payloads before a production upgrade.

For an application-defined persisted type that is not already registered:

```csharp
options.ConfigureOrleans(silo => silo.Services.Configure<
    Orleans.Serialization.Configuration.TypeManifestOptions>(manifest =>
        manifest.AddAllowedType(typeof(MyPersistedType))));
```

Register nested custom types as needed. Keep `AllowAllTypes` disabled. This change does
not switch storage formats or migrate existing data to System.Text.Json.

## Contract checks

Core and Host enable the Orleans contract analyzer. Their checked-in `OrleansContracts.txt`
files capture current RPC and grain identities; Versioning diagnostics fail builds and CI.
These are baselines for the current checkout, not a promise that older deployed clients
are compatible. Persisted-state schemas and behavioral compatibility need separate review.

After an intentional interface or grain identity change, run this separately for each owning project:

```powershell
dotnet format src/FabrCore.Core/FabrCore.Core.csproj analyzers --severity info --diagnostics ORLEANS0016 ORLEANS0017 ORLEANS0018 ORLEANS0019 ORLEANS0020 ORLEANS0022 ORLEANS0023 ORLEANS0024
dotnet format src/FabrCore.Host/FabrCore.Host.csproj analyzers --severity info --diagnostics ORLEANS0016 ORLEANS0017 ORLEANS0018 ORLEANS0019 ORLEANS0020 ORLEANS0022 ORLEANS0023 ORLEANS0024
```

Review changed wire identities and signatures and preserve retired entries. Do not
regenerate baselines automatically in CI to silence compatibility failures.

## Orleans telemetry

FabrCore silo registration and host-discovery client registration enable activity propagation.
The sample ServiceDefaults subscribes to `Microsoft.Orleans.*` and `FabrCore.*` activity
sources, and to the `Microsoft.Orleans` and `FabrCore.*` meters. Applications with their own
OpenTelemetry setup should add the same subscriptions to their existing exporter pipeline.

Update external dashboards and queries for these Orleans RPC tag changes:

| Previous | Current |
| --- | --- |
| `rpc.system` | `rpc.system.name` |
| `rpc.service` | `orleans.rpc.service` |
| `rpc.orleans.target_id` | `orleans.rpc.target_id` |
| `rpc.orleans.source_id` | `orleans.rpc.source_id` |

The runtime's `rpc.method` now includes `<interface>/<method>`. No external dashboards
are provisioned by this repository change.

## Optional durable SQL streams

Set this on **every silo** in the SQL cluster:

```json
{
  "FabrCore": {
    "Orleans": {
      "ClusteringMode": "SqlServer",
      "SqlServerStreams": "AdoNet"
    }
  }
}
```

Queues use the clustering `ConnectionString`, even when grain state has a separate
`StorageConnectionString`. The existing named stream provider remains `fabrcoreStreams`.
For clients which directly publish/subscribe to Orleans streams, use the same clustering
database in the existing Orleans configuration callback:

```csharp
client.AddFabrCoreSqlServerStreams(clusteringConnectionString);
```

Clients that only invoke grains do not need direct stream registration or database access.
Connection strings are supplied locally; gateway discovery does not distribute credentials.
The runtime database principal must be able to resolve Orleans' unqualified `OrleansQuery`
lookup (for example through its `orlns` default schema) and execute the streaming procedures.

When `AutoInitDatabase` is true, startup applies the embedded streaming migration. With it
disabled, apply `src/FabrCore.Host/SqlServer/SqlScripts/SQLServer-Streaming.sql` to the
clustering database before starting upgraded silos. For a new database, apply
`SQLServer-Main.sql` first. The migration preserves queued rows and sequence positions,
updates the six query definitions, and uses `CREATE OR ALTER PROCEDURE` to update existing
procedures. It requires SQL Server 2016 SP1 or newer and migration permissions.

Stop publishers, drain pending memory-stream work, and restart the whole cluster when
switching providers. Memory-stream messages are not migrated, and mixed provider modes
must not run together. Keep consumers idempotent: durable transport does not imply
exactly-once processing. Orleans defaults still apply, including ten-minute message expiry
and five delivery attempts; applications needing different retention/retry policies can
configure named `AdoNetStreamOptions` through `ConfigureOrleans`.

The SQL integration test creates and removes its own randomly named database. To run it:

```powershell
$env:FABRCORE_SQL_TEST_CONNECTION_STRING = 'Server=(localdb)\MSSQLLocalDB;Integrated Security=true;TrustServerCertificate=true'
dotnet test src/FabrCore.Host.Tests/FabrCore.Host.Tests.csproj --filter FullyQualifiedName~OrleansSqlStreamingTests
```

CI runs these SQL checks on LocalDB, storage compatibility checks, client tests, and the
telemetry sample build. No existing application database is modified by the tests.
