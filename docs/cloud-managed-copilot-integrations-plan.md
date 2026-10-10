# Cloud-managed Copilot integrations plan

This plan describes how a FabrCore cloud server provisions and manages each customer's Microsoft
365 Copilot integration: the Copilot/Teams channel, the A2A endpoint for Copilot Studio,
Connections, and remote agents. Insights is Vulcan365's cloud server and the first implementation;
the FabrCore side is vendor-neutral and lands in the open protocol, so any conforming cloud server
can do the same.

Read the [integration guide](microsoft-365-copilot-native-integration.md) first. It defines what
"native" means, what ships, and the backlog (B1–B19) this plan draws from.

## 1. Outcome

A customer administrator signs in to the cloud server, connects their Microsoft 365 tenant once,
chooses which agent to publish, and gets a working agent in Copilot and Teams. Afterwards they can
see, per host, whether the integration is on, as which identity, and whether a change is waiting on
a restart. No one edits a JSON file on a host.

Out of scope for this plan: the compliance plane itself (turn audit, Purview gate), which the
integration guide covers as B2 and B9. This plan makes the integration manageable; it does not by
itself make it compliant.

## 2. Tenancy

**Each cloud-server tenant maps to exactly one customer Entra tenant.** The link is established by
administrator consent and stored as the Entra tenant id. It is the root of everything else:

* It is the tenant the integration app is registered in.
* It is the `tid` every user principal carries (`entra-{tid}-{oid}`).
* It is the only tenant whose tokens the customer's hosts accept.

A customer with several Entra tenants gets several cloud-server tenants. A cloud-server tenant can
have several clusters and environments; they share the tenant link and, by default, one integration
app per environment (section 3).

This is the target, not the present. An Insights tenant today is a key, a display name, and
free-text metadata with no Entra tenant id, and Insights users are not bound to tenants
(section 15.1). The link is a new field, and until managed provisioning verifies it through
consent it is a value an operator typed.

## 3. The per-customer integration app

New multi-tenant Azure Bot registrations were retired after 31 July 2025. A bot identity must now
be single tenant, so one shared Vulcan365 bot cannot serve every customer. Each customer gets a
single-tenant **FabrCore integration app** registered in their own tenant.

One registration plays four roles, deliberately:

| Role | What uses it | Configuration |
| --- | --- | --- |
| Bot identity | Azure Bot Service, to deliver activities and accept replies | `Microsoft365Copilot:ClientId`, `TenantId` |
| Single sign-on API | Teams and Copilot, to issue the user's sign-on token | Application ID URI `api://botid-{appId}` |
| On-behalf-of client | Connections, to exchange that token for Work IQ, Graph, and Copilot Studio tokens | `ConnectionProfile.ClientId` |
| A2A token audience | Copilot Studio, to obtain a delegated token for the A2A endpoint | `A2A:Authentication:JwtBearer:Audience`, scope `agent.invoke` |

Using one registration is what makes the sign-on token a valid on-behalf-of assertion for
Connections (integration guide, B1). Splitting the roles across registrations would break that.

### Credential

Two credentials are supported, and the choice is per customer (decision D2).

**A client secret, published with the settings.** The cloud server publishes
`Microsoft365Copilot:ClientSecret` in the `settings` map like any other key. This works on every
hosting platform, needs nothing from the host but the cloud connection, and is what the Insights
settings catalog already offers. The same applies to A2A API key values and to
`FabrCore:ConnectionCredentials:{reference}:Secret`, which is what lets Connections perform the
on-behalf-of exchange with the same app.

**A federated identity credential that trusts the host's workload identity** (on AKS, the cluster's
OIDC issuer and the host's service account subject). The host proves who it is with a token its
platform issues, so there is no secret to store or rotate. It needs a platform that issues workload
identities, and Connections cannot use it until F5.

Publishing secrets is an accepted practice in this plan. It has three consequences to plan for
rather than to avoid:

* **Hosts cache the envelope in plaintext.** `fabrcore.cloud-cache.json` holds every published
  setting on every silo, the same exposure the model API keys in the envelope already have. Treat
  the file, and the volume it lives on, as secret material.
* **The cloud server holds the secret.** In Insights it sits inside the configuration document,
  which is encrypted at rest as a whole, and it stays in every earlier revision of that document.
  Rotating means publishing a new value; the old one remains readable in history until that
  history is removed.
* **Secrets expire.** An Entra client secret has an end date, and an expired one takes the bot
  offline with no change on the host. The cloud server records the expiry date and warns before
  it; the `bot-service-credential` diagnostic is what confirms a replacement works.

## 4. Provisioning

A Vulcan365-owned **multi-tenant application, "Insights Microsoft Integration"**, is the
provisioning identity. A customer administrator grants it consent once. It then creates and
maintains the per-tenant objects:

| Object | Where | Notes |
| --- | --- | --- |
| Integration app registration and service principal | Customer Entra tenant | Single tenant; owned by the provisioning app |
| Application ID URI, `agent.invoke` scope, sign-on scope, pre-authorized Microsoft clients | On that registration | |
| Client secret, or a federated identity credential | On that registration | A secret is created with an end date and published to the hosts; a federated credential names the issuer and subject of the host's workload identity |
| Delegated permission grants | Customer Entra tenant | Work IQ, Copilot Studio, and Graph scopes the agents need |
| Azure Bot resource and its Teams channel | An Azure subscription (decision D3) | Messaging endpoint points at the customer's host |
| Teams app in the organization catalog | Customer tenant | The package generated by the host |

