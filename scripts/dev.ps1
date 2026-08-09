param(
    [ValidateSet('build', 'run', 'test', 'publish')]
    [string]$Command = 'build',

    [ValidateSet('debug', 'publish')]
    [string]$Launch
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
. (Join-Path $PSScriptRoot 'toolchain.ps1')

$solution = Join-Path $root 'NaniKore.slnx'
$project = Join-Path $root 'src\NaniKore\NaniKore.csproj'
$debugExecutable = Join-Path $root 'src\NaniKore\bin\Debug\net10.0-windows10.0.19041.0\NaniKore.exe'
$publishOutput = Join-Path $root 'artifacts\win-x64'
$publishedExecutable = Join-Path $publishOutput 'NaniKore.exe'

function Start-NaniKoreExecutable {
    param(
        [Parameter(Mandatory)]
        [string]$Path,

        [Parameter(Mandatory)]
        [string]$Description
    )

    if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) {
        throw "$Description executable was not found: $Path. Run the corresponding build or publish command first."
    }

    # 从仓库根目录启动，确保程序可直接找到项目本地的 models/ 目录。
    Start-Process -FilePath $Path -WorkingDirectory $root
}

if ($PSBoundParameters.ContainsKey('Launch')) {
    if ($PSBoundParameters.ContainsKey('Command')) {
        throw '-Launch only starts an existing executable. Do not combine it with a build, run, test, or publish command.'
    }

    switch ($Launch) {
        'debug' {
            Start-NaniKoreExecutable -Path $debugExecutable -Description 'Debug build'
        }
        'publish' {
            Start-NaniKoreExecutable -Path $publishedExecutable -Description 'Published'
        }
    }

    exit 0
}

$dotnet = Get-NaniKoreDotnetExecutable
if ($null -eq $dotnet) {
    throw 'No usable .NET SDK was found. Configure DOTNET_ROOT or dotnet on Path, or run scripts/bootstrap.ps1.'
}

$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
$env:DOTNET_NOLOGO = '1'
Set-NaniKoreNuGetCache

if (-not (Test-NaniKoreSdkResolution -Dotnet $dotnet -WorkingDirectory $root)) {
    throw 'No SDK compatible with global.json was found. Run scripts/bootstrap.ps1 to install the required SDK.'
}

$exitCode = 1
Push-Location -LiteralPath $root
try {
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
            # 发布物固定保留在 artifacts\win-x64，避免将 EXE 与运行时 DLL 写到仓库根目录。
            $stalePdb = Join-Path $publishOutput 'NaniKore.pdb'
            $staleOnnxRuntime = Join-Path $publishOutput 'onnxruntime.dll'
            $staleOnnxProviders = Join-Path $publishOutput 'onnxruntime_providers_shared.dll'
            Remove-Item -LiteralPath $stalePdb, $staleOnnxRuntime, $staleOnnxProviders -Force -ErrorAction SilentlyContinue

            & $dotnet publish $project --configuration Release --runtime win-x64 --self-contained false -p:PublishSingleFile=true -p:DebugType=None -p:DebugSymbols=false --output $publishOutput -p:NuGetAudit=false -p:RestoreIgnoreFailedSources=true
            Remove-Item -LiteralPath (Join-Path $publishOutput 'onnxruntime.lib'), (Join-Path $publishOutput 'onnxruntime_providers_shared.lib') -Force -ErrorAction SilentlyContinue
        }
    }

    $exitCode = $LASTEXITCODE
}
finally {
    Pop-Location
}

exit $exitCode
