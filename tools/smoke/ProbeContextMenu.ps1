# 只读探针：对正在运行的 StellarHub 主窗口做右键菜单诊断（F-11）
# 点击画廊首卡 → Shift+F10 → 枚举菜单项 Name+IsEnabled → Esc 关闭
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName UIAutomationClient, UIAutomationTypes
Add-Type -AssemblyName System.Windows.Forms
Add-Type @"
using System;
using System.Runtime.InteropServices;
using System.Text;
public class Probe {
    [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr hWnd, out RECT rect);
    [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr hWnd);
    [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint pid);
    [DllImport("user32.dll")] public static extern IntPtr FindWindow(string cls, string title);
    [DllImport("user32.dll", EntryPoint = "EnumWindows")] public static extern bool EnumWindows2(EnumProc cb, IntPtr lParam);
    public delegate bool EnumProc(IntPtr hWnd, IntPtr lParam);
    public struct RECT { public int L, T, R, B; }
}
"@

$proc = Get-Process -Name StellarHub | Where-Object { $_.MainWindowTitle -like '*千星汇图*' } | Select-Object -First 1
if (-not $proc) { Write-Host 'NO RUNNING APP'; exit 1 }
$hwnd = $proc.MainWindowHandle
[Probe]::SetForegroundWindow($hwnd) | Out-Null
Start-Sleep -Milliseconds 600

$rect = New-Object Probe+RECT
[Probe]::GetWindowRect($hwnd, [ref]$rect) | Out-Null
$w = $rect.R - $rect.L; $h = $rect.B - $rect.T
Write-Host "Window rect: L=$($rect.L) T=$($rect.T) W=$w H=$h"

# UIA 定位画廊 ListBox 实际矩形，在内部 30% 处右键
$win = [System.Windows.Automation.AutomationElement]::FromHandle($hwnd)
$listCond = New-Object System.Windows.Automation.PropertyCondition(
    [System.Windows.Automation.AutomationElement]::ControlTypeProperty,
    [System.Windows.Automation.ControlType]::List)
$lists = $win.FindAll([System.Windows.Automation.TreeScope]::Descendants, $listCond)
Write-Host "Found $($lists.Count) List element(s)"
$target = $null
$targetArea = 0.0
foreach ($l in $lists) {
    $r = $l.Current.BoundingRectangle
    if ([double]::IsInfinity($r.X) -or [double]::IsInfinity($r.Width) -or $r.Width -le 0) { continue }
    $area = $r.Width * $r.Height
    Write-Host ("  List '{0}' rect=({1:N0},{2:N0}) {3:N0}x{4:N0}" -f $l.Current.Name, $r.X, $r.Y, $r.Width, $r.Height)
    if ($area -gt $targetArea) { $target = $l; $targetArea = $area }
}
if ($target -eq $null) { Write-Host 'NO GALLERY LIST FOUND'; exit 1 }
$tr = $target.Current.BoundingRectangle
$clickX = [int]($tr.X + $tr.Width * 0.35)
$clickY = [int]($tr.Y + $tr.Height * 0.35)

Add-Type -MemberDefinition @'
[DllImport("user32.dll")] public static extern void mouse_event(uint f, uint dx, uint dy, uint d, UIntPtr e);
'@ -Name U -Namespace W
[Probe]::SetForegroundWindow($hwnd) | Out-Null
Start-Sleep -Milliseconds 500
[System.Windows.Forms.Cursor]::Position = New-Object System.Drawing.Point($clickX, $clickY)
Start-Sleep -Milliseconds 250
# 左键选中一张卡，再右键
[W.U]::mouse_event(2, 0, 0, 0, [UIntPtr]::Zero)
[W.U]::mouse_event(4, 0, 0, 0, [UIntPtr]::Zero)
Start-Sleep -Milliseconds 600
[W.U]::mouse_event(8, 0, 0, 0, [UIntPtr]::Zero)
[W.U]::mouse_event(16, 0, 0, 0, [UIntPtr]::Zero)
Start-Sleep -Milliseconds 1000
Write-Host "Clicked left+right at ($clickX, $clickY)"

$menuFound = $false
# 检测 1：枚举进程所有可见顶层窗口（含弹窗类）
$foundHwnds = New-Object System.Collections.Generic.List[string]
$cbEnum = {
    param($h, $l)
    $p2 = 0
    [Probe]::GetWindowThreadProcessId($h, [ref]$p2) | Out-Null
    if ($p2 -eq $proc.Id -and [W.U2]::IsWindowVisible($h)) {
        $cls = New-Object System.Text.StringBuilder 128
        [W.U2]::GetClassName($h, $cls, 128) | Out-Null
        $ttl = New-Object System.Text.StringBuilder 128
        [W.U2]::GetWindowText2($h, $ttl, 128) | Out-Null
        $script:foundHwnds.Add("$($cls.ToString()) | '$($ttl.ToString())' | hwnd=$h")
    }
    return $true
}
Add-Type -MemberDefinition @'
[DllImport("user32.dll")] public static extern int GetClassName(IntPtr hWnd, System.Text.StringBuilder sb, int n);
[DllImport("user32.dll", EntryPoint="GetWindowTextW", CharSet=CharSet.Unicode)] public static extern int GetWindowText2(IntPtr hWnd, System.Text.StringBuilder sb, int n);
[DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr hWnd);
'@ -Name U2 -Namespace W
$script:foundHwnds = New-Object System.Collections.Generic.List[string]
[Probe]::EnumWindows2($cbEnum, [IntPtr]::Zero) | Out-Null
Write-Host "Process top-level visible windows after right-click:"
foreach ($h in $script:foundHwnds) { Write-Host "  $h"; if ($h -like '*#32768*') { $menuFound = $true } }

# 检测 2：UIA Menu 搜索（原方法）
$root = [System.Windows.Automation.AutomationElement]::RootElement
$menuCond = New-Object System.Windows.Automation.PropertyCondition(
    [System.Windows.Automation.AutomationElement]::ControlTypeProperty,
    [System.Windows.Automation.ControlType]::Menu)
$menus = $root.FindAll([System.Windows.Automation.TreeScope]::Children, $menuCond)
foreach ($m in $menus) {
    try {
        $p = 0; [Probe]::GetWindowThreadProcessId($m.Current.NativeWindowHandle, [ref]$p) | Out-Null
        if ($p -ne $proc.Id) { continue }
        $menuFound = $true
        Write-Host "=== Menu: $($m.Current.Name) ==="
        $items = $m.FindAll([System.Windows.Automation.TreeScope]::Descendants, (New-Object System.Windows.Automation.PropertyCondition(
            [System.Windows.Automation.AutomationElement]::ControlTypeProperty,
            [System.Windows.Automation.ControlType]::MenuItem)))
        foreach ($i in $items) {
            Write-Host ("  [{0}] {1}" -f ($(if ($i.Current.IsEnabled) {'ENABLED '} else {'DISABLED'})), $i.Current.Name)
        }
    } catch { Write-Host "  (menu read error: $($_.Exception.Message))" }
}
if (-not $menuFound) { Write-Host 'NO CONTEXT MENU OPENED' }

# Esc 关闭菜单
[System.Windows.Forms.SendKeys]::SendWait('{ESC}')
Write-Host 'DONE'