The provisioning app is the high-value target in this design: it holds standing permission in every
customer tenant. Two constraints keep it survivable. It requests only the permission to manage
applications it created. And a customer can sever it at any time by removing its service principal,
which stops provisioning without stopping their running agents.

Where a customer uses a client secret, the cloud server also holds that secret, so a compromise of
the cloud server exposes the integration app of every such customer, not only the ability to
provision. Each app is single tenant and holds only the permissions that customer granted, which
bounds the damage per customer, and a customer can end it by deleting the secret. Customers who
need the cloud server to hold nothing use the federated credential.

## 5. Hosting

**Recommended: one FabrCore cluster per customer tenant.** A host has exactly one bot identity
today: the addon binds one `Microsoft365Copilot` section, synthesizes one Agents SDK service
connection, validates one audience, and maps one messaging endpoint. One cluster per tenant matches
that, and it also keeps each customer's agent state, model keys, and Data Protection keys apart.

Several published agents for the same tenant on one cluster, each with its own Teams app, needs F8.
Several *tenants* on one cluster is not a goal of this plan.

## 6. Models

Unchanged. Existing Azure AI Foundry endpoints and keys continue to flow to hosts as
`modelConfigurations` and `apiKeys` in the configuration envelope. The integration only selects a
model configuration by name through the agent binding.

In Insights these are scoped to a **cluster**, not a tenant, and they reach hosts only after an
operator applies a deployment to a cluster's configuration (section 15.6). Nothing in this plan
changes that or depends on it being per tenant.

## 7. What the cloud server publishes

The integration is expressed entirely as host settings in the envelope's `settings` map. The three
rows marked *secret* carry credentials; everything else is an identifier or an option.

| Keys | Purpose |
| --- | --- |
| `AgentBindings:{name}:AgentType`, `Handle`, `Models`, `SystemPrompt`, `Plugins`, `Tools`, `Args` | The published agent, shared by both channels |
| `Microsoft365Copilot:Enabled`, `TenantId`, `ClientId`, `AuthType` | The bot identity and how the host authenticates as it |
| `Microsoft365Copilot:ClientSecret` | *Secret.* The bot's client secret, when `AuthType` is `ClientSecret` |
| `A2A:Authentication:ApiKey:Keys:{n}:Name`, `Value` | *Secret.* A2A API keys, when A2A uses `ApiKey` rather than `JwtBearer` |
| `FabrCore:ConnectionCredentials:{reference}:Secret` | *Secret.* The client secret Connections uses for the same app |
| `Microsoft365Copilot:Principal:Strategy` = `CanonicalEntra`, `Agent:Binding` | Identity mapping and routing |
| `Microsoft365Copilot:Manifest:*` | App name, descriptions, public host name, conversation starters |
| `Microsoft365Copilot:UserAuthorization:Handlers:*` | Single sign-on handler |
| `Microsoft365Copilot:Proactive:*`, `Streaming:*` | Optional behavior |
| `A2A:Enabled`, `PublicBaseUrl`, `Principal:Strategy`, `Agents:{n}:Name`, `Binding` | The A2A endpoint |
| `A2A:Authentication:Mode` = `JwtBearer`, `JwtBearer:Authority`, `Audience`, `RequiredScopes` | A2A caller authentication |
| `FabrCore:Connections:Enabled`, `EntraAgentIdEnabled`, `ClientHandoffEnabled`, `HandoffAuthority`, `HandoffAudience` | Connections (needs F2) |
| `FabrCore:RemoteAgents:Enabled`, `Timeout` | Remote agents (needs F2) |

Every one of these is restart-required today. A publish is therefore a two-step operation for the
operator: publish, then restart the hosts at a time of their choosing. Hosts never restart
themselves, and they report which keys are waiting.

## 8. FabrCore changes

| Id | Change | Why the cloud server needs it | Phase |
| --- | --- | --- | --- |
| **F1** | Capability advertisement for A2A, the Copilot channel, and remote agents, through a contributor interface add-ons implement | Decide what to show for a cluster without probing it | 1 |
| **F2** | Configuration-bound enablement for Connections and remote agents; the Copilot addon starts disabled instead of throwing when unconfigured | Turn features on by publishing settings; ship one host image | 1 |
| **F3** | Applied-settings reporting for the channel and A2A in the configuration-state report | Show what is actually applied on each host, and pending restarts | 1 |
| **F4** | Integrations administration API: status, findings, manifest, app package, diagnostics | Health and posture per host; authenticated package download | 1 |
| **F5** | Workload-identity credential provider for Connections | The federated credential must work for outbound token exchange, not only for the bot. Not needed by customers who use a client secret. | 3 |
| **F6** | Connection profiles in the envelope | Provision Work IQ and Copilot Studio connections without a per-host API call | 4 |
| **F7** | Live reload of non-identity Copilot options | Edit a welcome message or a conversation starter without a restart | 5 |
| **F8** | Several bots per host | Several published agents per cluster, each with its own Teams app | 5 |
| **F9** | Fix security finding F-02 (unauthenticated `x-user-handle`), and add an `IntegrationManagement` audit category | F-02 undermines every channel's authentication; integration changes need their own audit trail | 3 |
| **F10** | Protocol documentation, reference cloud server support, conformance tests | Keep the feature vendor-neutral | 1 (docs), 3 (rest) |

Design notes:

* **F1.** `IFabrCoreCapabilityContributor` lives in `FabrCore.Core`, not the host, because the
  remote-agents package references the SDK and cannot reference the host. A contributor registers
  even when its feature is disabled, reporting `available: false`, so a cloud server can tell
  "installed but off" from "not installed".
