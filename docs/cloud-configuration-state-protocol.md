# Configuration-state v1: independent cloud server contract

This extension is open and server-neutral. It requires no Vulcan365 account, Insights package,
Insights database, or proprietary endpoint. A .NET server can reference `FabrCore.Core.CloudServer`;
other languages can implement the camel-case JSON contract below. Named code rules and provider
observation run in `FabrCore.Host` for every configured cloud-server URL.

See the [base cloud protocol](cloud-server-protocol.md), [host rule API](cloud-configuration-reconciliation.md),
and runnable [independent reference server](../samples/FabrCore.ReferenceCloud/README.md).

## Discovery and transport

- Heartbeat v1 advertises `capabilities["configuration-state"] = "1"` and includes optional `configurationState`.
- `GET /fabrcoreapi/admin/v1/settings/state` returns a fresh `CloudConfigurationState` directly.
- `POST /fabrcoreapi/admin/v1/settings/preview` accepts a flat JSON object of candidate keys to string-or-null values and returns the same report type. The object replaces the complete cloud runtime settings layer for evaluation; merge shared and environment intent before sending it. Model/API-key configuration is not part of this request.
- Use the existing authenticated administration transport, directly where enabled or via outbound v2 commands. These paths run on the host, not on an Insights server. The existing host admin authorization and target-header validation apply.
- Hosts lacking this capability may return 404. Keep legacy configuration readings labeled unverified; do not infer Localhost from missing keys or example values. Ignore unknown additive JSON fields. Do not interpret an unsupported report `apiVersion` as v1.

Example `configurationState` (inside a heartbeat, alongside its cluster/environment/host identity):

```json
{
  "apiVersion": "1",
  "bootId": "f95a1c75c58c48caa8ee588b26a18705",
  "sequence": 12,
  "observedAt": "2026-09-13T14:00:00Z",
  "desiredRevision": "revision-42",
  "rejectedRevision": null,
  "error": null,
  "applicationBuild": "ExampleApp.1.2.3",
  "started": true,
  "truncated": false,
  "settings": [{
    "key": "FabrCore:Orleans:ClusteringMode",
    "hasDesiredValue": true,
    "desiredValue": "Localhost",
    "resolvedValue": "SqlServer",
    "appliedValue": "SqlServer",
    "appliedKnown": true,
    "source": "code-override",
    "sourceId": "Example.Hosting",
    "reason": "Deployed instances require durable shared state.",
    "applyMode": "RestartRequired",
    "secret": false,
    "overridden": true,
    "pendingRestart": false,
    "canAdopt": true,
    "appliedRevision": "revision-41"
  }]
}
```

## Meaning

| Fields | Meaning |
| --- | --- |
| `bootId`, `sequence`, `observedAt` | Process UUID, increasing observation sequence, and UTC observation timestamp. Sequence gaps are valid. |
| `desiredRevision` | Last cloud revision received, which may have been rejected. It is not a universal application acknowledgment. |
| `rejectedRevision`, `error` | A rejected revision or reporting/application error. Error text must not contain values or credentials. |
| `started`, `truncated` | Startup completion and report completeness. Missing rows in a partial report are unknown, not deleted settings. |
| `hasDesiredValue`, `desiredValue` | Distinguish an absent key from an explicit null. These describe intent at observation time, not necessarily the newest server revision. |
| `resolvedValue` | Host result after configuration and observable code rules/defaults. Null can mean unknown, unset, or redacted; it does not prove a consumer was cleared. |
| `appliedKnown`, `appliedValue` | Evidence from a supported consumer/provider. Never present a value as applied unless startup completed and `appliedKnown` is true. |
| `source`, `sourceId`, `reason` | Provenance and explanation. Current sources: `cloud`, `operator`, `file-or-provider`, `code-default`, `code-override`, `code-or-consumer`, `derived`, `runtime`, `unknown`. Preserve unfamiliar source strings for forward compatibility. |
| `applyMode`, `pendingRestart` | `Live` or `RestartRequired`; pending restart is a known applied/resolved difference, not just a cloud-layer change. |
| `overridden` | Cloud intent is masked by a different source. Ownership can be explicit even when values are equal. |
| `secret`, `canAdopt` | Redaction and adoption eligibility hints. Servers must enforce their own validation and permissions as well. |
| `appliedRevision` | Revision associated with an observed consumer's application. Startup consumers can retain an older revision while live consumers advance. |

