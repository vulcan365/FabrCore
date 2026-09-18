# Optional connections and Microsoft integration

FabrCore can run without Microsoft identities, external accounts, or a cloud server.
The new packages add explicit opt-ins:

| Package / option | Responsibility |
| --- | --- |
| `FabrCore.Connections` | Provider-neutral contracts, user/admin API clients, client-side handoff encryption |
| `FabrCore.Services.Connections` / `Enabled` | Principal-owned profiles, protected grants, resource token acquisition, API endpoints, blueprint expansion |
| `EntraAgentIdEnabled` | Separately enabled Entra Agent ID parent/child token exchanges |
| `ClientHandoffEnabled` | Single-use encrypted user authorization through a cloud broker |
| `FabrCore.Services.RemoteAgents` / `Enabled` | Handle-addressable Work IQ A2A and Copilot Studio conversations |

Existing inbound A2A and Microsoft 365 Copilot exposure remain separate opt-ins.
FabrCore adds API endpoints only. Client applications own login, consent, callbacks,
and their user experience. Insights is one possible cloud administration server.

## Host setup

Reference the service packages that the host intends to enable. FabrCore Host now
supplies credential protection automatically when connections are enabled. This
uses the Data Protection cryptographic library, adds no UI, and does not change an
application's authentication-cookie protection.

Leave `FabrCore:DataProtection:Mode` unset to use `Auto`. The built-in host setup
requires no application calls to `AddDataProtection` or `AddFabrCoreDataProtection`
and no `ProtectedKeyRingConfigured` flag. SQL still needs the deployment certificate
described below; FabrCore selects and initializes the key repository automatically.

```csharp
using FabrCore.Host;
using FabrCore.Services.Connections;
using FabrCore.Services.RemoteAgents;

builder.AddFabrCoreServer(new FabrCoreServerOptions {
    AdditionalAssemblies = [typeof(ConnectionsExtensions).Assembly,
                            typeof(RemoteAgent).Assembly]
});
builder.Services.AddFabrCoreConnections(o => {
    o.Enabled = true;
    o.EntraAgentIdEnabled = false;
    o.ClientHandoffEnabled = false;
});
builder.Services.AddFabrCoreRemoteAgents(o => o.Enabled = true);

// Also configure the host's normal user authentication/authorization.
var app = builder.Build();
app.UseFabrCoreServer();
app.MapFabrCoreConnections();
app.Run();
```

`FabrCore:DataProtection:Mode` defaults to `Auto`:

| Effective persistence | Automatic protection |
| --- | --- |
| Default Localhost with in-memory entities/grains | One singleton ephemeral provider; keys and connection state are lost on restart |
| Integrated SQL database, including explicit Localhost clustering | Encrypted shared key ring in the operational database |
| Orleans-only SqlServer mode | Encrypted shared key ring in `StorageConnectionString`, falling back to `ConnectionString` |
| Azure/custom persistence without SQL | Explicit shared provider required; never silently use ephemeral keys |

For SQL, supply the deployment's key-encryption certificate through protected host
configuration. FabrCore manages the key repository and registration:

```json
{
  "FabrCore": {
    "DataProtection": {
      "ApplicationName": "my-cluster-production",
      "CertificatePath": "/run/secrets/fabrcore-protection.pfx"
    }
  }
}
```

Use `CertificatePassword` from a secret provider if the PFX is password protected.
All silos require the private key. The database stores **encrypted** Data Protection
key XML in `fabrOps.DataProtectionKey`; it does not store the certificate private key.
Default application identity combines Orleans service ID, cluster ID, and host
environment. Set a unique stable `ApplicationName` when sharing a database across
clusters. Changing that name makes previous protected records inaccessible.

The key table is initialized only when protection is activated, using the existing
`FabrCore:Database:AutoInitialize` policy. For manual-schema deployments apply the
[key-ring migration](../assets/data-protection.sql) to the operational database.
Orleans-only SQL uses its `AutoInitDatabase` setting instead. Startup checks key
protection before accepting consent; missing certificates/schema or an unavailable
database cause failure without an ephemeral fallback. Existing cached keys can
continue to protect data during a later database outage.

