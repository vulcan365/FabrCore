#!/usr/bin/env pwsh
param([switch]$DryRun)
. (Join-Path $PSScriptRoot 'Build-Common.ps1')
if ($DryRun) {
    Write-Host '[DryRun] Would fast-forward develop and main, merge develop into main, and push main. No Git state changed.'
    Write-Host "Packages: $($FabrCoreBuild.Packages -join ', ')"
    return
}
Assert-CleanReleaseTree
Invoke-CheckedGit @('fetch','origin')
Invoke-CheckedGit @('checkout','develop')
Invoke-CheckedGit @('pull','--ff-only','origin','develop')
Invoke-CheckedGit @('log','origin/main..develop','--oneline')
if ((Read-Host 'Merge develop into main? (y/n)') -ne 'y') { return }
Invoke-CheckedGit @('checkout','main')
Invoke-CheckedGit @('pull','--ff-only','origin','main')
Invoke-CheckedGit @('merge','develop','--no-ff','--no-edit')
Invoke-CheckedGit @('push','origin','main')
Write-Host 'develop merged into main. Run Release-Patch.ps1, Release-Minor.ps1 or Release-Major.ps1 to release.'
