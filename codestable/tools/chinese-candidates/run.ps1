param(
    [switch]$NativeAot,
    [string]$DictionaryDirectory
)
$ErrorActionPreference = 'Stop'
$root = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../../..'))
. (Join-Path $root 'scripts/toolchain.ps1')
$dotnet = Get-NaniKoreDotnetExecutable
if (-not $dotnet) { throw 'DOTNET_ROOT/PATH 中没有可用的 dotnet。' }
Set-NaniKoreNuGetCache
$project = Join-Path $PSScriptRoot 'ChineseCandidates.csproj'
$stamp = [DateTime]::UtcNow.ToString('yyyyMMdd-HHmmss-fff')
$mode = if ($NativeAot) { 'aot' } else { 'debug' }
$runRoot = Join-Path $root "artifacts/chinese-candidates/$stamp-$mode"
New-Item -ItemType Directory -Path $runRoot | Out-Null
$buildLog = Join-Path $runRoot 'build.log'
Push-Location -LiteralPath $root
try {
    # dev.ps1 固定构建桌面工程且递增生产配置版本；隔离探针直接使用同一工具链，禁止调用其构建入口。
    if ($NativeAot) {
        $publishDirectory = Join-Path $runRoot 'publish'
        & $dotnet publish $project -c Release -r win-x64 -p:PublishAot=true -o $publishDirectory *> $buildLog
        $built = $LASTEXITCODE
        $executable = Join-Path $publishDirectory 'ChineseCandidates.exe'
        $arguments = @()
    }
    else {
        & $dotnet build $project -c Debug *> $buildLog
        $built = $LASTEXITCODE
        $executable = $dotnet
        $arguments = @((Join-Path $PSScriptRoot 'bin/Debug/net11.0/ChineseCandidates.dll'))
    }
    if ($built -ne 0) { Write-Host "构建失败：$buildLog"; exit $built }
    $arguments += @($root, (Join-Path $runRoot 'comparison'))
    if ($DictionaryDirectory) { $arguments += [IO.Path]::GetFullPath($DictionaryDirectory) }
    # 不吞掉已知准确性失败；每次新进程均独立报告，不覆盖旧证据。
    & $executable @arguments *> (Join-Path $runRoot 'run.log')
    $result = $LASTEXITCODE
    "mode=$mode`nexit=$result`nsdk=$dotnet`nreport=$runRoot/comparison/report.txt" | Set-Content -LiteralPath (Join-Path $runRoot 'status.txt') -Encoding utf8
    Write-Host "exit=$result artifacts=$runRoot"
    exit $result
}
finally { Pop-Location }
