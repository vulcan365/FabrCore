#!/usr/bin/env pwsh
param([switch]$DryRun)
. (Join-Path $PSScriptRoot 'Build-Common.ps1')
if ($DryRun) {
    Write-Host '[DryRun] Would fast-forward develop and main, merge develop into main, and push main. No Git state changed.'
    Write-Host "Packages: $($FabrCoreBuild.Packages -join ', ')"
    return
}
Assert-CleanReleaseTree
$developPath = Get-BranchWorktreePath 'develop'
$mainPath = Get-BranchWorktreePath 'main'
if (-not $developPath) { $developPath = $script:FabrCoreRepoRoot }
if (-not $mainPath) { $mainPath = $developPath }
Assert-CleanReleaseTree -WorkingDirectory $developPath
Assert-CleanReleaseTree -WorkingDirectory $mainPath
Invoke-CheckedGit @('fetch','origin')
Invoke-CheckedGit @('checkout','develop') -WorkingDirectory $developPath
Invoke-CheckedGit @('pull','--ff-only','origin','develop') -WorkingDirectory $developPath
Invoke-CheckedGit @('log','origin/main..develop','--oneline')
if ((Read-Host 'Merge develop into main? (y/n)') -ne 'y') { return }
Assert-CleanReleaseTree -WorkingDirectory $mainPath
Invoke-CheckedGit @('checkout','main') -WorkingDirectory $mainPath
Invoke-CheckedGit @('pull','--ff-only','origin','main') -WorkingDirectory $mainPath
Invoke-CheckedGit @('merge','develop','--no-ff','--no-edit') -WorkingDirectory $mainPath
Invoke-CheckedGit @('push','origin','main') -WorkingDirectory $mainPath
Write-Host "Main checkout: $mainPath"
Write-Host 'develop merged into main. Run Release-Patch.ps1, Release-Minor.ps1 or Release-Major.ps1 to release.'
