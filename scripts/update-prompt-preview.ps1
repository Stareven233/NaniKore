#!/usr/bin/env pwsh
<#
.SYNOPSIS
  重新生成 scripts/prompt-preview.html 的内嵌数据（提示词常量 + 语言探测区间）。
.DESCRIPTION
  预览工具中的提示词与探测数据全部由本脚本从源码提取，保证「所见即所发」：
    - src/NaniKore/Translation/PromptTemplates.cs   六个提示词变量
    - src/NaniKore/Translation/LanguageDetector.cs  字符区间与简体独占字表
  字面量解析复刻 C# 原始字符串规则：按闭合定界符的列宽剥离公共前导空白，
  纯空白行归一为空行；随后复刻 PromptLiteral 的行尾归一化（CRLF/CR -> LF）。
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
    'DefaultVisionBasePrompt', 'DefaultExplainBasePrompt', 'DefaultExplainUserPrompt'
)
$missing = $required | Where-Object { -not $literals.Contains($_) }
if ($missing) {
    throw ("未能从 PromptTemplates.cs 提取：{0}" -f ($missing -join ', '))
}

$data = [ordered]@{
    source = [ordered]@{
        prompts = 'src/NaniKore/Translation/PromptTemplates.cs'
        detector = 'src/NaniKore/Translation/LanguageDetector.cs'
    }
    prompts = [ordered]@{
        translationBase = $literals['DefaultTranslationBasePrompt']
        visionBase = $literals['DefaultVisionBasePrompt']
        explainBase = $literals['DefaultExplainBasePrompt']
        explainUser = $literals['DefaultExplainUserPrompt']
        japanese = $literals['JapanesePlugin']
        english = $literals['EnglishPlugin']
    }
    detector = $detector
}

$json = $data | ConvertTo-Json -Depth 8 -Compress

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
