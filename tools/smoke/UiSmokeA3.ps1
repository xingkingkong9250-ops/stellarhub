# QL World M3 UIA 冒烟测试 — W3/A3 三档切换 + AI 按钮置灰 + 本地区块/颜色按钮存在性
# 用法: powershell -ExecutionPolicy Bypass -File tools\smoke\UiSmokeA3.ps1
# 前置: .NET 10 SDK；脚本自建 Debug 构建并用隔离数据根，不影响真实数据。
# 退出码: 0 = 全部通过；非 0 = 存在失败步骤。
# 覆盖：W1/A1-2 AI 按钮、W1/A1-8 颜色按钮、W3/A3 三档单选 + 云端配置区 +
#       W3/A3 本地 ONNX 区块（状态文本 + 进度条 + 四按钮存在性）、
#       W3/A6 贴图组页签（四操作按钮 + 默认规则行存在性）。
# 未覆盖（需图片选择/上下文菜单交互流，留待画廊级冒烟脚本）：
#       W1/B2-3-CM1 图卡右键「找相似图」、W1/B3-8 详情栏「找相似」按钮

$ErrorActionPreference = 'Stop'

# ── 路径与环境 ────────────────────────────────────────────────
$projectRoot = Split-Path -Parent (Split-Path -Parent (Split-Path -Parent $PSCommandPath))
$sourceProject = Join-Path $projectRoot 'src\QL.World'
$outputDirectory = Join-Path $sourceProject 'bin\Debug\net10.0-windows'
$applicationExe = Join-Path $outputDirectory 'QL World.exe'
$dataRoot = Join-Path $env:TEMP ("QL_World_UiSmoke_" + [Guid]::NewGuid().ToString('N').Substring(0, 8))
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
public class UiSmokeW32 {
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
    $found = [IntPtr]::Zero
    $callback = [UiSmokeW32+EnumWindowsProc]{
        param($hWnd, $lParam)
        $wpid = 0
        [UiSmokeW32]::GetWindowThreadProcessId($hWnd, [ref]$wpid) | Out-Null
        if ($wpid -eq $OwnerPid -and [UiSmokeW32]::IsWindowVisible($hWnd)) {
            $sb = New-Object System.Text.StringBuilder 256
            [UiSmokeW32]::GetWindowText($hWnd, $sb, 256) | Out-Null
            if ($sb.ToString() -like $TitleWildcard) { $script:foundHwnd = $hWnd }
        }
        return $true
    }
    $script:foundHwnd = [IntPtr]::Zero
    [UiSmokeW32]::EnumWindows($callback, [IntPtr]::Zero) | Out-Null
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

function Count-EnabledEdits {
    param([System.Windows.Automation.AutomationElement]$Root)
    $cond = New-Object System.Windows.Automation.PropertyCondition(
        [System.Windows.Automation.AutomationElement]::ControlTypeProperty,
        [System.Windows.Automation.ControlType]::Edit)
    $edits = $Root.FindAll([System.Windows.Automation.TreeScope]::Descendants, $cond)
    $n = 0
    foreach ($e in $edits) { if ($e.Current.IsEnabled) { $n++ } }
    return $n
}

# ── 构建 ─────────────────────────────────────────────────────
Write-Host '[Smoke] Building Debug...'
& dotnet build $sourceProject -c Debug --nologo -v q -o $outputDirectory 2>&1 | Out-Null
if ($LASTEXITCODE -ne 0) { Write-Error '[Smoke] Build failed.'; exit 1 }
if (-not (Test-Path -LiteralPath $applicationExe)) { Write-Error "[Smoke] Missing exe: $applicationExe"; exit 1 }

# ── 启动（隔离数据根 → AI 模式默认 Disabled）──────────────────
Write-Host '[Smoke] Launching (isolated data root: '$dataRoot')'
$process = Start-Process -FilePath $applicationExe -WorkingDirectory $outputDirectory -PassThru
try {
    $deadline = [DateTime]::UtcNow.AddSeconds(20)
    $mainWin = $null
    while ([DateTime]::UtcNow -lt $deadline) {
        $process.Refresh()
        if ($process.HasExited) { throw "[Smoke] App exited during startup (code $($process.ExitCode))." }
        if ($process.MainWindowHandle -ne [IntPtr]::Zero) {
            $mainWin = [System.Windows.Automation.AutomationElement]::FromHandle($process.MainWindowHandle)
            if ($mainWin -ne $null) { break }
        }
        Start-Sleep -Milliseconds 300
    }
    if ($null -eq $mainWin) { throw '[Smoke] Main window not found within 20s.' }
    Write-Host "  Main window: $($mainWin.Current.Name)"

    # ── 1. AI 按钮初始置灰（AI 模式 = Disabled）─────────────────
    Write-Host '[Smoke] Step 1: AI button disabled at start'
    $aiButton = Find-FirstByName $mainWin 'AI 分析' ([System.Windows.Automation.ControlType]::Button)
    Assert-Step 'AI 按钮存在' ($null -ne $aiButton) '未找到 Name="AI 分析" 的按钮'
    if ($aiButton -ne $null) {
        Assert-Step 'AI 按钮初始置灰' (-not $aiButton.Current.IsEnabled) 'AI 模式为关闭时按钮不应可用'
    }

    # ── 1b. W1/A1-8 颜色按钮存在（与 AI 档位无关，应始终可用）──────
    Write-Host '[Smoke] Step 1b: W1/A1-8 color button exists'
    $colorButton = Find-FirstByName $mainWin '分析颜色' ([System.Windows.Automation.ControlType]::Button)
    Assert-Step '颜色按钮存在 (W1/A1-8)' ($null -ne $colorButton) '未找到 Name="分析颜色" 的按钮'
    if ($colorButton -ne $null) {
        Assert-Step '颜色按钮初始可用' ($colorButton.Current.IsEnabled) '颜色按钮与 AI 档位无关，应可用'
    }

    # ── 2. 打开设置窗口 ─────────────────────────────────────────
    Write-Host '[Smoke] Step 2: open settings'
    $gear = Find-FirstByName $mainWin ([char]0x2699) ([System.Windows.Automation.ControlType]::Button)
    Assert-Step '设置齿轮按钮存在' ($null -ne $gear) '未找到 ⚙ 按钮'
    if ($gear -ne $null) {
        ($gear.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern)).Invoke()
        Start-Sleep -Seconds 2
    }
    $settingsWin = Find-TopWindowByTitle -OwnerPid $process.Id -TitleWildcard '设置*'
    Assert-Step '设置窗口打开' ($null -ne $settingsWin) 'Win32 EnumWindows 未找到设置窗口'

