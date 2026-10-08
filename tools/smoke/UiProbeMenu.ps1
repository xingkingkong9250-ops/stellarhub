# 受控实验：隔离实例导入后，键盘方式打开画廊右键菜单并枚举项状态（F-11 定性）
# 用法: powershell -ExecutionPolicy Bypass -File tools\smoke\UiProbeMenu.ps1
$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path -Parent (Split-Path -Parent (Split-Path -Parent $PSCommandPath))
$sourceProject = Join-Path $projectRoot 'src\QL.World'
$outputDirectory = Join-Path $sourceProject 'bin\Debug\net10.0-windows'
$applicationExe = Join-Path $outputDirectory 'StellarHub.exe'
$dataRoot = Join-Path $env:TEMP ("QL_Probe_" + [Guid]::NewGuid().ToString('N').Substring(0, 8))
$shutdownEventName = 'Local\QL_World_UiTest_GracefulShutdown'
$env:QL_WORLD_UI_TEST_MODE = '1'
$env:QL_WORLD_DATA_ROOT = $dataRoot
$testFolder = 'C:\Users\75943\Desktop\圣灵教AI\AI图'

Add-Type -AssemblyName UIAutomationClient, UIAutomationTypes
Add-Type -AssemblyName System.Windows.Forms
Add-Type @"
using System;
using System.Runtime.InteropServices;
using System.Text;
public class PW {
    [DllImport("user32.dll")] public static extern bool EnumWindows(EnumWindowsProc cb, IntPtr l);
    [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr h, out uint p);
    [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr h);
    [DllImport("user32.dll", EntryPoint="GetWindowTextW", CharSet=CharSet.Unicode)] public static extern int GetTitle(IntPtr h, StringBuilder s, int n);
    [DllImport("user32.dll")] public static extern bool EnumChildWindows(IntPtr h, EnumWindowsProc cb, IntPtr l);
    [DllImport("user32.dll")] public static extern int GetDlgCtrlID(IntPtr h);
    [DllImport("user32.dll", EntryPoint="SendMessageW", CharSet=CharSet.Unicode)] public static extern IntPtr SetText(IntPtr h, uint m, IntPtr w, string l);
    [DllImport("user32.dll", EntryPoint="SendMessageW")] public static extern IntPtr SendMsg(IntPtr h, uint m, IntPtr w, IntPtr l);
    public delegate bool EnumWindowsProc(IntPtr h, IntPtr l);
    public const uint WM_SETTEXT = 0x000C;
    public const uint BM_CLICK = 0x00F5;
}
"@

function Find-TopWindowByTitle {
    param([int]$OwnerPid, [string]$TitleWildcard)
    $deadline = [DateTime]::UtcNow.AddSeconds(15)
    $found = [IntPtr]::Zero
    while ([DateTime]::UtcNow -lt $deadline -and $found -eq [IntPtr]::Zero) {
        $cbEnum2 = {
            param($h, $l)
            $p = 0; [PW]::GetWindowThreadProcessId($h, [ref]$p) | Out-Null
            if ($p -eq $OwnerPid -and [PW]::IsWindowVisible($h)) {
                $sb = New-Object System.Text.StringBuilder 256
                [PW]::GetTitle($h, $sb, 256) | Out-Null
                if ($sb.ToString() -like $TitleWildcard) { $script:foundHwnd2 = $h }
            }
            return $true
        }
        $script:foundHwnd2 = [IntPtr]::Zero
        [PW]::EnumWindows($cbEnum2, [IntPtr]::Zero) | Out-Null
        $found = $script:foundHwnd2
        if ($found -eq [IntPtr]::Zero) { Start-Sleep -Milliseconds 300 }
    }
    if ($found -ne [IntPtr]::Zero) {
        return [System.Windows.Automation.AutomationElement]::FromHandle($found)
    }
    return $null
}
function Find-ButtonByNameContains {
    param([System.Windows.Automation.AutomationElement]$Root, [string]$Substring)
    $cond = New-Object System.Windows.Automation.PropertyCondition(
        [System.Windows.Automation.AutomationElement]::ControlTypeProperty,
        [System.Windows.Automation.ControlType]::Button)
    $buttons = $Root.FindAll([System.Windows.Automation.TreeScope]::Descendants, $cond)
    foreach ($b in $buttons) { if ($b.Current.Name -like "*$Substring*") { return $b } }
    return $null
}

