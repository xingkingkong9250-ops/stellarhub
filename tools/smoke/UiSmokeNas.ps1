# QL World M5-C UIA 冒烟测试 — W3/A7 NAS 连接页签（列表 + 新建/删除 + 详情表单 + 测试/保存按钮）
# 用法: powershell -ExecutionPolicy Bypass -File tools\smoke\UiSmokeNas.ps1
# 前置: .NET 10 SDK；脚本自建 Debug 构建并用隔离数据根，不影响真实数据。
# 退出码: 0 = 全部通过；非 0 = 存在失败步骤。
# 覆盖：W3/A7 NAS 连接页签 — 列表（N1）/ 详情表单 N2-N5 / 测试连接 + 保存（N6）
#       M5 仅 WebDAV；SMB/SFTP 预留（下拉只有 WebDav 一项）。

$ErrorActionPreference = 'Stop'

# ── 路径与环境 ────────────────────────────────────────────────
$projectRoot = Split-Path -Parent (Split-Path -Parent (Split-Path -Parent $PSCommandPath))
$sourceProject = Join-Path $projectRoot 'src\QL.World'
$outputDirectory = Join-Path $sourceProject 'bin\Debug\net10.0-windows'
$applicationExe = Join-Path $outputDirectory 'StellarHub.exe'
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
public class UiSmokeNasW32 {
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
    $callback = [UiSmokeNasW32+EnumWindowsProc]{
        param($hWnd, $lParam)
        $wpid = 0
        [UiSmokeNasW32]::GetWindowThreadProcessId($hWnd, [ref]$wpid) | Out-Null
        if ($wpid -eq $OwnerPid -and [UiSmokeNasW32]::IsWindowVisible($hWnd)) {
            $sb = New-Object System.Text.StringBuilder 256
            [UiSmokeNasW32]::GetWindowText($hWnd, $sb, 256) | Out-Null
            if ($sb.ToString() -like $TitleWildcard) { $script:foundHwnd = $hWnd }
        }
        return $true
    }
    [UiSmokeNasW32]::EnumWindows($callback, [IntPtr]::Zero) | Out-Null
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

# ── 构建 ─────────────────────────────────────────────────────
Write-Host '[Smoke] Building Debug...'
& dotnet build $sourceProject -c Debug --nologo -v q -o $outputDirectory 2>&1 | Out-Null
if ($LASTEXITCODE -ne 0) { Write-Error '[Smoke] Build failed.'; exit 1 }
if (-not (Test-Path -LiteralPath $applicationExe)) { Write-Error "[Smoke] Missing exe: $applicationExe"; exit 1 }

# ── 启动 ───────────────────────────────────────────────────────
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

