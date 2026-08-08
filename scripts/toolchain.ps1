# 共享工具链通过标准环境变量配置，仓库本身不绑定个人目录。
function Get-NaniKoreSharedDotnetRoot {
    if ([string]::IsNullOrWhiteSpace($env:DOTNET_ROOT)) {
        return $null
    }

    return $env:DOTNET_ROOT
}

function Get-NaniKoreDotnetExecutable {
    # 优先复用用户已配置到 Path 的 SDK；命令不可用时才回退到 DOTNET_ROOT。
    $command = Get-Command dotnet -CommandType Application -ErrorAction SilentlyContinue | Select-Object -First 1
    if ($null -ne $command) {
        & $command.Source --version *> $null
        if ($LASTEXITCODE -eq 0) {
            return $command.Source
        }
    }

    $sharedDotnetRoot = Get-NaniKoreSharedDotnetRoot
    if ($null -eq $sharedDotnetRoot) {
        return $null
    }

    $sharedDotnet = Join-Path $sharedDotnetRoot 'dotnet.exe'
    if (Test-Path -LiteralPath $sharedDotnet) {
        return $sharedDotnet
    }

    return $null
}

function Set-NaniKoreNuGetCache {
    if (-not [string]::IsNullOrWhiteSpace($env:NUGET_PACKAGES)) {
        return
    }

    $sharedDotnetRoot = Get-NaniKoreSharedDotnetRoot
    if ($null -ne $sharedDotnetRoot) {
        # 默认与共享 SDK 同级放置缓存，避免还原依赖时回落到用户目录。
        $env:NUGET_PACKAGES = Join-Path (Split-Path -Parent $sharedDotnetRoot) 'nuget-packages'
    }
}