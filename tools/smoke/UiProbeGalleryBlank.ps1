# Probe: launch with real data root, inspect gallery ListBox render state + view toggles
$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path -Parent (Split-Path -Parent (Split-Path -Parent $PSCommandPath))
$applicationExe = Join-Path $projectRoot 'src\QL.World\bin\Debug\net10.0-windows\StellarHub.exe'
$shutdownEventName = 'Local\QL_World_UiTest_GracefulShutdown'
$env:QL_WORLD_UI_TEST_MODE = '1'
# NOTE: QL_WORLD_DATA_ROOT intentionally unset -> real user data
$shotDir = Join-Path $projectRoot 'tools\smoke\shots'
New-Item -ItemType Directory -Force -Path $shotDir | Out-Null

# Window title fragments as unicode escapes (PS5.1 reads this file as ANSI)
# Login window title: "登录 — 千星汇图"
# Main window title: "千星汇图 — 3D 美术图片素材管理系统"
# Use "3D" (ASCII, unique to main window subtitle) to avoid matching login window.
$loginFrag = [string]([char]0x767B) + [char]0x5F55                       # deng lu
$loginWild = '*' + $loginFrag + '*'
$mainWild  = '*3D*'
$gridGlyph = [char]0x25A6   # white square with vertical bisecting line
$listGlyph = [char]0x2261   # identical-to (three lines)

Add-Type -AssemblyName UIAutomationClient, UIAutomationTypes
Add-Type -AssemblyName System.Drawing
Add-Type @"
using System;
using System.Runtime.InteropServices;
using System.Text;
public class PWX {
    [DllImport("user32.dll")] public static extern bool EnumWindows(EnumWindowsProc cb, IntPtr l);
    [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr h, out uint p);
    [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr h);
    [DllImport("user32.dll", EntryPoint="GetWindowTextW", CharSet=CharSet.Unicode)] public static extern int GetTitle(IntPtr h, StringBuilder s, int n);
    [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out RECT r);
    [DllImport("user32.dll")] public static extern bool PrintWindow(IntPtr h, IntPtr dc, uint flags);
    public delegate bool EnumWindowsProc(IntPtr h, IntPtr l);
    public struct RECT { public int L, T, R, B; }
}
"@

function Log($msg) { [Console]::WriteLine("[Probe] $msg") }
function fmt($v){ if([double]::IsInfinity($v) -or [double]::IsNaN($v)){ 'inf' } else { [string][int]$v } }

function Find-Window($ownerPid, $wildcard, $timeoutSec = 15) {
    $deadline = [DateTime]::UtcNow.AddSeconds($timeoutSec)
    while ([DateTime]::UtcNow -lt $deadline) {
        $script:found = [IntPtr]::Zero
        $cb = [PWX+EnumWindowsProc]{
            param($h,$l)
            $p=0; [PWX]::GetWindowThreadProcessId($h,[ref]$p)|Out-Null
            if ($p -eq $ownerPid -and [PWX]::IsWindowVisible($h)) {
                $sb = New-Object System.Text.StringBuilder 256
                [PWX]::GetTitle($h,$sb,256)|Out-Null
                if ($sb.ToString() -like $wildcard) { $script:found = $h }
            }
            return $true
        }
        [PWX]::EnumWindows($cb,[IntPtr]::Zero)|Out-Null
        if ($script:found -ne [IntPtr]::Zero) { return [System.Windows.Automation.AutomationElement]::FromHandle($script:found) }
        Start-Sleep -Milliseconds 300
    }
    return $null
}

function Capture($name, $hwnd) {
    $rect = New-Object PWX+RECT
    [PWX]::GetWindowRect($hwnd,[ref]$rect)|Out-Null
    $w = $rect.R-$rect.L; $h = $rect.B-$rect.T
    $bmp = New-Object System.Drawing.Bitmap $w,$h
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $hdc = $g.GetHdc()
    try { [void][PWX]::PrintWindow($hwnd, $hdc, 2) } finally { $g.ReleaseHdc($hdc) }
    $path = Join-Path $shotDir $name
    $bmp.Save($path,[System.Drawing.Imaging.ImageFormat]::Png)
    $g.Dispose(); $bmp.Dispose()
    Log "Shot -> $name"
}

function Get-ToggleByGlyph($root, [string]$glyph) {
    $els = $root.FindAll([System.Windows.Automation.TreeScope]::Descendants,
        [System.Windows.Automation.Condition]::TrueCondition)
    foreach($e in $els){
        try {
            $tp=$e.GetCurrentPattern([System.Windows.Automation.TogglePattern]::Pattern)
            if($tp -and $e.Current.Name -and $e.Current.Name.Contains($glyph)){ return $e }
        } catch {}
    }
    return $null
}