For certificate rotation, distribute the new PFX to every silo, change
`CertificatePath`, and retain old private keys under
`PreviousCertificates:0:Path` / `PreviousCertificates:0:Password` (and subsequent
indices). Restart hosts to apply changes. Retain old decryption certificates and
key-ring rows while protected records need them; back up both. Switching providers
requires preserving the existing key ring and application identity, not generating
a replacement key ring for existing data. Protect silo-to-silo traffic as well.

An explicitly configured standard Data Protection repository with key encryption
is preserved by `Auto`. For other custom providers, set `Mode=Custom` and register
`IDataProtectionProvider`, or directly register `IFabrCoreDataProtectionProvider`
from `FabrCore.Host.Security`. Custom providers own their persistence/encryption
guarantees. Use this for vault-backed protection or other deployment-specific
stores. `Mode=Ephemeral` is rejected for known durable persistence. The old
`ProtectedKeyRingConfigured` property remains for source compatibility but is no
longer required or treated as proof of a working provider.

Omitting registration or setting `Enabled = false` adds no connection services,
workers, endpoints, or external requests. A disabled profile cannot acquire tokens.
Feature registration is host bootstrap configuration; an administrator cannot
enable Agent ID merely by submitting an agent blueprint.

## Ownership and authorization

A connection is identified by `(ownerPrincipal, name)`. An agent declares aliases
for those bindings, and the profile separately grants exact full agent handles.
Both are necessary. Delegated connections require the agent's owning principal to
match the connection owner. Application connections may belong to a service
principal and explicitly grant agents belonging to other principals.

The SDK obtains the principal and agent handle from trusted host context. Prompts
cannot supply those identities. Diagnostic turns cannot access production
connections. Do not expose raw-token methods as model tools.

The default user API resolver maps authenticated Entra `tid` and `oid` through
`EntraPrincipalHandle.Create`. Other client authentication systems register an
`IConnectionPrincipalResolver` which maps **validated** claims to the canonical
FabrCore principal. A connected Google account can belong to that FabrCore
principal without changing its FabrCore ID.

## Profiles and application credentials

Profiles contain metadata and credential references, never secret values. Create
through `FabrCoreConnectionAdministrationClient.SaveAsync(principal, profile, "*")`.
For updates, read `GetAsync` and pass the returned revision. Stale revisions fail
with 412; writes are not automatically retried. Any profile update invalidates
cached authorization and remote conversation bindings.

```csharp
var mail = new ConnectionProfile {
    Name = "mail-service",
    Enabled = true,
    Provider = "microsoft",
    Authentication = ConnectionAuthentication.ClientCredentials,
    Authority = $"https://login.microsoftonline.com/{tenantId}/v2.0",
    ClientId = appRegistrationClientId,
    CredentialReference = "mail-application",
    AllowedAgents = ["alice:mail-agent"],
    Resources = new() {
        ["graph"] = new() {
            BaseUrl = "https://graph.microsoft.com/v1.0/",
            Scopes = ["https://graph.microsoft.com/.default"]
        }
    }
};
await admin.SaveAsync("mail-service-principal", mail, "*");
```

The app registration must have the appropriate Microsoft Graph **application**
permissions and tenant administrator consent. For app-only mail access use
`users/{mailbox}/messages`, not `/me`. Provider-side mailbox restrictions continue
to apply. FabrCore grants access to the connection; it does not grant Graph
permissions. Configure only the permissions/mailboxes the application needs.

The default credential provider resolves one of these server configuration keys:

* `FabrCore:ConnectionCredentials:mail-application:Secret`
* `FabrCore:ConnectionCredentials:mail-application:CertificatePath` and optional
  `CertificatePassword` for a signing certificate with its private key.

