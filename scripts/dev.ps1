param(
    [ValidateSet('build', 'run', 'test', 'publish', 'aot')]
    [string]$Command = 'build',

    [ValidateSet('build', 'publish')]
    [string]$Launch,

    [switch]$Full,

    [switch]$ForceBump
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
. (Join-Path $PSScriptRoot 'toolchain.ps1')

$solution = Join-Path $root 'NaniKore.slnx'
$project = Join-Path $root 'src\NaniKore\NaniKore.csproj'
$debugExecutable = Join-Path $root 'src\NaniKore\bin\Debug\net10.0\NaniKore.exe'
$artifactsDirectory = Join-Path $root 'artifacts'
$publishOutput = Join-Path $root 'artifacts\win-x64'
$publishedExecutable = Join-Path $publishOutput 'NaniKore.exe'
$buildOutputDirectory = Join-Path $root 'src\NaniKore\bin'
$dependenciesDirectory = Join-Path $root 'deps'
$debugOutputDirectory = Split-Path -Parent $debugExecutable
$dataDirectory = Join-Path $root '.data'

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

function Test-NaniKoreSourceChanged {
    param(
        [Parameter(Mandatory)]
        [string]$OutputExecutable,

        [Parameter(Mandatory)]
        [string]$RootDirectory
    )

    $outputAssembly = Join-Path (Split-Path -Parent $OutputExecutable) 'NaniKore.dll'
    $targetFile = if (Test-Path -LiteralPath $outputAssembly -PathType Leaf) { $outputAssembly } else { $OutputExecutable }
    if (-not (Test-Path -LiteralPath $targetFile -PathType Leaf)) {
        return $true
    }

    $lastBuildTime = (Get-Item -LiteralPath $targetFile).LastWriteTimeUtc

    # 1. 检查源码与目录是否有修改时间晚于上次构建产物
    $sourceDir = Join-Path $RootDirectory 'src'
    $newerItem = Get-ChildItem -LiteralPath $sourceDir -Recurse |
        Where-Object {
            $_.FullName -notmatch '[\\/](bin|obj)([\\/]|$)' -and
            $_.LastWriteTimeUtc -gt $lastBuildTime
        } |
        Select-Object -First 1

    if ($null -ne $newerItem) {
        return $true
    }

    # 2. 检查工程定义与工具链全局配置
    foreach ($file in @('NaniKore.slnx', 'global.json', 'NuGet.config')) {
        $path = Join-Path $RootDirectory $file
        if ((Test-Path -LiteralPath $path -PathType Leaf) -and ((Get-Item -LiteralPath $path).LastWriteTimeUtc -gt $lastBuildTime)) {
            return $true
        }
    }

    # 3. 检查 HEAD 最近提交是否有晚于构建产物的提交（覆盖 pull/rebase 场景）
    try {
        $ct = & git log -1 --format=%ct HEAD -- src/ 2>$null
        if ($LASTEXITCODE -eq 0 -and $ct) {
            $commitTime = [DateTimeOffset]::FromUnixTimeSeconds([int64]$ct).UtcDateTime
            if ($commitTime -gt $lastBuildTime) {
                return $true
            }
        }
    }
    catch {
        # Git 探测失败时不阻塞，以文件系统时间戳为准
    }

    return $false
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

function Remove-NaniKoreJunction {
    param(
        [Parameter(Mandatory)]
        [string]$Path,

        # 仅用于发布输出里的 deps 这类构建残留（模型可用 scripts/sync-deps.py 重新拉取）。
        # 用户数据目录（.data）严禁打开此开关：绝不整目录删除。
        [switch]$IncludeRealDirectory
    )

    $item = Get-Item -Force -LiteralPath $Path -ErrorAction SilentlyContinue
    if ($null -eq $item) {
        return
    }

    if (($item.Attributes -band [System.IO.FileAttributes]::ReparsePoint) -ne 0) {
        # 只删联接点本身，绝不加 -Recurse：个别宿主会穿透链接把目标目录的内容一起删掉。
        Remove-Item -LiteralPath $Path -Force
        return
    }

    if ($IncludeRealDirectory) {
        Write-Warning "$Path 不是联接点而是真实目录，已按构建残留清除。"
        Remove-Item -LiteralPath $Path -Recurse -Force
    }
}

function Set-NaniKoreJunction {
    param(
        [Parameter(Mandatory)]
        [string]$Path,

        [Parameter(Mandatory)]
        [string]$Target,

        # .data 由应用首启自行创建，构建脚本不能因为还没有数据目录就失败。
        [switch]$CreateTarget
    )

    # 归一化成绝对路径再比较，避免 ..\ 与尾部分隔符造成「目标明明相同却重建链接」。
    $resolvedTarget = [System.IO.Path]::GetFullPath($Target).TrimEnd('\', '/')
    $existing = Get-Item -Force -LiteralPath $Path -ErrorAction SilentlyContinue

    if ($null -ne $existing) {
        if (($existing.Attributes -band [System.IO.FileAttributes]::ReparsePoint) -ne 0) {
            # PowerShell 6+ 才暴露联接目标；读不到时保守认为已挂对（本脚本建的链目标不会变）。
            $currentTarget = $existing.Target
            if ($null -eq $currentTarget -or
                [System.IO.Path]::GetFullPath($currentTarget).TrimEnd('\', '/') -eq $resolvedTarget) {
                return
            }

            # 目标变了：摘旧链后按新目标重建，只动链接不动数据。
            Remove-Item -LiteralPath $Path -Force
        }
        elseif ($existing.PSIsContainer -and -not @(Get-ChildItem -LiteralPath $Path -Force)) {
            # 空目录（dotnet publish 等留下的残壳）直接替换，不值得留备份。
            Remove-Item -LiteralPath $Path -Force
        }
        else {
            # 真实目录/文件绝不静默覆盖：应用自己创建的 .data 就长这样，
            # 整体改名备份后再挂链，并明确告知原内容去哪了。
            if (Get-Process -Name 'NaniKore' -ErrorAction SilentlyContinue) {
                Write-Warning 'NaniKore 正在运行，其数据目录即将被移动：请退出应用后重启，否则运行中的实例仍读写旧位置。'
            }

            $stamp = Get-Date -Format 'yyyyMMdd-HHmmss'
            $backupPath = "$Path.backup-$stamp"
            $suffix = 2
            while (Test-Path -LiteralPath $backupPath) {
                $backupPath = "$Path.backup-$stamp-$suffix"
                $suffix++
            }

            Move-Item -LiteralPath $Path -Destination $backupPath
            Write-Warning "$Path 已存在且不是联接点，原内容已备份到 $backupPath，随后挂接到 $resolvedTarget。"
        }
    }

    if (-not (Test-Path -LiteralPath $resolvedTarget -PathType Container)) {
        if (-not $CreateTarget) {
            throw "联接点目标不存在：$resolvedTarget。请先创建该目录。"
        }

        New-Item -ItemType Directory -Path $resolvedTarget -Force | Out-Null
    }

    $parentDirectory = Split-Path -Parent $Path
    if (-not (Test-Path -LiteralPath $parentDirectory -PathType Container)) {
        New-Item -ItemType Directory -Path $parentDirectory -Force | Out-Null
    }

    New-Item -ItemType Junction -Path $Path -Target $resolvedTarget | Out-Null
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
        # .data 是本机运行期数据（配置、密钥密文、收藏库）：从 artifacts\win-x64 跑过一次应用就会生成它，
        # 一个排除项全挡住，杜绝把用户数据打进发布包（issue 017）。.data.backup-* 是挂接联接点时为
        # 既有运行期数据留的备份，同样一件都不能进交付物。nanikore.db 是 .data 之前的旧收藏库名，
        # dotnet publish 不清输出目录，旧版运行留下的残留同样不能进交付物。
        Get-ChildItem -LiteralPath $PublishOutput -Force |
            Where-Object { $_.Name -ne '.data' -and $_.Name -ne 'nanikore.db' -and $_.Name -notlike '.data.backup-*' } |
            Copy-Item -Destination $stagingDirectory -Recurse -Force

        if ($IncludeFullDependencies) {
            Copy-Item -LiteralPath $DependenciesDirectory -Destination $stagingDirectory -Recurse -Force

            # 注音词典的生成暂存（源码 zip / 解包件 / CSV 中间产物）不随发布包分发：
            # 完整包只承诺「解压即用」的编译态词典（issue 008），暂存件几十 MB 且仅供 codestable/notes/phonetics-pipeline.md 再生。
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
            # 仅在源码发生变动或显式指定 -ForceBump 时递增 build 版本号，避免重复构建无谓更新配置
            if ($ForceBump -or (Test-NaniKoreSourceChanged -OutputExecutable $debugExecutable -RootDirectory $root)) {
                & python (Join-Path $root 'scripts\update_version.py') --bump build
                if ($LASTEXITCODE -ne 0) {
                    throw "Failed to update the product version."
                }
            }
            else {
                Write-Host 'Code unchanged; skipping product version bump.'
            }

            & $dotnet build $solution --configuration Debug -p:NuGetAudit=false -p:RestoreIgnoreFailedSources=true
            $exitCode = $LASTEXITCODE
            if ($exitCode -eq 0) {
                Set-NaniKoreOnnxRuntimeLinks -OutputDirectory $buildOutputDirectory -DependenciesDirectory $dependenciesDirectory
                Remove-NaniKoreGeneratedImportLibraries -OutputDirectory $buildOutputDirectory
            }
            # 本机数据目录单一真相（issue 017）：调试产物与发布产物都挂接仓库根 .data，
            # 不各存一份，免得「调试版改的配置发布版看不到」。deps 同理挂到仓库 deps/。
            Set-NaniKoreJunction -Path (Join-Path $debugOutputDirectory 'deps') -Target $dependenciesDirectory
            Set-NaniKoreJunction -Path (Join-Path $debugOutputDirectory '.data') -Target $dataDirectory -CreateTarget
            Set-NaniKoreJunction -Path (Join-Path $publishOutput '.data') -Target $dataDirectory -CreateTarget
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

            # deps 先摘链：dotnet publish 与打包阶段的递归复制都会穿透联接点，
            # 带着整棵模型树跑一遍既慢又会把 deps/ 原样拖进 zip staging；发布收尾再挂回来。
            $publishDepsLink = Join-Path $publishOutput 'deps'
            Remove-NaniKoreJunction -Path $publishDepsLink -IncludeRealDirectory

            # NativeAOT 发布（epic 002 P6 正式接入）：精简 exe + 随包原生 DLL 旁置；不内嵌、不走 PublishSingleFile。
            & $dotnet publish $project --configuration Release --runtime win-x64 -p:PublishAot=true --output $publishOutput -p:NuGetAudit=false -p:RestoreIgnoreFailedSources=true
            $exitCode = $LASTEXITCODE
            if ($exitCode -ne 0) {
                break
            }

            # 默认发布包不携带固定的 ONNX Runtime DLL；完整包从 deps/ 原样打包。
            # 摘除是正确性要求：NuGet 复制进根目录的 onnxruntime.dll 会被优先加载，导致 deps/ 中校验哈希的 DLL 失效。
            Remove-Item -LiteralPath (Join-Path $publishOutput 'onnxruntime.dll'), (Join-Path $publishOutput 'onnxruntime_providers_shared.dll') -Force -ErrorAction SilentlyContinue

            # 显式同步交付物模板与声明文件：
            # config.template.yaml 首启按字节复制到 .data/config.yaml；同时清理可能残留的运行期 config.yaml。
            Remove-Item -LiteralPath (Join-Path $publishOutput 'config.yaml') -Force -ErrorAction SilentlyContinue
            Copy-Item -LiteralPath (Join-Path $root 'config.template.yaml') -Destination $publishOutput -Force
            Copy-Item -LiteralPath (Join-Path $root 'NOTICE') -Destination $publishOutput -Force

            # NuGet 附带的 .lib 是 Windows 链接器导入库，应用运行时只需要 DLL，不进交付物。
            Remove-NaniKoreGeneratedImportLibraries -OutputDirectory $publishOutput

            # 运行期数据（.data/config.yaml、keys.bin、collections.db）不再进交付物；发布只带配置模板与 NOTICE。
            $productVersion = Get-NaniKoreProductVersion -ConfigPath (Join-Path $root 'config.template.yaml')
            $archivePath = New-NaniKoreReleaseArchive `
                -PublishOutput $publishOutput `
                -ArtifactsDirectory $artifactsDirectory `
                -DependenciesDirectory $dependenciesDirectory `
                -ProductVersion $productVersion `
                -IncludeFullDependencies:$Full

            # 打包结束才挂本机链接：发布目录与仓库根 .data 共用同一份配置与密钥，调试版同样指向那里；
            # 交付 zip 不含 .data，解压出去的副本完全独立，用户数据不会随发布包外流。
            Set-NaniKoreJunction -Path $publishDepsLink -Target $dependenciesDirectory
            Set-NaniKoreJunction -Path (Join-Path $publishOutput '.data') -Target $dataDirectory -CreateTarget
            Write-Host "Created release archive: $archivePath"
        }
        'aot' {
            # 兼容别名：epic 002 P6 起 NativeAOT 即为唯一正式发布通道
            & $PSCommandPath -Command publish -Full:$Full
            $exitCode = $LASTEXITCODE
        }
    }
}
finally {
    Pop-Location
}

exit $exitCode
