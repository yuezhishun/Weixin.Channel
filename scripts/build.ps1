param([string]$Dotnet = $env:WEIXIN_DOTNET)
$ErrorActionPreference = 'Stop'
$projectRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
if (-not $Dotnet) { $Dotnet = 'dotnet' }
$Dotnet = (Get-Command $Dotnet -ErrorAction Stop).Source
Push-Location $projectRoot
try {
    $expected = (Get-Content -Raw -LiteralPath (Join-Path $projectRoot 'global.json') | ConvertFrom-Json).sdk.version
    $actual = & $Dotnet --version
    if ($LASTEXITCODE -ne 0 -or $actual.Trim() -ne $expected) { throw "需要 SDK $expected，实际为 $actual" }
    $stamp = Get-Date -Format 'yyyyMMdd-HHmmss'
    $evidence = Join-Path $projectRoot "artifacts/verification/windows-$stamp"
    $packages = Join-Path $projectRoot 'artifacts/packages/win-x64'
    $publish = Join-Path $projectRoot "artifacts/publish/win-x64-$stamp"
    New-Item -ItemType Directory -Force -Path $evidence, $packages, $publish | Out-Null
    function Invoke-Dotnet([string[]]$DotnetArguments) {
        & $Dotnet @DotnetArguments 2>&1 | Tee-Object -FilePath (Join-Path $evidence 'build.log') -Append | Out-Host
        if ($LASTEXITCODE -ne 0) { throw "dotnet $($DotnetArguments -join ' ') 失败，退出码 $LASTEXITCODE" }
    }
    Invoke-Dotnet @('--info')
    Invoke-Dotnet @('restore', 'Weixin.Channel.slnx', '--locked-mode')
    Invoke-Dotnet @('build', 'Weixin.Channel.slnx', '-c', 'Release', '--no-restore', '-warnaserror')
    Invoke-Dotnet @('test', 'tests/Weixin.Channel.Tests/Weixin.Channel.Tests.csproj', '-c', 'Release', '--no-restore', '--no-build', '--logger', 'trx;LogFileName=weixin.trx', '--results-directory', $evidence)
    [xml]$testResult = Get-Content -Raw -LiteralPath (Join-Path $evidence 'weixin.trx')
    $counters = $testResult.TestRun.ResultSummary.Counters
    if ([int]$counters.total -le 0 -or [int]$counters.notExecuted -ne 0 -or [int]$counters.failed -ne 0) { throw '测试为空、失败或包含跳过，不满足验收要求。' }
    foreach ($app in @('Weixin.Cli', 'Weixin.Web')) {
        Invoke-Dotnet @('publish', "samples/$app/$app.csproj", '-c', 'Release', '-r', 'win-x64', '--self-contained', 'true', '-p:RestoreLockedMode=true', '-o', (Join-Path $publish $app), '-warnaserror')
    }
    & (Join-Path $PSScriptRoot 'verify-web.ps1') -Dotnet $Dotnet -PublishDirectory $publish
    Copy-Item -LiteralPath (Join-Path $projectRoot 'README.md'), (Join-Path $projectRoot 'docs'), (Join-Path $projectRoot 'third-party') -Destination $publish -Recurse
    $archive = Join-Path $projectRoot "artifacts/Weixin.Channel-win-x64-$stamp.zip"
    [IO.Compression.ZipFile]::CreateFromDirectory($publish, $archive)
    Invoke-Dotnet @('pack', 'src/Weixin.Channel/Weixin.Channel.csproj', '-c', 'Release', '--no-restore', '--no-build', '-o', $packages, '-warnaserror')
    $consumer = Join-Path ([IO.Path]::GetTempPath()) ('weixin-consumer-' + [Guid]::NewGuid().ToString('N'))
    New-Item -ItemType Directory -Path $consumer | Out-Null
    Copy-Item -LiteralPath (Join-Path $projectRoot 'global.json') -Destination $consumer
    Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'consumer.csproj.template') -Destination (Join-Path $consumer 'Consumer.csproj')
    Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'consumer.cs.template') -Destination (Join-Path $consumer 'Program.cs')
    $packageSource = [Security.SecurityElement]::Escape($packages)
    @"
<?xml version="1.0" encoding="utf-8"?>
<configuration>
  <packageSources>
    <clear />
    <add key="local-weixin" value="$packageSource" />
    <add key="nuget.org" value="https://api.nuget.org/v3/index.json" />
  </packageSources>
</configuration>
"@ | Set-Content -LiteralPath (Join-Path $consumer 'NuGet.Config') -Encoding utf8
    Push-Location $consumer
    try {
        Invoke-Dotnet @('restore', 'Consumer.csproj', '--configfile', (Join-Path $consumer 'NuGet.Config'), '--packages', (Join-Path $consumer 'packages'))
        Invoke-Dotnet @('restore', 'Consumer.csproj', '--locked-mode', '--configfile', (Join-Path $consumer 'NuGet.Config'), '--packages', (Join-Path $consumer 'packages'))
        Invoke-Dotnet @('run', '--project', 'Consumer.csproj', '-c', 'Release', '--no-restore')
    }
    finally { Pop-Location }
    "独立消费者：$consumer`n测试数：$($counters.total)`n跳过：$($counters.notExecuted)`n真机验收：未执行；见 docs/acceptance.md" | Set-Content -LiteralPath (Join-Path $evidence 'result.txt') -Encoding utf8
    "$((Get-FileHash -LiteralPath $archive -Algorithm SHA256).Hash.ToLowerInvariant())  $(Split-Path $archive -Leaf)" | Set-Content -LiteralPath (Join-Path $evidence 'release.sha256')
    Write-Host "构建、测试、自包含发布和独立 nupkg 消费通过。证据：$evidence"
}
finally { Pop-Location }