    # ── 1. 打开设置窗口 ─────────────────────────────────────────
    Write-Host '[Smoke] Step 1: open settings'
    $gear = Find-FirstByName $mainWin ([char]0x2699) ([System.Windows.Automation.ControlType]::Button)
    Assert-Step '设置齿轮按钮存在' ($null -ne $gear) '未找到 ⚙ 按钮'
    if ($gear -ne $null) {
        ($gear.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern)).Invoke()
        Start-Sleep -Seconds 2
    }
    $settingsWin = Find-TopWindowByTitle -OwnerPid $process.Id -TitleWildcard '设置*'
    Assert-Step '设置窗口打开' ($null -ne $settingsWin) 'Win32 EnumWindows 未找到设置窗口'

    if ($null -ne $settingsWin) {
        # ── 2. 切到 NAS 连接页签 ────────────────────────────────
        Write-Host '[Smoke] Step 2: switch to NAS tab'
        $nasTab = Find-FirstByName $settingsWin 'NAS 连接' ([System.Windows.Automation.ControlType]::TabItem)
        Assert-Step 'NAS 连接页签存在 (W3/A7)' ($null -ne $nasTab) '未找到 Name="NAS 连接" TabItem'
        if ($nasTab -ne $null) {
            ($nasTab.GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern)).Select()
            Start-Sleep -Milliseconds 800

            # ── 3. 列表区控件存在性 ─────────────────────────────
            Write-Host '[Smoke] Step 3: list area controls'
            $btnAdd    = Find-FirstByName $settingsWin '新建 NAS 连接' ([System.Windows.Automation.ControlType]::Button)
            $btnDelete = Find-FirstByName $settingsWin '删除 NAS 连接' ([System.Windows.Automation.ControlType]::Button)
            Assert-Step 'A7-N1：新建按钮存在' ($null -ne $btnAdd) '未找到 Name="新建 NAS 连接" 按钮'
            Assert-Step 'A7-N1：删除选中按钮存在' ($null -ne $btnDelete) '未找到 Name="删除 NAS 连接" 按钮'

            # ListBox 控件存在（绑定 NasConnections ObservableCollection）
            $lbCond = New-Object System.Windows.Automation.PropertyCondition(
                [System.Windows.Automation.AutomationElement]::ControlTypeProperty,
                [System.Windows.Automation.ControlType]::ListBox)
            $listBox = $settingsWin.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $lbCond)
            Assert-Step 'A7-N1：连接 ListBox 控件存在' ($null -ne $listBox) '未找到 ListBox'

            # ── 4. 点击新建 → 列表新增一行 "新连接" + 详情表单显形 ──
            Write-Host '[Smoke] Step 4: click add → list populates + detail form appears'
            if ($btnAdd -ne $null) {
                ($btnAdd.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern)).Invoke()
                Start-Sleep -Milliseconds 800
            }
            $newRow = Find-FirstByName $settingsWin '新连接' ([System.Windows.Automation.ControlType]::Text)
            Assert-Step 'A7-N1：新建后列表出现「新连接」行' ($null -ne $newRow) '未找到 Name="新连接" 行'

            # ── 5. 详情表单 N2-N5 控件存在性 ───────────────────
            Write-Host '[Smoke] Step 5: detail form N2-N5 controls'
            $eName  = Find-FirstByName $settingsWin 'NAS 连接名称' ([System.Windows.Automation.ControlType]::Edit)
            $eProto = Find-FirstByName $settingsWin 'NAS 协议' ([System.Windows.Automation.ControlType]::ComboBox)
            $eHost  = Find-FirstByName $settingsWin 'NAS 主机' ([System.Windows.Automation.ControlType]::Edit)
            $ePort  = Find-FirstByName $settingsWin 'NAS 端口' ([System.Windows.Automation.ControlType]::Edit)
            $eUser  = Find-FirstByName $settingsWin 'NAS 用户名' ([System.Windows.Automation.ControlType]::Edit)
            $ePass  = Find-FirstByName $settingsWin 'NAS 密码' ([System.Windows.Automation.ControlType]::Edit)
            $eRoot  = Find-FirstByName $settingsWin 'NAS 根路径' ([System.Windows.Automation.ControlType]::Edit)
            $cbTls  = Find-FirstByName $settingsWin 'NAS 启用 TLS' ([System.Windows.Automation.ControlType]::CheckBox)
            $cbCert = Find-FirstByName $settingsWin 'NAS 信任自签' ([System.Windows.Automation.ControlType]::CheckBox)
            Assert-Step 'A7-N2：名称输入框存在' ($null -ne $eName) '未找到 Name="NAS 连接名称"'
            Assert-Step 'A7-N3：协议下拉存在' ($null -ne $eProto) '未找到 Name="NAS 协议"'
            Assert-Step 'A7-N3：主机输入框存在' ($null -ne $eHost) '未找到 Name="NAS 主机"'
            Assert-Step 'A7-N3：端口输入框存在' ($null -ne $ePort) '未找到 Name="NAS 端口"'
            Assert-Step 'A7-N4：用户名输入框存在' ($null -ne $eUser) '未找到 Name="NAS 用户名"'
            Assert-Step 'A7-N4：密码框存在' ($null -ne $ePass) '未找到 Name="NAS 密码"'
            Assert-Step 'A7-N5：根路径输入框存在' ($null -ne $eRoot) '未找到 Name="NAS 根路径"'
            Assert-Step 'A7-N5：启用 TLS 复选框存在' ($null -ne $cbTls) '未找到 Name="NAS 启用 TLS"'
            Assert-Step 'A7-N5：信任自签复选框存在' ($null -ne $cbCert) '未找到 Name="NAS 信任自签"'

            # ── 6. 测试连接 + 保存按钮（N6）存在性 ──────────────
            Write-Host '[Smoke] Step 6: test + save buttons (N6)'
            $btnTest = Find-FirstByName $settingsWin '测试 NAS 连接' ([System.Windows.Automation.ControlType]::Button)
            $btnSave = Find-FirstByName $settingsWin '保存 NAS 连接' ([System.Windows.Automation.ControlType]::Button)
            Assert-Step 'A7-N6：测试连接按钮存在' ($null -ne $btnTest) '未找到 Name="测试 NAS 连接" 按钮'
            Assert-Step 'A7-N6：保存按钮存在' ($null -ne $btnSave) '未找到 Name="保存 NAS 连接" 按钮'
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
