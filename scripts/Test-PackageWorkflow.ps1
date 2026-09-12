# Offline regression checks. Fake dotnet creates tiny packages; no network or credentials.
. (Join-Path $PSScriptRoot 'Build-Common.ps1')
$testFeed = Join-Path ([IO.Path]::GetTempPath()) ('fabrcore-packaging-' + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $testFeed | Out-Null
Add-Type -AssemblyName System.IO.Compression.FileSystem
Add-Type -AssemblyName System.IO.Compression
$global:FabrCorePackagingTestCommands = @()
$global:FabrCorePackagingTestFailBuild = $false
$originalApiKey = $env:NUGET_API_KEY
$global:FabrCorePackagingTestUnlists = @()
function Read-Host { param([string]$Prompt) return 'yes' }
function Invoke-RestMethod {
    param($Uri, $Method)
    return @{ resources = @(@{ '@type' = 'PackagePublish/2.0.0'; '@id' = 'https://example.invalid/packages' }) }
}
function Invoke-WebRequest {
    param($Uri, $Method, $Headers, [switch]$UseBasicParsing)
    if ($Method -ne 'Delete' -or $Uri -notlike 'https://example.invalid/packages/*') { throw 'Unexpected publish request.' }
    $global:FabrCorePackagingTestUnlists += $Uri
}
function dotnet {
    $global:FabrCorePackagingTestCommands += ,@($args)
    $global:LASTEXITCODE = 0
    if ($args[0] -eq 'build' -and $global:FabrCorePackagingTestFailBuild) { $global:LASTEXITCODE = 1; return }
    if ($args[0] -ne 'pack') { return }
    $packageId = [IO.Path]::GetFileNameWithoutExtension($args[1])
    $packageVersion = (@($args | Where-Object { $_ -like '-p:MinVerVersionOverride=*' })[0] -split '=', 2)[1]
    $feedIndex = [Array]::IndexOf($args, '--output')
    $packagePath = Join-Path $args[$feedIndex + 1] "$packageId.$packageVersion.nupkg"
    $archive = [IO.Compression.ZipFile]::Open($packagePath, [IO.Compression.ZipArchiveMode]::Create)
    try {
        $entry = $archive.CreateEntry("$packageId.nuspec")
        $writer = [IO.StreamWriter]::new($entry.Open())
        try { $writer.Write("<package><metadata><id>$packageId</id><version>$packageVersion</version></metadata></package>") }
        finally { $writer.Dispose() }
    } finally { $archive.Dispose() }
}
try {
    & (Join-Path $PSScriptRoot 'Build.ps1') -Version 2.0.0-blah -OutputDirectory $testFeed
    if (@(Get-FeedPackageMetadata $testFeed).Count -ne $FabrCoreBuild.Packages.Count) { throw 'Default build did not pack all packages.' }
    if (@($global:FabrCorePackagingTestCommands | Where-Object { $_[0] -eq 'test' }).Count -ne $FabrCoreBuild.VSTestProjects.Count) { throw 'Default build skipped tests.' }
    foreach ($command in @($global:FabrCorePackagingTestCommands | Where-Object { $_[0] -in @('restore','build','pack') })) {
        if ('-p:MinVerVersionOverride=2.0.0-blah' -notin $command) { throw 'Version override was not propagated.' }
    }
    $preview = (& (Join-Path $PSScriptRoot 'Push-NuGet.ps1') -Version 2.0.0-blah -LocalFeed $testFeed -DryRun 6>&1 | Out-String)
    if ($preview -notmatch 'Version:\s+2.0.0-blah') { throw 'Exact version selection failed.' }
    $env:NUGET_API_KEY = 'offline-test-key'
    & (Join-Path $PSScriptRoot 'Push-NuGet.ps1') -Version 2.0.0-blah -LocalFeed $testFeed
    $pushCommands = @($global:FabrCorePackagingTestCommands | Where-Object { $_[0] -eq 'nuget' -and $_[1] -eq 'push' })
    if ($pushCommands.Count -ne $FabrCoreBuild.Packages.Count -or
        $global:FabrCorePackagingTestUnlists.Count -ne $FabrCoreBuild.Packages.Count) { throw 'Publish/unlist did not process all nine packages.' }
    foreach ($command in $pushCommands) {
        if ($command[2] -notlike '*2.0.0-blah.nupkg') { throw 'Publish selected the wrong package version.' }
    }
    foreach ($suffix in @('rc.9','rc.10')) {
        & (Join-Path $PSScriptRoot 'Build.ps1') -Version "2.0.0-$suffix" -OutputDirectory $testFeed -SkipTests
    }
    $preview = (& (Join-Path $PSScriptRoot 'Push-NuGet.ps1') -LocalFeed $testFeed -DryRun 6>&1 | Out-String)
    if ($preview -notmatch 'Version:\s+2.0.0-rc.10') { throw 'Highest semantic version selection failed.' }
    & (Join-Path $PSScriptRoot 'Pack-Local.ps1') -Version 2.0.0-local.20260910120500 -LocalFeed $testFeed
    $packed = @(Get-FeedPackageMetadata $testFeed | Where-Object { $_.Version -eq '2.0.0-local.20260910120500' })
    if ($packed.Count -ne $FabrCoreBuild.Packages.Count) { throw 'Pack-Local ignored the explicit version.' }
    & (Join-Path $PSScriptRoot 'Pack-Local.ps1') -LocalFeed $testFeed
    $automaticPreview = (& (Join-Path $PSScriptRoot 'Push-NuGet.ps1') -LocalFeed $testFeed -DryRun 6>&1 | Out-String)
    if ($automaticPreview -notmatch 'Version:\s+2.0.1-local\.\d{14}') { throw 'Pack-Local automatic version did not advance beyond the existing feed.' }
    $global:LASTEXITCODE = 0
    dotnet pack src/FabrCore.Core/FabrCore.Core.csproj --output $testFeed '-p:MinVerVersionOverride=9.0.0-partial'
    $partialRejected = $false
    try { & (Join-Path $PSScriptRoot 'Push-NuGet.ps1') -LocalFeed $testFeed -DryRun }
    catch { $partialRejected = $_.Exception.Message -like '*incomplete*' }
    if (-not $partialRejected) { throw 'Automatic push silently fell back from an incomplete latest version.' }
    $missingRejected = $false
    try { & (Join-Path $PSScriptRoot 'Push-NuGet.ps1') -Version 2.0.0-missing -LocalFeed $testFeed -DryRun }
    catch { $missingRejected = $_.Exception.Message -like '*incomplete*' }
    if (-not $missingRejected) { throw 'Missing exact version did not fail closed.' }
    $nextVersion = Get-NextLocalPackageVersion $testFeed
    if (([version]($nextVersion -split '-')[0]) -lt [version]'2.0.0') { throw 'Automatic version downgraded the local line.' }
    foreach ($package in @(Get-FeedPackageMetadata $testFeed)) {
        if ((Compare-PackageVersion $nextVersion $package.Version) -le 0) { throw 'Automatic version would not be selected by the next push.' }
    }
    $global:FabrCorePackagingTestFailBuild = $true
    $global:FabrCorePackagingTestCommands = @()
    $failed = $false
    try { & (Join-Path $PSScriptRoot 'Build.ps1') -Version 2.0.0-failure -OutputDirectory $testFeed -SkipTests }
    catch { $failed = $true }
    if (-not $failed -or @($global:FabrCorePackagingTestCommands | Where-Object { $_[0] -eq 'pack' }).Count) { throw 'Failed build did not prevent packing.' }
    Write-Host 'Package workflow checks passed.'
} finally {
    Remove-Item Function:\dotnet
    Remove-Item Function:\Read-Host, Function:\Invoke-RestMethod, Function:\Invoke-WebRequest
    $env:NUGET_API_KEY = $originalApiKey
    Remove-Variable FabrCorePackagingTestUnlists -Scope Global
    Remove-Variable FabrCorePackagingTestCommands, FabrCorePackagingTestFailBuild -Scope Global
    # Only remove this test's explicitly created temporary directory.
    $resolvedTestFeed = [IO.Path]::GetFullPath($testFeed)
    $resolvedTemp = [IO.Path]::GetFullPath([IO.Path]::GetTempPath())
    if (-not $resolvedTestFeed.StartsWith($resolvedTemp, [StringComparison]::OrdinalIgnoreCase) -or
        [IO.Path]::GetFileName($resolvedTestFeed) -notlike 'fabrcore-packaging-*') { throw 'Unexpected test cleanup path.' }
    Remove-Item -LiteralPath $resolvedTestFeed -Recurse -Force
    $global:LASTEXITCODE = 0
}

