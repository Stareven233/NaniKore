param()

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$globalJson = Get-Content -LiteralPath (Join-Path $root 'global.json') -Raw | ConvertFrom-Json
$sdkVersion = $globalJson.sdk.version
$installDir = Join-Path $root '.tools\dotnet'
$dotnet = Join-Path $installDir 'dotnet.exe'
$solution = Join-Path $root 'NaniKore.slnx'

$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
$env:DOTNET_NOLOGO = '1'

if (Test-Path -LiteralPath $dotnet) {
    Write-Host "Project-local .NET SDK already exists: $dotnet"
    & $dotnet --version
    if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
}
else {
    New-Item -ItemType Directory -Force -Path (Join-Path $root '.tools') | Out-Null
    $installer = Join-Path $root '.tools\dotnet-install.ps1'

    Write-Host 'Downloading official dotnet-install.ps1...'
    Invoke-WebRequest -UseBasicParsing 'https://dot.net/v1/dotnet-install.ps1' -OutFile $installer

    Write-Host "Installing .NET SDK $sdkVersion into $installDir..."
    & $installer -Version $sdkVersion -InstallDir $installDir -NoPath
    if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
}

& (Join-Path $PSScriptRoot 'bootstrap-models.ps1')
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

Write-Host 'Restoring project-local NuGet dependencies...'
& $dotnet restore $solution -p:NuGetAudit=false
exit $LASTEXITCODE