* **F2.** The configuration sections are `FabrCore:Connections` and `FabrCore:RemoteAgents`. The
  top-level `Connections` section is not usable: the Microsoft 365 Agents SDK owns it, and the
  Copilot addon decides whether to synthesize its service connection by checking whether that
  section exists. A code delegate still runs last, so a host that pins a value in code keeps it.
* **F3.** Reported key names must not contain `token` or `secret`. The settings catalog redacts any
  key containing those fragments, so `TokenValidation:Enabled` is reported as redacted like any
  other; facts that would otherwise need such a name are reported under `Runtime:` keys.
* **F4.** Read-only apart from diagnostics. Diagnostics acquires a Bot Service token to prove the
  credential works, then discards it. It proves the host can authenticate outbound. It cannot prove
  the messaging endpoint is reachable from Microsoft; that check belongs on the cloud-server side.
* **F5.** The projected workload-identity token is itself the client assertion. The provider reads
  it from the token file and supplies `client_assertion_type` and `client_assertion`, with no
  signing key on the host.
* **F6.** Additive to the envelope, mirroring `blueprints`: principal-scoped profiles applied
  through the existing revision-checked save. Profiles carry credential *references* only.
* **F9.** F-02 is a release gate for automated provisioning. Provisioning publishes a public
  endpoint for a customer; the same host must not also expose an unauthenticated route to the same
  agents.

## 9. Cloud-server module (Insights)

| Id | Component | Responsibility | Phase |
| --- | --- | --- | --- |
| **I1** | Tenant link | Administrator consent for the provisioning app; record and verify the Entra tenant id | 3 |
| **I2** | Graph provisioner | Create and reconcile the integration app, its API, federated credential, and permission grants | 3 |
| **I3** | ARM bot provisioner | Create and reconcile the Azure Bot resource and its Teams channel | 3 |
| **I4** | App publisher | Fetch the package from a host and publish it to the organization catalog | 3 |
| **I5** | Channel settings composer | Turn the integration record into the settings keys in section 7 and publish them | 2 |
| **I6** | Connected agents | Manage connection profiles and `connectedAgents` blueprints for Work IQ and Graph | 4 |
| **I7** | Copilot Studio link | Guide and verify the A2A connection from Copilot Studio; manage outbound Copilot Studio agents | 4 |
| **I8** | Health and posture | Per-host status, findings, diagnostics, and pending restarts | 2 |
| **I9** | Agent ID | Blueprint and agent identity provisioning in place of the app registration | 5 |
| **I10** | Offboarding | Disable, unpublish, and delete in reverse order; leave nothing that can authenticate | 3 |

### What the Insights code changes about this table

The module was first drafted on seven assumptions about Insights. They were then checked against
the code (section 15). Four held, three did not, and the differences reshape three components:

| Assumption | Result | Effect on the module |
| --- | --- | --- |
| A tenant is the unit of customer isolation and has no Entra tenant id | Half right. It has no Entra tenant id, and it is not really an isolation unit either: users are deployment-wide. | I1 adds the link as new data. Tenant-level authorization for customer administrators does not exist to build on. |
| Settings publish per cluster with a shared base and an environment overlay, and a module can contribute keys | Right about layering. There is no "contribute keys" call: a publish replaces a whole layer. | I5 reads a layer, edits its `settings`, and republishes with a version check. It must track which keys it owns. |
| Preview and adopt exist | Right, as browser-side features only. Nothing is stored and there is no server-side preview service. | I5 cannot hand an operator a saved draft. It needs its own review step. |
| Capabilities and the configuration-state report are stored per host instance | Right, as one raw JSON string per host, latest only. | I8 needs a typed reader. A history of posture does not exist. |
| The connect broker can target a cluster and return a binary body | Right at the broker. The client the pages use returns text only. | I4 and package download need a byte-returning call. |
| A Connections page proxies the host's connection routes | Right. It stores nothing and does not do handoffs. | I6 starts from a working proxy. |
| Tenant Foundry endpoints and keys are stored encrypted and delivered as model configuration | Wrong on scope. They are per cluster, with one deployment-wide management identity. | None. See section 6. |

One thing was not anticipated at all: **the runtime settings catalog already has complete
Microsoft 365 Copilot, A2A, and agent-binding areas**, so an operator can already type every key in
section 7 by hand, including the client secret. I5 is therefore not the first way to publish these
keys. It is a guided, validated way that must coexist with a general form editing the same keys.

## 10. Data model

These records hold identifiers and metadata only. A client secret, when one is used, is not kept
here: it goes into the configuration document as a published setting, where the existing
whole-document encryption and revision history apply. The record keeps only the secret's expiry
date and key id, so the cloud server can warn before it lapses without reading it back.

| Entity | Scope | Fields |
| --- | --- | --- |
| `MicrosoftIntegration` | One per tenant | Entra tenant id and whether it was verified by consent; provisioning mode (`byo`, `managed`); state; consented-by object id and time |
| `MicrosoftIntegrationApp` | One per cluster environment | Integration app client id and object id; service principal object id; application ID URI; credential kind; for a secret, its key id and expiry date; for a federated credential, its id, issuer, and subject; bot resource id; Teams app id and catalog id; the keys this record last published and the configuration version it published them in |
| `MicrosoftIntegrationBinding` | One per published agent | Binding name; agent type; handle; model configuration name; principal strategy; public host name; A2A route name; manifest name, descriptions, and version; agent identity client id (I9) |
| `MicrosoftIntegrationConnection` | One per outbound connection | Name; owner mode (`per-user`, `service principal`); provider; authentication mode; resource name, base URL, and scopes; credential **reference name** |
| `MicrosoftIntegrationHostStatus` | One per host instance | Host instance id; observed time; last status document; last diagnostics report |
| `MicrosoftIntegrationOperation` | Append-only | Actor; operation; target object id; outcome; correlation id |

