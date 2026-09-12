param(
    [Parameter(Mandatory)][ValidateSet('Export','Import')][string]$Mode,
    [Parameter(Mandatory)][uri]$HostUrl,
    [Parameter(Mandatory)][string]$Path,
    [string]$AdminApiKey = $env:FABRCORE_ADMIN_API_KEY
)
$ErrorActionPreference = 'Stop'
if (-not $HostUrl.IsAbsoluteUri -or $HostUrl.Scheme -notin @('http','https')) { throw 'HostUrl must be an absolute HTTP(S) host URL.' }
if ($HostUrl.UserInfo -or $HostUrl.Query -or $HostUrl.Fragment) { throw 'HostUrl cannot contain credentials, a query or a fragment.' }
if ([string]::IsNullOrWhiteSpace($AdminApiKey)) { throw 'Set FABRCORE_ADMIN_API_KEY to the existing FabrCore admin credential.' }
$headers = @{ Authorization = "Bearer $AdminApiKey" }
$baseUrl = $HostUrl.AbsoluteUri.TrimEnd('/')
if ($Mode -eq 'Export') {
    if (Test-Path -LiteralPath $Path) { throw 'Export destination already exists. Choose a new path.' }
    $data = Invoke-RestMethod -Uri "$baseUrl/fabrcoreapi/admin/v1/access" -Headers $headers
    $json = $data | ConvertTo-Json -Depth 100
    $stream = [IO.File]::Open($Path, [IO.FileMode]::CreateNew, [IO.FileAccess]::Write)
    try {
        $writer = [IO.StreamWriter]::new($stream, [Text.UTF8Encoding]::new($false))
        try { $writer.Write($json) } finally { $writer.Dispose() }
    } finally { $stream.Dispose() }
} else {
    $body = Get-Content -LiteralPath $Path -Raw
    $null = $body | ConvertFrom-Json -ErrorAction Stop
    Invoke-RestMethod -Method Post -Uri "$baseUrl/fabrcoreapi/admin/v1/access/import" -Headers $headers -ContentType 'application/json' -Body $body
}
