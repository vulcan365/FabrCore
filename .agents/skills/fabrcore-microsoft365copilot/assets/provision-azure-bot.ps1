<#
.SYNOPSIS
    Provisions the Azure resources for a FabrCore Microsoft 365 Copilot agent:
    Entra app registration + client secret, Azure Bot resource wired to /api/messages,
    and the Teams channel (which also carries Microsoft 365 Copilot traffic).

.NOTES
    Requires: Azure CLI (az) logged in with rights to create app registrations and bots.
    The printed TenantId/ClientId/ClientSecret go into the Microsoft365Copilot section
    of fabrcore.json. Prefer certificates or managed identity over client secrets in
    production (set Microsoft365Copilot:AuthType accordingly).
#>
param(
    [Parameter(Mandatory = $true)]
    [string]$DisplayName,                       # e.g. "My FabrCore Agent"

    [Parameter(Mandatory = $true)]
    [string]$ResourceGroup,                     # e.g. "my-agents-rg"

    [Parameter(Mandatory = $true)]
    [string]$BotName,                           # globally unique bot resource name

    [Parameter(Mandatory = $true)]
    [string]$MessagingEndpoint,                 # e.g. "https://myhost.example.com/api/messages"

    [string]$Location = "eastus",
    [ValidateSet("F0", "S1")]
    [string]$Sku = "F0"
)

$ErrorActionPreference = "Stop"

Write-Host "Creating Entra app registration '$DisplayName' (SingleTenant)..."
$appId = az ad app create --display-name $DisplayName --sign-in-audience AzureADMyOrg --query appId -o tsv
$secret = az ad app credential reset --id $appId --query password -o tsv
$tenantId = az account show --query tenantId -o tsv

Write-Host "Ensuring resource group '$ResourceGroup'..."
az group create -n $ResourceGroup -l $Location -o none

Write-Host "Creating Azure Bot '$BotName' -> $MessagingEndpoint ..."
az bot create --resource-group $ResourceGroup --name $BotName --app-type SingleTenant `
    --appid $appId --tenant-id $tenantId --endpoint $MessagingEndpoint --sku $Sku -o none

Write-Host "Enabling the Teams channel (carries Teams + Microsoft 365 Copilot traffic)..."
az bot msteams create --resource-group $ResourceGroup --name $BotName -o none

Write-Host ""
Write-Host "Done. Put these in the Microsoft365Copilot section of fabrcore.json:"
Write-Host "  TenantId:     $tenantId"
Write-Host "  ClientId:     $appId"
Write-Host "  ClientSecret: $secret"
Write-Host ""
Write-Host "To retarget the endpoint later (e.g. a dev tunnel):"
Write-Host "  az bot update -g $ResourceGroup -n $BotName --endpoint https://<host>/api/messages"
