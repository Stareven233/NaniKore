param(
    [ValidateSet('build', 'run', 'test', 'publish')]
    [string]$Command = 'build',

    [ValidateSet('build', 'publish')]
    [string]$Launch,

    [switch]$Full
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
$buildOutputDirectory = Join-Path $root 'src\NaniKore\bin'
$dependenciesDirectory = Join-Path $root 'deps'

$onnxRuntimeNativeFiles = @(
    'onnxruntime.dll',
    'onnxruntime_providers_shared.dll'
)

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

function Set-NaniKoreOnnxRuntimeLinks {
    param(
        [Parameter(Mandatory)]
        [string]$OutputDirectory,

        [Parameter(Mandatory)]
        [string]$DependenciesDirectory
    )

    if (-not (Test-Path -LiteralPath $OutputDirectory -PathType Container)) {
        return
    }

    $runtimeDirectories = @(Get-ChildItem -LiteralPath $OutputDirectory -Recurse -Directory -Force -Filter 'runtimes')
    foreach ($runtimeDirectory in $runtimeDirectories) {
        # 当前应用固定发布 win-x64，NuGet 附带的 arm64/Linux/Android 等 RID 资产没有保留价值。
        Get-ChildItem -LiteralPath $runtimeDirectory.FullName -Directory -Force |
            Where-Object { $_.Name -ne 'win-x64' } |
            Remove-Item -Recurse -Force
    }

    foreach ($fileName in $onnxRuntimeNativeFiles) {
        $source = Join-Path $DependenciesDirectory "onnxruntime\\$fileName"
        if (-not (Test-Path -LiteralPath $source -PathType Leaf)) {
            continue
        }

        $sourceHash = (Get-FileHash -LiteralPath $source -Algorithm SHA256).Hash
        $destinations = @(Get-ChildItem -LiteralPath $OutputDirectory -Recurse -Force -File -Filter $fileName |
            Where-Object { $_.FullName -ne $source })
        foreach ($destination in $destinations) {
            if ((Get-FileHash -LiteralPath $destination.FullName -Algorithm SHA256).Hash -ne $sourceHash) {
                continue
            }

            # 先创建临时硬链接再替换原文件，避免硬链接创建失败时丢失构建产物。
            $temporaryLink = "$($destination.FullName).link-$([guid]::NewGuid().ToString('N'))"
            try {
                New-Item -ItemType HardLink -Path $temporaryLink -Target $source | Out-Null
                Remove-Item -LiteralPath $destination.FullName -Force
                Move-Item -LiteralPath $temporaryLink -Destination $destination.FullName
            }
            finally {
                Remove-Item -LiteralPath $temporaryLink -Force -ErrorAction SilentlyContinue
            }
        }
    }
}

function Remove-NaniKoreGeneratedImportLibraries {
    param(
        [Parameter(Mandatory)]
        [string]$OutputDirectory
    )

    if (-not (Test-Path -LiteralPath $OutputDirectory -PathType Container)) {
        return
    }

    # .lib 是 Windows 链接器导入库，应用运行时只需要 DLL；发布目录不应携带它们。
    Get-ChildItem -LiteralPath $OutputDirectory -Recurse -Force -File -Filter '*.lib' |
        Remove-Item -Force
}

function Remove-NaniKorePackageCacheFiles {
    param(
        [Parameter(Mandatory)]
        [string]$Directory
    )

    if (-not (Test-Path -LiteralPath $Directory -PathType Container)) {
        return
    }

    # ModelScope 下载工具生成的缓存不是 OCR 运行时依赖，不进入发行 ZIP。
    Get-ChildItem -LiteralPath $Directory -Recurse -Force |
        Where-Object { $_.Name -eq '.ms_upload_cache' } |
        Remove-Item -Recurse -Force
}

function New-NaniKoreReleaseArchive {
    param(
        [Parameter(Mandatory)]
        [string]$PublishOutput,

        [Parameter(Mandatory)]
        [string]$ArtifactsDirectory,

        [Parameter(Mandatory)]
        [string]$DependenciesDirectory,

        [Parameter(Mandatory)]
        [string]$ProductVersion,

        [switch]$IncludeFullDependencies
    )

    if (-not (Test-Path -LiteralPath $PublishOutput -PathType Container)) {
        throw "Publish output was not found: $PublishOutput"
    }

    if ($IncludeFullDependencies) {
        # 完整包承诺「解压即用」：本地 OCR 权重、ONNX Runtime 与三语注音词典缺一不可（issue 008）
        $requiredDependencies = @(
            'ocr-onnx\pp-ocr-v6-small\det\inference.onnx',
            'ocr-onnx\pp-ocr-v6-small\det\inference.yml',
            'ocr-onnx\pp-ocr-v6-small\rec\inference.onnx',
            'ocr-onnx\pp-ocr-v6-small\rec\inference.yml',
            'onnxruntime\onnxruntime.dll',
            'onnxruntime\onnxruntime_providers_shared.dll',
            'phonetics\ja\sys.dic',
            'phonetics\ja\unk.dic',
            'phonetics\ja\matrix.bin',
            'phonetics\ja\char.bin',
            'phonetics\en\ipa_dict.db',
            'phonetics\zh\sys.dic',
            'phonetics\zh\unk.dic',
            'phonetics\zh\matrix.bin',
            'phonetics\zh\char.bin'
        )
        $missingDependencies = @(
            $requiredDependencies |
                Where-Object { -not (Test-Path -LiteralPath (Join-Path $DependenciesDirectory $_) -PathType Leaf) }
        )
        if ($missingDependencies.Count -gt 0) {
            throw "Required full-package dependencies were not found in ${DependenciesDirectory}: $($missingDependencies -join ', ')."
        }
    }

    $archiveSuffix = if ($IncludeFullDependencies) { '-full' } else { '' }
    $archivePath = Join-Path $ArtifactsDirectory "NaniKore-v$ProductVersion-win_amd64$archiveSuffix.zip"
    $stagingDirectory = Join-Path $ArtifactsDirectory ".package-staging-$([guid]::NewGuid().ToString('N'))"

    try {
        New-Item -ItemType Directory -Path $stagingDirectory | Out-Null

        # 发布目录内容直接置于 ZIP 根目录；完整包额外携带 deps/，解压后即可使用本地 OCR。
        Get-ChildItem -LiteralPath $PublishOutput -Force |
            Copy-Item -Destination $stagingDirectory -Recurse -Force

        if ($IncludeFullDependencies) {
            Copy-Item -LiteralPath $DependenciesDirectory -Destination $stagingDirectory -Recurse -Force

            # 注音词典的生成暂存（源码 zip / 解包件 / CSV 中间产物）不随发布包分发：
            # 完整包只承诺「解压即用」的编译态词典（issue 008），暂存件几十 MB 且仅供 tools/phonetics 再生。
            foreach ($staleDirectory in @('phonetics\_source', 'phonetics\_build')) {
                $stalePath = Join-Path $stagingDirectory "deps\$staleDirectory"
                if (Test-Path -LiteralPath $stalePath) {
                    Remove-Item -LiteralPath $stalePath -Recurse -Force
                }
            }
        }

        Remove-NaniKorePackageCacheFiles -Directory $stagingDirectory
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

    # 从仓库根目录启动，确保程序可直接找到 deps/ 目录。
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
            # 构建前自动递增 build 版本号；发布流程不触发该更新，避免改变发行包版本。
            & python (Join-Path $root 'scripts\update_version.py') --bump build
            if ($LASTEXITCODE -ne 0) {
                throw "Failed to update the product version."
            }

            & $dotnet build $solution --configuration Debug -p:NuGetAudit=false -p:RestoreIgnoreFailedSources=true
            $exitCode = $LASTEXITCODE
            if ($exitCode -eq 0) {
                Set-NaniKoreOnnxRuntimeLinks -OutputDirectory $buildOutputDirectory -DependenciesDirectory $dependenciesDirectory
                Remove-NaniKoreGeneratedImportLibraries -OutputDirectory $buildOutputDirectory
            }
            $depsLink = Join-Path $PSScriptRoot "..\src\NaniKore\bin\Debug\net10.0-windows10.0.19041.0\deps"
            if (-not (Test-Path $depsLink)) { New-Item -ItemType Junction -Path $depsLink -Target (Join-Path $PSScriptRoot "..\deps") }
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
            if ($exitCode -ne 0) {
                break
            }
            $depsLink = Join-Path $PSScriptRoot "..\artifacts\win-x64\deps"
            Set-NaniKoreOnnxRuntimeLinks -OutputDirectory $buildOutputDirectory -DependenciesDirectory $dependenciesDirectory
            Remove-NaniKoreGeneratedImportLibraries -OutputDirectory $buildOutputDirectory
            # 默认发布包不携带固定的 ONNX Runtime DLL；完整包从 deps/ 原样打包。
            Remove-Item -LiteralPath (Join-Path $publishOutput 'deps'), (Join-Path $publishOutput 'onnxruntime.dll'), (Join-Path $publishOutput 'onnxruntime_providers_shared.dll') -Recurse -Force -ErrorAction SilentlyContinue
            Remove-NaniKoreGeneratedImportLibraries -OutputDirectory $publishOutput
            # 发布时同步根目录配置模板到发布输出，确保运行时配置与构建版本一致，喵
            $rootConfig = Join-Path $root "config.yaml"
            $publishConfig = Join-Path $publishOutput "config.yaml"
            Copy-Item -LiteralPath $rootConfig -Destination $publishConfig -Force
            $productVersion = Get-NaniKoreProductVersion -ConfigPath $rootConfig
            $archivePath = New-NaniKoreReleaseArchive `
                -PublishOutput $publishOutput `
                -ArtifactsDirectory $artifactsDirectory `
                -DependenciesDirectory $dependenciesDirectory `
                -ProductVersion $productVersion `
                -IncludeFullDependencies:$Full
            if (-not (Test-Path $depsLink)) {
                New-Item -ItemType Junction -Path $depsLink -Target (Join-Path $PSScriptRoot "..\deps")
            }
            Write-Host "Created release archive: $archivePath"
        }
    }
}
finally {
    Pop-Location
}

exit $exitCode
