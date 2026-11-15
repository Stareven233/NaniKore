param(
    [ValidateSet('Prepare', 'Native', 'Run', 'All')][string]$Stage = 'All'
)

$ErrorActionPreference = 'Stop'
$workspace = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../../..'))
. (Join-Path $workspace 'scripts/toolchain.ps1')
$dotnet = Get-NaniKoreDotnetExecutable
if (-not $dotnet) { throw '未找到 DOTNET_ROOT 指定的 .NET SDK。' }
Set-NaniKoreNuGetCache
$probeRoot = Join-Path $workspace '.temp/misaki-annotation-spike'
New-Item -ItemType Directory -Path $probeRoot -Force | Out-Null
$env:DOTNET_CLI_HOME = Join-Path $workspace '.temp/dotnet-home'
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
$env:DOTNET_SKIP_FIRST_TIME_EXPERIENCE = '1'
$env:DOTNET_GENERATE_ASPNET_CERTIFICATE = 'false'
$originalAssembly = Join-Path $env:NUGET_PACKAGES 'misakisharp/2.2.0/lib/netstandard2.0/MisakiSharp.dll'
if (-not (Test-Path -LiteralPath $originalAssembly)) { throw '请先还原 MisakiSharp 2.2.0；探针不向包缓存写入修改后的程序集。' }

function Invoke-ProbeBuild([string]$Project, [string]$Name, [string[]]$Properties, [bool]$Publish = $true) {
    $action = if ($Publish) { 'publish' } else { 'build' }
    $configuration = if ($Publish) { 'Release' } else { 'Debug' }
    $output = Join-Path $probeRoot "bin/$Name"
    $arguments = @($action, (Join-Path $PSScriptRoot $Project), '-c', $configuration,
        "-p:BaseIntermediateOutputPath=$probeRoot/obj/$Name/", "-p:OutputPath=$output/",
        '-p:NuGetAudit=false', '-p:RestoreIgnoreFailedSources=true') + $Properties
    if ($Publish) { $arguments += @('-r', 'win-x64', '-o', $output) }
    & $dotnet @arguments *> (Join-Path $probeRoot "$Name-build.log")
    if ($LASTEXITCODE -ne 0) {
        Get-Content -LiteralPath (Join-Path $probeRoot "$Name-build.log") -Tail 35
        throw "$Name 构建失败。"
    }
    Write-Host "$Name built: $output"
}

