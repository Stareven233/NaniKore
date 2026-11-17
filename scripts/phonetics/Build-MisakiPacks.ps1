<#
.SYNOPSIS
    构建并验收日英注音语言包（MisakiSharp + UniDic）。

.DESCRIPTION
    唯一入口。固定版本、哈希、资源白名单与包定义全部读自 misaki-packs.json；
    对不上就停，不做兜底。所有下载与中间产物在 .temp/misaki-pack，
    产物展开到 deps/phonetics/{ja,en}/，并将三语本地目录打包为 ModelScope 根目录资源。

.PARAMETER Stage
    All（默认）| Prepare | Native | Verify。Prepare 完成后才能 Native，Native 完成后才能 Verify。
#>
param(
    [ValidateSet('All', 'Prepare', 'Native', 'Verify')][string]$Stage = 'All'
)

$ErrorActionPreference = 'Stop'
$workspace = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
. (Join-Path $workspace 'scripts/toolchain.ps1')
$dotnet = Get-NaniKoreDotnetExecutable
if (-not $dotnet) { throw '未找到 DOTNET_ROOT 指定的 .NET SDK。' }
Set-NaniKoreNuGetCache

$pinPath = Join-Path $PSScriptRoot 'misaki-packs.json'
$pin = Get-Content -LiteralPath $pinPath -Raw | ConvertFrom-Json
$work = Join-Path $workspace '.temp/misaki-pack'
$packsRoot = Join-Path $workspace 'deps/phonetics'
New-Item -ItemType Directory -Path $work -Force | Out-Null
$env:DOTNET_CLI_HOME = Join-Path $workspace '.temp/dotnet-home'
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
$env:DOTNET_SKIP_FIRST_TIME_EXPERIENCE = '1'

