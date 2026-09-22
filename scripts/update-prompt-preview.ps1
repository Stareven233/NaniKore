#!/usr/bin/env pwsh
<#
.SYNOPSIS
  重新生成 scripts/prompt-preview.html 的内嵌数据（提示词常量 + 语言探测区间 + 样本矩阵）。
.DESCRIPTION
  预览工具中的数据全部来自源码与可执行文件，保证「所见即所发」：
    - src/NaniKore/Translation/PromptTemplates.cs   五个提示词变量
    - src/NaniKore/Translation/LanguageDetector.cs  字符区间与简体独占字表
    - NaniKore.exe --dump-prompt --json              样本矩阵的组装真值
  字面量解析复刻 C# 原始字符串规则：按闭合定界符的列宽剥离公共前导空白，
  纯空白行归一为空行；随后复刻 PromptLiteral 的行尾归一化（CRLF/CR -> LF）。
  样本矩阵由已构建的 Debug 版 NaniKore.exe 逐场景转储而来，前端只渲染、不模拟组装逻辑；
  exe 缺失时跳过样本矩阵（仅告警），静态零件照常更新。
  改动内置提示词或探测器后必须重跑本脚本，否则预览将与实际下发漂移。
.EXAMPLE
  pwsh scripts/update-prompt-preview.ps1
#>
param(
    [string]$Root = (Split-Path -Parent $PSScriptRoot)
)

$ErrorActionPreference = 'Stop'

$promptFile = Join-Path $Root 'src/NaniKore/Translation/PromptTemplates.cs'
$detectorFile = Join-Path $Root 'src/NaniKore/Translation/LanguageDetector.cs'
$htmlFile = Join-Path $PSScriptRoot 'prompt-preview.html'

foreach ($file in @($promptFile, $detectorFile, $htmlFile)) {
    if (-not (Test-Path -LiteralPath $file)) {
        throw "缺少文件：$file"
    }
}

function ConvertTo-CodePoint {
    param([string]$Literal)
    if ($Literal -match '^\\u([0-9a-fA-F]{4})$') {
        return [int]("0x" + $Matches[1])
    }
    if ($Literal.Length -eq 1) {
        return [int][char]$Literal
    }
    throw "无法解析的字符字面量：'$Literal'"
}

# ── 1. 提示词字面量：复刻 C# """ 剥离 + PromptLiteral 归一化 ──────────────────
function Get-PromptLiterals {
    param([string]$Text)

    $literals = [ordered]@{}
    $rawPattern = '(?ms)internal\s+(?:static\s+readonly|const)\s+string\s+(\w+)\s*=\s*PromptLiteral\("""\r?\n(.*?)\r?\n([ \t]*)"""\);'
    foreach ($match in [regex]::Matches($Text, $rawPattern)) {
        $name = $match.Groups[1].Value
        $indent = $match.Groups[3].Value.Length
        $lines = $match.Groups[2].Value -split "\r?\n"
        $normalized = foreach ($line in $lines) {
            if ([string]::IsNullOrWhiteSpace($line)) {
                ''
            }
            elseif ($line.Length -ge $indent -and $line.Substring(0, $indent).Trim().Length -eq 0) {
                $line.Substring($indent)
            }
            else {
                $line
            }
        }
        $literals[$name] = ($normalized -join "`n")
    }

    return $literals
}

# ── 2. 探测器：字符区间按源码顺序提取（假名3 / 拉丁2 / 汉字2 / 简体10） ────────
function Get-DetectorData {
    param([string]$Text)

    $ranges = @(
        [regex]::Matches($Text, "ch >= '([^']+)' && ch <= '([^']+)'") | ForEach-Object {
            ,@(
                (ConvertTo-CodePoint $_.Groups[1].Value),
                (ConvertTo-CodePoint $_.Groups[2].Value)
            )
        }
    )
    if ($ranges.Count -ne 17) {
        throw "LanguageDetector.cs 的字符区间数量为 $($ranges.Count)，与生成器预期（17）不一致；请同步更新 update-prompt-preview.ps1 的区间切分。"
    }

    $block = [regex]::Match($Text, '(?s)private const string ExplicitSimplifiedChars\s*=\s*(.*?);').Groups[1].Value
    $explicit = (([regex]::Matches($block, '"([^"]*)"')) | ForEach-Object { $_.Groups[1].Value }) -join ''
    if ([string]::IsNullOrEmpty($explicit)) {
        throw '未能提取 ExplicitSimplifiedChars。'
    }

    return [ordered]@{
        kana      = @($ranges[0..2])
        latin     = @($ranges[3..4])
        cjk       = @($ranges[5..6])
        simplified = @($ranges[7..16])
        explicit  = $explicit
    }
}

$promptText = [System.IO.File]::ReadAllText($promptFile)
$detectorText = [System.IO.File]::ReadAllText($detectorFile)

$literals = Get-PromptLiterals -Text $promptText
$detector = Get-DetectorData -Text $detectorText

