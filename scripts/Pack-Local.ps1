[CmdletBinding()]
param(
    [Alias('OutputDirectory')][string]$LocalFeed,
    [string]$Version,
    [switch]$DryRun,
    # Retained for existing callers. Explicit versions are honored; automatic versions
    # now advance beyond the feed and do not need a downgrade override.
    [switch]$AllowVersionDowngrade
)

# One implementation owns version selection, building, and package verification.
$buildParameters = @{ SkipTests = $true; Pack = $true; DryRun = $DryRun }
if ($LocalFeed) { $buildParameters.OutputDirectory = $LocalFeed }
if ($Version) { $buildParameters.Version = $Version }
& (Join-Path $PSScriptRoot 'Build.ps1') @buildParameters