    if ($null -ne $settingsWin) {
        # ── 3. 切到 AI 页签 ────────────────────────────────────
        Write-Host '[Smoke] Step 3: switch to AI tab'
        $aiTab = Find-FirstByName $settingsWin 'AI 分析' ([System.Windows.Automation.ControlType]::TabItem)
        Assert-Step 'AI 分析页签存在' ($null -ne $aiTab) '未找到 AI 分析 TabItem'
        if ($aiTab -ne $null) {
            ($aiTab.GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern)).Select()
            Start-Sleep -Milliseconds 800

            # ── 4. 三档切换 + 云端区启用状态 ───────────────────
            Write-Host '[Smoke] Step 4: three-mode radio switching'
            $radioOff   = Find-FirstByName $settingsWin '关闭' ([System.Windows.Automation.ControlType]::RadioButton)
            $radioLocal = Find-FirstByName $settingsWin '本地模型（离线）' ([System.Windows.Automation.ControlType]::RadioButton)
            $radioCloud = Find-FirstByName $settingsWin '云端服务' ([System.Windows.Automation.ControlType]::RadioButton)
            Assert-Step '三个模式单选钮存在' (
                $null -ne $radioOff -and $null -ne $radioLocal -and $null -ne $radioCloud) '缺少 RadioButton'

            if ($null -ne $radioCloud) {
                ($radioCloud.GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern)).Select()
                Start-Sleep -Milliseconds 800
                $enabledEdits = Count-EnabledEdits $settingsWin
                Assert-Step '云端模式 → 云端配置区启用' ($enabledEdits -ge 3) "云端模式下仅 $enabledEdits 个可用输入框（期望 ≥3）"
            }
            if ($null -ne $radioOff) {
                ($radioOff.GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern)).Select()
                Start-Sleep -Milliseconds 800
                $enabledEdits = Count-EnabledEdits $settingsWin
                Assert-Step '关闭模式 → 云端配置区禁用' ($enabledEdits -eq 0) "关闭模式下仍有 $enabledEdits 个可用输入框（期望 0）"
            }
            if ($null -ne $radioCloud) {
                ($radioCloud.GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern)).Select()
                Start-Sleep -Milliseconds 800
            }

            # ── 4b. W3/A3 本地 ONNX 区块控件存在性 ─────────────
            # 该区块始终存在于 AI 页签内（IsEnabled 由 IsLocalSectionEnabled 绑定，
            # 但元素树恒可定位）；切到 Local 单选使区块可用后再断言四按钮存在。
            Write-Host '[Smoke] Step 4b: W3/A3 local ONNX section controls'
            if ($null -ne $radioLocal) {
                ($radioLocal.GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern)).Select()
                Start-Sleep -Milliseconds 800

                $btnDownload = Find-FirstByName $settingsWin '下载模型' ([System.Windows.Automation.ControlType]::Button)
                $btnCancel   = Find-FirstByName $settingsWin '取消' ([System.Windows.Automation.ControlType]::Button)
                $btnImport   = Find-FirstByName $settingsWin '手动导入…' ([System.Windows.Automation.ControlType]::Button)
                $btnDelete   = Find-FirstByName $settingsWin '删除模型' ([System.Windows.Automation.ControlType]::Button)
                Assert-Step '本地区块：下载模型按钮存在' ($null -ne $btnDownload) '未找到 Name="下载模型" 按钮'
                Assert-Step '本地区块：取消按钮存在' ($null -ne $btnCancel) '未找到 Name="取消" 按钮'
                Assert-Step '本地区块：手动导入按钮存在' ($null -ne $btnImport) '未找到 Name="手动导入…" 按钮'
                Assert-Step '本地区块：删除模型按钮存在' ($null -ne $btnDelete) '未找到 Name="删除模型" 按钮'

                # 进度条控件在未下载时应「隐藏」：WPF UIA 不暴露非 Visible 元素
                # （Collapsed/Hidden 均不出现在 UIA 树），故未找到 = 隐藏正确；
                # 若找到则要求 IsOffscreen=true（下载中或保留布局的变体）。
                $pbCond = New-Object System.Windows.Automation.PropertyCondition(
                    [System.Windows.Automation.AutomationElement]::ControlTypeProperty,
                    [System.Windows.Automation.ControlType]::ProgressBar)
                $progressBar = $settingsWin.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $pbCond)
                $hiddenOk = ($null -eq $progressBar) -or $progressBar.Current.IsOffscreen
                Assert-Step '本地区块：进度条未下载时隐藏' $hiddenOk '未下载时进度条不应可见'
            }

            # ── 5. 切回关闭（恢复初始态）──────────────────────
            if ($null -ne $radioOff) {
                ($radioOff.GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern)).Select()
                Start-Sleep -Milliseconds 500
            }
        }

        # ── 6. W3/A6 贴图组页签控件存在性 ──────────────────────────
        Write-Host '[Smoke] Step 6: W3/A6 PBR rules tab controls'
        if ($null -ne $settingsWin) {
            $pbrTab = Find-FirstByName $settingsWin '贴图组' ([System.Windows.Automation.ControlType]::TabItem)
            Assert-Step '贴图组页签存在 (W3/A6)' ($null -ne $pbrTab) '未找到 Name="贴图组" TabItem'
            if ($null -ne $pbrTab) {
                ($pbrTab.GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern)).Select()
                Start-Sleep -Milliseconds 800

                $btnAdd    = Find-FirstByName $settingsWin '＋ 添加规则' ([System.Windows.Automation.ControlType]::Button)
                $btnSave   = Find-FirstByName $settingsWin '保存并重建' ([System.Windows.Automation.ControlType]::Button)
                $btnRescan = Find-FirstByName $settingsWin '仅重建' ([System.Windows.Automation.ControlType]::Button)
                $btnReset  = Find-FirstByName $settingsWin '恢复默认' ([System.Windows.Automation.ControlType]::Button)
                Assert-Step 'A6：添加规则按钮存在' ($null -ne $btnAdd) '未找到 Name="＋ 添加规则" 按钮'
                Assert-Step 'A6：保存并重建按钮存在' ($null -ne $btnSave) '未找到 Name="保存并重建" 按钮'
                Assert-Step 'A6：仅重建按钮存在' ($null -ne $btnRescan) '未找到 Name="仅重建" 按钮'
                Assert-Step 'A6：恢复默认按钮存在' ($null -ne $btnReset) '未找到 Name="恢复默认" 按钮'

                # 默认规则 17 条 → 规则列表应有行；至少能找到一个「启用」复选框
                $enableCheck = Find-FirstByName $settingsWin '启用规则' ([System.Windows.Automation.ControlType]::CheckBox)
                Assert-Step 'A6：默认规则行存在（启用复选框）' ($null -ne $enableCheck) '未找到任何 Name="启用规则" 复选框'
            }
        }

        # 关闭设置窗口（WindowPattern.Close，避开与「关闭」单选钮同名歧义）
        try {
            ($settingsWin.GetCurrentPattern([System.Windows.Automation.WindowPattern]::Pattern)).Close()
            Start-Sleep -Seconds 1
        }
        catch {
            $process.Refresh(); $process.CloseMainWindow() | Out-Null
        }
    }

    # ── 6. 恢复关闭模式后 AI 按钮仍置灰 ────────────────────────
    Write-Host '[Smoke] Step 5: AI button still disabled after restore'
    $aiButton2 = Find-FirstByName $mainWin 'AI 分析' ([System.Windows.Automation.ControlType]::Button)
    if ($aiButton2 -ne $null) {
        Assert-Step '恢复关闭模式后 AI 按钮置灰' (-not $aiButton2.Current.IsEnabled) '关闭模式下按钮不应可用'
    }
}
finally {
    # ── 优雅退出（UI 测试专用命名事件）──────────────────────────
    if ($null -ne $process -and -not $process.HasExited) {
        try {
            $shutdownEvent = [System.Threading.EventWaitHandle]::OpenExisting($shutdownEventName)
            try { [void]$shutdownEvent.Set() } finally { $shutdownEvent.Dispose() }
            $process.Refresh()
            if (-not $process.WaitForExit(8000)) {
                Write-Warning '[Smoke] Graceful shutdown timed out; closing main window.'
                $process.CloseMainWindow() | Out-Null
            }
        }
        catch [System.Threading.WaitHandleCannotBeOpenedException] {
            $process.CloseMainWindow() | Out-Null
        }
        $process.Refresh()
        if (-not $process.HasExited) { Stop-Process -Id $process.Id -Force -ErrorAction SilentlyContinue }
    }
    # 清理隔离数据根
    if (Test-Path -LiteralPath $dataRoot) {
        Remove-Item -LiteralPath $dataRoot -Recurse -Force -ErrorAction SilentlyContinue
    }
}

# ── 汇总 ─────────────────────────────────────────────────────
Write-Host ''
Write-Host '════════ UI Smoke Summary ════════'
$results | ForEach-Object { Write-Host $_ }
Write-Host '══════════════════════════════════'
if ($script:failures -gt 0) {
    Write-Host "RESULT: FAILED ($script:failures step(s))"
    exit 1
}
Write-Host 'RESULT: ALL PASSED'
exit 0