Push-Location -LiteralPath $workspace
try {
    if ($Stage -in @('Prepare', 'All')) {
        & python (Join-Path $PSScriptRoot 'prepare.py') fetch
        if ($LASTEXITCODE -ne 0) { throw '上游源码下载失败。' }
        Invoke-ProbeBuild 'Probe.csproj' 'inspect' @("-p:MisakiAssembly=$originalAssembly", "-p:CecilAssembly=$probeRoot/cecil-0.11.6/lib/netstandard2.0/Mono.Cecil.dll") $false
        & $dotnet "$probeRoot/bin/inspect/Probe.dll" prepare $probeRoot | Set-Content -LiteralPath "$probeRoot/trim-report.json" -Encoding utf8
        if ($LASTEXITCODE -ne 0) { throw '资源裁剪失败。' }
        & $dotnet "$probeRoot/bin/inspect/Probe.dll" baseline | Set-Content -LiteralPath "$probeRoot/baseline.json" -Encoding utf8
        if ($LASTEXITCODE -ne 0) { throw '原版 API 探测失败。' }
        & python (Join-Path $PSScriptRoot 'prepare.py') kana
        if ($LASTEXITCODE -ne 0) { throw 'UniDic 同版本词条匹配失败。' }
        & python (Join-Path $PSScriptRoot 'prepare.py') patch
        if ($LASTEXITCODE -ne 0) { throw '固定提交补丁失配。' }
    }

    $variants = @(
        @{ Name = 'MisakiJaRaw'; Package = 'ja'; Patched = $false },
        @{ Name = 'MisakiEnCoreRaw'; Package = 'en-core'; Patched = $false },
        @{ Name = 'MisakiEnRaw'; Package = 'en-full'; Patched = $false },
        @{ Name = 'MisakiJaPatched'; Package = 'ja'; Patched = $true },
        @{ Name = 'MisakiEnPatched'; Package = 'en-full'; Patched = $true }
    )
    if ($Stage -in @('Native', 'All')) {
        foreach ($variant in $variants) {
            $properties = @("-p:AssemblyName=$($variant.Name)", "-p:Package=$($variant.Package)")
            if ($variant.Patched) {
                $resourceRoot = Join-Path $probeRoot "patched-resources/$($variant.Package)"
                New-Item -ItemType Directory -Path $resourceRoot -Force | Out-Null
                $manifest = (Get-Content -LiteralPath "$probeRoot/trim-report.json" -Raw | ConvertFrom-Json).manifests | Where-Object package -EQ $variant.Package
                foreach ($resource in $manifest.resources) {
                    $name = $resource.Substring('MisakiSharp.'.Length)
                    $source = if ($variant.Package -eq 'ja' -and $name -eq 'ja_lexicon.tsv.gz') { "$probeRoot/kana/$name" } else { "$probeRoot/resources/$name" }
                    Copy-Item -LiteralPath $source -Destination (Join-Path $resourceRoot $name) -Force
                }
                $properties += @('-p:Patched=true', "-p:PatchedSources=$probeRoot/patched", "-p:ResourcesRoot=$resourceRoot")
            }
            else { $properties += "-p:MisakiAssembly=$probeRoot/trimmed/$($variant.Package)/MisakiSharp.dll" }
            Invoke-ProbeBuild 'Native.csproj' $variant.Name $properties
        }
        Invoke-ProbeBuild 'Client.csproj' 'client-jit' @() $false
        Invoke-ProbeBuild 'Client.csproj' 'client-aot' @('-p:PublishAot=true')
        Invoke-ProbeBuild 'Client.csproj' 'client-aot-invariant' @('-p:PublishAot=true', '-p:InvariantGlobalization=true')
    }

    if ($Stage -in @('Run', 'All')) {
        foreach ($variant in $variants) {
            foreach ($mode in @('jit', 'aot')) {
                $library = "$probeRoot/bin/$($variant.Name)/$($variant.Name).dll"
                $report = "$probeRoot/$($variant.Name)-$mode.json"
                if ($mode -eq 'jit') { & $dotnet "$probeRoot/bin/client-jit/Client.dll" $library > $report }
                else { & "$probeRoot/bin/client-aot/Client.exe" $library > $report }
                if ($LASTEXITCODE -ne 0) { throw "$($variant.Name) $mode 检查失败；报告：$report" }
                Write-Host "$($variant.Name) $mode passed"
            }
        }
        & "$probeRoot/bin/client-aot/Client.exe" "$probeRoot/bin/MisakiJaPatched/MisakiJaPatched.dll" "$probeRoot/bin/MisakiEnPatched/MisakiEnPatched.dll" > "$probeRoot/combined-aot.json"
        if ($LASTEXITCODE -ne 0) { throw '日英原生库同进程检查失败。' }
        & "$probeRoot/bin/client-aot-invariant/Client.exe" "$probeRoot/bin/MisakiJaPatched/MisakiJaPatched.dll" "$probeRoot/bin/MisakiEnPatched/MisakiEnPatched.dll" > "$probeRoot/combined-aot-invariant.json"
        if ($LASTEXITCODE -ne 0) { throw 'invariant AOT 宿主加载多语库检查失败。' }
        & python (Join-Path $PSScriptRoot 'summarize.py') > "$probeRoot/summary-output.json"
        if ($LASTEXITCODE -ne 0) { throw '探针结果汇总失败。' }
    }
}
finally { Pop-Location }
