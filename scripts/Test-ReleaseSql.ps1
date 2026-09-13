[CmdletBinding()]
param(
    [string]$Container = 'sql2025',
    [ValidateSet('All','Host','Memory','GraphRag')][string]$Suite = 'All',
    [ValidateSet('Debug','Release')][string]$Configuration = 'Release',
    [string]$ResultsDirectory = (Join-Path $PSScriptRoot '../artifacts/test-results/sql'),
    [string]$PackageDirectory,
    [string]$Version
)
. (Join-Path $PSScriptRoot 'Build-Common.ps1')
if ($PackageDirectory -and $Suite -ne "All") { throw "Package release validation requires all SQL suites." }
$info = docker inspect $Container | ConvertFrom-Json
if ($LASTEXITCODE -ne 0 -or -not $info[0].State.Running) { throw 'A running isolated SQL Server 2025 container is required.' }
$passwordEntry = $info[0].Config.Env | Where-Object { $_ -like 'MSSQL_SA_PASSWORD=*' -or $_ -like 'SA_PASSWORD=*' } | Select-Object -First 1
if (-not $passwordEntry) { throw 'The SQL test container must expose its initial test password.' }
$password = $passwordEntry.Substring($passwordEntry.IndexOf('=') + 1)
$bindings = @($info[0].NetworkSettings.Ports.'1433/tcp')
if (-not $bindings.Count -or -not $bindings[0]) { throw 'Publish SQL port 1433 to the host.' }
$server = "localhost,$($bindings[0].HostPort)"
$run = [guid]::NewGuid().ToString('N')
$database = "FabrCoreRelease_$run"
$ResultsDirectory = [IO.Path]::GetFullPath((Join-Path $ResultsDirectory $run))
New-Item -ItemType Directory -Force -Path $ResultsDirectory | Out-Null
# The name is generated here and never accepts a caller-provided database identifier.
if ($database -notmatch '^FabrCoreRelease_[a-f0-9]{32}$') { throw 'Invalid isolated database name.' }
function Invoke-TestSql([string]$Query) {
    & docker exec -e "SQLCMDPASSWORD=$password" $Container /opt/mssql-tools18/bin/sqlcmd -S localhost -U sa -C -b -Q $Query
    if ($LASTEXITCODE -ne 0) { throw 'SQL test database command failed.' }
}
function Assert-TestReports([string]$Directory) {
    $reports = @(Get-ChildItem -LiteralPath $Directory -Filter '*.trx' -Recurse)
    if (-not $reports.Count) { throw "No test report produced in $Directory." }
    foreach ($report in $reports) {
        [xml]$xml = Get-Content -LiteralPath $report.FullName -Raw
        $counts = $xml.SelectSingleNode("//*[local-name()='Counters']")
        if (-not $counts -or [int]$counts.executed -le 0 -or [int]$counts.failed -ne 0 -or [int]$counts.notExecuted -ne 0 -or [int]$counts.passed -ne [int]$counts.total) {
            throw "Required SQL tests failed, skipped, or did not execute: $($report.Name)."
        }
    }
}
$variables = @('FABRCORE_SQL_TEST_PASSWORD','FABRCORE_SQL_TEST_SERVER','FABRCORE_SQL_TEST_CONNECTION_STRING',
    'FABRCORE_MEMORY_TEST_CONNECTION_STRING','FABRCORE_GRAPHRAG_TEST_CONNECTION_STRING','FABRCORE_GRAPHRAG_ALLOW_DATABASE_CREATION','FABRCORE_SMOKE_CONNECTION_STRING')
$previous = @{}
foreach ($name in $variables) { $previous[$name] = [Environment]::GetEnvironmentVariable($name) }
$created = $false
try {
    Invoke-TestSql "CREATE DATABASE [$database]"
    $created = $true
    # Quote the password as a connection-string value, including embedded quotes.
    $escapedPassword = $password.Replace('"','""')
    $connection = "Server=$server;Database=$database;User ID=sa;Password=`"$escapedPassword`";TrustServerCertificate=true"
    $env:FABRCORE_SQL_TEST_PASSWORD = $password
    $env:FABRCORE_SQL_TEST_SERVER = $server
    $env:FABRCORE_SQL_TEST_CONNECTION_STRING = $connection
    $env:FABRCORE_MEMORY_TEST_CONNECTION_STRING = $connection
    $env:FABRCORE_GRAPHRAG_TEST_CONNECTION_STRING = $connection
    $env:FABRCORE_GRAPHRAG_ALLOW_DATABASE_CREATION = "true"
    $env:FABRCORE_SMOKE_CONNECTION_STRING = $connection
    if ($Suite -in @('All','Host')) {
    $hostResults = Join-Path $ResultsDirectory 'host'
    dotnet test (Join-Path $FabrCoreRepoRoot 'src/FabrCore.Host.Tests/FabrCore.Host.Tests.csproj') -c $Configuration --no-build --no-restore --filter 'TestCategory=SqlMode|TestCategory=SqlIntegration' --logger trx --results-directory $hostResults
    if ($LASTEXITCODE -ne 0) { throw 'Host SQL validation failed.' }
    Assert-TestReports $hostResults
    }
    foreach ($project in $FabrCoreBuild.TestingPlatformProjects) {
        if ($Suite -eq "Host" -or ($Suite -ne "All" -and $project -notlike "*.$Suite.Tests/*")) { continue }
        $results = Join-Path $ResultsDirectory ([IO.Path]::GetFileNameWithoutExtension($project))
        dotnet run --project (Join-Path $FabrCoreRepoRoot $project) -c $Configuration --no-build --no-restore -- --filter 'TestCategory=Integration&TestCategory!=Evaluation' --report-trx --results-directory $results
        if ($LASTEXITCODE -ne 0) { throw "SQL integration tests failed: $project" }
        Assert-TestReports $results
    }
    if ($PackageDirectory) {
        if (-not $Version) { throw 'Version is required for package smoke tests.' }
        & (Join-Path $PSScriptRoot 'Test-PackageConsumers.ps1') -PackageDirectory $PackageDirectory -Version $Version -RequireSql
    }
    Write-Host "SQL $Suite validation passed. Reports: $ResultsDirectory"
} finally {
    foreach ($name in $variables) { [Environment]::SetEnvironmentVariable($name, $previous[$name]) }
    if ($created) { Invoke-TestSql "ALTER DATABASE [$database] SET SINGLE_USER WITH ROLLBACK IMMEDIATE; DROP DATABASE [$database]" }
}