Supply these through the host's protected configuration/vault system. Do not put
them in agent arguments, blueprints, cloud configuration documents, or client
requests. Certificate assertions are short-lived. An application can replace
`IConnectionCredentialProvider` to obtain federated client assertions or use a
different vault/HSM. Automatic managed-identity/federation discovery is not built
into the default provider.

Client credentials acquire a new access token when necessary; they do not need a
refresh token. Disconnect clears local cached authorization, but an enabled
application profile can acquire another token. Disable the profile to stop use.

## User consent in an external client

An authorization-code profile declares `Authority`, `ClientId`, exact HTTPS
`RedirectUris`, `SignInScopes` including `openid`, and allowed resources. Request
`offline_access` with Microsoft when continued refresh is needed. For Google,
use `https://accounts.google.com`; the provider adapter requests offline access.
Google service-account/domain-wide delegation uses a different grant and is not
implemented by the standard OAuth `ClientCredentials` mode.
The app registration must allow the same callback and OAuth client configuration.
If the client is confidential, configure its credential reference on the host.

```csharp
// http authenticates the current user to FabrCore, not to Graph or Google.
var connections = new FabrCoreConnectionsClient(http);
var transaction = await connections.BeginAsync(
    new("work", "https://my-client.example/connections/callback"));
// Client navigates the browser to transaction.AuthorizationUrl.
// Remember transaction.State and connection name in the client's session.

// On the registered callback, verify returned state against that client session.
await connections.CompleteAsync(new(
    "work", returnedState, returnedCode, returnedProviderError));
```

FabrCore generates state, nonce, and PKCE verifier, stores them protected for ten
minutes, and exchanges the code. The callback is on the external client. Complete
must authenticate as the same FabrCore principal as begin. A transaction is
consumed before exchange and cannot be replayed. An uncertain exchange requires
new consent rather than replaying a code. FabrCore validates the ID token's
signature, issuer, audience, lifetime, and nonce before linking the account.

Authorization codes, assertions, tokens, and callback query strings must be
excluded from application HTTP body/query logging. Cookie clients need a
same-origin mutation request. Cross-origin client apps should use authenticated
bearer requests and the host's explicit CORS policy.

The connection APIs return status, never access or refresh tokens. Provider
interaction/consent failures return `interaction-required`. Refresh is serialized
by a principal-owned Orleans grain, including refresh-token rotation.

## Agent SDK usage and MCP

Inside a `FabrCoreAgentProxy` implementation:

```csharp
using var graph = await Connections.GetHttpClientAsync("mail", "graph", ct);
var messages = await graph.GetStringAsync(
    "users/" + Uri.EscapeDataString(mailbox) + "/messages?$top=10", ct);

// Only when a trusted SDK requires the raw token:
var token = await Connections.GetAccessTokenAsync("mail", "graph", ct);
await trustedSdk.CallAsync(token.AccessToken, ct);
```

The authenticated client acquires/renews a token for each request, restricts URLs
to the configured resource base, disables cookies and redirects, and fails if its
connection is replaced or disconnected. Tokens are excluded from JSON serialization
and `ToString`; trusted code must still avoid logging `AccessToken` itself.

For HTTP MCP, set `McpServerConfig.Connection` to the agent's alias and `Resource`
to its configured resource name. Do not supply a static Authorization header.
Existing unauthenticated/static-header MCP configurations keep their behavior.
After reauthorizing/replacing a connection, reconfigure the agent to establish a
new MCP session. Token renewal within the same authorization works per request.

## Reusable blueprints

`connectedAgents` is a top-level canonical blueprint extension. Defaults apply
to every agent in that group. An agent can override individual bindings through
its `args.connections` JSON string. `$principal` resolves at deployment time.
Expansion and preview compute configurations only: no provisioning, grants,
consent, token acquisition, or remote requests happen during preview.