function Get-Sha256([string]$Path) {
    (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash
}

function Assert-Pin([string]$Path, [string]$Expected, [string]$What) {
    if (-not (Test-Path -LiteralPath $Path)) { throw "$What 不存在：$Path" }
    $actual = Get-Sha256 $Path
    if ($actual -ne $Expected) { throw "$What 哈希不符（固定 $Expected，实际 $actual）：$Path" }
}

function Get-Pinned([string]$Url, [string]$Target, [int64]$Bytes, [string]$Sha256, [string]$What) {
    # 已存在就只校验；不存在才下载，避免每次重跑都拉 500 MiB。
    if (-not (Test-Path -LiteralPath $Target)) {
        Write-Host "下载$What：$Url"
        $parent = Split-Path -Parent $Target
        New-Item -ItemType Directory -Path $parent -Force | Out-Null
        Invoke-WebRequest -Uri $Url -OutFile $Target
    }
    if ($Bytes -gt 0 -and (Get-Item -LiteralPath $Target).Length -ne $Bytes) {
        throw "$What 大小不符（固定 $Bytes，实际 $((Get-Item -LiteralPath $Target).Length)）：$Target"
    }
    Assert-Pin $Target $Sha256 $What
}

function Move-Previous([string]$Path) {
    # 旧产物归档到 .temp，不删除，也避免旧文件混进新包。
    if (-not (Test-Path -LiteralPath $Path)) { return }
    $archive = Join-Path $work ("previous/" + (Split-Path $Path -Leaf) + "-" + (Get-Date -Format 'yyyyMMdd-HHmmss'))
    New-Item -ItemType Directory -Path (Split-Path $archive -Parent) -Force | Out-Null
    Move-Item -LiteralPath $Path -Destination $archive
}

function Invoke-Dotnet([string[]]$Arguments, [string]$Log) {
    & $dotnet @Arguments *> $Log
    if ($LASTEXITCODE -ne 0) {
        Get-Content -LiteralPath $Log -Tail 40
        throw "构建失败（日志：$Log）"
    }
}

$assemblyPath = Join-Path $env:NUGET_PACKAGES ($pin.upstream.assembly.path -replace '/', [IO.Path]::DirectorySeparatorChar)
$packNames = @($pin.packs.PSObject.Properties.Name)

Push-Location -LiteralPath $workspace
try {
    if ($Stage -in @('Prepare', 'All')) {
        # 1. 上游源码与许可：逐文件核对哈希，失配即说明上游或提交对不上。
        $upstream = Join-Path $work 'upstream'
        New-Item -ItemType Directory -Path $upstream -Force | Out-Null
        foreach ($name in $pin.upstream.sources.PSObject.Properties.Name) {
            $target = Join-Path $upstream $name
            $url = "$($pin.upstream.repository.Replace('github.com', 'raw.githubusercontent.com'))/$($pin.upstream.commit)/$name"
            Get-Pinned $url $target 0 $pin.upstream.sources.$name "上游文件 $name"
        }

        # 2. NuGet 原件只读：包清单必须与固定版本一致，但我们不改缓存、不改 DLL。
        $nupkg = Join-Path $env:NUGET_PACKAGES ($pin.upstream.nupkg.path -replace '/', [IO.Path]::DirectorySeparatorChar)
        Assert-Pin $nupkg $pin.upstream.nupkg.sha256 'MisakiSharp nupkg'
        Assert-Pin $assemblyPath $pin.upstream.assembly.sha256 'MisakiSharp 程序集'

        # 3. 打补丁：先 --check 再应用，任何锚点漂移都直接失败。
        $patchedDir = Join-Path $work 'patch'
        Move-Previous $patchedDir
        New-Item -ItemType Directory -Path $patchedDir -Force | Out-Null
        Get-ChildItem -LiteralPath $upstream -Filter *.cs | Copy-Item -Destination $patchedDir
        $patchPath = Join-Path $PSScriptRoot $pin.upstream.patch.file
        Assert-Pin $patchPath $pin.upstream.patch.sha256 '源码补丁'
        $relativePatched = [IO.Path]::GetRelativePath($workspace, $patchedDir).Replace('\', '/')
        & git apply --check "--directory=$relativePatched" $patchPath 2>&1
        if ($LASTEXITCODE -ne 0) { throw '源码补丁与固定上游不匹配。' }
        & git apply "--directory=$relativePatched" $patchPath 2>&1
        if ($LASTEXITCODE -ne 0) { throw '源码补丁应用失败。' }
        foreach ($name in $pin.upstream.patch.patched.PSObject.Properties.Name) {
            Assert-Pin (Join-Path $patchedDir $name) $pin.upstream.patch.patched.$name "补丁后的 $name"
        }

        # 4. 构建期工具：导出上游内嵌资源、审计 IPA 覆盖。
        Invoke-Dotnet @('build', (Join-Path $PSScriptRoot 'tools/Tools.csproj'), '-c', 'Release',
            "-p:BaseIntermediateOutputPath=$work/obj/tools/", "-p:OutputPath=$work/bin/tools/",
            "-p:RepositoryRoot=$workspace", '-p:NuGetAudit=false') "$work/tools-build.log"
        $tools = Join-Path $work 'bin/tools/Tools.dll'
        $resources = Join-Path $work 'resources'
        Move-Previous $resources
        New-Item -ItemType Directory -Path $resources -Force | Out-Null
        # 导出所有包声明的资源；日语的词表随后会被同版 UniDic 再生版替换。
        $wanted = @($pin.packs.PSObject.Properties.Value | ForEach-Object { $_.resources } | Sort-Object -Unique)
        & $dotnet $tools extract $assemblyPath $resources @wanted | Set-Content -LiteralPath "$work/resources.json" -Encoding utf8
        if ($LASTEXITCODE -ne 0) { throw '上游内嵌资源导出失败。' }

        # 5. 日语书面假名词表：从同版 sys.dic 再生，逐条核对身份。
        $dictionary = Join-Path $work 'unidic-3.1.0.zip'
        Get-Pinned $pin.dictionary.source $dictionary $pin.dictionary.archive.bytes $pin.dictionary.archive.sha256 'UniDic'
        $jaResources = Join-Path $work 'resources-ja'
        Move-Previous $jaResources
        New-Item -ItemType Directory -Path $jaResources -Force | Out-Null
        Copy-Item -LiteralPath (Join-Path $resources 'ja_char_unk.tsv.gz'), (Join-Path $resources 'ja_matrix_compact.bin.gz') -Destination $jaResources
        & python (Join-Path $PSScriptRoot 'RegenerateKana.py') --pin $pinPath --archive $dictionary `
            --lexicon (Join-Path $resources 'ja_lexicon.tsv.gz') --work (Join-Path $work 'unidic') `
            --output $jaResources --report (Join-Path $work 'kana-report.json')
        if ($LASTEXITCODE -ne 0) { throw '日语书面假名词表再生失败。' }

        # 6. 英语资源只按白名单裁剪；espeak 回退表不进包（未收录词不猜读音）。
        $enResources = Join-Path $work 'resources-en'
        Move-Previous $enResources
        New-Item -ItemType Directory -Path $enResources -Force | Out-Null
        foreach ($name in $pin.packs.en.resources) { Copy-Item -LiteralPath (Join-Path $resources $name) -Destination $enResources }

        # 7. IPA 转换覆盖审计：转不了的字符要能点名，不能静默丢。
        & $dotnet $tools audit-ipa (Join-Path $resources 'en_lexicon_us.tsv.gz') | Set-Content -LiteralPath "$work/ipa-audit.json" -Encoding utf8
        if ($LASTEXITCODE -ne 0) { throw '英语 IPA 转换审计失败。' }

        # 8. 每包一份源码目录：只编本包需要的上游文件。
        foreach ($name in $packNames) {
            $sources = Join-Path $work "sources/$name"
            Move-Previous $sources
            New-Item -ItemType Directory -Path $sources -Force | Out-Null
            foreach ($file in $pin.packs.$name.sources) { Copy-Item -LiteralPath (Join-Path $patchedDir $file) -Destination $sources }
        }

        Write-Host 'Prepare 完成：上游/数据校验、补丁、词表再生、资源裁剪、IPA 审计就绪。'
    }

    if ($Stage -in @('Native', 'All')) {
        foreach ($name in $packNames) {
            $definition = $pin.packs.$name
            $output = Join-Path $work "bin/$name"
            Move-Previous $output
            New-Item -ItemType Directory -Path $output -Force | Out-Null
            Invoke-Dotnet @('publish', (Join-Path $PSScriptRoot 'native/NaniKore.Phonetics.Native.csproj'), '-c', 'Release',
                '-r', 'win-x64', '-o', $output,
                "-p:BaseIntermediateOutputPath=$work/obj/$name/", "-p:OutputPath=$output/",
                "-p:PackLanguage=$name", "-p:PackAssemblyName=$([IO.Path]::GetFileNameWithoutExtension($definition.library))",
                "-p:SourcesRoot=$work/sources/$name", "-p:ResourcesRoot=$work/resources-$name",
                "-p:RepositoryRoot=$workspace", '-p:NuGetAudit=false') "$work/$name-build.log"

            # 只把 DLL、清单与许可放进 deps；构建副产品（.lib 等）留在 .temp。
            $packDirectory = Join-Path $packsRoot $name
            Move-Previous $packDirectory
            New-Item -ItemType Directory -Path $packDirectory -Force | Out-Null
            Copy-Item -LiteralPath (Join-Path $output $definition.library) -Destination $packDirectory
            $licenses = Join-Path $packDirectory 'licenses'
            New-Item -ItemType Directory -Path $licenses -Force | Out-Null
            Copy-Item -LiteralPath (Join-Path $work 'upstream/LICENSE') -Destination (Join-Path $licenses 'MisakiSharp-LICENSE.txt')
            Copy-Item -LiteralPath (Join-Path $work 'upstream/NOTICE') -Destination (Join-Path $licenses 'MisakiSharp-NOTICE.txt')
            Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'licenses/DERIVED.md') -Destination $licenses
            if ($name -eq 'ja') {
                $unidic = Join-Path $work 'unidic'
                foreach ($license in Get-ChildItem -LiteralPath $unidic -Recurse -File | Where-Object { $_.Name -in @('BSD', 'COPYING', 'dicrc') }) {
                    $target = switch ($license.Name) {
                        'BSD' { 'UniDic-BSD.txt' }
                        'COPYING' { 'UniDic-COPYING.txt' }
                        default { 'UniDic-dicrc.txt' }
                    }
                    Copy-Item -LiteralPath $license.FullName -Destination (Join-Path $licenses $target)
                }
            }

            $manifestArguments = @((Join-Path $PSScriptRoot 'MakePackManifest.py'), '--pin', $pinPath, '--pack', $name,
                '--resources', (Join-Path $work "resources-$name"), '--output', $packDirectory)
            if ($name -eq 'ja') { $manifestArguments += @('--kana-report', (Join-Path $work 'kana-report.json')) }
            & python @manifestArguments
            if ($LASTEXITCODE -ne 0) { throw "$name 包清单生成失败。" }

            $archive = Join-Path $work "$name-pack.zip"
            & python (Join-Path $PSScriptRoot 'MakePackArchive.py') --source $packDirectory --output $archive
            if ($LASTEXITCODE -ne 0) { throw "$name ZIP 生成失败。" }
            $archiveTarget = Join-Path $packsRoot "$name.zip"
            Copy-Item -LiteralPath $archive -Destination $archiveTarget -Force
            Write-Host "$name.zip: $((Get-Item $archiveTarget).Length) bytes / $(Get-Sha256 $archiveTarget)"
        }
        # 三语远端资源统一由此阶段打包；本地中文只维护 zh/ 下的明文 TSV。
        $zhSource = Join-Path $packsRoot 'zh/zh_pinyin.txt'
        if (-not (Test-Path -LiteralPath $zhSource -PathType Leaf)) {
            throw "中文拼音数据不存在：$zhSource（先运行 --build-zh-data 生成）"
        }
        $zhArchive = Join-Path $work 'zh_pinyin.txt.gz'
        $sourceStream = [IO.File]::OpenRead($zhSource)
        try {
            $targetStream = [IO.File]::Create($zhArchive)
            try {
                $gzip = [IO.Compression.GZipStream]::new($targetStream, [IO.Compression.CompressionLevel]::Optimal)
                try { $sourceStream.CopyTo($gzip) } finally { $gzip.Dispose() }
            }
            finally { $targetStream.Dispose() }
        }
        finally { $sourceStream.Dispose() }
        $zhTarget = Join-Path $packsRoot 'zh_pinyin.txt.gz'
        Copy-Item -LiteralPath $zhArchive -Destination $zhTarget -Force
        Write-Host "zh_pinyin.txt.gz: $((Get-Item $zhTarget).Length) bytes / $(Get-Sha256 $zhTarget)"
        Write-Host "Native 完成：$packsRoot"
    }

    if ($Stage -in @('Verify', 'All')) {
        Invoke-Dotnet @('build', (Join-Path $PSScriptRoot 'verify/Verify.csproj'), '-c', 'Release',
            "-p:BaseIntermediateOutputPath=$work/obj/verify/", "-p:OutputPath=$work/bin/verify/",
            '-p:NuGetAudit=false') "$work/verify-build.log"
        $packDirectories = $packNames | ForEach-Object { Join-Path $packsRoot $_ }
        & $dotnet (Join-Path $work 'bin/verify/Verify.dll') @packDirectories | Set-Content -LiteralPath "$work/verify.json" -Encoding utf8
        if ($LASTEXITCODE -ne 0) { throw "语言包验收失败；报告：$work/verify.json" }
        Get-Content -LiteralPath "$work/verify.json" -Raw
        Write-Host 'Verify 完成：日英包全部检查通过。'
    }
}
finally { Pop-Location }
