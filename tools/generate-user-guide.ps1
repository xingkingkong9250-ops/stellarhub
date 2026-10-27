# 千星汇图 安装使用说明 docx 生成器（Word COM）
# 用法：powershell -ExecutionPolicy Bypass -File tools\generate-user-guide.ps1 -Version 1.0.0 -Out <目标.docx>
# 结构（四章节固定）：封面 → 第一章 安装 → 第二章 功能全览（源自 docs/USER_GUIDE.md）
#                    → 第三章 版本更新说明（源自 CHANGELOG.md）→ 第四章 卸载与反馈
param(
    [Parameter(Mandatory = $true)][string]$Version,
    [Parameter(Mandatory = $true)][string]$Out
)

$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$guidePath = Join-Path $root 'docs\USER_GUIDE.md'
$changelogPath = Join-Path $root 'CHANGELOG.md'

# Word 内置样式枚举（避免本地化样式名问题）
$wdStyleTitle = -63; $wdStyleH1 = -2; $wdStyleH2 = -3; $wdStyleH3 = -4; $wdStyleNormal = -1
$wdFormatDocx = 16

function Add-Para($doc, [string]$text, [int]$style) {
    $p = $doc.Paragraphs.Add()
    $p.Range.Style = $style
    $p.Range.Text = $text
    $p.Range.InsertParagraphAfter() | Out-Null
    return $p
}

function Convert-Inline([string]$s) {
    # 去掉行内 markdown 标记（Word 文档中保留纯文本）
    $s = $s -replace '\*\*', ''
    $s = $s -replace '`', ''
    $s = $s -replace '\[(?<t>[^\]]+)\]\([^)]+\)', '${t}'
    return $s.Trim()
}