```json
{
  "name": "work-assistant",
  "connectedAgents": {
    "connections": {
      "work": { "ownerPrincipal": "$principal", "name": "workiq" }
    },
    "agents": [{
      "handle": "copilot",
      "agentType": "remote-agent",
      "args": {
        "provider": "work-iq",
        "connection": "work",
        "resource": "workiq",
        "endpoint": "https://workiq.svc.cloud.microsoft/a2a/",
        "timeZone": "America/Chicago"
      }
    }]
  }
}
```

Provision the `workiq` profile separately and allow the deployed full handle,
for example `alice:copilot`. For a service connection use its explicit owner
instead of `$principal`. Agent ID bindings live on the referenced connection
profile; blueprints reuse them without carrying credentials or provisioning
Microsoft directory objects.

## Copilot Studio and Work IQ

Remote agents run behind ordinary FabrCore handles. A user and that user's agents
share the remote handle's conversation. Cross-principal callers are rejected.
Context is stored in normal FabrCore agent state and survives failover when the
host has durable storage. Changing endpoint, provider, connection binding, or
authorization resets that context.

* `provider = work-iq` uses A2A 1.0, streaming by default. Set `streaming = false`
  for synchronous send. Configure the delegated Work IQ resource scope
  `api://workiq.svc.cloud.microsoft/WorkIQAgent.Ask`. Work IQ uses delegated
  permissions; an app-only Graph registration is not a substitute.
* `provider = copilot-studio` uses `Microsoft.Agents.CopilotStudio.Client`
  `1.3.171-beta`. Set `endpoint` to the published agent's direct-connect URL and
  resource base URL to its HTTPS origin/path. Configure delegated
  `https://api.powerplatform.com/CopilotStudio.Copilots.Invoke` consent.
  FabrCore does not treat inbound Studio A2A support as proof of outbound Studio
  A2A support. This adapter uses the Studio client SDK.
* Work IQ tools can also use authenticated MCP or ordinary plugin HTTP calls.
  No dedicated Work IQ tool runtime is required. Configure the resource scopes
  from the current Work IQ permission reference for the chosen endpoint.

Work IQ replies retain structured artifacts in `AgentMessage.Data`. Streaming
progress is delivered as `_remote-progress` messages; Studio typing uses `_status`.
Work IQ task/context IDs persist as updates arrive. Send message types
`_remote-task-status`, `_remote-task-resume`, or `_remote-task-cancel` to inspect,
subscribe to, or cancel the current task. `_remote-reset` starts a fresh local
conversation binding; it does not cancel provider execution. Nonterminal tasks
return `_remote-task` and task metadata. Timeouts are not automatically retried;
inspect a known task before deciding whether to send again. No task ID can be
recovered when a provider request was accepted but no response arrived.

## Optional Entra Agent ID

Ordinary application registrations and user OAuth do not need Entra Agent ID.
When separately enabled, configure:

* `Authentication = AgentIdApplication` or `AgentIdOnBehalfOf`;
* `ClientId` and `CredentialReference` for the agent identity blueprint;
* `AgentIdentityClientId` for the provisioned child agent identity;
* `BlueprintAudience` for the blueprint API in the delegated case.

The implementation obtains the parent's exchange token with `fmi_path` set to the
child, then uses that token as the child's client assertion. Delegated mode also
requires a validated human assertion targeting the blueprint API. The client
obtains that assertion interactively, then submits it using
`ConnectAssertionAsync(new(connectionName, userAssertion))`. Conventional
`OnBehalfOf` uses the ordinary app's credential and incoming API audience instead.
OBO refresh uses a resource refresh token if the provider issues one; otherwise
the client must provide a current assertion. There is no fallback to app-only
permissions after a delegated denial.

This is a protocol implementation behind FabrCore's SDK facade, not automatic
Entra directory provisioning. Blueprint/identity creation, consent, sponsors,
tenant governance, and directory lifecycle remain external administrative steps.
The credential-provider extension supports integrating additional Microsoft
identity tooling. Agent ID compatibility with each target Microsoft API must be
validated in the target tenant; it is not implied by supporting the exchange.

