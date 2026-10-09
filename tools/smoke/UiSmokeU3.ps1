# QL World U3 UIA 冒烟测试 — B1-COL 分区折叠+计数+重启持久化 / B1-5 属性筛选 chips /
# A1 完整性复选框 false 持久化（WhenWritingDefault 修复的 UI 级验证）
# 用法: powershell -ExecutionPolicy Bypass -File tools\smoke\UiSmokeU3.ps1
# 前置: .NET 10 SDK；脚本自建 Debug 构建并用隔离数据根，不影响真实数据。
# 退出码: 0 = 全部通过；非 0 = 存在失败步骤。
# 覆盖：W1/B1-COL 五分区 ToggleButton、折叠态计数文本、config.json false 显式写入、
#       同数据根重启后折叠态保持；W1/B1-5 属性筛选展开后 chips 存在性；
#       W3/A1 完整性复选框取消勾选后的持久化。
# 未覆盖：W1/B2-3-ACT hover 操作组（空库无卡片，且 IsMouseOver 需真实光标，留待人工验收）。

$ErrorActionPreference = 'Stop'

# ── 路径与环境 ────────────────────────────────────────────────
$projectRoot = Split-Path -Parent (Split-Path -Parent (Split-Path -Parent $PSCommandPath))
$sourceProject = Join-Path $projectRoot 'src\QL.World'
$outputDirectory = Join-Path $sourceProject 'bin\Debug\net10.0-windows'
$applicationExe = Join-Path $outputDirectory 'StellarHub.exe'
$dataRoot = Join-Path $env:TEMP ("QL_World_UiSmokeU3_" + [Guid]::NewGuid().ToString('N').Substring(0, 8))
$configPath = Join-Path $dataRoot 'config.json'
$shutdownEventName = 'Local\QL_World_UiTest_GracefulShutdown'

$env:QL_WORLD_UI_TEST_MODE = '1'
$env:QL_WORLD_DATA_ROOT = $dataRoot

$results = New-Object System.Collections.Generic.List[string]
$script:failures = 0

function Assert-Step {
    param([string]$Name, [bool]$Condition, [string]$Detail = '')
    if ($Condition) {
        $script:results.Add("PASS  $Name") | Out-Null
        Write-Host "  [PASS] $Name"
    }
    else {
        $script:results.Add("FAIL  $Name :: $Detail") | Out-Null
        $script:failures++
        Write-Host "  [FAIL] $Name :: $Detail"
    }
}

Add-Type -AssemblyName UIAutomationClient, UIAutomationTypes

Add-Type @"
using System;
using System.Runtime.InteropServices;
using System.Text;
public class UiSmokeU3W32 {
    [DllImport("user32.dll")] public static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);
    [DllImport("user32.dll")] public static extern int GetWindowText(IntPtr hWnd, StringBuilder lpString, int nMaxCount);
    [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);
    [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr hWnd);
    public delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);
}
"@

function Find-TopWindowByTitle {
    # WPF 模态窗口在 UIA 顶层枚举可能不可见，走 Win32 EnumWindows fallback（M2 经验）
    param([int]$OwnerPid, [string]$TitleWildcard)
    $script:foundHwnd = [IntPtr]::Zero
    $callback = [UiSmokeU3W32+EnumWindowsProc]{
        param($hWnd, $lParam)
        $wpid = 0
        [UiSmokeU3W32]::GetWindowThreadProcessId($hWnd, [ref]$wpid) | Out-Null
        if ($wpid -eq $OwnerPid -and [UiSmokeU3W32]::IsWindowVisible($hWnd)) {
            $sb = New-Object System.Text.StringBuilder 256
            [UiSmokeU3W32]::GetWindowText($hWnd, $sb, 256) | Out-Null
            if ($sb.ToString() -like $TitleWildcard) { $script:foundHwnd = $hWnd }
        }
        return $true
    }
    [UiSmokeU3W32]::EnumWindows($callback, [IntPtr]::Zero) | Out-Null
    if ($script:foundHwnd -ne [IntPtr]::Zero) {
        return [System.Windows.Automation.AutomationElement]::FromHandle($script:foundHwnd)
    }
    return $null
}

