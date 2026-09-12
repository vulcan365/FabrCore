# Shared by build, pack and release entry points. Dot-source; do not invoke directly.
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$script:FabrCoreRepoRoot = Split-Path $PSScriptRoot -Parent
$script:FabrCoreBuild = Import-PowerShellDataFile (Join-Path $script:FabrCoreRepoRoot 'builds/Projects.psd1')

function Invoke-CheckedGit {
    param([Parameter(Mandatory)][string[]]$Arguments)
    & git -C $script:FabrCoreRepoRoot @Arguments
    if ($LASTEXITCODE -ne 0) { throw "Git failed: $($Arguments[0]) (exit $LASTEXITCODE)." }
}

function Get-LatestStableTag {
    $tags = @(Invoke-CheckedGit @('tag', '--list', 'v*'))
    $tag = $tags | Where-Object { $_ -match '^v\d+\.\d+\.\d+$' } |
        Sort-Object { [version]$_.Substring(1) } | Select-Object -Last 1
    if (-not $tag) { throw 'No stable v<major>.<minor>.<patch> tag found. Fetch release tags first.' }
    return $tag
}

function Assert-CleanReleaseTree {
    if (@(Invoke-CheckedGit @('status', '--porcelain')).Count -gt 0) {
        throw 'Commit or stash changes before releasing.'
    }
}

function Assert-PackageVersion {
    param([string]$Version)
    if ($Version -notmatch '^\d+\.\d+\.\d+(?:-[0-9A-Za-z-]+(?:\.[0-9A-Za-z-]+)*)?$') {
        throw "Invalid package version '$Version'. Use X.Y.Z or X.Y.Z-suffix (for example 2.0.0-local.20260910120000)."
    }
}

function Get-DefaultPackageFeed {
    if ([Environment]::OSVersion.Platform -eq [PlatformID]::Win32NT) { return 'C:\repos\nuget' }
    return (Join-Path $script:FabrCoreRepoRoot 'artifacts/packages')
}

function Compare-PackageVersion {
    param([string]$Left, [string]$Right)
    $leftParts = $Left -split '-', 2
    $rightParts = $Right -split '-', 2
    $comparison = ([version]$leftParts[0]).CompareTo([version]$rightParts[0])
    if ($comparison -ne 0) { return $comparison }
    if ($leftParts.Count -eq 1) { if ($rightParts.Count -eq 1) { return 0 }; return 1 }
    if ($rightParts.Count -eq 1) { return -1 }
    $leftLabels = $leftParts[1] -split '\.'
    $rightLabels = $rightParts[1] -split '\.'
    for ($i = 0; $i -lt [Math]::Min($leftLabels.Count, $rightLabels.Count); $i++) {
        $leftNumeric = $leftLabels[$i] -match '^\d+$'
        $rightNumeric = $rightLabels[$i] -match '^\d+$'
        if ($leftNumeric -and $rightNumeric) {
            $comparison = ([bigint]$leftLabels[$i]).CompareTo([bigint]$rightLabels[$i])
        } elseif ($leftNumeric) { $comparison = -1
        } elseif ($rightNumeric) { $comparison = 1
        } else { $comparison = [StringComparer]::OrdinalIgnoreCase.Compare($leftLabels[$i], $rightLabels[$i]) }
        if ($comparison -ne 0) { return $comparison }
    }
    return $leftLabels.Count.CompareTo($rightLabels.Count)
}

function Get-FeedPackageMetadata {
    param([string]$LocalFeed)
    if (-not (Test-Path -LiteralPath $LocalFeed -PathType Container)) { return }
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    Get-ChildItem -LiteralPath $LocalFeed -Recurse -File -Filter 'FabrCore*.nupkg' |
        Where-Object { $_.Name -notlike '*.symbols.nupkg' } |
        ForEach-Object { Get-NuGetPackageMetadata -PackageFile $_ }
}

function Get-NextLocalPackageVersion {
    param([string]$LocalFeed)
    $tagVersion = [version](Get-LatestStableTag).Substring(1)
    $baseVersion = [version]::new($tagVersion.Major, $tagVersion.Minor, $tagVersion.Build + 1)
    $existing = @(Get-FeedPackageMetadata -LocalFeed $LocalFeed)
    foreach ($package in $existing) {
        if ($package.Id -notin $script:FabrCoreBuild.Packages) { continue }
        Assert-PackageVersion $package.Version
        $parts = $package.Version -split '-', 2
        $candidate = [version]$parts[0]
        if ($parts.Count -eq 1) { $candidate = [version]::new($candidate.Major, $candidate.Minor, $candidate.Build + 1) }
        if ($candidate -gt $baseVersion) { $baseVersion = $candidate }
    }
    $stamp = [DateTime]::UtcNow
    # Stay ahead of future-dated local builds and prereleases such as rc.10, which
    # sort above local.*. A subsequent no-argument push must select this build.
    foreach ($package in $existing) {
        if ($package.Id -notin $script:FabrCoreBuild.Packages) { continue }
        if ((Compare-PackageVersion "$baseVersion-local.$($stamp.ToString('yyyyMMddHHmmss'))" $package.Version) -le 0) {
            if ($package.Version -match '^\d+\.\d+\.\d+-local\.(\d{14})$') {
                $previousStamp = [DateTime]::ParseExact($Matches[1], 'yyyyMMddHHmmss', [Globalization.CultureInfo]::InvariantCulture)
                $stamp = $previousStamp.AddSeconds(1)
            } else {
                $baseVersion = [version]::new($baseVersion.Major, $baseVersion.Minor, $baseVersion.Build + 1)
            }
        }
    }
    do {
        $nextVersion = "$baseVersion-local.$($stamp.ToString('yyyyMMddHHmmss'))"
        $stamp = $stamp.AddSeconds(1)
    } while ($nextVersion -in @($existing | ForEach-Object { $_.Version }))
    return $nextVersion
}

function Get-NuGetPackageMetadata {
    param(
        [Parameter(Mandatory)]
        [System.IO.FileInfo]$PackageFile
    )

    $archive = [System.IO.Compression.ZipFile]::OpenRead($PackageFile.FullName)
    try {
        $nuspecEntries = @($archive.Entries | Where-Object { $_.FullName -like "*.nuspec" })
        if ($nuspecEntries.Count -ne 1) {
            throw "Expected one .nuspec in '$($PackageFile.FullName)', found $($nuspecEntries.Count)."
        }

        $reader = [System.IO.StreamReader]::new($nuspecEntries[0].Open())
        try {
            [xml]$nuspec = $reader.ReadToEnd()
        }
        finally {
            $reader.Dispose()
        }

        $idNode = $nuspec.SelectSingleNode(
            "/*[local-name()='package']/*[local-name()='metadata']/*[local-name()='id']")
        $versionNode = $nuspec.SelectSingleNode(
            "/*[local-name()='package']/*[local-name()='metadata']/*[local-name()='version']")

        if ($null -eq $idNode -or [string]::IsNullOrWhiteSpace($idNode.InnerText)) {
            throw "Package '$($PackageFile.FullName)' does not contain a package ID."
        }

        if ($null -eq $versionNode -or [string]::IsNullOrWhiteSpace($versionNode.InnerText)) {
            throw "Package '$($PackageFile.FullName)' does not contain a package version."
        }

        [pscustomobject]@{
            Id            = $idNode.InnerText
            Version       = $versionNode.InnerText
            Path          = $PackageFile.FullName
            FileName      = $PackageFile.Name
            LastWriteTime = $PackageFile.LastWriteTimeUtc
        }
    }
    finally {
        $archive.Dispose()
    }
}


