[CmdletBinding()]
param(
    [Parameter(Mandatory)][ValidateSet('Patch','Minor','Major')][string]$Bump,
    [switch]$DryRun
)
. (Join-Path $PSScriptRoot 'Build-Common.ps1')
if (-not $DryRun) {
    Assert-CleanReleaseTree
    if ((Invoke-CheckedGit @('branch','--show-current')) -ne 'main') { throw 'Release from main.' }
    Invoke-CheckedGit @('fetch','origin','--tags')
    Invoke-CheckedGit @('pull','--ff-only','origin','main')
}
$lastTag = Get-LatestStableTag
$version = [version]$lastTag.Substring(1)
$newTag = switch ($Bump) {
    'Patch' { "v$($version.Major).$($version.Minor).$($version.Build + 1)" }
    'Minor' { "v$($version.Major).$($version.Minor + 1).0" }
    'Major' { "v$($version.Major + 1).0.0" }
}
Write-Host "Current: $lastTag; proposed: $newTag"
Write-Host "Packages: $($FabrCoreBuild.Packages -join ', ')"
if ($DryRun) {
    Write-Host '[DryRun] Local tags only. Would update main, create and push the tag. No Git state changed.'
    return
}
if ((Read-Host "Create and push $newTag from main? (y/n)") -ne 'y') { return }
Assert-CleanReleaseTree
Invoke-CheckedGit @('tag', $newTag)
Invoke-CheckedGit @('push','origin', $newTag)
Write-Host "Tag pushed. GitHub Actions will build, test and publish the configured package set."
Write-Host "Release notes: https://github.com/vulcan365/FabrCore/releases/new?tag=$newTag"
