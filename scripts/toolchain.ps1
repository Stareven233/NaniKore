# 共享工具链通过标准环境变量配置，仓库本身不绑定个人目录。
function Get-NaniKoreSharedDotnetRoot {
    if (-not [string]::IsNullOrWhiteSpace($env:DOTNET_ROOT)) {
        return $env:DOTNET_ROOT
    }

    # 兼容刚写入用户或系统环境变量、但当前终端尚未重启的场景。
    foreach ($target in @('User', 'Machine')) {
        $dotnetRoot = [Environment]::GetEnvironmentVariable('DOTNET_ROOT', $target)
        if (-not [string]::IsNullOrWhiteSpace($dotnetRoot)) {
            $env:DOTNET_ROOT = $dotnetRoot
            return $dotnetRoot
        }
    }

    return $null
}

function Get-NaniKoreDotnetExecutable {
    # DOTNET_ROOT 是用户显式指定的 SDK，因此优先于 Path 中可能残留的旧版 dotnet。
    $sharedDotnetRoot = Get-NaniKoreSharedDotnetRoot
    if ($null -ne $sharedDotnetRoot) {
        $sharedDotnet = Join-Path $sharedDotnetRoot 'dotnet.exe'
        if (Test-Path -LiteralPath $sharedDotnet) {
            return $sharedDotnet
        }
    }

    # 未指定或指定目录无效时，才回退到当前进程 Path 中的 dotnet。
    $command = Get-Command dotnet -CommandType Application -ErrorAction SilentlyContinue | Select-Object -First 1
    if ($null -ne $command) {
        return $command.Source
    }

    return $null
}

function Test-NaniKoreSdkResolution {
    param(
        [Parameter(Mandatory)]
        [string]$Dotnet,

        [Parameter(Mandatory)]
        [string]$WorkingDirectory
    )

    # global.json 只会影响其所在目录及子目录；必须在仓库根目录验证实际可用的 SDK。
    Push-Location -LiteralPath $WorkingDirectory
    try {
        $null = & $Dotnet --version 2>&1
        return $LASTEXITCODE -eq 0
    }
    finally {
        Pop-Location
    }
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