$required = @(
    'DefaultTranslationBasePrompt', 'JapanesePlugin', 'EnglishPlugin',
    'DefaultVisionBasePrompt', 'DefaultExplainUserPrompt'
)
$missing = $required | Where-Object { -not $literals.Contains($_) }
if ($missing) {
    throw ("未能从 PromptTemplates.cs 提取：{0}" -f ($missing -join ', '))
}

# ── 3. 样本矩阵：调用已构建的 exe 转储组装真值 ────────────────────────────
# 组装真值只来自 C#；PowerShell 端只做静态字面量搬运与矩阵编排，绝不复刻探测/组装逻辑。
function Get-NaniKoreDebugExecutable {
    param([string]$Root)

    $known = Join-Path $Root 'src\NaniKore\bin\Debug\net10.0-windows10.0.19041.0\NaniKore.exe'
    if (Test-Path -LiteralPath $known) {
        return $known
    }

    # TFM 目录改名时不至于全盘失效：退回 Debug 输出树里最近的一个 exe。
    $fallback = Get-ChildItem -LiteralPath (Join-Path $Root 'src\NaniKore\bin\Debug') -Recurse -Force -Filter 'NaniKore.exe' -ErrorAction SilentlyContinue |
        Sort-Object FullName | Select-Object -First 1
    return $fallback?.FullName
}

