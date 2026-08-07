param(
    [string]$LocalFeed = "C:\repos\nuget"
)

# Packs the supported FabrCore OSS package set to a local NuGet feed.
if (-not (Test-Path -LiteralPath $LocalFeed)) {
    New-Item -ItemType Directory -Path $LocalFeed | Out-Null
}

$solutionDir = Join-Path $PSScriptRoot "..\src"
$packages = @(
    "FabrCore.Core",
    "FabrCore.Sdk",
    "FabrCore.Client.Orleans",
    "FabrCore.Client.WebSocket",
    "FabrCore.Services.Contracts",
    "FabrCore.Host",
    "FabrCore.Host.SqlServer",
    "FabrCore.Host.AzureStorage",
    "FabrCore.Services.Microsoft365Copilot",
    "FabrCore.Services.Memory",
    "FabrCore.Services.GraphRag",
    "FabrCore.Surface"
)

# Use the latest git tag (across all branches) to determine the base version,
# since MinVer only sees tags that are ancestors of the current branch.
$latestTag = & git describe --tags --abbrev=0 $(git rev-list --tags --max-count=1) 2>$null
if ($latestTag -and $latestTag -match "^v?(\d+)\.(\d+)\.(\d+)$") {
    $major = [int]$Matches[1]
    $minor = [int]$Matches[2]
    $patch = [int]$Matches[3] + 1
    $baseVersion = if ($major -lt 1 -or ($major -eq 1 -and $minor -lt 5)) {
        "1.5.0"
    } else {
        "$major.$minor.$patch"
    }
} else {
    $baseVersion = "1.5.0"
    Write-Host "Could not determine version from git tags, using fallback: $baseVersion" -ForegroundColor Yellow
}

$timestamp = (Get-Date).ToString('yyyyMMddHHmmss')
$localVersion = "$baseVersion-local.$timestamp"

Write-Host ""
Write-Host "Package version: $localVersion" -ForegroundColor Cyan
Write-Host "Packages: $($packages -join ', ')" -ForegroundColor Cyan
Write-Host ""

foreach ($package in $packages) {
    $projectPath = Join-Path $solutionDir "$package\$package.csproj"
    dotnet pack $projectPath `
        --configuration Release `
        --output $LocalFeed `
        /p:MinVerVersionOverride=$localVersion
    if ($LASTEXITCODE -ne 0) {
        Write-Error "Packing failed: $projectPath"
        exit $LASTEXITCODE
    }
}

foreach ($package in $packages) {
    $packagePath = Join-Path $LocalFeed "$package.$localVersion.nupkg"
    if (-not (Test-Path $packagePath)) {
        Write-Error "Expected package was not created: $packagePath"
        exit 1
    }
}

Write-Host ""
Write-Host "Packages published to $LocalFeed" -ForegroundColor Green
Write-Host "Use version '$localVersion' in PackageReference elements." -ForegroundColor Green
