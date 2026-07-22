# Azure Bot Provisioning Reference

Everything the Microsoft 365 Copilot channel needs on the Azure side: one Entra app
registration (the bot's identity) and one Azure Bot resource whose messaging endpoint points at
the FabrCore host's `/api/messages`, with the Teams channel enabled. The FabrCore host itself can
run anywhere (Azure, on-prem, another cloud) — only the HTTPS endpoint must be reachable by Azure
Bot Service.

## Quick path (script)

Run `assets/provision-azure-bot.ps1`, or the equivalent CLI:

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

`TENANT_ID` / `APP_ID` / `SECRET` map to `Microsoft365Copilot:TenantId` / `ClientId` /
`ClientSecret`.

## App identity types

| `--app-type` / addon `AuthType` | When |
|---|---|
| `SingleTenant` + `ClientSecret` (default) | Simplest; bot serves one tenant |
| `SingleTenant` + `Certificate` / `CertificateSubjectName` | Secretless; set `CertificateThumbprint` or `CertificateSubjectName` (+ optional `CertificateStoreName`, default `My`) |
| `MultiTenant` | Bot serves multiple tenants. Omit `TenantId` or set `AuthorityEndpoint` to `https://login.microsoftonline.com/botframework.com`; use `Principal:Strategy: TenantAndObjectId` |
| `UserAssignedMSI` / `SystemManagedIdentity` | Host runs in Azure with a managed identity; no secret. Set `FederatedClientId` for user-assigned |
| `FederatedCredentials` | App registration with a federated credential backed by a managed identity (`FederatedClientId`) |
| `WorkloadIdentity` | AKS workload identity; set `FederatedTokenFile` |

The addon translates these into the Microsoft 365 Agents SDK `Connections` configuration.
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

1. Host logs on startup: `Microsoft 365 Copilot channel ready: endpoint /api/messages, agent …,
   token validation on …`.
2. Azure portal → the bot resource → *Test in Web Chat* exercises the endpoint end-to-end
   (Web Chat users have no Entra identity — expect the identity-rejection message unless
   `Principal:AllowChannelIdFallback: true`).
3. 401s in host logs mean the token's audience/issuer didn't match `ClientId`/`TenantId`.
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
    endpoint: 'https://${publicHost}/api/messages'
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