The tenant record holds only what is true of the whole customer. The app registration is per
environment because an Azure Bot resource has one messaging endpoint, so a test environment and a
production environment cannot share a bot.

`MicrosoftIntegrationHostStatus` is a cache of what the host reported. It is never the source of
truth for what should be configured, and a missing row means "unknown", not "off". Phase 2 does
not need it: heartbeats already store each host's latest report, and status is read live.

Phase 2 as built (section 16) is narrower than this table. It has one `MicrosoftIntegration` row
per tenant, which carries what the first three entities describe for a single environment and a
single agent, and none of the others. The table above remains the target: the per-environment and
per-agent split comes back when a tenant needs a second environment or a second agent, and the
consent fields arrive with managed provisioning.

## 11. Phases

| Phase | Name | Delivers | Exit criterion |
| --- | --- | --- | --- |
| **0** | Tenant validation | The integration guide's how-to run by hand in a real tenant. No product code. | The four roles work on one registration, with a client secret; where the platform supports it, a federated credential authenticates the bot from the host's workload identity; the cross-tenant bot question (D3) is answered |
| **1** | Host observability and enablement | F1, F2, F3, F4, and the F10 documentation | A cloud server can discover, enable, observe, and diagnose the integration using only the open protocol |
| **2** | Bring your own app registration | I5, I8; package download; pending-restart view; the integration record. The customer creates the registration and bot themselves. | A customer with an existing registration reaches a working agent from the cloud server's UI |
| **3** | Managed provisioning | I1, I2, I3, I4, I10; F5; F9; the rest of F10 | A customer with nothing but administrator rights reaches a working agent. F-02 is closed. |
| **4** | Reach out | I6, I7; F6; B1 | A Teams user's agent calls Work IQ as that user with no second consent prompt |
| **5** | Scale and governance | F7, F8; I9; B2 and B9 from the guide | Several agents per cluster; agent identities; turn audit and Purview gate |

Phase 2 comes before managed provisioning on purpose. It needs no standing permission in customer
tenants, it exercises every host-side contract, and it produces the settings composer and status
views that managed provisioning reuses unchanged.

Phase 1 is built in FabrCore (section 14) and Phase 2 is built in Insights (section 16). Phase 2's
exit criterion is not yet met: it has not been run against a real host and a real tenant, and it
needs a FabrCore release that contains Phase 1.

## 12. Decisions

| Id | Decision | Options | Recommendation |
| --- | --- | --- | --- |
| **D1** | Cluster topology | One cluster per customer tenant; or one cluster serving several bots | **One per tenant.** It is what the host supports, and it isolates state and keys. Revisit after F8. |
| **D2** | Integration app credential | A client secret published with the settings; a federated credential to the host's workload identity; a certificate | **Decided, 7 October 2026: publishing keys and secrets to clusters is acceptable.** A client secret is the default because it works on every platform and for Connections today. A federated credential remains available for customers who want the cloud server to hold nothing. |
| **D3** | Where the Azure Bot resource lives | The customer's Azure subscription; or a Vulcan365 subscription referencing the customer's single-tenant app | **Vulcan365 subscription when Vulcan365 hosts the cluster; the customer's when they self-host.** Many Microsoft 365 customers have no Azure subscription. The cross-tenant reference must be proven in Phase 0; if it is not supported, the customer's subscription is the only option and I3 needs a role assignment as well as consent. |
| **D4** | How delegated permissions are consented | The provisioning app writes permission grants; or the administrator follows a second consent link for the integration app | **A second consent link.** Writing grants needs a far broader permission in every customer tenant than managing owned applications does. |
| **D5** | Agent identity | Ordinary app registration now, Entra Agent ID later; or Agent ID from the start | **App registration now.** The data model reserves the agent identity fields. Agent ID acceptance varies per Microsoft API and must be validated before it carries production traffic. |

## 13. Risks

| Risk | Effect | Mitigation |
| --- | --- | --- |
| A bot resource cannot reference an app in another tenant | D3's preferred option fails | Phase 0 answers it before any provisioning code is written |
| The workload-identity issuer changes when a cluster is rebuilt | The federated credential stops matching; the bot goes silent | Record issuer and subject; diagnostics (F4) fails loudly; I2 reconciles the credential |
| The provisioning app is compromised | Standing access in every customer tenant | Owned-applications permission only; customer-side kill switch |
| The cloud server is compromised | The client secret of every customer that uses one is exposed | Accepted (D2). Single-tenant apps bound the damage per customer; a customer can delete the secret; a federated credential for customers who need the cloud server to hold nothing |
| Every integration setting is restart-required | A publish does nothing until hosts restart; operators assume it failed | Pending-restart view in Phase 2; F7 later for the non-identity keys |
| Published secrets are cached in plaintext on hosts | Anyone who can read `fabrcore.cloud-cache.json` on a silo can act as the bot | Accepted (D2). The same exposure the model API keys already have; restrict the file and its volume; rotate by publishing a new value |
| A client secret expires | The bot goes offline with no change on the host | Record the expiry date; warn ahead of it; diagnostics confirms the replacement |
| A rotated secret stays in configuration history | An old revision still contains a working secret until it expires or is deleted in Entra | Delete the old secret in Entra when rotating, not only in configuration |
| F-02 remains open | Provisioning advertises a host whose other routes accept any caller | F9 gates Phase 3; until then the host port stays behind a proxy that forwards only the channel routes |
| Diagnostics passes while the bot is unreachable | False confidence | Document the limit; add an inbound reachability probe on the cloud-server side |
| Copilot Studio sends an application token, not a delegated one | Users reach a shared `app-…` principal instead of their own agent | I7 verifies the principal that arrives and warns before the link is declared healthy |
| Microsoft 365 Agents SDK changes | The addon's configuration bridge depends on the SDK's section names | Pin the version; the bridge already defers to natively configured sections |
| Entra Agent ID surface changes before I9 | Rework | I9 is last; nothing earlier depends on it |
| One cluster per tenant is costly for small customers | Pressure to share clusters | F8 addresses several bots per tenant, not several tenants; state the boundary |

