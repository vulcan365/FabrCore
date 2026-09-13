#!/usr/bin/env pwsh

[CmdletBinding()]
param(
    [ValidateNotNullOrEmpty()]
    [string]$LocalFeed,

    [string]$Version,

    [switch]$DryRun
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

$nugetSource = "https://api.nuget.org/v3/index.json"
$nugetApiKey = $env:NUGET_API_KEY
. (Join-Path $PSScriptRoot 'Build-Common.ps1')
if (-not $LocalFeed) { $LocalFeed = Get-DefaultPackageFeed }
if ($Version) { Assert-PackageVersion $Version }
$expectedPackageIds = $FabrCoreBuild.Packages

function Get-PackagePublishEndpoint {
    $serviceIndex = Invoke-RestMethod -Uri $nugetSource -Method Get
    $publishResource = @($serviceIndex.resources) |
        Where-Object { @($_.'@type') -match "^PackagePublish/" } |
        Select-Object -First 1

    if ($null -eq $publishResource -or [string]::IsNullOrWhiteSpace($publishResource.'@id')) {
        throw "NuGet.org did not advertise a PackagePublish endpoint."
    }

    return $publishResource.'@id'.TrimEnd("/")
}

function Invoke-NuGetUnlist {
    param(
        [Parameter(Mandatory)]
        [string]$PublishEndpoint,

        [Parameter(Mandatory)]
        [string]$PackageId,

        [Parameter(Mandatory)]
        [string]$PackageVersion,

        [Parameter(Mandatory)]
        [string]$ApiKey,

        [int]$MaximumAttempts = 3
    )

    $escapedId = [uri]::EscapeDataString($PackageId)
    $escapedVersion = [uri]::EscapeDataString($PackageVersion)
    $unlistUri = "$PublishEndpoint/$escapedId/$escapedVersion"
    $headers = @{
        "X-NuGet-ApiKey" = $ApiKey
    }

    for ($attempt = 1; $attempt -le $MaximumAttempts; $attempt++) {
        try {
            Invoke-WebRequest `
                -Uri $unlistUri `
                -Method Delete `
                -Headers $headers `
                -UseBasicParsing | Out-Null
            return
        }
        catch {
            if ($attempt -eq $MaximumAttempts) {
                throw "Failed to unlist $PackageId $PackageVersion after $MaximumAttempts attempts. " +
                    "The package may still be listed. Rerun this script to retry. $($_.Exception.Message)"
            }

            $delaySeconds = [math]::Pow(2, $attempt)
            Write-Warning "Unlisting $PackageId failed (attempt $attempt of $MaximumAttempts). Retrying in $delaySeconds seconds."
            Start-Sleep -Seconds $delaySeconds
        }
    }
}

if (-not (Test-Path -LiteralPath $LocalFeed -PathType Container)) {
    throw "Local NuGet feed does not exist: $LocalFeed"
}

if (-not (Get-Command dotnet -ErrorAction SilentlyContinue)) {
    throw "dotnet was not found on PATH."
}

$packageMetadata = @(Get-FeedPackageMetadata -LocalFeed $LocalFeed |
    Where-Object { $_.Id -in $expectedPackageIds })
if ($packageMetadata.Count -eq 0) { throw "No supported FabrCore packages were found in $LocalFeed. Run Build.ps1 first." }

if (-not $Version) {
    foreach ($candidateVersion in @($packageMetadata | Select-Object -ExpandProperty Version -Unique)) {
        Assert-PackageVersion $candidateVersion
        if (-not $Version -or (Compare-PackageVersion $candidateVersion $Version) -gt 0) {
            $Version = $candidateVersion
        }
    }
}

$packagesForVersion = @()
foreach ($expectedPackageId in $expectedPackageIds) {
    $matchingPackages = @($packageMetadata | Where-Object { $_.Id -ieq $expectedPackageId -and $_.Version -ieq $Version })
    if ($matchingPackages.Count -eq 0) {
        throw "Version $Version is incomplete: missing $expectedPackageId. Run Build.ps1 -Version $Version -OutputDirectory `"$LocalFeed`". No packages were pushed."
    }
    # A feed may contain both flat and version-folder copies from earlier workflows.
    if ($matchingPackages.Count -gt 1) {
        $hashes = @($matchingPackages | ForEach-Object { (Get-FileHash -LiteralPath $_.Path -Algorithm SHA256).Hash } | Select-Object -Unique)
        if ($hashes.Count -ne 1) { throw "Conflicting copies of $expectedPackageId $Version exist in $LocalFeed. No packages were pushed." }
    }
    $packagesForVersion += $matchingPackages[0]
}
$selectedSet = [pscustomobject]@{ Version = $Version; Packages = $packagesForVersion }

Write-Host ""
Write-Host "NuGet.org prerelease candidate" -ForegroundColor Cyan
Write-Host "Local feed: $LocalFeed"
Write-Host "Version:    $($selectedSet.Version)" -ForegroundColor Yellow
Write-Host "Packages:"
$selectedSet.Packages |
    Select-Object Id, FileName |
    Format-Table -AutoSize |
    Out-Host

if ($DryRun) {
    Write-Host "Dry run complete. No packages were pushed or unlisted." -ForegroundColor Green
    exit 0
}

if ([string]::IsNullOrWhiteSpace($nugetApiKey) -or $nugetApiKey -eq "***") {
    # Windows Export-Clixml protects this SecureString for the current user/machine.
    # The ignored script-relative copy also works when a terminal redirects LOCALAPPDATA.
    $credentialPaths = @(Join-Path $PSScriptRoot 'Push-NuGet.credential.clixml')
    $localAppDataPaths = @([Environment]::GetFolderPath('LocalApplicationData'), $env:LOCALAPPDATA)
    foreach ($localAppDataPath in $localAppDataPaths) {
        if (-not [string]::IsNullOrWhiteSpace($localAppDataPath)) {
            $credentialPaths += Join-Path $localAppDataPath 'FabrCore/NuGetApiKey.clixml'
        }
    }
    foreach ($credentialPath in ($credentialPaths | Select-Object -Unique)) {
        if (Test-Path -LiteralPath $credentialPath -PathType Leaf) {
            $savedKey = Import-Clixml -LiteralPath $credentialPath
            if ($savedKey -isnot [Security.SecureString]) {
                throw "Expected an encrypted SecureString in $credentialPath."
            }
            $nugetApiKey = [Net.NetworkCredential]::new('', $savedKey).Password
            if (-not [string]::IsNullOrWhiteSpace($nugetApiKey) -and $nugetApiKey -ne "***") {
                break
            }
        }
    }
}
if ([string]::IsNullOrWhiteSpace($nugetApiKey) -or $nugetApiKey -eq "***") {
    throw "Set NUGET_API_KEY to a nuget.org key with push and unlist permissions, or configure the encrypted Windows credential at %LOCALAPPDATA%/FabrCore/NuGetApiKey.clixml."
}

$confirmation = Read-Host "Type 'yes' to push and immediately unlist version $($selectedSet.Version)"
if ($confirmation -ine "yes") {
    Write-Host "Confirmation was not 'yes'. Nothing was published." -ForegroundColor Yellow
    exit 0
}

# Resolve the unlist endpoint before publishing anything so a service discovery
# failure cannot leave a package listed.
$publishEndpoint = Get-PackagePublishEndpoint

foreach ($package in $selectedSet.Packages) {
    Write-Host ""
    Write-Host "Pushing $($package.Id) $($package.Version)..." -ForegroundColor Cyan

    & dotnet nuget push $package.Path `
        --source $nugetSource `
        --api-key $nugetApiKey `
        --skip-duplicate `
        --no-symbols `
        --force-english-output

    if ($LASTEXITCODE -ne 0) {
        throw "Push failed for $($package.Id) $($package.Version). Rerun this script after correcting the error."
    }

    Write-Host "Unlisting $($package.Id) $($package.Version)..." -ForegroundColor Cyan
    Invoke-NuGetUnlist `
        -PublishEndpoint $publishEndpoint `
        -PackageId $package.Id `
        -PackageVersion $package.Version `
        -ApiKey $nugetApiKey

    Write-Host "Pushed and unlisted $($package.Id) $($package.Version)." -ForegroundColor Green
}

Write-Host ""
Write-Host "All FabrCore packages were pushed to nuget.org and unlisted." -ForegroundColor Green
Write-Host "Use exact version '$($selectedSet.Version)' when restoring these prereleases." -ForegroundColor Green