function Find-FirstByName {
    param([System.Windows.Automation.AutomationElement]$Root, [string]$Name,
          [System.Windows.Automation.ControlType]$Type)
    $cond = New-Object System.Windows.Automation.AndCondition(
        (New-Object System.Windows.Automation.PropertyCondition(
            [System.Windows.Automation.AutomationElement]::NameProperty, $Name)),
        (New-Object System.Windows.Automation.PropertyCondition(
            [System.Windows.Automation.AutomationElement]::ControlTypeProperty, $Type)))
    return $Root.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $cond)
}

function Get-ToggleState {
    param([System.Windows.Automation.AutomationElement]$Element)
    $pattern = $Element.GetCurrentPattern([System.Windows.Automation.TogglePattern]::Pattern)
    return $pattern.Current.ToggleState
}

function Get-HeaderText {
    # ToggleButton 的 AutomationProperties.Name 固定为分区名，
    # 可见计数文本取其后代第一个 Text 元素的 Name（如「素材库 (12)」）。
    param([System.Windows.Automation.AutomationElement]$Toggle)
    $cond = New-Object System.Windows.Automation.PropertyCondition(
        [System.Windows.Automation.AutomationElement]::ControlTypeProperty,
        [System.Windows.Automation.ControlType]::Text)
    $text = $Toggle.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $cond)
    if ($null -eq $text) { return '' }
    return $text.Current.Name
}

function Start-App {
    $p = Start-Process -FilePath $applicationExe -WorkingDirectory $outputDirectory -PassThru
    $deadline = [DateTime]::UtcNow.AddSeconds(20)
    while ([DateTime]::UtcNow -lt $deadline) {
        $p.Refresh()
        if ($p.HasExited) { throw "[Smoke] App exited during startup (code $($p.ExitCode))." }
        if ($p.MainWindowHandle -ne [IntPtr]::Zero) {
            $win = [System.Windows.Automation.AutomationElement]::FromHandle($p.MainWindowHandle)
            if ($win -ne $null) { return @{ Process = $p; Window = $win } }
        }
        Start-Sleep -Milliseconds 300
    }
    throw '[Smoke] Main window not found within 20s.'
}

function Stop-App {
    param($App)
    if ($null -ne $App.Process -and -not $App.Process.HasExited) {
        try {
            $ev = [System.Threading.EventWaitHandle]::OpenExisting($shutdownEventName)
            try { [void]$ev.Set() } finally { $ev.Dispose() }
            $App.Process.Refresh()
            if (-not $App.Process.WaitForExit(8000)) {
                $App.Process.CloseMainWindow() | Out-Null
                $App.Process.WaitForExit(5000) | Out-Null
            }
        }
        catch [System.Threading.WaitHandleCannotBeOpenedException] {
            $App.Process.CloseMainWindow() | Out-Null
            $App.Process.WaitForExit(5000) | Out-Null
        }
        $App.Process.Refresh()
        if (-not $App.Process.HasExited) { Stop-Process -Id $App.Process.Id -Force -ErrorAction SilentlyContinue }
    }
}

# ── 构建 ─────────────────────────────────────────────────────
Write-Host '[Smoke] Building Debug...'
& dotnet build $sourceProject -c Debug --nologo -v q -o $outputDirectory 2>&1 | Out-Null
if ($LASTEXITCODE -ne 0) { Write-Error '[Smoke] Build failed.'; exit 1 }
if (-not (Test-Path -LiteralPath $applicationExe)) { Write-Error "[Smoke] Missing exe: $applicationExe"; exit 1 }