## 14. Phase 1 status

Phase 1 is implemented on `develop` and is not yet in a published package. It was verified by the
Host, Copilot add-on, and Connections test suites, including HTTP tests that run the administration
routes against a real FabrCore server.

| Item | Status | What shipped | Where |
| --- | --- | --- | --- |
| **F1** | Done | `IFabrCoreCapabilityContributor`; an `a2a` service that is always listed; contributed services and heartbeat flags from the Copilot add-on and remote agents, registered even when disabled | `FabrCore.Core/Services/Capabilities`, `FabrCore.Host/Services/ClusterCapabilityFactory.cs`, `CloudServerSyncService.BuildCapabilities` |
| **F2** | Done | `AddFabrCoreConnections(services, configuration)` and `AddFabrCoreRemoteAgents(services, configuration)`; catalog descriptors for both sections; the Copilot add-on starts off and records why when unconfigured | `ConnectionsExtensions.cs`, `RemoteAgent.cs`, `FabrCoreSettingsCatalog.cs`, `Microsoft365CopilotExtensions.cs` |
| **F3** | Done | `RuntimeSettingObservation.DefaultValue` and `code-default` resolution; channel and A2A settings in the configuration-state report | `RuntimeConfigurationState.cs`, `BuiltInRuntimeObservations.cs`, `Administration/CopilotHostContributors.cs` |
| **F4** | Done | `GET integrations/a2a`; `GET integrations/microsoft365`, `/manifest`, `/app-package`; `POST …/diagnostics` | `IntegrationsAdminController.cs`, `A2AIntegrationStatusBuilder.cs`, `Administration/CopilotIntegrationEndpoints.cs` |
| **F10** | Documentation done | The protocol section, the administration section, the add-on README, the connections guide, and release notes. Reference cloud server support and conformance tests are not started. | `docs/`, `RELEASE_NOTES.md` |

Things a cloud-server implementer should know that were not obvious before the work was done:

* **`Microsoft365Copilot:TokenValidation:Enabled` is reported redacted.** The settings catalog
  treats any key containing `token` as a secret, so this row has `secret: true` and no values. Its
  `pendingRestart` flag is still correct. Read the value from `tokenValidationEnabled` on the
  status route, or from the `token-validation-off` capability feature. A dedicated `Runtime:` fact
  with a name the catalog does not redact would close the gap; it was left out pending a decision
  on the name.
* **`streaming` is advertised only when streaming is on.** The channel lists the feature when
  `Streaming:Enabled` is true, which is the default, rather than whenever the channel is enabled.
* **A2A settings that nothing consumes are not claimed as applied.** `A2A:Authentication:Mode`,
  `A2A:Principal:Strategy`, and `A2A:PublicBaseUrl` have `appliedKnown: false` while A2A is off.
  `A2A:Enabled` and `A2A:Discovery:AgentTypes` keep their earlier behavior and are always reported
  as applied.
* **Diagnostics are audited under the existing category.** Runs are recorded as
  `RemoteAdministration` events. The dedicated `IntegrationManagement` category is part of F9.
* **`user-credential-forwarding` is a `fail`, not a `warn`,** when a user token is actually being
  copied onto messages. It is a `warn` when the setting is on but no sign-on handler exists, since
  nothing is forwarded yet.
* **The status route reports option defaults for an unconfigured channel.** `configured: false`
  is what says that nobody chose them.
* **Connections is absent, not unavailable, when disabled.** Unlike the Copilot add-on and remote
  agents, a disabled Connections registers nothing, so the capability document does not list it.
  A cloud server cannot tell "Connections package installed but off" from "not installed". This
  matters for I6 and is cheap to change if needed.

Not done in Phase 1, by design: nothing provisions Microsoft objects, nothing is live-reloadable,
and none of the compliance-plane items (B1, B2, B4, B9) are started.

## 15. Insights as built

The Insights code in `FabrCore-V365` was read at commit `7d851b0` to check what this plan assumed
about it. Paths are relative to that repository's `src/`. Each statement below was confirmed in
the code, not taken from Insights documentation, which disagrees with the code in a few places.

### 15.1 Multi-tenancy

* A tenant is the row `insights.Tenant`: `TenantId`, a unique `TenantKey`, `DisplayName`, `Status`,
  and a free-text `Metadata` column (`FabrCore.Insights/Migrations/M001_BaselineSchema.cs`). **It
  has no Entra tenant id**, and nothing else in the schema links a tenant to a customer directory.
* The hierarchy is tenant, cluster, cluster environment, host. Host rows are keyed by the
  host-reported instance id, which is new on every process start.
* **Users are not tenant-bound.** `insights.InsightsUser` is keyed by Entra object id for the whole
  deployment. Operators sign in through single-tenant OpenID Connect against one configured
  `AzureAd:TenantId` (`FabrCore.Insights.App/Program.cs`), and new users are looked up in that same
  directory. A customer administrator cannot sign in to Insights today.
