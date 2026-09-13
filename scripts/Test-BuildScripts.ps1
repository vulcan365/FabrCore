# Offline checks for build inventory, script parsing, dry-run isolation and Git failure handling.
. (Join-Path $PSScriptRoot 'Build-Common.ps1')
foreach ($file in Get-ChildItem -LiteralPath $PSScriptRoot -Filter '*.ps1') {
    $tokens = $null
    $parseErrors = $null
    $null = [Management.Automation.Language.Parser]::ParseFile($file.FullName, [ref]$tokens, [ref]$parseErrors)
    if ($parseErrors.Count) { throw "Invalid PowerShell: $($file.Name): $parseErrors" }
}
$declaredTests = @($FabrCoreBuild.VSTestProjects) + @($FabrCoreBuild.TestingPlatformProjects)
$actualTests = @(Get-ChildItem (Join-Path $FabrCoreRepoRoot 'src'), (Join-Path $FabrCoreRepoRoot 'samples') -Recurse -Filter '*.Tests.csproj' |
    ForEach-Object { [IO.Path]::GetRelativePath($FabrCoreRepoRoot, $_.FullName).Replace('\','/') })
if (Compare-Object ($declaredTests | Sort-Object) ($actualTests | Sort-Object)) { throw 'Test inventory does not match the checkout.' }
foreach ($project in $declaredTests) {
    [xml]$xml = Get-Content (Join-Path $FabrCoreRepoRoot $project) -Raw
    $isPlatform = $xml.Project.Sdk -like 'MSTest.Sdk/*'
    if ($isPlatform -ne ($project -in $FabrCoreBuild.TestingPlatformProjects)) { throw "Wrong test runner: $project" }
}
$before = @(Invoke-CheckedGit @('status','--porcelain','--branch')) -join "`n"
$originalLocation = Get-Location
Push-Location ([IO.Path]::GetTempPath())
try {
    & (Join-Path $PSScriptRoot 'Build.ps1') -Pack -DryRun
    foreach ($release in @('Patch','Minor','Major','Develop')) {
        & (Join-Path $PSScriptRoot "Release-$release.ps1") -DryRun
    }
} finally { Pop-Location }
if ((Get-Location).Path -ne $originalLocation.Path) { throw 'A script changed the caller location.' }
$after = @(Invoke-CheckedGit @('status','--porcelain','--branch')) -join "`n"
if ($before -ne $after) { throw 'A dry run changed Git state.' }
$failedAsExpected = $false
try { Invoke-CheckedGit @('rev-parse','--verify', 'refs/heads/fabrcore-validation-missing-' + [guid]::NewGuid().ToString('N')) 2>$null }
catch { $failedAsExpected = $true }
if (-not $failedAsExpected) { throw 'Git failure did not stop execution.' }
& (Join-Path $PSScriptRoot 'Test-PackageWorkflow.ps1')
Write-Host 'Build script checks passed.'
# GitHub's pwsh runner propagates LASTEXITCODE; the deliberate failing Git probe
# above must not make a successful validation step fail.
exit 0
