[CmdletBinding()]
param(
    [ValidateSet('Debug','Release')][string]$Configuration = 'Release',
    [switch]$SkipTests,
    [switch]$Pack = $true,
    [Alias('LocalFeed')][string]$OutputDirectory,
    [string]$Version,
    [switch]$DryRun
)
. (Join-Path $PSScriptRoot 'Build-Common.ps1')
if (-not $OutputDirectory) { $OutputDirectory = Get-DefaultPackageFeed }
$OutputDirectory = [IO.Path]::GetFullPath($OutputDirectory)
if (-not $Version) { $Version = Get-NextLocalPackageVersion -LocalFeed $OutputDirectory }
Assert-PackageVersion $Version
Write-Host "Package version: $Version"
Write-Host "Package feed: $OutputDirectory"
$projectPaths = @($FabrCoreBuild.Solution) + @($FabrCoreBuild.VSTestProjects) + @($FabrCoreBuild.TestingPlatformProjects) +
    @($FabrCoreBuild.Packages | ForEach-Object { "src/$_/$_.csproj" })
foreach ($path in $projectPaths) {
    if (-not (Test-Path -LiteralPath (Join-Path $FabrCoreRepoRoot $path) -PathType Leaf)) { throw "Missing build input: $path" }
}
function Invoke-Dotnet {
    param([string[]]$Arguments)
    Write-Host "dotnet $($Arguments -join ' ')"
    if ($DryRun) { return }
    & dotnet @Arguments
    if ($LASTEXITCODE -ne 0) { throw "dotnet $($Arguments[0]) failed (exit $LASTEXITCODE)." }
}
$versionArgs = if ($Version) { @("-p:MinVerVersionOverride=$Version") } else { @() }
Push-Location $FabrCoreRepoRoot
try {
    Invoke-Dotnet (@('restore', $FabrCoreBuild.Solution) + $versionArgs)
    Invoke-Dotnet (@('build', $FabrCoreBuild.Solution, '-c', $Configuration, '--no-restore') + $versionArgs)
    if (-not $SkipTests) {
        foreach ($project in $FabrCoreBuild.VSTestProjects) {
            Invoke-Dotnet @('test', $project, '-c', $Configuration, '--no-build', '--no-restore', '--filter', $FabrCoreBuild.OfflineTestFilter)
        }
        foreach ($project in $FabrCoreBuild.TestingPlatformProjects) {
            Invoke-Dotnet @('run', '--project', $project, '-c', $Configuration, '--no-build', '--no-restore', '--', '--filter', $FabrCoreBuild.OfflineTestFilter)
        }
    }
    if ($Pack) {
        foreach ($package in $FabrCoreBuild.Packages) {
            Invoke-Dotnet (@('pack', "src/$package/$package.csproj", '-c', $Configuration, '--no-build', '--no-restore', '--output', $OutputDirectory) + $versionArgs)
        }
        if (-not $DryRun) {
            foreach ($package in $FabrCoreBuild.Packages) {
                $packagePath = Join-Path $OutputDirectory "$package.$Version.nupkg"
                if (-not (Test-Path -LiteralPath $packagePath -PathType Leaf)) { throw "Expected package was not created: $packagePath" }
            }
            Write-Host "Built and packed $($FabrCoreBuild.Packages.Count) packages, version $Version, in $OutputDirectory."
        }
    }
} finally { Pop-Location }
