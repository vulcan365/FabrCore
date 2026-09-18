# Cloud configuration reconciliation

Implemented September 13, 2026 across FabrCore, FabrCore-V365 (Insights), and Impact.

Independent cloud servers have the same host capabilities. The [open wire contract](cloud-configuration-state-protocol.md)
and [reference server](../samples/FabrCore.ReferenceCloud/README.md) cover ingestion, inspection,
preview, and draft adoption without any Insights dependency.

## Desired, resolved, and applied

Cloud Server owns the desired runtime settings. A host combines those settings with local configuration, named code rules, and operator inputs. It reports the resolved values separately from values observed in actual providers and options consumers. Reports never update cloud settings automatically.

`fabrcore.cloud.json` remains enrollment/control-plane configuration. It is not rewritten with observed settings. The legacy `fabrcore.json` A2A fallback is loaded before cloud configuration at fallback priority, so it cannot overwrite cloud settings or operator inputs accidentally.

The new `configuration-state: 1` capability accompanies `GET /fabrcoreapi/admin/v1/settings/state`. Reports include process boot identity, sequence, observation time, application build, received/rejected revision, and setting rows containing desired/resolved/applied values, source and rule name, override reason, application confidence, and pending restart. Existing catalog endpoints remain compatible and are labeled configuration-only in Insights.

Built-in reporting observes Orleans membership implementation and separate storage, reminders, and stream provider types. SQL membership therefore reports `SqlServer` even when the mode was selected through database defaults or code and no JSON mode exists. Unsupported provider implementations remain unverified.

Supported FabrCore scalar options are observed when consumers access them; inspection does not instantiate every options object. Snapshot options retain the consumed value across refresh. Monitored options report successful consumer access/change callbacks; failed callbacks are unverified. Named options are separate runtime facts. A2A reports its singleton endpoint options. Arbitrary post-configuration is identified as `code-or-consumer` when observed values differ from input; those callbacks are never replayed for preview.

## Named code rules

Register pure configuration rules on `FabrCoreServerOptions` before `AddFabrCoreServer`:

```csharp
var options = new FabrCoreServerOptions()
    .ConfigureRuntime("Impact.Hosting", context =>
    {
        if (context.EnvironmentName is "impact-test" or "impact-prod")
        {
            const string key = "FabrCore:Orleans:ClusteringMode";
            context.Override(key, "SqlServer", "Deployed Impact uses shared durable state.");
            context.Require(key,
                value => string.Equals(value, "SqlServer", StringComparison.OrdinalIgnoreCase),
                "Test and Production require SQL Server clustering.");
        }
    });
builder.AddFabrCoreServer(options);
```

`Default` supplies only an absent key; an explicit null is not absent. `Override` takes ownership even when its value equals the cloud value. Environment and command-line providers retain final precedence in the standard provider order. `Require` validates the final value after all rules, including operator inputs. Two rule names owning the same key are rejected. Keep reasons and rule names free of secrets.

Rules run at registration, cloud refresh, and explicit preview. They must be deterministic and free of external side effects: do not initialize databases, register services, or capture mutable startup options. Read conditional inputs through the context. Ordinary file reloads and providers added after registration do not rerun the rule phase; use cloud refresh or restart when rule dependencies change through those paths. Arbitrary late provider registrations continue to follow .NET provider ordering.

Rejected rules leave the installed cloud layer and code outputs unchanged. Errors identify a rejected revision without leaking values. A consumer can still fail after a valid candidate is installed; the report records failure and does not claim rollback or universal application.

Custom components can register `IFabrCoreRuntimeSettingsContributor` to return `RuntimeSettingObservation` values from actual consumer instances. Only catalog-supported settings or `Runtime:` facts are reported. Add a settings descriptor for custom configurable keys; runtime facts are not adoptable. Mark sensitive observations `Secret: true`. Contributors must not construct replacement services or perform writes.

## Insights workflow

Each host sends a bounded, redacted report with its normal heartbeat, beginning after application startup. Insights persists the newest report per host process in the existing silo capabilities JSON. Older sequences cannot overwrite a newer report, legacy heartbeats retain the last report, and process reports remain separate. No database migration is required.

The Runtime Settings editor shows desired, host-resolved, applied, and source together. It identifies stored observations, incomplete startup, partial reports, code overrides, rejected revisions, pending restarts, and mixed active provider modes. A missing report never falls back to the schema's Localhost example.

Select **Adopt into draft** to copy an eligible applied value into the selected environment's draft. Adoption excludes secrets, unverified settings, derived/runtime facts, and instance identities. It preserves code ownership. Review and publish through the existing revision/concurrency path to change desired configuration.

Select **Preview draft on instance** to send the merged candidate to `POST /fabrcoreapi/admin/v1/settings/preview`. It evaluates pure rules and infrastructure defaults without applying configuration, firing reload notifications, or constructing another host. The current applied observations remain alongside candidate resolved values. A changed draft invalidates its preview. Preview is advisory: publishing still validates against each host's current code and inputs.

## Limits and rollout

- Reports cap rows and serialized size and redact secret values on both host and server. They do not include arbitrary object graphs, a full shadowed-provider history, or secret fingerprints.
- Applied means observed in a supported consumer/provider after startup. It is not a guarantee that every downstream operation succeeded. Unobserved custom services and nested options remain unverified.
- Pending restart compares known applied values with resolvable current inputs. A cloud value masked by code alone does not imply a restart. Unreplayable arbitrary callbacks cannot prove a prospective result.
- Reports travel in bounded heartbeats rather than a separate upload service. Offline hosts retain dated observations; a live endpoint can request a fresh snapshot.
- Confirmed commit, automatic rollback, and data/storage migration are intentionally deferred. Storage changes cannot be reversed safely by restoring a JSON revision alone.
- Impact now declares its Test/Production SQL requirement with a named rule. Build the matched FabrCore packages, build Impact against them, and deploy the updated Insights and Impact applications to expose the new behavior in AKS. This local implementation does not deploy or restart those workloads.