Write-Host '[Probe] Launching isolated instance...'
$process = Start-Process -FilePath $applicationExe -WorkingDirectory $outputDirectory -PassThru
try {
    $loginWin = Find-TopWindowByTitle $process.Id '登录*'
    if ($loginWin) {
        ($loginWin.GetCurrentPattern([System.Windows.Automation.WindowPattern]::Pattern)).Close()
        Start-Sleep -Seconds 2
    }
    $mainWin = Find-TopWindowByTitle $process.Id '*千星汇图*'
    if (-not $mainWin) { throw 'main window not found' }
    Write-Host "[Probe] Main: $($mainWin.Current.Name)"

    # 强制窗口 1600x900，绕开 F-04 紧凑模式（<1500 宽时按钮变图标）
    Add-Type @"
using System;
using System.Runtime.InteropServices;
public class Resize {
    [DllImport("user32.dll")] public static extern bool MoveWindow(IntPtr h, int x, int y, int w, int ht, bool r);
}
"@
    $mh = [IntPtr]$mainWin.Current.NativeWindowHandle
    [Resize]::MoveWindow($mh, 20, 20, 2048, 1100, $true) | Out-Null
    Start-Sleep -Seconds 1

    # 诊断：转储所有按钮名+矩形，确认实际控件
    $btnCond = New-Object System.Windows.Automation.PropertyCondition(
        [System.Windows.Automation.AutomationElement]::ControlTypeProperty,
        [System.Windows.Automation.ControlType]::Button)
    $allBtns = $mainWin.FindAll([System.Windows.Automation.TreeScope]::Descendants, $btnCond)
    Write-Host "[Probe] ALL buttons:"
    foreach ($b in $allBtns) {
        $r = $b.Current.BoundingRectangle
        if ([double]::IsInfinity($r.Width)) { continue }
        Write-Host ("  '{0}' rect=({1:N0},{2:N0}) {3:N0}x{4:N0}" -f $b.Current.Name, $r.X, $r.Y, $r.Width, $r.Height)
    }
    $importBtn = Find-ButtonByNameContains $mainWin '导入'
    if (-not $importBtn) { $importBtn = Find-ButtonByNameContains $mainWin '＋' }
    if (-not $importBtn) { throw 'import button not found' }
    Write-Host ("[Probe] import button: '{0}' enabled={1}" -f $importBtn.Current.Name, $importBtn.Current.IsEnabled)
    try {
        ($importBtn.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern)).Invoke()
    } catch {
        Write-Host "[Probe] InvokePattern failed: $($_.Exception.Message) — fallback: no-op click via keyboard"
    }
    Start-Sleep -Seconds 2
    $importDlg = $null
    $retry = 0
    while (-not $importDlg -and $retry -lt 10) {
        $importDlg = Find-TopWindowByTitle $process.Id '*导入*'
        if (-not $importDlg) {
            # 诊断：列出进程所有顶层窗口名
            $rootAll = [System.Windows.Automation.AutomationElement]::RootElement
            $pcond = New-Object System.Windows.Automation.PropertyCondition(
                [System.Windows.Automation.AutomationElement]::ProcessIdProperty, $process.Id)
            $allWins = $rootAll.FindAll([System.Windows.Automation.TreeScope]::Children, $pcond)
            $names = @(); foreach ($w in $allWins) { $names += $w.Current.Name }
            Write-Host ("[Probe] windows: " + ($names -join ' | '))
            Start-Sleep -Seconds 1
        }
        $retry++
    }
    if (-not $importDlg) { throw 'import dialog not found' }
    $pickBtn = Find-ButtonByNameContains $importDlg '选择文件夹'
    ($pickBtn.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern)).Invoke()
    Start-Sleep -Seconds 2

    # Win32 文件夹对话框
    $dlgHwnd = [IntPtr]::Zero
    $cb = [PW+EnumWindowsProc]{
        param($h, $l)
        $p = 0; [PW]::GetWindowThreadProcessId($h, [ref]$p) | Out-Null
        if ($p -eq $process.Id -and [PW]::IsWindowVisible($h)) {
            $sb = New-Object System.Text.StringBuilder 256
            [PW]::GetTitle($h, $sb, 256) | Out-Null
            if ($sb.ToString() -like '*素材文件夹*' -or $sb.ToString() -like '*浏览*') { $script:dlgHwnd = $h }
        }
        return $true
    }
    $deadline = [DateTime]::UtcNow.AddSeconds(10)
    while ([DateTime]::UtcNow -lt $deadline -and $dlgHwnd -eq [IntPtr]::Zero) {
        $script:dlgHwnd = [IntPtr]::Zero
        [PW]::EnumWindows($cb, [IntPtr]::Zero) | Out-Null
        if ($dlgHwnd -ne [IntPtr]::Zero) { break }
        Start-Sleep -Milliseconds 300
    }
    if ($dlgHwnd -eq [IntPtr]::Zero) { throw 'folder dialog not found' }
    $editHwnd = [IntPtr]::Zero; $okHwnd = [IntPtr]::Zero
    $cbChild = [PW+EnumWindowsProc]{
        param($h, $l)
        $id = [PW]::GetDlgCtrlID($h)
        if ($id -eq 1152) { $script:editHwnd = $h }
        if ($id -eq 1) { $script:okHwnd = $h }
        return $true
    }
    [PW]::EnumChildWindows($dlgHwnd, $cbChild, [IntPtr]::Zero) | Out-Null
    if ($editHwnd -eq [IntPtr]::Zero -or $okHwnd -eq [IntPtr]::Zero) { throw 'folder dialog controls not found' }
    [PW]::SetText($editHwnd, [PW]::WM_SETTEXT, [IntPtr]::Zero, $testFolder) | Out-Null
    Start-Sleep -Milliseconds 400
    [PW]::SendMsg($okHwnd, [PW]::BM_CLICK, [IntPtr]::Zero, [IntPtr]::Zero) | Out-Null
    Start-Sleep -Seconds 2

    $runBtn = $null
    $retry = 0
    while (-not $runBtn -and $retry -lt 10) {
        $dlg2 = Find-TopWindowByTitle $process.Id '*导入*'
        if ($dlg2) {
            $cond = New-Object System.Windows.Automation.PropertyCondition(
                [System.Windows.Automation.AutomationElement]::NameProperty, '开始导入')
            $runBtn = $dlg2.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $cond)
        }
        Start-Sleep -Milliseconds 500; $retry++
    }
    if (-not $runBtn) { throw 'run import button not found' }
    ($runBtn.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern)).Invoke()
    Write-Host '[Probe] Importing...'
    $impDeadline = [DateTime]::UtcNow.AddSeconds(180)
    while ([DateTime]::UtcNow -lt $impDeadline) {
        $dlg = Find-TopWindowByTitle $process.Id '*导入*'
        if (-not $dlg) { break }
        $done = $null
        $dcond = New-Object System.Windows.Automation.PropertyCondition(
            [System.Windows.Automation.AutomationElement]::NameProperty, '完成')
        $done = $dlg.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $dcond)
        if ($done) { ($done.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern)).Invoke(); break }
        Start-Sleep -Seconds 2
    }
    Start-Sleep -Seconds 3
    Write-Host '[Probe] Import done.'

    # 键盘方式：画廊 List → SetFocus → Down 选中 → Shift+F10
    $mainWin = Find-TopWindowByTitle $process.Id '*千星汇图*'
    $listCond = New-Object System.Windows.Automation.PropertyCondition(
        [System.Windows.Automation.AutomationElement]::ControlTypeProperty,
        [System.Windows.Automation.ControlType]::List)
    $lists = $mainWin.FindAll([System.Windows.Automation.TreeScope]::Descendants, $listCond)
    $gallery = $null; $area = 0.0
    foreach ($l in $lists) {
        $r = $l.Current.BoundingRectangle
        if ([double]::IsInfinity($r.Width) -or $r.Width -le 0) { continue }
        $a = $r.Width * $r.Height
        if ($a -gt $area) { $gallery = $l; $area = $a }
    }
    if (-not $gallery) { throw 'gallery list not found' }
    $gr = $gallery.Current.BoundingRectangle
    Write-Host ("[Probe] Gallery rect: ({0:N0},{1:N0}) {2:N0}x{3:N0}" -f $gr.X, $gr.Y, $gr.Width, $gr.Height)
    $gallery.SetFocus()
    Start-Sleep -Milliseconds 500
    [System.Windows.Forms.SendKeys]::SendWait('{DOWN}')
    Start-Sleep -Milliseconds 600

    # 真实鼠标右键在画廊中心（决定性测试）
    Add-Type @"
