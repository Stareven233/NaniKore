param(
    [ValidateSet('build', 'run', 'test', 'publish')]
    [string]$Command = 'build'
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$dotnet = Join-Path $root '.tools\dotnet\dotnet.exe'
$solution = Join-Path $root 'NaniKore.slnx'
$project = Join-Path $root 'src\NaniKore\NaniKore.csproj'

if (-not (Test-Path -LiteralPath $dotnet)) {
    throw 'Project-local .NET SDK is missing. Run: powershell -ExecutionPolicy Bypass -File scripts/bootstrap.ps1'
}

$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
$env:DOTNET_NOLOGO = '1'

switch ($Command) {
    'build' {
        & $dotnet build $solution --configuration Debug -p:NuGetAudit=false -p:RestoreIgnoreFailedSources=true
    }
    'run' {
        & $dotnet run --project $project -p:NuGetAudit=false -p:RestoreIgnoreFailedSources=true
    }
    'test' {
        & $dotnet run --project $project -p:NuGetAudit=false -p:RestoreIgnoreFailedSources=true -- --self-test
    }
    'publish' {
        $output = Join-Path $root 'artifacts\win-x64'
        $stalePdb = Join-Path $output 'NaniKore.pdb'
        $staleOnnxRuntime = Join-Path $output 'onnxruntime.dll'
        $staleOnnxProviders = Join-Path $output 'onnxruntime_providers_shared.dll'
        Remove-Item -LiteralPath $stalePdb, $staleOnnxRuntime, $staleOnnxProviders -Force -ErrorAction SilentlyContinue

        & $dotnet publish $project --configuration Release --runtime win-x64 --self-contained false -p:PublishSingleFile=true -p:DebugType=None -p:DebugSymbols=false --output $output -p:NuGetAudit=false -p:RestoreIgnoreFailedSources=true
        Remove-Item -LiteralPath (Join-Path $output 'onnxruntime.lib'), (Join-Path $output 'onnxruntime_providers_shared.lib') -Force -ErrorAction SilentlyContinue
    }
}

exit $LASTEXITCODE
