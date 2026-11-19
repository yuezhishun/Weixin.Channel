param([string]$Dotnet = $env:WEIXIN_DOTNET, [string]$PublishDirectory)
$ErrorActionPreference = 'Stop'
$projectRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
if (-not $Dotnet) { $Dotnet = 'dotnet' }
$Dotnet = (Get-Command $Dotnet -ErrorAction Stop).Source
$portProbe = [Net.Sockets.TcpListener]::new([Net.IPAddress]::Loopback, 3083)
try { $portProbe.Start() }
catch { throw '127.0.0.1:3083 已被占用；为避免误操作已有服务，Web smoke 拒绝继续。' }
finally { $portProbe.Stop() }
$evidence = Join-Path $projectRoot ('artifacts/verification/web-' + (Get-Date -Format 'yyyyMMdd-HHmmss'))
$state = Join-Path $evidence 'state'
New-Item -ItemType Directory -Force -Path $evidence | Out-Null
if ($PublishDirectory) {
    $serverCommand = Join-Path $PublishDirectory 'Weixin.Web/Weixin.Web.exe'
    $serverArguments = @()
    $cliCommand = Join-Path $PublishDirectory 'Weixin.Cli/Weixin.Cli.exe'
    $cliArguments = @()
} else {
    $serverCommand = $Dotnet
    $serverArguments = @(('"{0}"' -f (Join-Path $projectRoot 'samples/Weixin.Web/bin/Release/net11.0/Weixin.Web.dll')))
    $cliCommand = $Dotnet
    $cliArguments = @((Join-Path $projectRoot 'samples/Weixin.Cli/bin/Release/net11.0/Weixin.Cli.dll'))
}
$serverArguments += @('--state', ('"{0}"' -f $state))
$server = Start-Process -FilePath $serverCommand -ArgumentList $serverArguments -PassThru -WindowStyle Hidden -RedirectStandardOutput (Join-Path $evidence 'server.log') -RedirectStandardError (Join-Path $evidence 'server-error.log')
$client = [Net.Http.HttpClient]::new()
$client.Timeout = [TimeSpan]::FromSeconds(10)
$base = 'http://127.0.0.1:3083'
function Assert-Http([string]$Path, [int]$Expected, [hashtable]$Headers = @{ 'x-weixin-request' = '1' }, [string]$Method = 'GET', [string]$Body = '') {
    $request = [Net.Http.HttpRequestMessage]::new([Net.Http.HttpMethod]::new($Method), $base + $Path)
    foreach ($name in $Headers.Keys) { $request.Headers.TryAddWithoutValidation($name, [string]$Headers[$name]) | Out-Null }
    if ($Body) { $request.Content = [Net.Http.StringContent]::new($Body, [Text.Encoding]::UTF8, 'application/json') }
    $response = $client.SendAsync($request).GetAwaiter().GetResult()
    try {
        $text = $response.Content.ReadAsStringAsync().GetAwaiter().GetResult()
        if ([int]$response.StatusCode -ne $Expected) { throw "$Method $Path 期望 $Expected，实际 $([int]$response.StatusCode)：$text" }
        if ($Expected -ge 400 -and $response.Content.Headers.ContentType.MediaType -ne 'application/problem+json') { throw "$Path 未返回 ProblemDetails" }
        "$Method $Path -> $Expected" | Tee-Object -FilePath (Join-Path $evidence 'checks.log') -Append | Out-Host
        return $text
    }
    finally { $response.Dispose(); $request.Dispose() }
}
try {
    $ready = $false
    for ($attempt = 0; $attempt -lt 50; $attempt++) {
        if ($server.HasExited) { throw 'Web 在启动时退出，请检查 server-error.log。' }
        try { $null = Assert-Http '/health' 200; $ready = $true; break } catch { Start-Sleep -Milliseconds 100 }
    }
    if (-not $ready) { throw 'Web 未在5秒内就绪。' }
    $null = Assert-Http '/health' 403 @{}
    $null = Assert-Http '/health' 403 @{ 'x-weixin-request' = '1'; Host = 'evil.example:3083' }
    $null = Assert-Http '/health' 403 @{ 'x-weixin-request' = '1'; Host = '127.0.0.1:9999' }
    $null = Assert-Http '/health' 403 @{ 'x-weixin-request' = '1'; Origin = 'https://evil.example' }
    $null = Assert-Http '/health' 200 @{ 'x-weixin-request' = '1'; Origin = $base }
    if ((Assert-Http '/api/accounts' 200 | ConvertFrom-Json).Count -ne 0) { throw '空状态目录出现账号。' }
    $null = Assert-Http '/api/inbox/pending' 200
    $null = Assert-Http '/api/inbox/claim' 204 @{ 'x-weixin-request' = '1' } 'POST'
    $null = Assert-Http '/api/logins/missing' 404
    $null = Assert-Http '/api/logins/missing/code' 400 @{ 'x-weixin-request' = '1' } 'POST' '{"code":"abc"}'
    $null = Assert-Http '/api/messages' 400 @{ 'x-weixin-request' = '1' } 'POST' '{'
    $null = Assert-Http '/api/messages/timing' 400 @{ 'x-weixin-request' = '1' } 'POST' '{"deliveryId":"test","operation":"test","elapsedMilliseconds":-1}'
    $null = Assert-Http '/missing' 404
    $null = Assert-Http '/openapi/v1.json' 200
    & $Dotnet run --file (Join-Path $projectRoot 'tests/tools/WebSmokeFixture.cs') -c Release -- $state $evidence 2>&1 | Tee-Object -FilePath (Join-Path $evidence 'fixture.log') | Out-Host
    if ($LASTEXITCODE -ne 0) { throw '非空 SSE / OpenAPI schema / 持久收据验证失败。' }
    $conflict = & $cliCommand @cliArguments status --state $state 2>&1
    if ($LASTEXITCODE -eq 0 -or ($conflict -join '') -notmatch '状态目录正被另一个进程使用') { throw 'CLI/Web 状态目录冲突没有明确失败。' }
    $conflict | Set-Content -LiteralPath (Join-Path $evidence 'directory-conflict.log')
}
finally {
    $client.Dispose()
    if (-not $server.HasExited) { $server.Kill(); $server.WaitForExit() }
    $server.Dispose()
}
$cliReceipts = & $cliCommand @cliArguments receipts web-receipt-fixture --state $state
if ($LASTEXITCODE -ne 0 -or ($cliReceipts | ConvertFrom-Json).Count -ne 3 -or ($cliReceipts -join '') -match 'FIXTURE_SECRET_MUST_NOT_APPEAR') { throw 'CLI未正确恢复脱敏持久收据。' }
$cliReceipts | Set-Content -LiteralPath (Join-Path $evidence 'cli-receipts.json')
"HTTP边界、ProblemDetails、OpenAPI响应schema、非空SSE无副作用、租约、持久收据与跨进程目录互斥全部通过；未请求真实微信服务。" | Set-Content -LiteralPath (Join-Path $evidence 'result.txt')
Write-Host "Web 验证通过：$evidence"