* Scope checks exist (`IInsightsScopeAuthorizer`), reading `insights.InsightsUserScope`. **No code
  writes that table.** In practice administrators see everything and operators see nothing unless a
  row is inserted by hand.
* Data access is Dapper with hand-written SQL, and schema changes are numbered C# migrations
  (`M001` to `M011`). There are no query filters; scoping is explicit ids plus authorizer calls.

What this corrects: "each Insights tenant maps to one customer Entra tenant" describes a link that
must be built, and the people who would manage a customer integration are Vulcan365 operators, not
the customer. The consent step in managed provisioning (I1) can still be completed by a customer
administrator, because consent is a link they follow rather than a session in Insights.

### 15.2 Settings publish, preview, and adopt

* Published configuration is the append-only table `insights.ConfigDocument`, one row per cluster,
  environment, and version. The shared layer uses an empty environment id. The body holds models,
  credentials, a flat `settings` map of strings, and blueprints. The whole body is encrypted at
  rest with Data Protection.
* The effective configuration merges the shared layer with the environment overlay: settings merge
  by key with the environment winning, and an explicit null masks a shared value
  (`FabrCore.Insights/Services/ConfigurationResolver.cs`). `configurationVersion` is the SHA-256 of
  the merged document.
* **A module can publish programmatically.** `IConfigDocumentService.PublishAsync(clusterId,
  environmentName, bodyJson, actor, comment, ct, expectedVersion, reviewedVersions)` is the call,
  and the Foundry integration, the gateway, and the configuration assistant already use it. A
  publish **replaces the whole layer body**; there is no call that contributes a few keys. With
  `expectedVersion` a concurrent change is rejected; without it the last writer wins.
* The server rejects the three enrollment key families, malformed keys, and oversized payloads at
  publish (`CloudConfigurationPolicy`). **Typed validation is in the Blazor app only**
  (`RuntimeCatalog.Validate`, reached through `ConfigurationDraft`). A direct service or REST
  publish can store `A2A:Enabled = "yes"`.
* **Drafts are not stored.** A draft is a JSON object held by the page for the life of the browser
  circuit. A module cannot leave a pending draft for an operator to review later.
* **Preview and adopt are page features.** `RuntimeSettingsForm.razor` posts the flat settings of
  the draft to the selected host at `settings/preview`, keeps the result only while the draft is
  unchanged, and reads `settings/state` for the live report. Adopt copies one applied value into
  the open draft. Neither is a server-side service, and nothing is persisted.
* The runtime settings catalog (`FabrCore.Insights.App/Configuration/RuntimeCatalog.cs`) **already
  has form areas for A2A, Microsoft 365 Copilot, and Integrations** (agent bindings and connection
  credentials). It marks everything in them restart-required. It has no entry for
  `FabrCore:Connections` or `FabrCore:RemoteAgents`, which did not exist as settings until Phase 1.
* The catalog offers `Microsoft365Copilot:ClientSecret`, A2A API key values, and
  `FabrCore:ConnectionCredentials` as secret fields. A value entered there is delivered in the
  `settings` map to every host in scope and kept in every later revision. This is the mechanism
  section 3 relies on for the client-secret credential, so the composer can reuse it as it is.

### 15.3 The Connections page

`FabrCore.Insights.App/Components/Pages/EnvironmentConnections.razor` is a working proxy to the
connection administration routes on the host, shown when an environment is selected. It lists the
connections of a principal, reads and saves a profile with `If-Match`, and clears authorization. It
stores nothing in the Insights database. It does not call the handoff routes, cannot delete a
profile, and cannot target one host instance. It decides whether to show itself by looking for a
capability service named `connections`.

### 15.4 Connect broker and command leases

* `IConnectCommandService.EnqueueAndWaitAsync(clusterId, environment, request, actor, ct)` queues a
  command in `insights.ConnectCommand` and polls until a host answers. It is SQL-backed and safe
  across replicas. There is no fire-and-forget form.
* **One host instance can be targeted.** `InsightsConnectRequest.TargetHostInstanceId` restricts
  which host may lease the command. The target is not checked against the host roster, so a stale
  instance id simply times out.
* A command lives 45 seconds and a lease 30. Bodies are capped at 4 MiB each way. Only `GET` is
  re-leased after a lease expires.
* The broker is binary-safe. The client the pages use is not:
  `IInsightsEnvironmentAdminClient.SendAsync` decodes the response body as UTF-8 text. A zip
  download needs a new call that returns bytes.
* The broker sets `X-FabrCore-Admin-Actor` itself from the signed-in operator and forwards only
  `Accept`, `Content-Type`, `If-Match`, and `If-None-Match` from the caller.
* `/fabrcoreapi/admin/v1` is on the broker allowlist, so the integration routes pass.
* **Command rows are never deleted**, and request and response bodies are stored unencrypted.

### 15.5 Per-host reports

* Each heartbeat is validated, redacted, and stored whole. `insights.ClusterSilo` keeps the latest
  payload per host instance in a column named `Capabilities`, which in fact holds the entire
  heartbeat JSON. `insights.ClusterHeartbeat` keeps the latest per environment.
* The capability flags are at `$.capabilities` inside that string. There is no typed accessor and
  no page that lists them.
* The configuration-state report is kept per host instance, latest only, replaced when the boot id
  matches and the sequence is higher. There is no history.
* Rows whose key matches a name heuristic (`apikey`, `secret`, `password`, `connectionstring`,
  `token`) have their values removed before storage. That includes
  `Microsoft365Copilot:TokenValidation:Enabled` and `UserAuthorization:PassUserTokenToAgent`.
