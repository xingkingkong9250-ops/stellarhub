# F-08 UIA 滚动探针：隔离实例导入后快速上下滚动，截图保存供人工/视觉核对空白块
$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path -Parent (Split-Path -Parent (Split-Path -Parent $PSCommandPath))
$sourceProject = Join-Path $projectRoot 'src\QL.World'
$outputDirectory = Join-Path $sourceProject 'bin\Debug\net10.0-windows'
$applicationExe = Join-Path $outputDirectory 'StellarHub.exe'
$dataRoot = Join-Path $env:TEMP ("QL_Thumb_" + [Guid]::NewGuid().ToString('N').Substring(0, 8))
$shutdownEventName = 'Local\QL_World_UiTest_GracefulShutdown'
$env:QL_WORLD_UI_TEST_MODE = '1'
$env:QL_WORLD_DATA_ROOT = $dataRoot
$testFolder = 'C:\Users\75943\Desktop\圣灵教AI\AI图'
$shotDir = Join-Path $projectRoot 'tools\smoke\shots'
New-Item -ItemType Directory -Force -Path $shotDir | Out-Null

Add-Type -AssemblyName UIAutomationClient, UIAutomationTypes
Add-Type -AssemblyName System.Windows.Forms
Add-Type -AssemblyName System.Drawing
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
    [DllImport("user32.dll")] public static extern bool MoveWindow(IntPtr h, int x, int y, int w, int ht, bool r);
    [DllImport("user32.dll")] public static extern bool SetCursorPos(int x, int y);
    [DllImport("user32.dll")] public static extern void mouse_event(uint f, int dx, int dy, uint d, UIntPtr e);
    [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out RECT r);
    [DllImport("user32.dll")] public static extern bool PrintWindow(IntPtr h, IntPtr dc, uint flags);
    [DllImport("gdi32.dll")] public static extern IntPtr GetDC(IntPtr h);
    [DllImport("gdi32.dll")] public static extern int ReleaseDC(IntPtr h, IntPtr dc);
    public delegate bool EnumWindowsProc(IntPtr h, IntPtr l);
    public struct RECT { public int L, T, R, B; }
    public const uint WM_SETTEXT = 0x000C;
    public const uint BM_CLICK = 0x00F5;
}
"@

function Find-Window($ownerPid, $wildcard) {
    $deadline = [DateTime]::UtcNow.AddSeconds(15)
    while ([DateTime]::UtcNow -lt $deadline) {
        $script:found = [IntPtr]::Zero
        $cb = [PW+EnumWindowsProc]{
            param($h,$l)
            $p=0; [PW]::GetWindowThreadProcessId($h,[ref]$p)|Out-Null
            if ($p -eq $ownerPid -and [PW]::IsWindowVisible($h)) {
                $sb = New-Object System.Text.StringBuilder 256
                [PW]::GetTitle($h,$sb,256)|Out-Null
                if ($sb.ToString() -like $wildcard) { $script:found = $h }
            }
            return $true
        }
        [PW]::EnumWindows($cb,[IntPtr]::Zero)|Out-Null
        if ($script:found -ne [IntPtr]::Zero) { return [System.Windows.Automation.AutomationElement]::FromHandle($script:found) }
        Start-Sleep -Milliseconds 300
    }
    return $null
}
function Find-Btn($root,$sub) {
    $cond = New-Object System.Windows.Automation.PropertyCondition(
        [System.Windows.Automation.AutomationElement]::ControlTypeProperty,
        [System.Windows.Automation.ControlType]::Button)
    foreach ($b in $root.FindAll([System.Windows.Automation.TreeScope]::Descendants,$cond)) {
        if ($b.Current.Name -like "*$sub*") { return $b }
    }
    return $null
}
function Capture($name) {
    $rect = New-Object PW+RECT
    if (-not [PW]::GetWindowRect($script:mainHwnd,[ref]$rect) -or
        ($rect.R-$rect.L) -le 0 -or ($rect.B-$rect.T) -le 0) {
        $refreshed = Find-Window $process.Id '*千星汇图*'
        if ($refreshed) { $script:mainHwnd = [IntPtr]$refreshed.Current.NativeWindowHandle }
        [PW]::GetWindowRect($script:mainHwnd,[ref]$rect)|Out-Null
    }
    $w = $rect.R-$rect.L; $h = $rect.B-$rect.T
    $bmp = New-Object System.Drawing.Bitmap $w,$h
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $hdc = $g.GetHdc()
    try {
        # PrintWindow(PW_RENDERFULLCONTENT) works even when the desktop is
        # locked/unavailable; CopyFromScreen throws "handle is invalid" there.
        $printed = [PW]::PrintWindow($script:mainHwnd, $hdc, 2)
    } finally {
        $g.ReleaseHdc($hdc)
    }
    if (-not $printed) {
        $retry = 0
        while ($true) {
            try {
                $g.CopyFromScreen($rect.L,$rect.T,0,0,(New-Object System.Drawing.Size $w,$h))
                break
            } catch {
                if (++$retry -ge 3) { throw }
                Start-Sleep -Milliseconds 500
            }
        }
    }
    $path = Join-Path $shotDir $name
    $bmp.Save($path,[System.Drawing.Imaging.ImageFormat]::Png)
    $g.Dispose(); $bmp.Dispose()
    Write-Host "[Shot] $path"
}

