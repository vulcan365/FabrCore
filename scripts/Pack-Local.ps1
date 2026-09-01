param(
    [string]$LocalFeed = "C:\repos\nuget",

    # Packs even when the computed version is lower than one already in the feed. Only for
    # deliberately rebuilding an older line.
    [switch]$AllowVersionDowngrade
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
    "FabrCore.Host.Testing",
    "FabrCore.Services.Microsoft365Copilot",
    "FabrCore.Services.Memory",
    "FabrCore.Services.GraphRag",
    "FabrCore.Surface"
)

# Use the latest git tag (across all branches) to determine the base version,
# since MinVer only sees tags that are ancestors of the current branch.
$latestTag = & git describe --tags --abbrev=0 $(git rev-list --tags --max-count=1) 2>$null
if (-not ($latestTag -and $latestTag -match "^v?(\d+)\.(\d+)\.(\d+)$")) {
    # A hardcoded fallback here silently produced versions below the line already in use, so a
    # fresh pack of newer code sorted older than what consumers referenced. Refuse instead: a
    # wrong version that restores is worse than a pack that does not run.
    Write-Error ("Could not determine a base version: no git tag matching 'v<major>.<minor>.<patch>' was found. " +
        "Tag a release (for example 'git tag v1.7.3') and rerun.")
    exit 1
}

$major = [int]$Matches[1]
$minor = [int]$Matches[2]
$patch = [int]$Matches[3] + 1
$baseVersion = "$major.$minor.$patch"

# The tag may lag packages already in the feed — another branch or machine may have packed a
# higher line. Publishing below it would hand consumers a newer build with a lower version.
$feedVersions = @(
    Get-ChildItem -LiteralPath $LocalFeed -File -Filter "FabrCore.*.nupkg" -ErrorAction SilentlyContinue |
        ForEach-Object {
            if ($_.BaseName -match "\.(\d+)\.(\d+)\.(\d+)(?:-|$)") {
                [version]::new([int]$Matches[1], [int]$Matches[2], [int]$Matches[3])
            }
        }
)

if ($feedVersions.Count -gt 0) {
    $highestInFeed = ($feedVersions | Sort-Object -Descending | Select-Object -First 1)
    $candidate = [version]$baseVersion
    if ($candidate -lt $highestInFeed) {
        $message = "Computed base version $baseVersion is lower than $highestInFeed, which is already in $LocalFeed. " +
            "Packing would give newer code a lower version than consumers already reference. " +
            "Tag the current line (for example 'git tag v$($highestInFeed)') and rerun, or pass -AllowVersionDowngrade."
        if (-not $AllowVersionDowngrade) {
            Write-Error $message
            exit 1
        }

        Write-Host "WARNING: $message" -ForegroundColor Yellow
    }
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
