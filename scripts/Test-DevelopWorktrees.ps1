# Exercise release promotion with a separate main checkout and a local bare remote.
& {
    . (Join-Path $PSScriptRoot 'Build-Common.ps1')
    $fixtureRoot = Join-Path ([IO.Path]::GetTempPath()) ('fabrcore-worktree-test-' + [guid]::NewGuid().ToString('N'))
    $checkout = Join-Path $fixtureRoot 'develop checkout'
    $mainCheckout = Join-Path $fixtureRoot 'main checkout'
    $remote = Join-Path $fixtureRoot 'remote.git'
    New-Item -ItemType Directory -Path $checkout -Force | Out-Null
    function GitAt([string]$Path, [string[]]$Arguments) {
        & git -C $Path @Arguments | Out-Null
        if ($LASTEXITCODE -ne 0) { throw "Fixture Git failed: $Arguments" }
    }
    try {
        GitAt $fixtureRoot @('init', '--bare', $remote)
        GitAt $checkout @('init', '-b', 'main')
        GitAt $checkout @('config', 'user.name', 'Release test')
        GitAt $checkout @('config', 'user.email', 'release-test@example.invalid')
        New-Item -ItemType Directory -Path (Join-Path $checkout 'scripts'), (Join-Path $checkout 'builds') | Out-Null
        foreach ($name in @('Build-Common.ps1', 'Release-Develop.ps1')) {
            Copy-Item -LiteralPath (Join-Path $PSScriptRoot $name) -Destination (Join-Path $checkout "scripts/$name")
        }
        Copy-Item -LiteralPath (Join-Path $FabrCoreRepoRoot 'builds/Projects.psd1') -Destination (Join-Path $checkout 'builds/Projects.psd1')
        GitAt $checkout @('add', '.')
        GitAt $checkout @('commit', '-m', 'Fixture baseline')
        GitAt $checkout @('remote', 'add', 'origin', $remote)
        GitAt $checkout @('push', '-u', 'origin', 'main')
        GitAt $checkout @('checkout', '-b', 'develop')
        Set-Content -LiteralPath (Join-Path $checkout 'feature.txt') -Value 'feature'
        GitAt $checkout @('add', '.')
        GitAt $checkout @('commit', '-m', 'Feature')
        GitAt $checkout @('push', '-u', 'origin', 'develop')
        GitAt $checkout @('worktree', 'add', $mainCheckout, 'main')
        $mainBefore = & git -C $mainCheckout rev-parse HEAD
        function Read-Host { param($Prompt) return 'y' }
        $release = Join-Path $checkout 'scripts/Release-Develop.ps1'
        foreach ($dirtyPath in @($mainCheckout, $checkout)) {
            $dirtyFile = Join-Path $dirtyPath 'uncommitted.txt'
            Set-Content -LiteralPath $dirtyFile -Value 'preserve me'
            $rejected = $false
            try { & $release }
            catch {
                if ($_.Exception.Message -notlike 'Commit or stash changes*') { throw }
                $rejected = $true
            }
            if (-not $rejected) { throw 'A dirty checkout was not rejected.' }
            if ((& git -C $mainCheckout rev-parse HEAD) -ne $mainBefore) { throw 'Dirty preflight moved main.' }
            Remove-Item -LiteralPath $dirtyFile
        }
        & $release
        if ((& git -C $checkout branch --show-current) -ne 'develop') { throw 'Develop checkout changed branches.' }
        GitAt $checkout @('merge-base', '--is-ancestor', 'develop', 'main')
        if ((& git -C $checkout rev-parse main) -ne (& git -C $checkout rev-parse origin/main)) { throw 'Main was not pushed.' }
        if (@(& git -C $mainCheckout status --porcelain).Count) { throw 'Main checkout is dirty.' }
        Write-Host 'Develop worktree release checks passed.'
    }
    finally {
        $resolvedFixture = [IO.Path]::GetFullPath($fixtureRoot)
        $tempRoot = [IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd([IO.Path]::DirectorySeparatorChar) + [IO.Path]::DirectorySeparatorChar
        if (-not $resolvedFixture.StartsWith($tempRoot, [StringComparison]::OrdinalIgnoreCase)) { throw 'Unsafe fixture cleanup path.' }
        Remove-Item -LiteralPath $resolvedFixture -Recurse -Force
    }
}
