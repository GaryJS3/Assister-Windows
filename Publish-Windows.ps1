[CmdletBinding()]
param([uri]$Server = 'http://10.44.0.33:8081', [string]$Version = '2026.10.7.1')
$ErrorActionPreference = 'Stop'
if ($Server.AbsolutePath -ne '/' -or $Server.Scheme -notin @('http','https') -or $Server.UserInfo -or $Server.Query -or $Server.Fragment) { throw 'Supply a server origin.' }
$root = $PSScriptRoot
$package = Join-Path $root 'artifacts\windows-x64\Assister.Windows.App.exe'
dotnet publish (Join-Path $root 'Assister.Windows.App') -c Release -r win-x64 --self-contained true -p:Version=$Version -o (Join-Path $root 'artifacts\windows-x64')
if ($LASTEXITCODE -ne 0) { throw 'Publish failed.' }
$size = (Get-Item $package).Length
$hash = (Get-FileHash $package -Algorithm SHA256).Hash
$credential = (Get-Content (Join-Path $root 'assister-dev-token.txt') -Raw).Trim()
$handler = [System.Net.Http.HttpClientHandler]::new()
$handler.AllowAutoRedirect = $false
$client = [System.Net.Http.HttpClient]::new($handler)
$client.Timeout = [TimeSpan]::FromMinutes(10)
$request = [System.Net.Http.HttpRequestMessage]::new([System.Net.Http.HttpMethod]::Put, [uri]::new($Server, "api/updates/assister/windows-x64/$Version`?name=Assister.Windows.App.exe"))
$request.Headers.Authorization = [System.Net.Http.Headers.AuthenticationHeaderValue]::new('Bearer', $credential)
$request.Content = [System.Net.Http.StreamContent]::new([System.IO.File]::OpenRead($package))
$request.Content.Headers.ContentType = [System.Net.Http.Headers.MediaTypeHeaderValue]::new('application/octet-stream')
try {
    $response = $client.SendAsync($request).GetAwaiter().GetResult()
    if (-not $response.IsSuccessStatusCode) { throw "Upload failed: HTTP $([int]$response.StatusCode)." }
    $release = $response.Content.ReadAsStringAsync().GetAwaiter().GetResult() | ConvertFrom-Json
    if ($release.size -ne $size -or $release.sha256 -ne $hash) { throw 'Published metadata mismatch.' }
    $url = [uri]::new($Server, $release.downloadUrl)
    if ($url.Scheme -ne $Server.Scheme -or $url.Authority -ne $Server.Authority -or $url.UserInfo) { throw 'Download origin mismatch.' }
    $verification = Join-Path $root 'artifacts\verify-update.exe'
    $download = $client.GetAsync($url, [System.Net.Http.HttpCompletionOption]::ResponseHeadersRead).GetAwaiter().GetResult()
    $download.EnsureSuccessStatusCode() | Out-Null
    $stream = [System.IO.File]::Create($verification)
    try { $download.Content.CopyToAsync($stream).GetAwaiter().GetResult() } finally { $stream.Dispose(); $download.Dispose() }
    if ((Get-Item $verification).Length -ne $size -or (Get-FileHash $verification -Algorithm SHA256).Hash -ne $hash) { throw 'Public download verification failed.' }
    Write-Host "Published and verified Windows $Version ($size bytes)."
} finally { $request.Dispose(); $client.Dispose(); $credential = $null }