Write-Host '[Thumb] Launching...'
$process = Start-Process -FilePath $applicationExe -WorkingDirectory $outputDirectory -PassThru
try {
    $login = Find-Window $process.Id '登录*'
    if ($login) { ($login.GetCurrentPattern([System.Windows.Automation.WindowPattern]::Pattern)).Close(); Start-Sleep -Seconds 2 }
    $main = Find-Window $process.Id '*千星汇图*'
    if (-not $main) { throw 'main not found' }
    $script:mainHwnd = [IntPtr]$main.Current.NativeWindowHandle
    [PW]::MoveWindow($script:mainHwnd, 20, 20, 1600, 950, $true) | Out-Null
    Start-Sleep -Seconds 1

    # 导入
    $btn = Find-Btn $main '导入'; if (-not $btn) { $btn = Find-Btn $main '＋' }
    ($btn.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern)).Invoke()
    Start-Sleep -Seconds 2
    $dlg = Find-Window $process.Id '*导入*'
    $pick = Find-Btn $dlg '选择文件夹'
    ($pick.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern)).Invoke()
    Start-Sleep -Seconds 2
    $script:fhwnd=[IntPtr]::Zero
    $fcb=[PW+EnumWindowsProc]{ param($h,$l)
        $p=0;[PW]::GetWindowThreadProcessId($h,[ref]$p)|Out-Null
        if ($p -eq $process.Id -and [PW]::IsWindowVisible($h)){
            $sb=New-Object System.Text.StringBuilder 256;[PW]::GetTitle($h,$sb,256)|Out-Null
            if ($sb.ToString() -like '*素材文件夹*' -or $sb.ToString() -like '*浏览*'){ $script:fhwnd=$h }}
        return $true }
    $dd=[DateTime]::UtcNow.AddSeconds(10)
    while([DateTime]::UtcNow -lt $dd -and $script:fhwnd -eq [IntPtr]::Zero){ [PW]::EnumWindows($fcb,[IntPtr]::Zero)|Out-Null; Start-Sleep -Milliseconds 300 }
    $script:eh=[IntPtr]::Zero;$script:oh=[IntPtr]::Zero
    $ccb=[PW+EnumWindowsProc]{ param($h,$l)
        $id=[PW]::GetDlgCtrlID($h)
        if ($id -eq 1152){$script:eh=$h}; if($id -eq 1){$script:oh=$h}; return $true }
    [PW]::EnumChildWindows($script:fhwnd,$ccb,[IntPtr]::Zero)|Out-Null
    [PW]::SetText($script:eh,[PW]::WM_SETTEXT,[IntPtr]::Zero,$testFolder)|Out-Null
    Start-Sleep -Milliseconds 400
    [PW]::SendMsg($script:oh,[PW]::BM_CLICK,[IntPtr]::Zero,[IntPtr]::Zero)|Out-Null
    Start-Sleep -Seconds 2
    $run=$null; $r=0
    while(-not $run -and $r -lt 10){ $d2=Find-Window $process.Id '*导入*'
        if($d2){ $c=New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::NameProperty,'开始导入')
            $run=$d2.FindFirst([System.Windows.Automation.TreeScope]::Descendants,$c) }
        Start-Sleep -Milliseconds 500;$r++ }
    ($run.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern)).Invoke()
    Write-Host '[Thumb] Importing...'
    $id=[DateTime]::UtcNow.AddSeconds(180)
    while([DateTime]::UtcNow -lt $id){
        $d=Find-Window $process.Id '*导入*'
        if(-not $d){break}
        $c=New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::NameProperty,'完成')
        $done=$d.FindFirst([System.Windows.Automation.TreeScope]::Descendants,$c)
        if($done){ ($done.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern)).Invoke(); break }
        Start-Sleep -Seconds 2
    }
    Write-Host '[Thumb] Import done. Waiting for initial thumbnails...'
    Start-Sleep -Seconds 8
    Capture 'thumb_01_initial.png'

    # 定位画廊矩形
    $main = Find-Window $process.Id '*千星汇图*'
    $lcond = New-Object System.Windows.Automation.PropertyCondition(
        [System.Windows.Automation.AutomationElement]::ControlTypeProperty,
        [System.Windows.Automation.ControlType]::List)
    $gallery=$null;$area=0.0
    foreach($l in $main.FindAll([System.Windows.Automation.TreeScope]::Descendants,$lcond)){
        $rr=$l.Current.BoundingRectangle
        if([double]::IsInfinity($rr.Width) -or $rr.Width -le 0){continue}
        $a=$rr.Width*$rr.Height
        if($a -gt $area){$gallery=$l;$area=$a}
    }
    $gr=$gallery.Current.BoundingRectangle
    $cx=[int]($gr.X+$gr.Width/2); $cy=[int]($gr.Y+$gr.Height/2)
    Write-Host "[Thumb] Gallery center=($cx,$cy); rapid scrolling..."
    [PW]::SetCursorPos($cx,$cy)|Out-Null
    Start-Sleep -Milliseconds 300
    # 快速滚到底（每格 120px，共 40 格）
    for($i=0;$i -lt 40;$i++){ [PW]::mouse_event(0x0800,0,0,4294967176,[UIntPtr]::Zero); Start-Sleep -Milliseconds 60 }
    Start-Sleep -Seconds 2
    Capture 'thumb_02_bottom.png'
    # 快速滚回顶
    for($i=0;$i -lt 40;$i++){ [PW]::mouse_event(0x0800,0,0,120,[UIntPtr]::Zero); Start-Sleep -Milliseconds 60 }
    Start-Sleep -Seconds 2
    Capture 'thumb_03_top.png'
    # 再来回两轮，停在中间
    for($i=0;$i -lt 20;$i++){ [PW]::mouse_event(0x0800,0,0,4294967176,[UIntPtr]::Zero); Start-Sleep -Milliseconds 50 }
    Start-Sleep -Seconds 3
    Capture 'thumb_04_middle.png'
    Write-Host '[Thumb] DONE'
}
finally {
    if ($process -and -not $process.HasExited) {
        try { $ev=[System.Threading.EventWaitHandle]::OpenExisting($shutdownEventName); [void]$ev.Set();$ev.Dispose()
            $process.Refresh(); if(-not $process.WaitForExit(8000)){ $process.CloseMainWindow()|Out-Null } } catch { $process.CloseMainWindow()|Out-Null }
        $process.Refresh(); if(-not $process.HasExited){ Stop-Process -Id $process.Id -Force -ErrorAction SilentlyContinue }
    }
    if (Test-Path -LiteralPath $dataRoot) { Remove-Item -LiteralPath $dataRoot -Recurse -Force -ErrorAction SilentlyContinue }
}