Arbitrary startup code is supported but cannot be safely replayed. A changed options value can be
reported as `code-or-consumer`; prospective resolution can remain unknown until a restart. A named
rule records ownership even when equal values prevent value comparison from detecting an override.

## Ingestion and storage

Authenticate first. Bind the request to the authenticated tenant/cluster and allowed environment;
reject mismatched headers/body scope. Store observations separately from desired configuration.
Use `(tenant, cluster, environment, hostInstanceId)` as the storage scope. The SDK generates a
process-specific host identity; `bootId` identifies the runtime reporter within that process.

Within that identity and boot, accept only increasing sequences. Duplicate or older reports must
not replace newer state. Do not replace a retained report with a different boot claiming the same
process identity. Separate process identities remain separate during rolling deployments. A legacy
heartbeat without a report can update liveness while retaining the previous dated observation.
Keep receive time separate from observed time; neither liveness nor an incoming report proves
that every other instance is using the same settings. Apply a retention policy in production.

Validate v1 metadata and rows before persistence. Current host limits are 256 rows, a 196,608-byte
UTF-8 serialized report, keys/source identifiers up to 256 characters, source labels up to 64,
reasons/error up to 512, scalar values up to 8,192, and revision/build strings up to 256.
Limit the complete heartbeat request too (Insights uses 262,144 bytes; the general reference
fixture has a 1 MiB transport limit). Require a UUID boot, positive sequence, valid timestamp,
unique case-insensitive keys, and a recognized apply mode. Reject enrollment keys under
`FabrCore:CloudServer`, `FabrCore:RemoteAdministration`, and `FabrCore:HostUrl`.

Redact before storage and before rendering. All three values must be hidden when `secret` is true;
also detect connection strings and credential-like keys independently. Do not log raw heartbeat
bodies. Extenders must mark custom secrets, and source IDs/reasons must contain no secret values.
The reference store demonstrates defensive validation, bounded retention, cloning, and redaction.

## Preview and deliberate reconciliation

1. Display desired-at-observation, resolved, applied, source, and observation age per host. Keep mixed
   instances and unknown readings visible.
2. To preview, send the complete candidate cloud settings map to the selected host's preview endpoint.
   It runs the pure rule/default resolver, without applying settings or firing consumer reloads.
   Rule failures return 400; cloud runtime settings must be enabled. Current applied observations
   remain unchanged in the response. `desiredRevision` is null for previews; never persist a preview
   as an observed heartbeat. The sequence is not a preview identity or publish token.
3. Bind a preview in the console to its exact candidate, target process, and build. Invalidate it when
   the draft changes. Preview is advisory; every host validates the eventual published revision again.
4. Adoption copies selected eligible `appliedValue` entries into an explicit environment draft. Require
   started/known/nonsecret values, exclude runtime/derived/instance identity settings, and validate
   against your desired-settings schema. Preserve unrelated draft keys and code ownership.
5. Publish through your normal authenticated, revision-checked configuration store. Use the existing
   envelope/ETag protocol and optionally heartbeat `refreshRequested`. Do not change cloud intent
   automatically when a report arrives or bypass a host's compiled constraint.

The reference server exposes operator-only report inspection and draft generation to demonstrate
steps 1 and 4. Its generic command channel handles steps 2 and 3. Draft generation returns a base
revision and never writes the configuration file. Production persistence, concurrency, operator
permissions, and the console are the implementer's responsibility. No automatic rollback or storage
migration is implied by a successful publish or preview.