function Invoke-PromptDump {
    param(
        [Parameter(Mandatory)][string]$Executable,
        [Parameter(Mandatory)][string[]]$DumpArguments
    )

    # Start-Process -ArgumentList 不会给含空格的值自动加引号，"Hello world" 会被劈成两个参数。
    $argumentLine = ($DumpArguments | ForEach-Object {
        if ($_ -match '[ \t"]') { '"' + ($_ -replace '"', '\"') + '"' } else { $_ }
    }) -join ' '

    # 经临时文件中转：既避开 PowerShell 对原生控制台输出的解码干扰，也让非零退出码可判。
    $stdoutPath = [IO.Path]::GetTempFileName()
    $stderrPath = [IO.Path]::GetTempFileName()
    try {
        $process = Start-Process -FilePath $Executable -ArgumentList $argumentLine -Wait -PassThru -NoNewWindow `
            -RedirectStandardOutput $stdoutPath -RedirectStandardError $stderrPath
        if ($process.ExitCode -ne 0) {
            throw ("--dump-prompt 退出码 {0}：{1}" -f $process.ExitCode, ([IO.File]::ReadAllText($stderrPath)))
        }

        return [IO.File]::ReadAllText($stdoutPath)
    }
    finally {
        Remove-Item -LiteralPath $stdoutPath, $stderrPath -Force -ErrorAction SilentlyContinue
    }
}

$samples = @()
$executable = Get-NaniKoreDebugExecutable -Root $Root
if ($null -eq $executable) {
    Write-Warning '未找到 Debug 版 NaniKore.exe，跳过样本矩阵（静态零件照常更新）。先运行 ./scripts/dev.ps1 build 再重跑本脚本即可补齐。'
}
else {
    # 1×1 白色 PNG：视觉路径不需要真实内容，只需要「请求确实携带图片」以触发路径二组装。
    $sampleImage = Join-Path $env:TEMP 'nanikore-prompt-preview-sample.png'
    [IO.File]::WriteAllBytes($sampleImage, [Convert]::FromBase64String(
        'iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAAAXNSR0IArs4c6QAAAARnQU1BAACxjwv8YQUAAAAJcEhZcwAADsMAAA7DAcdvqGQAAAANSURBVBhXY/j///9/AAn7A/0FQ0XKAAAAAElFTkSuQmCC'))

    # 横跨三条路径，并把 009（视觉一条龙改为 Base+插件）/ 010（日英门控独立）的前后差异写进 note。
    $matrix = @(
        @{ Label = '纯假名日语'; Text = 'こんにちは'; Kind = 'translate'; Target = '中文（简体）'; Image = $false
           Note = '009 之前视觉与文本共用同一句提示词；009 之后文本路径的日语注音规约来自独立插件，可按需替换。只有日语插件命中，英语插件不注入。' }
        @{ Label = '日英混排'; Text = 'Appleの新しいiPhone発表会'; Kind = 'translate'; Target = '中文（简体）'; Image = $false
           Note = '010 之前 !HasJapanese 守卫让混排句只拿振仮名、丢掉 IPA；010 之后两个门控彼此独立，日英混排同享两套规约。' }
        @{ Label = '片假名 + 少量拉丁'; Text = 'ゲームSTART'; Kind = 'translate'; Target = '中文（简体）'; Image = $false
           Note = '010 的稀疏拉丁场景：混入的拉丁借词同样纳入英语规约，噪声防护由插件文案「拉丁字母拼写的单词」承担。' }
        @{ Label = '纯日语无拉丁'; Text = 'ラーメンは美味しい'; Kind = 'translate'; Target = '中文（简体）'; Image = $false
           Note = '010 之后：去掉守卫并没有放宽注入面——无拉丁字母仍只拿振仮名，英语插件照旧不注入。' }
        @{ Label = '纯汉字日文（目标中文）'; Text = '進入禁止'; Kind = 'translate'; Target = '中文（简体）'; Image = $false
           Note = '无假名的纯汉字 + 目标语言为中文 → 判定纯汉字日文，注入日语注音插件（is_pure_hanzi_japanese_candidate）。' }
        @{ Label = '纯汉字日文（目标英文）'; Text = '進入禁止'; Kind = 'translate'; Target = 'English'; Image = $false
           Note = '同一句原文、目标语言改为英文 → 不再判定纯汉字日文，两个插件都不注入：目标语言参与探测，不能只看字符。' }
        @{ Label = '简体中文'; Text = '今天天气很好，我们一起去公园散步吧'; Kind = 'translate'; Target = '中文（简体）'; Image = $false
           Note = '命中简体独占字 → 判定简体中文，两套注音规约都不注入；这是探测层「排除」价值的所在。' }
        @{ Label = '纯英语'; Text = 'Hello world'; Kind = 'translate'; Target = '中文（简体）'; Image = $false
           Note = '纯拉丁 → 只有英语插件命中，IPA 规约只作用于拉丁字母拼写的单词。' }
        @{ Label = '视觉一条龙'; Text = ''; Kind = 'translate'; Target = '中文（简体）'; Image = $true
           Note = '009 之后：视觉 Base + 两个语言插件恒定同拼，system prompt 与输入无关、恒定单变体，前缀缓存 100% 命中。' }
        @{ Label = '精讲 · 日语'; Text = '通れない'; Kind = 'explain'; Target = '中文（简体）'; Image = $false
           Note = '精讲 Base + 日语插件。注意应用内的精讲是阶段二追问：届时 system 通道沿用阶段一锁定值，本行展示的是不带 session 的一次性精讲组装。' }
        @{ Label = '精讲 · 日英混排'; Text = 'Appleの新しいiPhone発表会'; Kind = 'explain'; Target = '中文（简体）'; Image = $false
           Note = '010 的精讲侧验证：精讲路径与文本路径共用同一套 AppendLanguagePlugins，混排句同样同享两套规约。' }
    )

    try {
        foreach ($case in $matrix) {
            $dumpArguments = @('--dump-prompt', '--json', '--kind', $case.Kind, '--target', $case.Target)
            if ($case.Image) {
                $dumpArguments += @('--image', $sampleImage)
            }
            else {
                $dumpArguments += @('--text', $case.Text)
            }

            $samples += [ordered]@{
                label = $case.Label
                note  = $case.Note
                dump  = (Invoke-PromptDump -Executable $executable -DumpArguments $dumpArguments | ConvertFrom-Json)
            }
        }
    }
    finally {
        Remove-Item -LiteralPath $sampleImage -Force -ErrorAction SilentlyContinue
    }
}

$data = [ordered]@{
    source = [ordered]@{
        prompts = 'src/NaniKore/Translation/PromptTemplates.cs'
        detector = 'src/NaniKore/Translation/LanguageDetector.cs'
    }
    prompts = [ordered]@{
        translationBase = $literals['DefaultTranslationBasePrompt']
        visionBase = $literals['DefaultVisionBasePrompt']
        explainUser = $literals['DefaultExplainUserPrompt']
        japanese = $literals['JapanesePlugin']
        english = $literals['EnglishPlugin']
    }
    detector = $detector
    samples = @($samples)
}

$json = $data | ConvertTo-Json -Depth 12 -Compress

# JSON 中的 < > & 转义为 \u 形式，避免与 HTML 脚本块解析冲突
$safeJson = $json -replace '<', '\u003c' -replace '>', '\u003e' -replace '&', '\u0026'

$startMarker = '/*__EMBEDDED_DATA__*/'
$endMarker = '/*__END_EMBEDDED_DATA__*/'
$html = [System.IO.File]::ReadAllText($htmlFile)
if (-not $html.Contains($startMarker) -or -not $html.Contains($endMarker)) {
    throw "prompt-preview.html 中找不到内嵌数据标记（$startMarker ... $endMarker）。"
}

$replacement = "$startMarker`r`nconst EMBEDDED = $safeJson;`r`n$endMarker"
$pattern = [regex]::Escape($startMarker) + '(?s).*?' + [regex]::Escape($endMarker)
$html = [regex]::Replace($html, $pattern, { param($m) $replacement })

[System.IO.File]::WriteAllText($htmlFile, $html, (New-Object System.Text.UTF8Encoding($false)))

Write-Host "已更新 $htmlFile"
Write-Host ("  提示词变量：{0} 个（{1}）" -f $required.Count, ($required -join ', '))
Write-Host ("  探测区间：假名 {0} / 拉丁 {1} / 汉字 {2} / 简体 {3}；简体独占字 {4} 个" -f `
    $detector.kana.Count, $detector.latin.Count, $detector.cjk.Count, $detector.simplified.Count, $detector.explicit.Length)
Write-Host ("  样本矩阵：{0} 个场景（{1}）" -f $samples.Count, (($samples | ForEach-Object { $_.label }) -join ', '))