* **A pending-restart view already exists** per host, per environment, and as a cluster count, fed
  by `pendingRestartSettings`. What does not exist is one filtered to the keys of a feature.
* Reports are kept until an operator purges stale hosts.

### 15.6 AI Foundry endpoints and keys

* A Foundry connection is stored per **cluster** in `insights.ProviderIntegration`, keyed by cluster
  and provider, with the project endpoint and key encrypted as one document.
* One deployment-wide management identity, a single row in `insights.FoundryManagement`, is shared
  by every cluster and tenant.
* Keys reach hosts only when an operator applies a deployment to a cluster. That writes a model
  configuration and a matching API key into the configuration document of the cluster, and hosts
  then receive both in the `configuration` element of the envelope.
* The gateway, which keeps provider keys in Insights and gives hosts a caller key instead, is off
  by default and enabled per cluster.

### 15.7 What follows

* Follow the Foundry integration as the template: contracts, a numbered migration, an internal
  service with revision-checked writes and audit entries, a controller, and a section inside the
  cluster workspace page rather than a route of its own. The preview host needs a stub for every
  new service or it stops building.
* The cluster workspace already uses the section id `integrations` and the label "AI provider
  integrations" for Foundry. The new section needs a different id and an unambiguous name.
* Insights has no Microsoft Graph, ARM, or MSAL client library and no consent flow. Its two
  existing Microsoft calls are hand-written token requests. Managed provisioning starts from that.
* Insights has one secret abstraction, a cipher for whole JSON documents, and the configuration
  document already uses it. Phase 2 puts the client secret there, as a setting, so it needs nothing
  more.

## 16. Phase 2: bring your own app registration

**Status: built in Insights (`main`, October 2026).** The decisions in 16.5 were taken before the
work started, and this section describes what was built. The operator documentation is
`docs/insights-microsoft-365.md` in the Insights repository.

The customer creates the app registration and the Azure Bot resource themselves, following a
checklist. Insights records the identifiers, composes and publishes the settings, including the
client secret when the customer uses one, and shows whether each host has applied them and is
healthy. It needs no permission in the customer tenant.

### 16.1 What an operator does

1. Opens **Microsoft 365** in the cluster workspace, with an environment selected.
2. Enters the Entra tenant id of the customer, the client id of the app, its client secret and the
   date the secret expires, and the public name of the host; picks the agent type and model
   configuration to publish; and chooses whether to enable A2A. A customer using a federated
   credential leaves the secret empty.
3. Reads the checklist of what the customer must have created, with every value to paste already
   computed: the messaging endpoint URL, the Application ID URI, the scope name, and the A2A
   endpoint.
4. Reviews the exact keys that will be added, changed, and removed, and publishes.
5. Sees which hosts are waiting on a restart, and restarts them by whatever means the deployment
   uses.
6. Runs diagnostics per host and reads the findings.
7. Downloads the app package and gives it to the customer administrator.

### 16.2 The five parts

**1. Integration record.** One table, `insights.MicrosoftIntegration`, added by migration `M012`
and keyed by `TenantId`. It holds the cluster and environment the integration is published to, a
revision, the configuration version it last published, the updated time and actor, and a JSON body
with the settings and the list of keys the record owns. The settings are the Entra tenant id,
client id, auth type, secret expiry date, public host name, binding name, agent type, handle,
model configuration name, manifest name, description and developer details, and the A2A switch
and route name.

There is one record per tenant (P1), so a tenant publishes one agent to one environment. Another
environment of the same tenant sees only where the integration lives. Moving it means removing it
there and publishing it again. If its environment or cluster is removed, the stored values are
offered so it can be published elsewhere.

The client secret itself is not stored in the record. It is written straight into the `settings` of
the environment layer when the operator publishes, where the configuration document is already
encrypted at rest, and the form never reads it back. The table is in the tenant purge used by the
test fixture. It is not in the tenant archive (16.3).

**2. Settings composer.** A pure function from the settings to a flat map of string settings, plus
a service that applies it.

* It emits the keys in section 7 for the channel, the binding, and optionally A2A, with
  `Principal:Strategy` fixed to `CanonicalEntra` on both channels. For A2A it sets the audience to
  the client id, adds `api://botid-{clientId}` as a second valid audience, requires the
  `agent.invoke` scope, and takes the first `A2A:Agents` index that no layer already uses.
* With `AuthType = ClientSecret` it publishes `Microsoft365Copilot:ClientSecret`. An empty secret
  field on a later edit means "keep the published value", so changing a manifest name does not
  require typing the secret again. With a secret-free auth type it publishes no secret and removes
  one it published before.
* It validates before publishing, because a bad channel configuration stops the host from starting:
  tenant id and client id are GUIDs, the public name is a host name, the auth type is one the
  Agents SDK accepts, a secret is present when the auth type needs one, the model configuration
  exists in the effective configuration, and neither `SharedAgentHandle` nor
  `AgentPerConversation` is set alongside the binding. The agent type is not checked at publish;
  the host's diagnostics confirms it.
* It warns when the recorded secret expiry is within 30 days, and says so on the page from then on.
* Applying reads the environment layer, removes the keys the record published last time, writes the
  new ones into `settings`, and calls `IConfigDocumentService.PublishAsync` with `expectedVersion`.
  It edits the JSON tree in place, as the Foundry integration does, so fields it does not know
  survive. The record's revision and the layer's version are both checked, and the record is
  written only after the configuration publish succeeds.
* A review step returns the added, changed, and removed keys for the operator to confirm, with the
  secret masked. Nothing is stored between review and publish.
