param()

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
. (Join-Path $PSScriptRoot 'toolchain.ps1')

$globalJson = Get-Content -LiteralPath (Join-Path $root 'global.json') -Raw | ConvertFrom-Json
$sdkVersion = $globalJson.sdk.version
# 使用主次版本通道安装最新稳定 SDK；global.json 仍保留可复现的最低兼容版本。
$sdkChannel = ($sdkVersion -split '\.')[0..1] -join '.'
$installDir = Get-NaniKoreSharedDotnetRoot
$solution = Join-Path $root 'NaniKore.slnx'
$dotnet = Get-NaniKoreDotnetExecutable

$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
$env:DOTNET_NOLOGO = '1'
Set-NaniKoreNuGetCache

# dotnet.exe 可能仅包含运行时，或只安装了与 global.json 不兼容的 SDK。
# 因此不能只凭可执行文件存在就跳过 SDK 安装。
$hasCompatibleSdk = $null -ne $dotnet -and (Test-NaniKoreSdkResolution -Dotnet $dotnet -WorkingDirectory $root)
if ($hasCompatibleSdk) {
    Write-Host "Using compatible .NET SDK: $dotnet"
    Push-Location -LiteralPath $root
    try {
        & $dotnet --version
        if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
    }
    finally {
        Pop-Location
    }
}
else {
    if ([string]::IsNullOrWhiteSpace($installDir)) {
        throw 'No compatible .NET SDK was found. Set DOTNET_ROOT to the directory where the shared SDK should be installed, then run this script again.'
    }

    $dotnet = Join-Path $installDir 'dotnet.exe'
    $installer = Join-Path (Split-Path -Parent $installDir) 'dotnet-install.ps1'
    New-Item -ItemType Directory -Force -Path (Split-Path -Parent $installDir) | Out-Null

    Write-Host 'Downloading official dotnet-install.ps1...'
    Invoke-WebRequest -UseBasicParsing 'https://dot.net/v1/dotnet-install.ps1' -OutFile $installer

    Write-Host "Installing the latest .NET SDK from channel $sdkChannel into $installDir (global.json minimum: $sdkVersion)..."
    & $installer -Channel $sdkChannel -InstallDir $installDir -NoPath
    if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

    if (-not (Test-NaniKoreSdkResolution -Dotnet $dotnet -WorkingDirectory $root)) {
        throw "The installation completed, but .NET SDK $sdkVersion could not be resolved from global.json. Verify DOTNET_ROOT: $installDir"
    }
}

Write-Host "Restoring dependencies with NuGet cache: $env:NUGET_PACKAGES"
& $dotnet restore $solution -p:NuGetAudit=false
exit $LASTEXITCODE