## Cloud administration and encrypted handoffs

Capability discovery advertises `connections`, `connectedAgents`, and the
separately enabled `entra-agent-id` / `encrypted-client-handoff` features.
Administration uses the existing privileged outbound cloud command channel:

| Method | Path under `/fabrcoreapi/admin/v1/principals/{principal}/connections` |
| --- | --- |
| GET | `/` — statuses |
| GET | `/{name}` — profile and revision |
| PUT | `/{name}` — profile; required `If-Match` revision (`*` for create) |
| DELETE | `/{name}/authorization` — clear cached authorization |
| POST | `/{name}/handoff` — short-lived public key challenge |
| POST | `/{name}/handoff/complete` — encrypted user operation |

User endpoints under `/fabrcoreapi/connections/v1` provide GET status, POST
`begin`, `complete`, `assertion`, and DELETE `/{name}`. They use authenticated user
identity and do not accept an owner override. They are not the cloud admin routes.

For clients that reach the cluster through a cloud broker, enable
`ClientHandoffEnabled` and configure `HandoffAuthority` / `HandoffAudience` for a
delegated access token intended for the FabrCore API. The default validator uses
Entra user claims. Other authentication systems implement
`IConnectionHandoffPrincipalValidator`; it must validate a signed token and map
the trusted subject to a FabrCore principal. Never trust an owner string from
the broker as proof that the user consented.

```csharp
// The app obtains this challenge through its authorized cloud backend.
var challenge = await admin.CreateHandoffAsync(owner, "work");

// Encrypt in the client BEFORE sending the result to the cloud backend.
var envelope = ConnectionHandoff.Encrypt(challenge, new() {
    UserProof = fabrcoreAudienceUserToken,
    Operation = "complete",
    State = returnedState,
    AuthorizationCode = returnedCode
});
// Only envelope crosses the durable cloud command channel.
await admin.CompleteHandoffAsync(owner, "work", envelope);
```

Use a fresh challenge for each operation (`begin`, `complete`, `assertion`, or
`disconnect`). Challenges expire in five minutes and are consumed durably before
decryption/validation. AES-256-GCM encrypts the payload; RSA-OAEP-SHA256 wraps its
key. Private keys and grants are encrypted in cluster storage. TLS and a trusted
cloud broker are still required to deliver the authentic cluster public key.
Neither administrator identity nor possession of an envelope replaces user proof.

Insights exposes these admin paths through its existing scoped cluster proxy and
connect broker. Its environment Connections page manages metadata, grants, and
disconnects. The client consent UI belongs to the external application. Any other
cloud server can implement the same transport. The reference cloud server can
forward these endpoints unchanged; it is a single-process conformance fixture,
not a durable production broker. Never send plaintext assertions or codes through
its ordinary command API.

## Validation and current boundaries

Automated coverage includes disabled registration, principal isolation, explicit
application grants, bounded HTTP destinations, token redaction, protected Orleans
persistence, conditional profile updates, stale client rejection, handoff owner
proof/replay/tampering, OAuth wire exchanges, and Work IQ stream/task parsing.
No real Microsoft/Google tenant credentials were used for validation. Deployment
requires tenant consent and live smoke tests for login, renewal, Studio, Work IQ,
and any selected Agent ID combination. Provider-side revocation and consent remain
provider operations; disconnect here clears FabrCore's local authorization.

References: [Work IQ A2A](https://learn.microsoft.com/en-us/microsoft-365/copilot/extensibility/work-iq/a2a/overview),
[Work IQ API](https://learn.microsoft.com/en-us/microsoft-365/copilot/extensibility/work-iq/api-overview),
[Entra Agent ID](https://learn.microsoft.com/en-us/entra/agent-id/),
[Microsoft Agents SDK](https://github.com/microsoft/Agents),
[OAuth client credentials](https://learn.microsoft.com/en-us/entra/identity-platform/v2-oauth2-client-creds-grant-flow).
