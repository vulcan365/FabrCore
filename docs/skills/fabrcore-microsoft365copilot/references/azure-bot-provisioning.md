# Azure Bot Provisioning Reference

Everything the Microsoft 365 Copilot channel needs on the Azure side, per exposed agent: one
Entra app registration (the bot's identity) and one Azure Bot resource whose messaging endpoint
points at that agent's route on the FabrCore host, with the Teams channel enabled. The FabrCore
host itself can run anywhere (Azure, on-prem, another cloud) — only the HTTPS endpoints must be
reachable by Azure Bot Service.

An Azure Bot resource has one messaging endpoint and one app id, and an app manifest holds one
bot. A host that exposes several agents therefore repeats everything below once per entry in
`Microsoft365Copilot:Agents`. An entry's route is its `Route`, which defaults to
`/api/messages/{Name}`; one entry may claim the bare `/api/messages`.

## Quick path (script)

Run `assets/provision-azure-bot.ps1` once per agent, or the equivalent CLI:

```powershell
./provision-azure-bot.ps1 -AgentName assistant -DisplayName "Assistant" -ResourceGroup my-agents-rg `
    -BotName contoso-assistant -MessagingEndpoint "https://<public-host>/api/messages"
./provision-azure-bot.ps1 -AgentName sales -DisplayName "Sales Agent" -ResourceGroup my-agents-rg `
    -BotName contoso-sales -MessagingEndpoint "https://<public-host>/api/messages/sales"
```

Each run prints an `Agents` entry to paste into `fabrcore.json`.

```bash
az login
APP_ID=$(az ad app create --display-name "My FabrCore Agent" --sign-in-audience AzureADMyOrg --query appId -o tsv)
SECRET=$(az ad app credential reset --id $APP_ID --query password -o tsv)
TENANT_ID=$(az account show --query tenantId -o tsv)

az group create -n my-agents-rg -l eastus
az bot create -g my-agents-rg -n my-fabrcore-agent --app-type SingleTenant \
  --appid $APP_ID --tenant-id $TENANT_ID \
  --endpoint "https://<public-host>/api/messages" --sku F0
az bot msteams create -g my-agents-rg -n my-fabrcore-agent
```

`TENANT_ID` maps to `Microsoft365Copilot:TenantId`. `APP_ID` / `SECRET` map to the agent's
`Microsoft365Copilot:Agents:{n}:ClientId` / `ClientSecret`, and the path of `--endpoint` is that
entry's `Route`.

## App identity types

| `--app-type` / addon `AuthType` | When |
|---|---|
| `SingleTenant` + `ClientSecret` (default) | Simplest; bot serves one tenant |
| `SingleTenant` + `Certificate` / `CertificateSubjectName` | Secretless; set `CertificateThumbprint` or `CertificateSubjectName` (+ optional `CertificateStoreName`, default `My`) |
| `MultiTenant` | Bot serves multiple tenants. Omit `TenantId` or set `AuthorityEndpoint` to `https://login.microsoftonline.com/botframework.com`; use `Principal:Strategy: TenantAndObjectId` |
| `UserAssignedMSI` / `SystemManagedIdentity` | Host runs in Azure with a managed identity; no secret. Set `FederatedClientId` for user-assigned |
| `FederatedCredentials` | App registration with a federated credential backed by a managed identity (`FederatedClientId`) |
| `WorkloadIdentity` | AKS workload identity; set `FederatedTokenFile` |

`AuthType` and its inputs are set at the top level as the default for every agent, and may be
overridden on an `Agents` entry.

The addon translates these into the Microsoft 365 Agents SDK `Connections` configuration, one
connection per agent.
Hosts that already maintain a native `Connections` section keep full control — the addon leaves
it untouched.

## Dev tunnels (local machine behind a real channel)

```bash
devtunnel user login
devtunnel host -p 5000 --allow-anonymous
az bot update -g my-agents-rg -n my-fabrcore-agent --endpoint "https://<tunnel-host>/api/messages"
```

Keep `TokenValidation:Enabled: true` — Azure Bot Service still sends real JWTs through the
tunnel. `--allow-anonymous` refers to the tunnel transport, not the bot auth.

## Verifying the pipeline

1. Host logs on startup, per agent: `Microsoft 365 Copilot agent assistant ready: endpoint
   /api/messages, bot …, agent type …`, then `Microsoft 365 Copilot channel ready: 1 agent(s),
   token validation on …`.
2. Azure portal → the bot resource → *Test in Web Chat* exercises the endpoint end-to-end
   (Web Chat users have no Entra identity — expect the identity-rejection message unless
   `Principal:AllowChannelIdFallback: true`).
3. 401s in host logs mean the token's audience/issuer didn't match any agent's
   `ClientId`/`TenantId`. A 403 means the token is valid but was issued for a different bot than
   the one that owns the route: the Azure Bot's messaging endpoint points at another agent's
   route.
4. `Unauthorized` responses *to* Azure Bot Service (bot can't reply) mean outbound credentials
   are wrong — check `ClientSecret`/certificate and `AuthType`.

## Bicep (infrastructure as code)

```bicep
resource bot 'Microsoft.BotService/botServices@2022-09-15' = {
  name: botName
  location: 'global'
  sku: { name: 'F0' }
  kind: 'azurebot'
  properties: {
    displayName: botName
    msaAppId: appId
    msaAppType: 'SingleTenant'
    msaAppTenantId: tenantId
    endpoint: 'https://${publicHost}${route}'   // the agent's Route, e.g. '/api/messages/sales'
  }
}

resource teamsChannel 'Microsoft.BotService/botServices/channels@2022-09-15' = {
  parent: bot
  name: 'MsTeamsChannel'
  location: 'global'
  properties: {
    channelName: 'MsTeamsChannel'
    properties: { isEnabled: true }
  }
}
```