using System;
using System.Runtime.InteropServices;
public class RC {
    [DllImport("user32.dll")] public static extern void mouse_event(uint f, uint dx, uint dy, uint d, UIntPtr e);
    [DllImport("user32.dll")] public static extern bool SetCursorPos(int x, int y);
}
"@
    $cx = [int]($gr.X + 120)
    $cy = [int]($gr.Y + 100)
    [RC]::SetCursorPos($cx, $cy) | Out-Null
    Start-Sleep -Milliseconds 300
    # 左键先选中卡片（右键不改变 WPF 选择）+ Ctrl+A 兜底全选
    [RC]::mouse_event(2, 0, 0, 0, [UIntPtr]::Zero)
    [RC]::mouse_event(4, 0, 0, 0, [UIntPtr]::Zero)
    Start-Sleep -Milliseconds 800
    [System.Windows.Forms.SendKeys]::SendWait('^a')
    Start-Sleep -Milliseconds 600
    [RC]::mouse_event(8, 0, 0, 0, [UIntPtr]::Zero)
    [RC]::mouse_event(16, 0, 0, 0, [UIntPtr]::Zero)
    Start-Sleep -Milliseconds 1200
    Write-Host "[Probe] Left-clicked (select) + right-clicked at ($cx, $cy)"

    # 枚举菜单：WPF ContextMenu 是无标题的 HwndWrapper 弹窗，需枚举进程窗口再 UIA 解析
    Start-Sleep -Milliseconds 800
    $menuFound = $false
    $cbMenu = {
        param($h, $l)
        $p = 0; [PW]::GetWindowThreadProcessId($h, [ref]$p) | Out-Null
        if ($p -eq $process.Id -and [PW]::IsWindowVisible($h) -and $h -ne $script:mainHwnd) {
            $sb = New-Object System.Text.StringBuilder 128
            [PW]::GetTitle($h, $sb, 128) | Out-Null
            if ([string]::IsNullOrEmpty($sb.ToString())) {
                $script:popupHwnds.Add($h)
            }
        }
        return $true
    }
    $script:mainHwnd = [IntPtr]$mainWin.Current.NativeWindowHandle
    $script:popupHwnds = New-Object System.Collections.Generic.List[IntPtr]
    [PW]::EnumWindows($cbMenu, [IntPtr]::Zero) | Out-Null
    Write-Host "[Probe] Found $($script:popupHwnds.Count) popup window(s)"
    foreach ($ph in $script:popupHwnds) {
        try {
            $el = [System.Windows.Automation.AutomationElement]::FromHandle($ph)
            $items = $el.FindAll([System.Windows.Automation.TreeScope]::Descendants, (New-Object System.Windows.Automation.PropertyCondition(
                [System.Windows.Automation.AutomationElement]::ControlTypeProperty,
                [System.Windows.Automation.ControlType]::MenuItem)))
            if ($items.Count -gt 0) {
                $menuFound = $true
                Write-Host "=== CONTEXT MENU at popup hwnd=${ph} ===  name=$($el.Current.Name)"
                foreach ($i in $items) {
                    $state = if ($i.Current.IsEnabled) { 'ENABLED ' } else { 'DISABLED' }
                    Write-Host ("  [{0}] {1}" -f $state, $i.Current.Name)
                }
                # 端到端验证：点击「复制文件路径」→ 校验剪贴板
                $copyItem = $null
                foreach ($i in $items) { if ($i.Current.Name -like '*复制文件路径*') { $copyItem = $i; break } }
                if ($copyItem) {
                    if (-not $copyItem.Current.IsEnabled) {
                        Write-Host 'VERIFY-RESULT: copy item DISABLED (command bound but no selection?)'
                    } else {
                        Add-Type -AssemblyName Microsoft.VisualBasic
                        [Microsoft.VisualBasic.Devices.Computer]::new().Clipboard.Clear()
                        ($copyItem.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern)).Invoke()
                        Start-Sleep -Milliseconds 1200
                        $clip = ''
                        try { $clip = [System.Windows.Forms.Clipboard]::GetText() } catch {}
                        if ($clip -and $clip -match '\.(jpg|jpeg|png|webp|gif|bmp|tga|exr|hdr|psd)$') {
                            Write-Host "VERIFY-RESULT: PASS clipboard=$clip"
                        } elseif ($clip) {
                            Write-Host "VERIFY-RESULT: PASS-WEAK clipboard=$clip"
                        } else {
                            Write-Host 'VERIFY-RESULT: FAIL clipboard empty'
                        }
                    }
                } else {
                    Write-Host 'VERIFY-RESULT: copy item not found in menu'
                }
            } else {
                Write-Host "  popup hwnd=${ph} name=$($el.Current.Name) (no MenuItems, $($el.Current.ControlType.ProgrammaticName))"
            }
        } catch { Write-Host "  popup hwnd=$ph read error: $($_.Exception.Message)" }
    }
    if (-not $menuFound) { Write-Host 'NO CONTEXT MENU FOUND (right-click)' }
    [System.Windows.Forms.SendKeys]::SendWait('{ESC}')
}
finally {
    if ($process -and -not $process.HasExited) {
        try {
            $ev = [System.Threading.EventWaitHandle]::OpenExisting($shutdownEventName)
            [void]$ev.Set(); $ev.Dispose()
            $process.Refresh()
            if (-not $process.WaitForExit(8000)) { $process.CloseMainWindow() | Out-Null }
        } catch { $process.CloseMainWindow() | Out-Null }
        $process.Refresh()
        if (-not $process.HasExited) { Stop-Process -Id $process.Id -Force -ErrorAction SilentlyContinue }
    }
    if (Test-Path -LiteralPath $dataRoot) { Remove-Item -LiteralPath $dataRoot -Recurse -Force -ErrorAction SilentlyContinue }
}
Write-Host 'PROBE DONE'