$word = New-Object -ComObject Word.Application
$word.Visible = $false
try {
    $doc = $word.Documents.Add()
    $sel = $word.Selection

    # ─── 封面 ───
    Add-Para $doc '' $wdStyleNormal | Out-Null
    Add-Para $doc '千星汇图（StellarHub）' $wdStyleTitle | Out-Null
    Add-Para $doc "安装使用说明  v$Version" $wdStyleH2 | Out-Null
    Add-Para $doc "更新日期：$(Get-Date -Format 'yyyy-MM-dd')" $wdStyleNormal | Out-Null
    Add-Para $doc '3D 美术图片素材管理系统 · Windows 10 21H2+ / 11' $wdStyleNormal | Out-Null
    $doc.Words.Item(1).InsertBreak(7) | Out-Null  # wdPageBreak

    # ─── 第一章 安装 ───
    Add-Para $doc '第一章  安装' $wdStyleH1 | Out-Null
    Add-Para $doc '1.1 安装包安装（推荐）' $wdStyleH2 | Out-Null
    @(
        "双击 StellarHub_Setup_v$Version.exe 启动安装向导。",
        '按向导选择安装目录（默认当前用户 Programs 目录，无需管理员权限）。',
        '可勾选「创建桌面快捷方式」，完成后即可从开始菜单或桌面启动。',
        '安装包为 self-contained 形态，内置 .NET 运行时，目标电脑无需预装任何环境。'
    ) | ForEach-Object { Add-Para $doc $_ $wdStyleNormal | Out-Null }
    Add-Para $doc '1.2 绿色免安装版' $wdStyleH2 | Out-Null
    @(
        "解压 StellarHub_$($Version)_win-x64_portable.zip 到任意目录。",
        '双击其中的 StellarHub.exe 即可运行；数据默认存放于用户目录，与程序目录解耦。'
    ) | ForEach-Object { Add-Para $doc $_ $wdStyleNormal | Out-Null }
    Add-Para $doc '1.3 首次启动' $wdStyleH2 | Out-Null
    @(
        '首次启动进入首次运行向导：设置素材库根目录、登录账户（默认管理员 admin）、可选配置 NAS 与 AI 后端。',
        '旧版本升级：数据库自动按 schema 版本逐版迁移，只增不删，无需手动操作。'
    ) | ForEach-Object { Add-Para $doc $_ $wdStyleNormal | Out-Null }

    # ─── 第二章 功能全览（源 USER_GUIDE.md） ───
    Add-Para $doc '第二章  功能全览' $wdStyleH1 | Out-Null
    $inChapter = $false
    foreach ($line in [System.IO.File]::ReadLines($guidePath)) {
        $t = $line.TrimEnd()
        if ($t -match '^##\s+\d') { $inChapter = $true }
        if (-not $inChapter) { continue }
        if ($t -match '^##\s+(.+)$') { Add-Para $doc (Convert-Inline $Matches[1]) $wdStyleH2 | Out-Null; continue }
        if ($t -match '^###\s+(.+)$') { Add-Para $doc (Convert-Inline $Matches[1]) $wdStyleH3 | Out-Null; continue }
        if ($t -match '^#{4,}\s+(.+)$') { Add-Para $doc (Convert-Inline $Matches[1]) $wdStyleH3 | Out-Null; continue }
        if ($t -match '^[-*]\s+(.+)$') { Add-Para $doc ('· ' + (Convert-Inline $Matches[1])) $wdStyleNormal | Out-Null; continue }
        if ($t -match '^\d+\.\s+(.+)$') { Add-Para $doc $t $wdStyleNormal | Out-Null; continue }
        if ($t -match '^[-—]{3,}\s*$' -or $t -match '^\|') { continue }
        if ($t.Trim().Length -eq 0) { continue }
        Add-Para $doc (Convert-Inline $t) $wdStyleNormal | Out-Null
    }

    # ─── 第三章 版本更新说明（源 CHANGELOG.md，统一 版本号·标题·日期 + bullet） ───
    Add-Para $doc '第三章  版本更新说明' $wdStyleH1 | Out-Null
    $versionRe = '^##\s+\[(?<v>\d+\.\d+\.\d+)\]\s+[—-]\s+(?<d>\d{4}-\d{2}-\d{2})\s*(?<t>.*)$'
    $current = $false
    $count = 0
    foreach ($line in [System.IO.File]::ReadLines($changelogPath)) {
        $t = $line.TrimEnd()
        if ($t -match $versionRe) {
            $v = $Matches['v']; $d = $Matches['d']; $title = $Matches['t']
            if ($v -match '^0\.(0|1)\.') { break }  # 历史开发记录不录入
            $count++
            Add-Para $doc "v$v  $title  ·  $d" $wdStyleH3 | Out-Null
            $current = $true
            continue
        }
        if ($t -match '^##\s+\[Unreleased\]') { $current = $false; continue }
        if (-not $current) { continue }
        if ($t -match '^###\s+') { continue }
        if ($t -match '^[-*]\s+\*\*(.+?)\*\*[：:]\s*(.+)$') {
            Add-Para $doc ('· ' + (Convert-Inline $Matches[1]) + '：' + (Convert-Inline $Matches[2])) $wdStyleNormal | Out-Null
            continue
        }
        if ($t -match '^[-*]\s+(.+)$') { Add-Para $doc ('· ' + (Convert-Inline $Matches[1])) $wdStyleNormal | Out-Null; continue }
        if ($t -match '^\s{2,}\S' -and $t.Trim().Length -gt 0) {
            Add-Para $doc ('  ' + (Convert-Inline $t.Trim())) $wdStyleNormal | Out-Null
        }
    }
    if ($count -eq 0) { Add-Para $doc "v$Version  首个正式版" $wdStyleH3 | Out-Null }

    # ─── 第四章 卸载与反馈 ───
    Add-Para $doc '第四章  卸载与反馈' $wdStyleH1 | Out-Null
    @(
        '卸载：Windows「设置 → 应用」中找到「千星汇图 (StellarHub)」执行卸载；绿色版直接删除解压目录即可。',
        '卸载不会删除素材库数据与本地数据库；如需彻底清除，请另行删除素材库根目录下的库文件。',
        '反馈：使用中遇到问题或功能建议，请记录软件版本号（顶栏右侧显示）与操作步骤后向开发者反馈。'
    ) | ForEach-Object { Add-Para $doc $_ $wdStyleNormal | Out-Null }

    $outDir = Split-Path $Out -Parent
    if (-not (Test-Path $outDir)) { New-Item -ItemType Directory -Force -Path $outDir | Out-Null }
    $doc.SaveAs([ref]$Out, [ref]$wdFormatDocx)
    $doc.Close()
    Write-Output "DOCX_OK: $Out"
}
finally {
    $word.Quit()
    [System.Runtime.InteropServices.Marshal]::ReleaseComObject($word) | Out-Null
}
