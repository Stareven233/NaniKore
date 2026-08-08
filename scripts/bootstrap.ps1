param()

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
. (Join-Path $PSScriptRoot 'toolchain.ps1')

$globalJson = Get-Content -LiteralPath (Join-Path $root 'global.json') -Raw | ConvertFrom-Json
$sdkVersion = $globalJson.sdk.version
$installDir = Get-NaniKoreSharedDotnetRoot
$solution = Join-Path $root 'NaniKore.slnx'
$dotnet = Get-NaniKoreDotnetExecutable

$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
$env:DOTNET_NOLOGO = '1'
Set-NaniKoreNuGetCache

if ($null -ne $dotnet) {
    Write-Host "Using configured .NET SDK: $dotnet"
    & $dotnet --version
    if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
}
else {
    if ($null -eq $installDir) {
        throw 'No usable .NET SDK was found. Set DOTNET_ROOT to the shared installation directory, then run this script again.'
    }

    $dotnet = Join-Path $installDir 'dotnet.exe'
    $installer = Join-Path (Split-Path -Parent $installDir) 'dotnet-install.ps1'
    New-Item -ItemType Directory -Force -Path (Split-Path -Parent $installDir) | Out-Null

    Write-Host 'Downloading official dotnet-install.ps1...'
    Invoke-WebRequest -UseBasicParsing 'https://dot.net/v1/dotnet-install.ps1' -OutFile $installer

    Write-Host "Installing .NET SDK $sdkVersion into $installDir..."
    & $installer -Version $sdkVersion -InstallDir $installDir -NoPath
    if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
}

& (Join-Path $PSScriptRoot 'bootstrap-models.ps1')
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

Write-Host "Restoring dependencies with NuGet cache: $env:NUGET_PACKAGES"
& $dotnet restore $solution -p:NuGetAudit=false
exit $LASTEXITCODE