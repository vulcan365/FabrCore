<#
.SYNOPSIS
    Provisions the Azure resources for one FabrCore Microsoft 365 Copilot agent:
    Entra app registration + client secret, Azure Bot resource wired to the agent's
    messaging route, and the Teams channel (which also carries Microsoft 365 Copilot traffic).

.DESCRIPTION
    Each agent listed in Microsoft 365 Copilot needs its own app registration and Azure Bot,
    because a bot has one messaging endpoint and one app id. Run this script once per entry
    in Microsoft365Copilot:Agents, with that entry's name and messaging endpoint.

.EXAMPLE
    ./provision-azure-bot.ps1 -AgentName assistant -DisplayName "Assistant" -ResourceGroup my-agents-rg `
        -BotName contoso-assistant -MessagingEndpoint "https://myhost.example.com/api/messages"

.EXAMPLE
    ./provision-azure-bot.ps1 -AgentName sales -DisplayName "Sales Agent" -ResourceGroup my-agents-rg `
        -BotName contoso-sales -MessagingEndpoint "https://myhost.example.com/api/messages/sales"

.NOTES
    Requires: Azure CLI (az) logged in with rights to create app registrations and bots.
    The printed entry goes into the Agents list of the Microsoft365Copilot section of
    fabrcore.json. Prefer certificates or managed identity over client secrets in
    production (set AuthType accordingly, at the top level or on the entry).
#>
param(
    [Parameter(Mandatory = $true)]
    [ValidatePattern('^[A-Za-z0-9][A-Za-z0-9_-]{0,62}$')]
    [string]$AgentName,                         # Name of the Agents entry, e.g. "sales"

    [Parameter(Mandatory = $true)]
    [string]$DisplayName,                       # e.g. "My FabrCore Agent"

    [Parameter(Mandatory = $true)]
    [string]$ResourceGroup,                     # e.g. "my-agents-rg"

    [Parameter(Mandatory = $true)]
    [string]$BotName,                           # globally unique bot resource name

    [Parameter(Mandatory = $true)]
    [string]$MessagingEndpoint,                 # e.g. "https://myhost.example.com/api/messages/sales"

    [string]$Location = "eastus",
    [ValidateSet("F0", "S1")]
    [string]$Sku = "F0"
)

$ErrorActionPreference = "Stop"

# The path of the messaging endpoint is the entry's Route.
$endpointUri = $null
if (-not [Uri]::TryCreate($MessagingEndpoint, [UriKind]::Absolute, [ref]$endpointUri) -or $endpointUri.Scheme -ne "https") {
    throw "MessagingEndpoint must be an absolute https URL, for example https://myhost.example.com/api/messages/$AgentName"
}
$route = $endpointUri.AbsolutePath.TrimEnd('/')
if (-not $route) {
    throw "MessagingEndpoint must include the agent's route, for example https://$($endpointUri.Host)/api/messages/$AgentName"
}

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
Write-Host "Done. Set Microsoft365Copilot:TenantId to $tenantId and add this entry to"
Write-Host "Microsoft365Copilot:Agents in fabrcore.json (fill in AgentType):"
Write-Host "  {"
Write-Host "    `"Name`": `"$AgentName`","
Write-Host "    `"Route`": `"$route`","
Write-Host "    `"ClientId`": `"$appId`","
Write-Host "    `"ClientSecret`": `"$secret`","
Write-Host "    `"AgentType`": `"<your-agent-alias>`""
Write-Host "  }"
Write-Host ""
Write-Host "To retarget the endpoint later (e.g. a dev tunnel):"
Write-Host "  az bot update -g $ResourceGroup -n $BotName --endpoint https://<host>$route"
