#!/usr/bin/env pwsh
<#
.SYNOPSIS
  启动提示词实时预览中转服务，伺服 HTML 并代理调用 NaniKore.exe --dump-prompt。
.DESCRIPTION
  为 scripts/prompt-preview.html 提供开发期本地中转能力：
    1. 托管 prompt-preview.html，使前端获得 HTTP 运行环境；
    2. 接收前端的 /api/dump 请求，代理调用 Debug 版 NaniKore.exe --dump-prompt --json；
    3. 返回真实的组装 JSON，实现前端任意输入的秒级无漂移实时真值预览。
  C# 主程序对本服务完全不可知，生产代码保持零侵入。
.EXAMPLE
  pwsh scripts/serve-preview.ps1
  pwsh scripts/serve-preview.ps1 -Port 8888 -NoLaunch
#>
param(
    [int]$Port = 8788,
    [switch]$NoLaunch
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$htmlPath = Join-Path $PSScriptRoot 'prompt-preview.html'

if (-not (Test-Path -LiteralPath $htmlPath)) {
    throw "未找到预览页面：$htmlPath"
}

# 定位 Debug 编译产物；若不存在提示用户先构建
function Get-NaniKoreDebugExecutable {
    param([string]$ProjectRoot)

    $known = Join-Path $ProjectRoot 'src\NaniKore\bin\Debug\net10.0-windows10.0.19041.0\NaniKore.exe'
    if (Test-Path -LiteralPath $known) {
        return $known
    }

    $fallback = Get-ChildItem -LiteralPath (Join-Path $ProjectRoot 'src\NaniKore\bin\Debug') -Recurse -Force -Filter 'NaniKore.exe' -ErrorAction SilentlyContinue |
        Sort-Object FullName | Select-Object -First 1
    return $fallback?.FullName
}

$executable = Get-NaniKoreDebugExecutable -ProjectRoot $root
if ($null -eq $executable) {
    Write-Error "未找到 Debug 版 NaniKore.exe！`n请先在仓库根目录执行 ./scripts/dev.ps1 build 进行编译后再启动本中转服务喵~"
    exit 1
}

# 创建临时 1x1 占位 PNG，供视觉一条龙（--image）路径组装取真值使用
$dummyPng = Join-Path $env:TEMP 'nanikore-preview-sample.png'
[System.IO.File]::WriteAllBytes($dummyPng, [System.Convert]::FromBase64String(
    'iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAAAXNSR0IArs4c6QAAAARnQU1BAACxjwv8YQUAAAAJcEhZcwAADsMAAA7DAcdvqGQAAAANSURBVBhXY/j///9/AAn7A/0FQ0XKAAAAAElFTkSuQmCC'))

# 绑定 HttpListener 端口（遇冲突自动递增）
$listener = $null
$boundPort = $Port
$maxPort = $Port + 20

while ($boundPort -le $maxPort) {
    try {
        $candidate = [System.Net.HttpListener]::new()
        $candidate.Prefixes.Add("http://127.0.0.1:$boundPort/")
        $candidate.Start()
        $listener = $candidate
        break
    }
    catch {
        $candidate.Close()
        $boundPort++
    }
}

if ($null -eq $listener) {
    Remove-Item -LiteralPath $dummyPng -Force -ErrorAction SilentlyContinue
    throw "无法在端口 $Port 至 $maxPort 范围内启动 HTTP 监听，请检查端口占用。"
}

$serverUrl = "http://127.0.0.1:$boundPort/"

# Ctrl+C 即时响应：
#   HttpListener.GetContext() 是同步阻塞调用，PowerShell 引擎在阻塞期间
#   无法收到中断信号（这就是「按了 Ctrl+C 没反应、要刷新页面终端才退出」的根因）。
#   这里注册 CancelKeyPress 处理器：收到 Ctrl+C 时主动 Stop() 监听器，
#   令挂起中的 GetContext 立即抛异常打破阻塞，同时顺手结束正在进行的 CLI 取真值子进程。
$script:stopRequested = $false
$script:activeProc = $null
$script:cancelHandler = {
    param($sender, $eventArgs)
    try { $eventArgs.Cancel = $true } catch { }
    $script:stopRequested = $true
    try {
        if ($null -ne $script:listener -and $script:listener.IsListening) {
            $script:listener.Stop()
        }
    }
    catch { }
    if ($null -ne $script:activeProc) {
        try { Stop-Process -Id $script:activeProc.Id -Force -ErrorAction SilentlyContinue }
        catch { }
    }
}
# 兜底：若宿主以 CLR CancelKeyPress 事件送达 Ctrl+C，直接置位并回收监听器。
# 注意本处理器不是主要机制——PowerShell 宿主自己会接管 Ctrl+C（症状就是"要刷新
# 页面才退出"），所以真正的解法在下面的取上下文循环：绝不让引擎卡死在阻塞调用里。
if ($null -ne [Console]::CancelKeyPress) {
    try { $null = [Console]::CancelKeyPress.Add($script:cancelHandler) }
    catch { Write-Warning "注册 Ctrl+C 兜底处理器失败：$($_.Exception.Message)" }
}


Write-Host "============================================================" -ForegroundColor Cyan
Write-Host " NaniKore 提示词实时预览中转服务已启动" -ForegroundColor Green
Write-Host " 本地地址: $serverUrl" -ForegroundColor Yellow
Write-Host " 调试程序: $executable" -ForegroundColor Gray
Write-Host " 按 Ctrl+C 停止服务" -ForegroundColor DarkGray
Write-Host "============================================================" -ForegroundColor Cyan

# 自动唤起默认浏览器
if (-not $NoLaunch) {
    Start-Process $serverUrl
}

try {
    # 用异步 GetContextAsync + 短超时轮询替代同步 GetContext()：
    # 同步阻塞调用会把 PowerShell 引擎钉死在当前语句，
    # Ctrl+C 只能排队等下一次语句边界（表现为"要刷新页面触发新请求才退出"）。
    # 轮询循环里每条语句边界都可被引擎检查中断，Ctrl+C 毫秒级生效。
    while ($listener.IsListening -and -not $script:stopRequested) {
        $pending = $listener.GetContextAsync()
        while (-not $pending.Wait(250) -and -not $script:stopRequested) {
            # Start-Sleep 是 cmdlet 调用，构成引擎中断检查点
            Start-Sleep -Milliseconds 50
        }
        if ($script:stopRequested -or -not $listener.IsListening)
        {
            try { $pending.Dispose() } catch { }
            break
        }
        $context = $pending.Result
        $request = $context.Request
        $response = $context.Response

        # 跨域与基础响应头设置
        $response.AddHeader('Access-Control-Allow-Origin', '*')
        $response.AddHeader('Access-Control-Allow-Methods', 'GET, OPTIONS')
        $response.AddHeader('Access-Control-Allow-Headers', 'Content-Type')

        if ($request.HttpMethod -eq 'OPTIONS') {
            $response.StatusCode = 204
            $response.Close()
            continue
        }

        $path = $request.Url.AbsolutePath

        # 路由 1：主页，伺服 prompt-preview.html
        if ($path -eq '/' -or $path -eq '/index.html') {
            $htmlBytes = [System.IO.File]::ReadAllBytes($htmlPath)
            $response.ContentType = 'text/html; charset=utf-8'
            $response.StatusCode = 200
            $response.OutputStream.Write($htmlBytes, 0, $htmlBytes.Length)
            $response.Close()
            continue
        }

        # 路由 2：/api/dump，代理调用 NaniKore.exe --dump-prompt --json 取真值
        if ($path -eq '/api/dump') {
            # 显式使用 UTF-8 解析 URL 查询参数，防止非 ASCII 乱码
            $query = [System.Web.HttpUtility]::ParseQueryString($request.Url.Query, [System.Text.Encoding]::UTF8)
            $text = $query['text']
            $kind = $query['kind']
            $target = $query['target']
            $image = $query['image']

            $psi = [System.Diagnostics.ProcessStartInfo]::new()
            $psi.FileName = $executable
            $psi.ArgumentList.Add('--dump-prompt')

            if (-not [string]::IsNullOrWhiteSpace($kind)) {
                $psi.ArgumentList.Add('--kind')
                $psi.ArgumentList.Add($kind)
            }

            if (-not [string]::IsNullOrWhiteSpace($target)) {
                $psi.ArgumentList.Add('--target')
                $psi.ArgumentList.Add($target)
            }

            if ($image -eq '1' -or $image -eq 'true') {
                $psi.ArgumentList.Add('--image')
                $psi.ArgumentList.Add($dummyPng)
            }
            elseif ($text -ne $null) {
                $psi.ArgumentList.Add('--text')
                $psi.ArgumentList.Add($text)
            }

            $psi.ArgumentList.Add('--json')
            $psi.RedirectStandardOutput = $true
            $psi.RedirectStandardError = $true
            $psi.UseShellExecute = $false
            $psi.CreateNoWindow = $true

            try {
                $proc = [System.Diagnostics.Process]::Start($psi)
                $script:activeProc = $proc
                $stdout = $proc.StandardOutput.ReadToEnd()
                $stderr = $proc.StandardError.ReadToEnd()
                $proc.WaitForExit()

                if ($proc.ExitCode -eq 0 -and -not [string]::IsNullOrWhiteSpace($stdout)) {
                    $jsonBytes = [System.Text.Encoding]::UTF8.GetBytes($stdout)
                    $response.ContentType = 'application/json; charset=utf-8'
                    $response.StatusCode = 200
                    $response.OutputStream.Write($jsonBytes, 0, $jsonBytes.Length)
                }
                else {
                    $response.ContentType = 'application/json; charset=utf-8'
                    $response.StatusCode = 400
                    $errObj = @{
                        error  = "CLI 执行异常（退出码 $($proc.ExitCode)）"
                        stderr = $stderr
                    } | ConvertTo-Json -Compress
                    $errBytes = [System.Text.Encoding]::UTF8.GetBytes($errObj)
                    $response.OutputStream.Write($errBytes, 0, $errBytes.Length)
                }
            }
            catch {
                $ex = $_.Exception
                $response.ContentType = 'application/json; charset=utf-8'
                $response.StatusCode = 500
                $errObj = @{
                    error   = "代理执行失败"
                    message = $ex.Message
                } | ConvertTo-Json -Compress
                $errBytes = [System.Text.Encoding]::UTF8.GetBytes($errObj)
                $response.OutputStream.Write($errBytes, 0, $errBytes.Length)
            }
            finally {
                $script:activeProc = $null
            }

            $response.Close()
            continue
        }

        # 未匹配路由返回 404
        $response.StatusCode = 404
        $response.Close()
    }
}
catch [System.Management.Automation.PipelineStoppedException] {
    # 引擎把 Ctrl+C 转成管线停止时静默收尾，清理逻辑统一在 finally
}
catch {
    # 个别宿主（如 WinPS 5.1 无 HaltException 类型）会把中断包成其他异常，
    # 仅"中断类"静默，其余放行不掩盖真实错误
    if ($_.Exception.GetType().Name -match 'Stop|Halt|Interrupt' -or "$($_.FullyQualifiedErrorId)" -match 'Stop|FlowControl') {
    }
    else {
        throw
    }
}
finally {
    try { [Console]::CancelKeyPress.Remove($script:cancelHandler) } catch { }
    if ($null -ne $listener) {
        if ($listener.IsListening) {
            $listener.Stop()
        }
        $listener.Close()
    }
    Remove-Item -LiteralPath $dummyPng -Force -ErrorAction SilentlyContinue
    Write-Host "`n中转服务已停止。" -ForegroundColor Gray
}
