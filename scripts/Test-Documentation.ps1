# Check repository targets used by the release's two entry-point documents.
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
foreach ($name in @('README.md', 'RELEASE_NOTES.md')) {
    $path = Join-Path $root $name
    $document = Get-Content -LiteralPath $path -Raw
    foreach ($match in [regex]::Matches($document, '\]\(([^)]+)\)')) {
        $target = $match.Groups[1].Value
        if ($target -match '^(https?://|mailto:|#)') { continue }
        $parts = $target -split '#', 2
        $resolved = Join-Path $root ([uri]::UnescapeDataString($parts[0]))
        if (-not (Test-Path -LiteralPath $resolved)) { throw "Broken link in ${name}: $target" }
        if ($parts.Count -eq 2 -and $resolved -like '*.md') {
            $headings = [regex]::Matches((Get-Content -LiteralPath $resolved -Raw), '(?m)^#{1,6}\s+(.+?)\s*\r?$')
            $anchors = @($headings | ForEach-Object { ($_.Groups[1].Value.ToLowerInvariant() -replace '[^\p{L}\p{N}_\-\s]', '') -replace '\s', '-' })
            if ($parts[1] -notin $anchors) { throw "Missing heading in ${name}: $target" }
        }
    }
}
Write-Host 'README and release-note repository links passed.'
