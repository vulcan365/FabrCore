[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$PackageDirectory,
    [Parameter(Mandatory)][string]$Version,
    [switch]$RequireSql
)
. (Join-Path $PSScriptRoot 'Build-Common.ps1')
Assert-PackageVersion $Version
$PackageDirectory = [IO.Path]::GetFullPath($PackageDirectory)
if ($RequireSql -and -not $env:FABRCORE_SMOKE_CONNECTION_STRING) { throw 'SQL package smoke testing requires an isolated test connection.' }
Add-Type -AssemblyName System.IO.Compression.FileSystem
foreach ($id in $FabrCoreBuild.Packages) {
    $path = Join-Path $PackageDirectory "$id.$Version.nupkg"
    $metadata = Get-NuGetPackageMetadata (Get-Item -LiteralPath $path)
    if ($metadata.Id -cne $id -or $metadata.Version -cne $Version) { throw "Incorrect package identity: $id" }
    $archive = [IO.Compression.ZipFile]::OpenRead($path)
    try {
        $entry = @($archive.Entries | Where-Object { $_.FullName -like '*.nuspec' })[0]
        $reader = [IO.StreamReader]::new($entry.Open())
        try { [xml]$nuspec = $reader.ReadToEnd() } finally { $reader.Dispose() }
        foreach ($dependency in $nuspec.SelectNodes("//*[local-name()='dependency']")) {
            if ($dependency.id -notlike 'FabrCore.*') { continue }
            if ($dependency.id -notin $FabrCoreBuild.Packages -or $dependency.version -notin @($Version,"[$Version]")) {
                throw "Unexpected internal dependency in ${id}: $($dependency.id) $($dependency.version)"
            }
        }
        if (-not ($archive.Entries | Where-Object FullName -eq "lib/net10.0/$id.dll")) { throw "Missing assembly: $id" }
        if (-not ($archive.Entries | Where-Object FullName -eq 'README.md')) { throw "Missing package readme: $id" }
    } finally { $archive.Dispose() }
}
$work = Join-Path ([IO.Path]::GetTempPath()) ('fabrcore-consumer-' + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $work | Out-Null
$previousCache = $env:NUGET_PACKAGES
$env:NUGET_PACKAGES = Join-Path $work 'cache'
try {
    Copy-Item -LiteralPath (Join-Path $FabrCoreRepoRoot 'global.json') -Destination $work
    $feed = [Security.SecurityElement]::Escape($PackageDirectory)
    @"
<configuration>
  <packageSources><clear/><add key="release" value="$feed"/><add key="nuget" value="https://api.nuget.org/v3/index.json"/></packageSources>
  <packageSourceMapping><clear/><packageSource key="release"><package pattern="FabrCore.*"/></packageSource><packageSource key="nuget"><package pattern="*"/></packageSource></packageSourceMapping>
</configuration>
"@ | Set-Content -LiteralPath (Join-Path $work 'NuGet.Config')
    $readme = Get-Content -LiteralPath (Join-Path $FabrCoreRepoRoot 'README.md') -Raw
    $examples = [regex]::Matches($readme, '(?s)```csharp\s*\r?\n(.*?)```')
    if ($examples.Count -ne 2) { throw 'Update consumer extraction for the current README examples.' }
    $projects = @('agent','readme-host','smoke')
    foreach ($project in $projects) { New-Item -ItemType Directory -Path (Join-Path $work $project) | Out-Null }
    $examples[1].Groups[1].Value | Set-Content (Join-Path $work 'agent/Agent.cs')
    "<Project Sdk=`"Microsoft.NET.Sdk`"><PropertyGroup><TargetFramework>net10.0</TargetFramework><Nullable>enable</Nullable><ImplicitUsings>enable</ImplicitUsings></PropertyGroup><ItemGroup><PackageReference Include=`"FabrCore.Sdk`" Version=`"[$Version]`"/></ItemGroup></Project>" | Set-Content (Join-Path $work 'agent/Agent.csproj')
    $examples[0].Groups[1].Value | Set-Content (Join-Path $work 'readme-host/Program.cs')
    "<Project Sdk=`"Microsoft.NET.Sdk.Web`"><PropertyGroup><TargetFramework>net10.0</TargetFramework><ImplicitUsings>enable</ImplicitUsings></PropertyGroup><ItemGroup><PackageReference Include=`"FabrCore.Host`" Version=`"[$Version]`"/></ItemGroup></Project>" | Set-Content (Join-Path $work 'readme-host/ReadmeHost.csproj')
    $references = ($FabrCoreBuild.Packages | ForEach-Object { "<PackageReference Include=`"$_`" Version=`"[$Version]`"/>" }) -join "`n"
    @"
<Project Sdk="Microsoft.NET.Sdk.Web">
  <PropertyGroup><TargetFramework>net10.0</TargetFramework><Nullable>enable</Nullable><ImplicitUsings>enable</ImplicitUsings></PropertyGroup>
  <ItemGroup>$references<ProjectReference Include="../agent/Agent.csproj"/><None Include="packages.txt" CopyToOutputDirectory="Always"/></ItemGroup>
</Project>
"@ | Set-Content (Join-Path $work 'smoke/Smoke.csproj')
    $FabrCoreBuild.Packages | Set-Content (Join-Path $work 'smoke/packages.txt')
    Copy-Item (Join-Path $FabrCoreRepoRoot 'builds/consumer/Program.cs') (Join-Path $work 'smoke/Program.cs')
    Push-Location $work
    try {
        foreach ($project in @('agent/Agent.csproj','readme-host/ReadmeHost.csproj','smoke/Smoke.csproj')) {
            dotnet restore $project --configfile ./NuGet.Config
            if ($LASTEXITCODE -ne 0) { throw "Package-only restore failed: $project" }
            dotnet build $project -c Release --no-restore
            if ($LASTEXITCODE -ne 0) { throw "Package consumer build failed: $project" }
        }
        dotnet run --project smoke/Smoke.csproj -c Release --no-build --no-restore
        if ($LASTEXITCODE -ne 0) { throw 'Package host startup failed.' }
    } finally { Pop-Location }
    Write-Host "All nine packages and README consumers verified at $Version."
} finally {
    $env:NUGET_PACKAGES = $previousCache
    $resolved = [IO.Path]::GetFullPath($work)
    $tempRoot = [IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd([IO.Path]::DirectorySeparatorChar) + [IO.Path]::DirectorySeparatorChar
    if (-not $resolved.StartsWith($tempRoot, [StringComparison]::OrdinalIgnoreCase) -or [IO.Path]::GetFileName($resolved) -notmatch '^fabrcore-consumer-[a-f0-9]{32}$') { throw 'Unexpected consumer cleanup path.' }
    Remove-Item -LiteralPath $resolved -Recurse -Force
}
