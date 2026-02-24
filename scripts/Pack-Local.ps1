# scripts/Pack-Local.ps1
# Packs all FabrCore projects and copies to local NuGet folder

$localFeed = "C:\repos\nuget"
if (-not (Test-Path $localFeed)) {
    New-Item -ItemType Directory -Path $localFeed | Out-Null
}

$solutionDir = Join-Path $PSScriptRoot "..\src"

# Use the latest git tag (across all branches) to determine the base version,
# since MinVer only sees tags that are ancestors of the current branch.
$latestTag = & git describe --tags --abbrev=0 $(git rev-list --tags --max-count=1) 2>$null
if ($latestTag -and $latestTag -match "^v?(.+)$") {
    $baseVersion = $Matches[1]
} else {
    $baseVersion = "0.5.0"
    Write-Host "Could not determine version from git tags, using fallback: $baseVersion" -ForegroundColor Yellow
}

$timestamp = (Get-Date).ToString('yyyyMMddHHmmss')
$localVersion = "$baseVersion-local.$timestamp"

Write-Host ""
Write-Host "Package version: $localVersion" -ForegroundColor Cyan
Write-Host ""

dotnet pack "$solutionDir\FabrCore.sln" `
    --configuration Release `
    --output $localFeed `
    /p:MinVerVersionOverride=$localVersion

Write-Host ""
Write-Host "Packages published to $localFeed" -ForegroundColor Green
Write-Host "Use version '$localVersion' in PackageReference elements." -ForegroundColor Green