* The keys the record owns are locked (P2). The runtime settings form shows them read-only with a
  link to the Microsoft 365 page, and the general configuration publish rejects a body that
  changes one, whichever editor or API client produced it. Shared defaults are never locked.
* It still detects drift, for the one path the lock does not cover: restoring an older
  configuration version. The page names the keys and republishing puts them back.

**3. Per-host status and diagnostics.** Read live, stored nowhere.

* The host list comes from the stored heartbeats, with a typed reader for the `m365copilot`,
  `m365copilot.enabled`, and `a2a` flags.
* Status calls `GET integrations/microsoft365` and `GET integrations/a2a` for one host instance
  through `IInsightsEnvironmentAdminClient`, which already supports instance targeting and returns
  JSON as text.
* Diagnostics calls `POST integrations/microsoft365/diagnostics` for one host instance, on demand.
* The host reports a client secret as a `credential-type` warning. When the record says the
  customer uses a client secret, the page shows that finding as accepted and does not count it
  against the host. The host's own report is unchanged.
* A host whose heartbeat has no `m365copilot` flag is shown as not reporting the add-on, not as
  broken. A host that reports a client id other than the published one is called out.

**4. App package download.** One controller action that sends
`GET integrations/microsoft365/app-package` to a host through the connect command service, which
already carries binary bodies, and streams the answer to the browser as `appPackage.zip`, with the
same environment authorization as the rest. The environment admin client is unchanged. The page
offers the download only for a connected host that reports the channel on and has no owned key
pending restart, because the package would otherwise describe the old manifest.

**5. Pending-restart view.** The existing per-host pending-restart data, filtered to the keys the
record owns, shown at the top of the page and per host. No new storage.

### 16.3 What it deliberately leaves out

* Creating anything in the customer tenant or in Azure (I1 to I4).
* Publishing the Teams app to the organization catalog. The administrator uploads the package.
* Connections, remote agents, and the Copilot Studio link beyond showing the A2A values to paste
  (I6, I7).
* Verifying the Entra tenant id. It is shown as entered, not verified.
* Restarting hosts.
* Any stored history of status or diagnostics.
* A second environment or a second agent for the same tenant (P1).
* The tenant archive. An exported tenant carries the published settings in its configuration but
  not the record; publishing once from the page in the destination adopts them again.
* Guarding configuration rollback. A rollback that reverts owned keys is reported as drift.

### 16.4 Prerequisites

* **A FabrCore release containing Phase 1.** Insights references FabrCore 2.0.1 packages; the
  integration routes, the flags, and the configuration-bound registrations are on `develop` only.
  Insights defines its own wire types for the status documents, as it does for the
  configuration-state report, so it builds against 2.0.1. The page has nothing to show for a host
  until that host runs the newer release.
* **A customer host that calls `AddMicrosoft365Copilot()`.** Publishing settings cannot install the
  add-on.

### 16.5 Decisions

| Id | Decision | Outcome |
| --- | --- | --- |
| **P1** | Record shape: one tenant record plus one app record per environment, or a single per-tenant record | **Decided, October 2026: one record per tenant.** The recommendation was tenant plus per-environment, because a bot has one messaging endpoint and two environments cannot share an app registration. The simpler shape was chosen; it allows one environment per tenant, and section 10's per-environment table returns when a tenant needs a second. |
| **P2** | Keys the record owns, in the runtime settings form: locked, or editable with drift detection | **Decided, October 2026: locked in the form**, and enforced on the server. Drift detection stays for configuration rollback. |
| **P3** | The catalog fields for `Microsoft365Copilot:ClientSecret` and A2A key values | **Decided, 7 October 2026: keep them.** Publishing keys and secrets to clusters is acceptable (D2), and the composer uses the same mechanism. |
| **P4** | Status and diagnostics: live only, or also stored | **Live only.** Heartbeats already store the at-rest view. Add storage when there is a reason to show history. |
| **P5** | Where the work lands in the Insights repository | **Decided, October 2026: committed to `main`.** The repository has no `develop` branch. |
| **P6** | Whether customer administrators will use this page | **Not in Phase 2.** Insights sign-in is single-tenant and scopes are not assignable. The page is an operator tool, guarded by the administrator policy like the pages beside it. |
| **P7** | The host's `credential-type` warning for a client secret | **Decided, October 2026: the host keeps reporting it as a warning; Insights shows it as accepted** when the integration uses a client secret. |

### 16.6 Size and tests

One migration, one contracts file, a composer and a service, one controller, one workspace
section, a lock in the runtime settings form and its collection editor, a guard in the general
configuration publish, and one preview stub.

* Unit: the composer output for each option; the secret is published for `ClientSecret`, kept when
  the field is left empty, and removed on a switch to a secret-free auth type; a replaced secret is
  reported without either value; owned-key removal on change; drift detection; the typed heartbeat
  flag reader; how findings, expiry, and pending restarts are shown; the locked fields in the
  runtime settings form; the page in each state.
* Integration (SQL): publish writes the settings and keeps the secret out of the record and the
  audit entry; a republish touches only what changed and an unchanged one creates no configuration
  version; a stale review is rejected; the general publish rejects a change to an owned key; drift
  is reported; a second environment of the same tenant is refused; removal; every call authorizes
  the actor for the environment.
* Protocol: the package action returns the bytes the host sent, under a file name, from the
  instance asked; explains why a host could not provide it; and refuses an operator without access
  to the environment before anything is sent.
* Against a real host: not yet run. The broker is faked in the tests, and the page was exercised in
  the preview host, whose integration service is an in-memory stub. The captured responses in the
  [cloud server how-to](cloud-server-microsoft-365-copilot.md) are the shapes the page reads.
