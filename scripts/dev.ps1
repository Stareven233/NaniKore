param(
    [ValidateSet('build', 'run', 'test', 'publish')]
    [string]$Command = 'build',

    [ValidateSet('build', 'publish')]
    [string]$Launch,

    [Alias('ocr-models')]
    [switch]$OcrModels
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
. (Join-Path $PSScriptRoot 'toolchain.ps1')

$solution = Join-Path $root 'NaniKore.slnx'
$project = Join-Path $root 'src\NaniKore\NaniKore.csproj'
$debugExecutable = Join-Path $root 'src\NaniKore\bin\Debug\net10.0-windows10.0.19041.0\NaniKore.exe'
$artifactsDirectory = Join-Path $root 'artifacts'
$publishOutput = Join-Path $root 'artifacts\win-x64'
$publishedExecutable = Join-Path $publishOutput 'NaniKore.exe'
$ocrModelsDirectory = Join-Path $root 'models'
$releaseRuntimeOutput = Join-Path $root 'src\NaniKore\bin\Release\net10.0-windows10.0.19041.0\win-x64'

function Get-NaniKoreProductVersion {
    param(
        [Parameter(Mandatory)]
        [string]$ConfigPath
    )

    $configContent = Get-Content -LiteralPath $ConfigPath -Raw
    $versionMatch = [regex]::Match($configContent, '(?m)^\s*version\s*:\s*["'']?(?<version>[^#\r\n"''\s]+)')
    if (-not $versionMatch.Success) {
        throw "Product version was not found in $ConfigPath."
    }

    $version = $versionMatch.Groups['version'].Value
    if ($version.IndexOfAny([System.IO.Path]::GetInvalidFileNameChars()) -ge 0) {
        throw "Product version '$version' contains characters that cannot be used in an archive filename."
    }

    return $version
}

function New-NaniKoreReleaseArchive {
    param(
        [Parameter(Mandatory)]
        [string]$PublishOutput,

        [Parameter(Mandatory)]
        [string]$ArtifactsDirectory,

        [Parameter(Mandatory)]
        [string]$ModelsDirectory,

        [Parameter(Mandatory)]
        [string]$ProductVersion,

        [switch]$IncludeOcrModels
    )

    if (-not (Test-Path -LiteralPath $PublishOutput -PathType Container)) {
        throw "Publish output was not found: $PublishOutput"
    }

    if ($IncludeOcrModels) {
        $defaultOcrModels = Join-Path $ModelsDirectory 'pp-ocr-v6-small'
        if (-not (Test-Path -LiteralPath $defaultOcrModels -PathType Container)) {
            throw "OCR models were not found: $defaultOcrModels. Run scripts/bootstrap-models.ps1 before publishing with -OcrModels."
        }
    }

    $archiveSuffix = if ($IncludeOcrModels) { '' } else { '-without-ocr-models' }
    $archivePath = Join-Path $ArtifactsDirectory "NaniKore-v$ProductVersion-win_amd64$archiveSuffix.zip"
    $stagingDirectory = Join-Path $ArtifactsDirectory ".package-staging-$([guid]::NewGuid().ToString('N'))"

    try {
        New-Item -ItemType Directory -Path $stagingDirectory | Out-Null

        # 将发布目录内容直接压入 ZIP 根目录，确保 models/ 与 EXE、DLL、config.yaml 同级。
        Get-ChildItem -LiteralPath $PublishOutput -Force |
            Copy-Item -Destination $stagingDirectory -Recurse -Force

        if ($IncludeOcrModels) {
            Copy-Item -LiteralPath $ModelsDirectory -Destination $stagingDirectory -Recurse -Force
        }

        $archiveEntries = @(Get-ChildItem -LiteralPath $stagingDirectory -Force)
        if ($archiveEntries.Count -eq 0) {
            throw "Package staging directory is empty: $stagingDirectory"
        }

        Compress-Archive -LiteralPath $archiveEntries.FullName -DestinationPath $archivePath -CompressionLevel Optimal -Force
    }
    finally {
        if (Test-Path -LiteralPath $stagingDirectory) {
            Remove-Item -LiteralPath $stagingDirectory -Recurse -Force
        }
    }

    return $archivePath
}

function Copy-NaniKoreOnnxRuntimeNativeLibraries {
    param(
        [Parameter(Mandatory)]
        [string]$BuildOutput,

        [Parameter(Mandatory)]
        [string]$PublishOutput
    )

    # single-file 发布会捆绑 ONNX Runtime 的原生 DLL；发行目录仍须保留外置副本，供加载器和 ZIP 分发使用。
    $nativeRuntimeFiles = @(
        'onnxruntime.dll',
        'onnxruntime_providers_shared.dll'
    )
    $missingNativeRuntimeFiles = @(
        $nativeRuntimeFiles |
            Where-Object { -not (Test-Path -LiteralPath (Join-Path $BuildOutput $_) -PathType Leaf) }
    )
    if ($missingNativeRuntimeFiles.Count -gt 0) {
        throw "Required ONNX Runtime native DLLs were not generated: $($missingNativeRuntimeFiles -join ', ')."
    }

    foreach ($nativeRuntimeFile in $nativeRuntimeFiles) {
        Copy-Item -LiteralPath (Join-Path $BuildOutput $nativeRuntimeFile) -Destination $PublishOutput -Force
    }
}

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
        'build' {
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
            $exitCode = $LASTEXITCODE
        }
        'run' {
            & $dotnet run --project $project -p:NuGetAudit=false -p:RestoreIgnoreFailedSources=true
            $exitCode = $LASTEXITCODE
        }
        'test' {
            & $dotnet run --project $project -p:NuGetAudit=false -p:RestoreIgnoreFailedSources=true -- --self-test
            $exitCode = $LASTEXITCODE
        }
        'publish' {
            # 发布物固定保留在 artifacts\win-x64，避免将 EXE 与运行时 DLL 写到仓库根目录。
            $stalePdb = Join-Path $publishOutput 'NaniKore.pdb'
            Remove-Item -LiteralPath $stalePdb -Force -ErrorAction SilentlyContinue

            & $dotnet publish $project --configuration Release --runtime win-x64 --self-contained false -p:PublishSingleFile=true -p:DebugType=None -p:DebugSymbols=false --output $publishOutput -p:NuGetAudit=false -p:RestoreIgnoreFailedSources=true
            $exitCode = $LASTEXITCODE
            if ($exitCode -eq 0) {
                Remove-Item -LiteralPath (Join-Path $publishOutput 'onnxruntime.lib'), (Join-Path $publishOutput 'onnxruntime_providers_shared.lib') -Force -ErrorAction SilentlyContinue
                Copy-NaniKoreOnnxRuntimeNativeLibraries -BuildOutput $releaseRuntimeOutput -PublishOutput $publishOutput
                $productVersion = Get-NaniKoreProductVersion -ConfigPath (Join-Path $root 'config.yaml')
                $archivePath = New-NaniKoreReleaseArchive `
                    -PublishOutput $publishOutput `
                    -ArtifactsDirectory $artifactsDirectory `
                    -ModelsDirectory $ocrModelsDirectory `
                    -ProductVersion $productVersion `
                    -IncludeOcrModels:$OcrModels
                Write-Host "Created release archive: $archivePath"
            }
        }
    }
}
finally {
    Pop-Location
}

exit $exitCode
