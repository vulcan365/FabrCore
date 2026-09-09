param([ValidateRange(1,100)][int]$Iterations = 3, [switch]$MinimalSelection, [switch]$VerifySelection)
$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path $PSScriptRoot -Parent
Push-Location $repoRoot
try {
    $project = 'src/FabrCore.Services.Memory.EvalConsole'
    & dotnet build $project --nologo -v quiet
    if ($LASTEXITCODE -ne 0) { throw 'Memory evaluation build failed.' }
    $assembly = Join-Path $repoRoot "$project/bin/Debug/net10.0/FabrCore.Services.Memory.EvalConsole.dll"
    $output = Join-Path $repoRoot ('artifacts/memory-selection-experiments/' + [DateTime]::UtcNow.ToString('yyyyMMddTHHmmss') + '-' + [Guid]::NewGuid().ToString('N'))
    $pairs = @(
        @{ Name='main'; Mode='code'; Corpus='corpus.json'; Order=@('control','compact') },
        @{ Name='holdout'; Mode='code'; Corpus='corpus-holdout.json'; Order=@('compact','control') },
        @{ Name='harness'; Mode='harness'; Corpus='corpus.json'; Order=@('control','compact') }
    )
    $results = @()
    $failed = $false
    foreach ($pair in $pairs) {
        $reports = @{}
        foreach ($variant in $pair.Order) {
            $runOutput = Join-Path $output ($pair.Name + '/' + $variant)
            $compact = if ($variant -eq 'compact') { 'true' } else { 'false' }
            $minimal = if ($MinimalSelection) { 'true' } else { 'false' }
            $verify = if ($VerifySelection -and $variant -eq 'compact') { 'true' } else { 'false' }
            & dotnet $assembly run --mode $pair.Mode --iterations $Iterations --compact-ids $compact --minimal-selection $minimal --verify-selection $verify --manifest "$project/$($pair.Corpus)" --output $runOutput
            $runExit = $LASTEXITCODE
            $reportFile = Get-ChildItem -LiteralPath $runOutput -Filter report.json -Recurse -ErrorAction SilentlyContinue | Select-Object -First 1
            if (-not $reportFile) { throw "Missing report for $($pair.Name)/$variant." }
            $reports[$variant] = $reportFile.FullName
            $report = Get-Content -LiteralPath $reportFile.FullName -Raw | ConvertFrom-Json
            if ($runExit -ne 0) { $failed = $true }
            $inputTokens = if (@($report.Calls | Where-Object { $null -eq $_.InputTokens }).Count -eq 0) { ($report.Calls | Measure-Object InputTokens -Sum).Sum } else { $null }
            $outputTokens = if (@($report.Calls | Where-Object { $null -eq $_.OutputTokens }).Count -eq 0) { ($report.Calls | Measure-Object OutputTokens -Sum).Sum } else { $null }
            $results += [pscustomobject]@{
                Pair=$pair.Name; Variant=$variant; RunExit=$runExit; Status=$report.Status
                MinimalSelection=[bool]$MinimalSelection; Verified=($verify -eq 'true')
                Passed=@($report.Checks | Where-Object Passed).Count; Checks=$report.Checks.Count
                ChatCalls=$report.Calls.Count; InputTokens=$inputTokens; OutputTokens=$outputTokens
                Report=$reportFile.FullName
            }
            $results | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $output 'experiment.json')
        }
        & dotnet $assembly compare --baseline $reports['control'] --candidate $reports['compact']
        if ($LASTEXITCODE -ne 0) { $failed = $true }
    }
    Write-Host "Experiment: $output"
    if ($failed) { exit 2 }
} finally { Pop-Location }