function Read-ViewStates($root, $tag) {
    foreach($g in @($gridGlyph,$listGlyph)){
        $e = Get-ToggleByGlyph $root ([string]$g)
        if($e){
            $tp=$e.GetCurrentPattern([System.Windows.Automation.TogglePattern]::Pattern)
            Log ("STATE {0} '{1}'={2}" -f $tag, $e.Current.Name, $tp.Current.ToggleState)
        } else {
            Log ("STATE {0} glyph 0x{1:X4} NOT FOUND" -f $tag, [int]$g)
        }
    }
}

function Click-ViewToggle($root, [string]$glyph, $expectLabel) {
    $e = Get-ToggleByGlyph $root $glyph
    if(-not $e){ Log "CLICK $expectLabel : NOT FOUND"; return $false }
    $tp=$e.GetCurrentPattern([System.Windows.Automation.TogglePattern]::Pattern)
    $before=$tp.Current.ToggleState.ToString()
    $tp.Toggle()
    Start-Sleep -Milliseconds 400
    $e2 = Get-ToggleByGlyph $root $glyph
    $after='?'
    if($e2){ $after=($e2.GetCurrentPattern([System.Windows.Automation.TogglePattern]::Pattern)).Current.ToggleState.ToString() }
    Log ("CLICK {0} '{1}': {2} -> {3}" -f $expectLabel, $e.Current.Name, $before, $after)
    return $true
}

function Dump-Gallery($root, $tag) {
    $lcond = New-Object System.Windows.Automation.PropertyCondition(
        [System.Windows.Automation.AutomationElement]::ControlTypeProperty,
        [System.Windows.Automation.ControlType]::List)
    $gallery=$null;$area=0.0
    foreach($l in $root.FindAll([System.Windows.Automation.TreeScope]::Descendants,$lcond)){
        $rr=$l.Current.BoundingRectangle
        if([double]::IsInfinity($rr.Width) -or $rr.Width -le 0){continue}
        $a=$rr.Width*$rr.Height
        if($a -gt $area){$gallery=$l;$area=$a}
    }
    if(-not $gallery){ Log "$tag : gallery list not found"; return }
    $items=$gallery.FindAll([System.Windows.Automation.TreeScope]::Children,
        (New-Object System.Windows.Automation.PropertyCondition(
            [System.Windows.Automation.AutomationElement]::ControlTypeProperty,
            [System.Windows.Automation.ControlType]::ListItem)))
    $gr=$gallery.Current.BoundingRectangle
    Log ("{0}: realizedItems={1} rect=({2},{3} {4}x{5})" -f $tag,$items.Count,(fmt $gr.X),(fmt $gr.Y),(fmt $gr.Width),(fmt $gr.Height))
}

Log 'Launching (real data root)...'
$process = Start-Process -FilePath $applicationExe -PassThru
try {
    $login = Find-Window $process.Id $loginWild 40
    if ($login) {
        Log 'Login window found, closing...'
        ($login.GetCurrentPattern([System.Windows.Automation.WindowPattern]::Pattern)).Close()
        Start-Sleep -Seconds 2
    }
    $main = Find-Window $process.Id $mainWild
    if (-not $main) { throw 'main window not found' }
    $script:mainHwnd = [IntPtr]$main.Current.NativeWindowHandle
    Log 'Main window found. Waiting 6s for gallery load...'
    Start-Sleep -Seconds 6

    # --- phase 1: startup state (expect persisted mode) ---
    Capture 'probe_01_startup.png' $script:mainHwnd
    $main = Find-Window $process.Id $mainWild
    Read-ViewStates $main 'startup'
    Dump-Gallery $main 'STARTUP'

    # --- phase 2: switch to LIST ---
    Click-ViewToggle $main ([string]$listGlyph) 'to-list' | Out-Null
    Start-Sleep -Seconds 2
    $main = Find-Window $process.Id $mainWild
    Capture 'probe_02_list.png' $script:mainHwnd
    Read-ViewStates $main 'list'
    Dump-Gallery $main 'LIST MODE'

    # --- phase 3: switch to GRID (the direction that blanked before) ---
    Click-ViewToggle $main ([string]$gridGlyph) 'to-grid' | Out-Null
    Start-Sleep -Seconds 3
    $main = Find-Window $process.Id $mainWild
    Capture 'probe_03_grid.png' $script:mainHwnd
    Read-ViewStates $main 'grid'
    Dump-Gallery $main 'GRID MODE'

    # NOTE: intentionally end in GRID mode so the next run starts in grid
    Log 'DONE'
}
finally {
    if ($process -and -not $process.HasExited) {
        try { $ev=[System.Threading.EventWaitHandle]::OpenExisting($shutdownEventName); [void]$ev.Set();$ev.Dispose()
            $process.Refresh(); if(-not $process.WaitForExit(8000)){ $process.CloseMainWindow()|Out-Null } } catch { $process.CloseMainWindow()|Out-Null }
        $process.Refresh(); if(-not $process.HasExited){ Stop-Process -Id $process.Id -Force -ErrorAction SilentlyContinue }
    }
}
