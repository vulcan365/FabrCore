# scripts/Pack-Local.ps1
# Packs all FabrCore projects and copies to local NuGet folder

$localFeed = "C:\repos\nuget"
if (-not (Test-Path $localFeed)) {
    New-Item -ItemType Directory -Path $localFeed | Out-Null
}

$solutionDir = Join-Path $PSScriptRoot "..\src"
$packages = @(
    "FabrCore.Core",
    "FabrCore.Sdk",
    "FabrCore.Host",
    "FabrCore.Client",
    "FabrCore.Surface"
)

# Use the latest git tag (across all branches) to determine the base version,
# since MinVer only sees tags that are ancestors of the current branch.
$latestTag = & git describe --tags --abbrev=0 $(git rev-list --tags --max-count=1) 2>$null
if ($latestTag -and $latestTag -match "^v?(\d+)\.(\d+)\.(\d+)$") {
    $major = [int]$Matches[1]
    $minor = [int]$Matches[2]
    $patch = [int]$Matches[3] + 1
    $baseVersion = "$major.$minor.$patch"
} else {
    $baseVersion = "0.5.1"
    Write-Host "Could not determine version from git tags, using fallback: $baseVersion" -ForegroundColor Yellow
}

$timestamp = (Get-Date).ToString('yyyyMMddHHmmss')
$localVersion = "$baseVersion-local.$timestamp"

Write-Host ""
Write-Host "Package version: $localVersion" -ForegroundColor Cyan
Write-Host "Packages: $($packages -join ', ')" -ForegroundColor Cyan
Write-Host ""

dotnet pack "$solutionDir\FabrCore.sln" `
    --configuration Release `
    --output $localFeed `
    /p:MinVerVersionOverride=$localVersion

foreach ($package in $packages) {
    $packagePath = Join-Path $localFeed "$package.$localVersion.nupkg"
    if (-not (Test-Path $packagePath)) {
        Write-Error "Expected package was not created: $packagePath"
        exit 1
    }
}

Write-Host ""
Write-Host "Packages published to $localFeed" -ForegroundColor Green
Write-Host "Use version '$localVersion' in PackageReference elements." -ForegroundColor Green
