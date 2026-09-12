param(
    [ValidateRange(1,100)][int]$Iterations = 2,
    [string[]]$Modes = @('code','tools','extract','compact','harness','holdout'),
    [string]$BaselineManifest = 'src/FabrCore.Services.Memory.EvalConsole/baselines.json'
)
$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path $PSScriptRoot -Parent
Push-Location $repoRoot
try {
    foreach ($mode in $Modes) {
        if ($mode -notin @('code','tools','extract','compact','harness','holdout')) { throw "Unknown mode: $mode" }
    }
    $project = 'src/FabrCore.Services.Memory.EvalConsole'
    & dotnet build $project --nologo -v quiet
    if ($LASTEXITCODE -ne 0) { throw 'Memory evaluation build failed.' }
    $assembly = Join-Path $repoRoot "$project/bin/Debug/net10.0/FabrCore.Services.Memory.EvalConsole.dll"
    $baselines = if (Test-Path -LiteralPath $BaselineManifest) { Get-Content -LiteralPath $BaselineManifest -Raw | ConvertFrom-Json -AsHashtable } else { @{} }
    $sessionOutput = Join-Path $repoRoot ('artifacts/memory-matrices/' + [DateTime]::UtcNow.ToString('yyyyMMddTHHmmss') + '-' + [Guid]::NewGuid().ToString('N'))
    $results = @()
    $failed = $false
    foreach ($mode in $Modes) {
        $output = Join-Path $sessionOutput $mode
        $actualMode = if ($mode -eq 'holdout') { 'code' } else { $mode }
        $arguments = @('run','--mode',$actualMode,'--iterations',"$Iterations",'--output',$output)
        if ($mode -eq 'holdout') { $arguments += @('--manifest',"$project/corpus-holdout.json") }
        & dotnet $assembly @arguments
        $runExit = $LASTEXITCODE
        $report = Get-ChildItem -LiteralPath $output -Filter report.json -Recurse -ErrorAction SilentlyContinue | Select-Object -First 1
        $compareExit = $null
        if ($runExit -ne 0 -or -not $report) { $failed = $true }
        if (-not $report) { Write-Warning "Missing evaluation report for $mode." }
        if ($report -and $baselines.ContainsKey($mode) -and (Test-Path -LiteralPath $baselines[$mode])) {
            & dotnet $assembly compare --baseline $baselines[$mode] --candidate $report.FullName
            $compareExit = $LASTEXITCODE
            if ($compareExit -ne 0) { $failed = $true }
        } else {
            Write-Warning "No available baseline comparison for $mode. Current run still has its own quality gates."
        }
        $results += [pscustomobject]@{ Mode=$mode; RunExit=$runExit; CompareExit=$compareExit; Report=$(if ($report) { $report.FullName } else { $null }) }
    }
    New-Item -ItemType Directory -Path $sessionOutput -Force | Out-Null
    $results | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath (Join-Path $sessionOutput 'matrix.json')
    Write-Host "Matrix: $sessionOutput"
    if ($failed) { exit 2 }
} finally { Pop-Location }
