param(
    [string]$Container = 'sql2025',
    [ValidateSet('Debug','Release')][string]$Configuration = 'Debug'
)
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$containerInfo = docker inspect $Container | ConvertFrom-Json
if ($LASTEXITCODE -ne 0) { throw 'Could not inspect the SQL test container.' }
if (-not $containerInfo[0].State.Running) { throw 'The SQL test container is not running.' }
$passwordEntry = $containerInfo[0].Config.Env | Where-Object { $_ -like 'MSSQL_SA_PASSWORD=*' -or $_ -like 'SA_PASSWORD=*' } | Select-Object -First 1
if (-not $passwordEntry) { throw 'The SQL test container does not expose its initial test password.' }
$bindings = @($containerInfo[0].NetworkSettings.Ports.'1433/tcp')
if ($bindings.Count -eq 0 -or $null -eq $bindings[0]) { throw 'SQL container port 1433 must be published to the host.' }
$port = $bindings[0].HostPort
$previousPassword = $env:FABRCORE_SQL_TEST_PASSWORD
$previousServer = $env:FABRCORE_SQL_TEST_SERVER
try {
    $env:FABRCORE_SQL_TEST_PASSWORD = $passwordEntry.Substring($passwordEntry.IndexOf('=') + 1)
    $env:FABRCORE_SQL_TEST_SERVER = "localhost,$port"
    $project = Join-Path $PSScriptRoot '../src/FabrCore.Host.Tests/FabrCore.Host.Tests.csproj'
    dotnet test $project --configuration $Configuration --filter 'TestCategory=SqlMode' -v quiet
    if ($LASTEXITCODE -ne 0) { throw 'SQL mode integration tests failed.' }
} finally {
    $env:FABRCORE_SQL_TEST_PASSWORD = $previousPassword
    $env:FABRCORE_SQL_TEST_SERVER = $previousServer
}