$app = $null
try {
    # ══ 第一轮：折叠 + 取消勾选 + 文件级断言 ════════════════════
    Write-Host '[Smoke] Launch #1 (isolated data root)'
    $app = Start-App
    $mainWin = $app.Window
    Write-Host "  Main window: $($mainWin.Current.Name)"

    # ── 1. 五分区 ToggleButton 存在（B1-COL + B1-5）──────────────
    Write-Host '[Smoke] Step 1: section header toggle buttons exist'
    $tLibrary  = Find-FirstByName $mainWin '素材库'   ([System.Windows.Automation.ControlType]::Button)
    $tCollect  = Find-FirstByName $mainWin '收藏夹'   ([System.Windows.Automation.ControlType]::Button)
    $tTopTags  = Find-FirstByName $mainWin '热门标签' ([System.Windows.Automation.ControlType]::Button)
    $tPbr      = Find-FirstByName $mainWin '贴图组'   ([System.Windows.Automation.ControlType]::Button)
    $tAttr     = Find-FirstByName $mainWin '属性筛选' ([System.Windows.Automation.ControlType]::Button)
    Assert-Step '五分区 ToggleButton 存在 (B1-COL + B1-5)' (
        $null -ne $tLibrary -and $null -ne $tCollect -and $null -ne $tTopTags -and
        $null -ne $tPbr -and $null -ne $tAttr) '缺少分区标题 ToggleButton'
    if ($null -ne $tLibrary) {
        Assert-Step '素材库标题支持 TogglePattern' (
            $tLibrary.GetSupportedPatterns() -contains [System.Windows.Automation.TogglePattern]::Pattern) '不支持 TogglePattern'
    }

    # ── 2. 折叠素材库 → 计数文本 (8) ───────────────────────────
    Write-Host '[Smoke] Step 2: collapse 素材库 shows count text'
    if ($null -ne $tLibrary) {
        ($tLibrary.GetCurrentPattern([System.Windows.Automation.TogglePattern]::Pattern)).Toggle()
        Start-Sleep -Milliseconds 800
        $state = Get-ToggleState $tLibrary
        Assert-Step '折叠后 ToggleState = Off' ($state -eq [System.Windows.Automation.ToggleState]::Off) "实际 $state"
        $tLibrary = Find-FirstByName $mainWin '素材库' ([System.Windows.Automation.ControlType]::Button)
        $header = Get-HeaderText $tLibrary
        # M12-F: 全部 + 6 新分类 + 未分类 = 8
        Assert-Step '折叠态标题显示「素材库 (8)」' ($header -eq '素材库 (8)') "实际「$header」（8 = 全部+6分类+未分类）"
    }

    # ── 3. 展开属性筛选 → chips 出现 ────────────────────────────
    Write-Host '[Smoke] Step 3: expand 属性筛选 shows chips'
    if ($null -ne $tAttr) {
        ($tAttr.GetCurrentPattern([System.Windows.Automation.TogglePattern]::Pattern)).Toggle()
        Start-Sleep -Milliseconds 800
        $chipMagenta = Find-FirstByName $mainWin '品红' ([System.Windows.Automation.ControlType]::Button)
        Assert-Step '颜色 chip「品红」出现 (B1-5-1)' ($null -ne $chipMagenta) '未找到 Name="品红" 的 chip 按钮'
        $allCond = New-Object System.Windows.Automation.AndCondition(
            (New-Object System.Windows.Automation.PropertyCondition(
                [System.Windows.Automation.AutomationElement]::NameProperty, '全部')),
            (New-Object System.Windows.Automation.PropertyCondition(
                [System.Windows.Automation.AutomationElement]::ControlTypeProperty,
                [System.Windows.Automation.ControlType]::Button)))
        $allChips = $mainWin.FindAll([System.Windows.Automation.TreeScope]::Descendants, $allCond)
        Assert-Step '四维各含「全部」chip（≥4）' ($allChips.Count -ge 4) "实际 $($allChips.Count) 个"
    }

    # ── 3b. M12-F: 展开筛选标签组 → 美术风格 chips 出现 ────────
    Write-Host '[Smoke] Step 3b: expand 筛选标签组 shows art-style chips (B1-6)'
    $tFilterGroups = Find-FirstByName $mainWin '筛选标签组' ([System.Windows.Automation.ControlType]::Button)
    Assert-Step '筛选标签组 ToggleButton 存在 (B1-6)' ($null -ne $tFilterGroups) '未找到 B1-6 分区标题'
    if ($null -ne $tFilterGroups) {
        ($tFilterGroups.GetCurrentPattern([System.Windows.Automation.TogglePattern]::Pattern)).Toggle()
        Start-Sleep -Milliseconds 800
        $chipRealistic = Find-FirstByName $mainWin '写实' ([System.Windows.Automation.ControlType]::Button)
        Assert-Step '美术风格 chip「写实」出现 (B1-6-1)' ($null -ne $chipRealistic) '未找到 Name="写实" 的 chip 按钮'
        $chipPng = Find-FirstByName $mainWin 'PNG' ([System.Windows.Automation.ControlType]::Button)
        Assert-Step '文件格式 chip「PNG」出现 (B1-6-2)' ($null -ne $chipPng) '未找到 Name="PNG" 的 chip 按钮'
    }

    # ── 4. 设置 A1 取消勾选完整性检查 ───────────────────────────
    Write-Host '[Smoke] Step 4: uncheck integrity checkbox in W3/A1'
    $gear = Find-FirstByName $mainWin ([char]0x2699) ([System.Windows.Automation.ControlType]::Button)
    if ($gear -ne $null) {
        ($gear.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern)).Invoke()
        Start-Sleep -Seconds 2
        $settingsWin = Find-TopWindowByTitle -OwnerPid $app.Process.Id -TitleWildcard '设置*'
        Assert-Step '设置窗口打开' ($null -ne $settingsWin) 'Win32 EnumWindows 未找到设置窗口'
        if ($null -ne $settingsWin) {
            $chk = Find-FirstByName $settingsWin '每次启动时检查缺失文件与数据库结构' (
                [System.Windows.Automation.ControlType]::CheckBox)
            Assert-Step 'A1 完整性复选框存在' ($null -ne $chk) '未找到完整性 CheckBox'
            if ($null -ne $chk) {
                if ((Get-ToggleState $chk) -eq [System.Windows.Automation.ToggleState]::On) {
                    ($chk.GetCurrentPattern([System.Windows.Automation.TogglePattern]::Pattern)).Toggle()
                    Start-Sleep -Milliseconds 500
                }
                Assert-Step '取消勾选后 ToggleState = Off' (
                    (Get-ToggleState $chk) -eq [System.Windows.Automation.ToggleState]::Off) '复选框未变为 Off'
            }
            ($settingsWin.GetCurrentPattern([System.Windows.Automation.WindowPattern]::Pattern)).Close()
            Start-Sleep -Seconds 1
        }
    }
    else {
        Assert-Step '设置齿轮按钮存在' $false '未找到 ⚙ 按钮'
    }

    # ── 5. config.json 显式写入 false（WhenWritingDefault 修复验证）──
    Write-Host '[Smoke] Step 5: config.json contains explicit false values'
    Start-Sleep -Milliseconds 500
    $json = Get-Content -LiteralPath $configPath -Raw -Encoding UTF8
    Assert-Step 'config.json 含 "checkIntegrityOnStartup": false' (
        $json -like '*"checkIntegrityOnStartup": false*') 'false 未被显式写入（会被 WhenWritingDefault 吞掉）'
    Assert-Step 'config.json 含 "sidebarLibraryExpanded": false' (
        $json -like '*"sidebarLibraryExpanded": false*') 'false 未被显式写入'

    # ══ 第二轮：同数据根重启 → 折叠态/计数保持 ══════════════════
    Write-Host '[Smoke] Graceful shutdown, relaunch with same data root'
    Stop-App $app
    Start-Sleep -Seconds 1
    $app = Start-App
    $mainWin = $app.Window

    Write-Host '[Smoke] Step 6: collapsed state persists after relaunch'
    $tLibrary2 = Find-FirstByName $mainWin '素材库' ([System.Windows.Automation.ControlType]::Button)
    Assert-Step '重启后素材库标题仍存在' ($null -ne $tLibrary2) '未找到素材库 ToggleButton'
    if ($null -ne $tLibrary2) {
        $state2 = Get-ToggleState $tLibrary2
        Assert-Step '重启后素材库保持折叠（Off）' (
            $state2 -eq [System.Windows.Automation.ToggleState]::Off) "实际 $state2"
        $header2 = Get-HeaderText $tLibrary2
        Assert-Step '重启后标题仍为「素材库 (8)」' ($header2 -eq '素材库 (8)') "实际「$header2」"
    }
    $tCollect2 = Find-FirstByName $mainWin '收藏夹' ([System.Windows.Automation.ControlType]::Button)
    if ($null -ne $tCollect2) {
        Assert-Step '重启后收藏夹保持展开（On）' (
            (Get-ToggleState $tCollect2) -eq [System.Windows.Automation.ToggleState]::On) '收藏夹应仍为展开'
    }
}
finally {
    if ($null -ne $app) { Stop-App $app }
    if (Test-Path -LiteralPath $dataRoot) {
        Remove-Item -LiteralPath $dataRoot -Recurse -Force -ErrorAction SilentlyContinue
    }
}

# ── 汇总 ─────────────────────────────────────────────────────
Write-Host ''
Write-Host '════════ UI Smoke (U3) Summary ════════'
$results | ForEach-Object { Write-Host $_ }
Write-Host '═══════════════════════════════════════'
if ($script:failures -gt 0) {
    Write-Host "RESULT: FAILED ($script:failures step(s))"
    exit 1
}
Write-Host 'RESULT: ALL PASSED'
exit 0
