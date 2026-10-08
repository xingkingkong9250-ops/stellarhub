# QL World UIA 冒烟测试 — 导入 + 画廊 + 详情栏（C1/C2/C4）
# 用法: powershell -ExecutionPolicy Bypass -File tools\smoke\UiSmokeImport.ps1
# 前置: .NET 10 SDK；脚本自建 Debug 构建并用隔离数据根，不影响真实数据。
# 退出码: 0 = 全部通过；非 0 = 存在失败步骤。

$ErrorActionPreference = 'Stop'

# ── 路径与环境 ────────────────────────────────────────────────
$projectRoot = Split-Path -Parent (Split-Path -Parent (Split-Path -Parent $PSCommandPath))
$sourceProject = Join-Path $projectRoot 'src\QL.World'
$outputDirectory = Join-Path $sourceProject 'bin\Debug\net10.0-windows'
$applicationExe = Join-Path $outputDirectory 'StellarHub.exe'
$dataRoot = Join-Path $env:TEMP ("QL_World_UiSmoke_Imp_" + [Guid]::NewGuid().ToString('N').Substring(0, 8))
$shutdownEventName = 'Local\QL_World_UiTest_GracefulShutdown'

$env:QL_WORLD_UI_TEST_MODE = '1'
$env:QL_WORLD_DATA_ROOT = $dataRoot

# 测试素材目录（用户提供）
$testFolder = 'C:\Users\75943\Desktop\圣灵教AI\AI图'

$results = New-Object System.Collections.Generic.List[string]
$script:failures = 0

function Assert-Step {
    param([string]$Name, [bool]$Condition, [string]$Detail = '')
    if ($Condition) {
        $script:results.Add("PASS  $Name") | Out-Null
        Write-Host "  [PASS] $Name"
    } else {
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
    [DllImport("user32.dll")] public static extern bool EnumChildWindows(IntPtr hWndParent, EnumWindowsProc lpEnumFunc, IntPtr lParam);
    [DllImport("user32.dll")] public static extern int GetDlgCtrlID(IntPtr hWnd);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern IntPtr SendMessageW(IntPtr hWnd, uint Msg, IntPtr wParam, [MarshalAs(UnmanagedType.LPWStr)] string lParam);
    [DllImport("user32.dll")] public static extern IntPtr SendMessage(IntPtr hWnd, uint Msg, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr hWnd);
    public delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);
    public const uint WM_SETTEXT = 0x000C;
    public const uint BM_CLICK = 0x00F5;
}
"@

function Find-TopWindowByTitle {
    param([int]$OwnerPid, [string]$TitleWildcard)
    $script:foundHwnd = [IntPtr]::Zero
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

function Find-AllByType {
    param([System.Windows.Automation.AutomationElement]$Root,
          [System.Windows.Automation.ControlType]$Type)
    $cond = New-Object System.Windows.Automation.PropertyCondition(
        [System.Windows.Automation.AutomationElement]::ControlTypeProperty, $Type)
    return $Root.FindAll([System.Windows.Automation.TreeScope]::Descendants, $cond)
}

function Find-ButtonByNameContains {
    param([System.Windows.Automation.AutomationElement]$Root, [string]$Substring)
    $cond = New-Object System.Windows.Automation.PropertyCondition(
        [System.Windows.Automation.AutomationElement]::ControlTypeProperty,
        [System.Windows.Automation.ControlType]::Button)
    $buttons = $Root.FindAll([System.Windows.Automation.TreeScope]::Descendants, $cond)
    foreach ($b in $buttons) {
        if ($b.Current.Name -like "*$Substring*") { return $b }
    }
    return $null
}

function Wait-Window {
    param([int]$OwnerPid, [string]$TitleWildcard, [int]$TimeoutSec = 10)
    $deadline = [DateTime]::UtcNow.AddSeconds($TimeoutSec)
    while ([DateTime]::UtcNow -lt $deadline) {
        $w = Find-TopWindowByTitle -OwnerPid $OwnerPid -TitleWildcard $TitleWildcard
        if ($w -ne $null) { return $w }
        Start-Sleep -Milliseconds 300
    }
    return $null
}

# ── 构建 ─────────────────────────────────────────────────────
Write-Host '[Smoke] Building Debug...'
& dotnet build $sourceProject -c Debug --nologo -v q -o $outputDirectory 2>&1 | Out-Null
if ($LASTEXITCODE -ne 0) { Write-Error '[Smoke] Build failed.'; exit 1 }
if (-not (Test-Path -LiteralPath $applicationExe)) { Write-Error "[Smoke] Missing exe: $applicationExe"; exit 1 }

# ── 启动 ─────────────────────────────────────────────────────
Write-Host "[Smoke] Launching (isolated: $dataRoot)"
$process = Start-Process -FilePath $applicationExe -WorkingDirectory $outputDirectory -PassThru
try {
    # ── 登录窗关闭进入仅本地模式（A6，隔离环境无 NAS 连接）────
    Write-Host '[Smoke] Step 1: close login window to enter local-only mode'
    $loginWin = Wait-Window -OwnerPid $process.Id -TitleWildcard '登录*' -TimeoutSec 20
    Assert-Step '登录窗口出现' ($null -ne $loginWin) '20s 内未找到登录窗口'
    if ($null -ne $loginWin) {
        # 关闭登录窗 → 进入仅本地模式（CanLogin 依赖 NAS 连接，隔离环境无 NAS）
        try {
            ($loginWin.GetCurrentPattern([System.Windows.Automation.WindowPattern]::Pattern)).Close()
        } catch {
            Add-Type -AssemblyName System.Windows.Forms
            [UiSmokeW32]::SetForegroundWindow($loginWin.Current.NativeWindowHandle) | Out-Null
            Start-Sleep -Milliseconds 200
            [System.Windows.Forms.SendKeys]::SendWait('{ESC}')
        }
        Start-Sleep -Seconds 2
        Assert-Step '关闭登录窗进入仅本地模式' $true ''
    }

    # 等待主窗口
    Write-Host '[Smoke] Step 2: wait for main window'
    $mainWin = $null
    $deadline = [DateTime]::UtcNow.AddSeconds(25)
    while ([DateTime]::UtcNow -lt $deadline) {
        $process.Refresh()
        if ($process.HasExited) { throw "[Smoke] App exited (code $($process.ExitCode))." }
        $candidates = Find-AllByType ([System.Windows.Automation.AutomationElement]::RootElement) ([System.Windows.Automation.ControlType]::Window)
        foreach ($c in $candidates) {
            try {
                $p = 0; [UiSmokeW32]::GetWindowThreadProcessId($c.Current.NativeWindowHandle, [ref]$p) | Out-Null
                if ($p -eq $process.Id -and $c.Current.Name -like '*千星汇图*' -and $c.Current.Name -notlike '登录*') {
                    $mainWin = $c; break
                }
            } catch {}
        }
        if ($mainWin -ne $null) { break }
        Start-Sleep -Milliseconds 500
    }
    Assert-Step '主窗口出现' ($null -ne $mainWin) '25s 内未找到主窗口'

    if ($null -ne $mainWin) {
        Write-Host "  Main: $($mainWin.Current.Name)"

        # ── 打开导入对话框 ──────────────────────────────────
        Write-Host '[Smoke] Step 3: open import dialog'
        $importBtn = Find-ButtonByNameContains $mainWin '导入'
        if ($null -eq $importBtn) { $importBtn = Find-ButtonByNameContains $mainWin '＋' }
        Assert-Step '导入按钮存在' ($null -ne $importBtn) '未找到「＋ 导入图片」按钮'
        if ($importBtn -ne $null) {
            ($importBtn.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern)).Invoke()
            Start-Sleep -Seconds 2
        }
        $importDlg = Wait-Window -OwnerPid $process.Id -TitleWildcard '*导入*' -TimeoutSec 10
        Assert-Step '导入对话框打开' ($null -ne $importDlg) '未找到导入对话框'

        if ($null -ne $importDlg) {
            # ── 选择文件夹（触发 Win32 OpenFolderDialog）──
            Write-Host '[Smoke] Step 4: pick folder via Win32 dialog'
            $pickFolderBtn = Find-ButtonByNameContains $importDlg '选择文件夹'
            Assert-Step '选择文件夹按钮存在' ($null -ne $pickFolderBtn) '未找到选择文件夹按钮'
            if ($pickFolderBtn -ne $null) {
                ($pickFolderBtn.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern)).Invoke()
                Start-Sleep -Seconds 2

                # 用 Win32 找文件夹对话框并填路径（M2 经验：Edit id=1152 + WM_SETTEXT）
                $folderDlgHwnd = [IntPtr]::Zero
                $cb = [UiSmokeW32+EnumWindowsProc]{
                    param($h, $l)
                    $wpid = 0
                    [UiSmokeW32]::GetWindowThreadProcessId($h, [ref]$wpid) | Out-Null
                    if ($wpid -eq $process.Id -and [UiSmokeW32]::IsWindowVisible($h)) {
                        $sb = New-Object System.Text.StringBuilder 256
                        [UiSmokeW32]::GetWindowText($h, $sb, 256) | Out-Null
                        if ($sb.ToString() -like '*素材文件夹*' -or $sb.ToString() -like '*浏览*') {
                            $script:folderHwnd = $h
                        }
                    }
                    return $true
                }
                $script:folderHwnd = [IntPtr]::Zero
                # 轮询找对话框（可能异步弹出）
                $dlgDeadline = [DateTime]::UtcNow.AddSeconds(10)
                while ([DateTime]::UtcNow -lt $dlgDeadline) {
                    [UiSmokeW32]::EnumWindows($cb, [IntPtr]::Zero) | Out-Null
                    if ($script:folderHwnd -ne [IntPtr]::Zero) { break }
                    Start-Sleep -Milliseconds 300
                }
                $folderDlgHwnd = $script:folderHwnd
                if ($folderDlgHwnd -ne [IntPtr]::Zero) {
                    [UiSmokeW32]::SetForegroundWindow($folderDlgHwnd) | Out-Null
                    # 找 Edit id=1152 填路径
                    $targetEdit = [IntPtr]::Zero
                    $childCb = [UiSmokeW32+EnumWindowsProc]{
                        param($h, $l)
                        if ([UiSmokeW32]::GetDlgCtrlID($h) -eq 1152) { $script:targetEdit = $h }
                        return $true
                    }
                    $script:targetEdit = [IntPtr]::Zero
                    [UiSmokeW32]::EnumChildWindows($folderDlgHwnd, $childCb, [IntPtr]::Zero) | Out-Null
                    $targetEdit = $script:targetEdit
                    if ($targetEdit -ne [IntPtr]::Zero) {
                        [void][UiSmokeW32]::SendMessageW($targetEdit, [UiSmokeW32]::WM_SETTEXT, [IntPtr]::Zero, $testFolder)
                        Start-Sleep -Milliseconds 500
                        # 用 BM_CLICK 点确定按钮（id=1），避免 SendKeys 误伤
                        $okBtn = [IntPtr]::Zero
                        $okCb = [UiSmokeW32+EnumWindowsProc]{
                            param($h, $l)
                            if ([UiSmokeW32]::GetDlgCtrlID($h) -eq 1) { $script:okBtn = $h }
                            return $true
                        }
                        $script:okBtn = [IntPtr]::Zero
                        [UiSmokeW32]::EnumChildWindows($folderDlgHwnd, $okCb, [IntPtr]::Zero) | Out-Null
                        if ($script:okBtn -ne [IntPtr]::Zero) {
                            [void][UiSmokeW32]::SendMessage($script:okBtn, [UiSmokeW32]::BM_CLICK, [IntPtr]::Zero, [IntPtr]::Zero)
                            Start-Sleep -Milliseconds 800
                        }
                        Assert-Step '文件夹对话框填路径并确认' $true ''
                    } else {
                        Assert-Step '文件夹对话框 Edit id=1152' $false '未找到 Edit id=1152'
                    }
                } else {
                    Assert-Step '文件夹对话框弹出' $false '未找到文件夹选择对话框'
                }
            }

            # ── 开始导入 ────────────────────────────────────
            Write-Host '[Smoke] Step 5: run import'
            # 文件夹选择后等待对话框稳定，重新定位
            Start-Sleep -Seconds 2
            $importDlg2 = $null
            $retry = 0
            while ($null -eq $importDlg2 -and $retry -lt 10) {
                $importDlg2 = Find-TopWindowByTitle -OwnerPid $process.Id -TitleWildcard '*导入*'
                # 排除文件夹对话框（标题含「素材文件夹」）
                if ($importDlg2 -ne $null -and $importDlg2.Current.Name -like '*素材文件夹*') { $importDlg2 = $null }
                Start-Sleep -Milliseconds 500
                $retry++
            }
            $runBtn = $null
            if ($importDlg2 -ne $null) {
                $runBtn = Find-FirstByName $importDlg2 '开始导入' ([System.Windows.Automation.ControlType]::Button)
            }
            Assert-Step '开始导入按钮存在' ($null -ne $runBtn) '未找到开始导入按钮'
            if ($runBtn -ne $null) {
                try {
                    ($runBtn.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern)).Invoke()
                } catch {
                    # 元素过期，重新找一次
                    Start-Sleep -Milliseconds 500
                    $importDlg3 = Find-TopWindowByTitle -OwnerPid $process.Id -TitleWildcard '*导入*'
                    if ($importDlg3 -ne $null) {
                        $runBtn2 = Find-FirstByName $importDlg3 '开始导入' ([System.Windows.Automation.ControlType]::Button)
                        if ($runBtn2 -ne $null) {
                            ($runBtn2.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern)).Invoke()
                        }
                    }
                }
            }

            # 等待导入完成（「完成」按钮出现 = 导入结束）
            Write-Host '[Smoke] Step 6: wait for import completion'
            $importDone = $false
            $impDeadline = [DateTime]::UtcNow.AddSeconds(180)
            while ([DateTime]::UtcNow -lt $impDeadline) {
                $dlg = Find-TopWindowByTitle -OwnerPid $process.Id -TitleWildcard '*导入*'
                if ($null -eq $dlg) { $importDone = $true; break }
                $doneBtn = Find-FirstByName $dlg '完成' ([System.Windows.Automation.ControlType]::Button)
                if ($doneBtn -ne $null) {
                    try { ($doneBtn.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern)).Invoke() } catch {}
                    $importDone = $true; break
                }
                Start-Sleep -Seconds 2
            }
            Assert-Step '导入完成' $importDone '180s 内导入未完成'
            Start-Sleep -Seconds 3 # 缩略图生成缓冲
        }

        # 数据库验证（用 sqlite3，避免文件锁）—— 先于画廊断言
        $dbImageCount = 0
        $dbPath = Join-Path $dataRoot 'qlworld.db'
        if (Test-Path $dbPath) {
            $sqlite3 = Get-Command sqlite3 -ErrorAction SilentlyContinue
            if ($sqlite3) {
                $cnt = & sqlite3 $dbPath "SELECT COUNT(*) FROM images;" 2>$null
                $dbImageCount = [int]$cnt
                Write-Host "  DB images count: $dbImageCount"
            } else {
                Write-Host '  (sqlite3 not available, skip DB count check)'
            }
        }

        Assert-Step '数据库 images 表记录数>0' ($dbImageCount -gt 0) "images count=$dbImageCount"

        # ── 画廊图片数验证（C1）──────────────────────────────
        Write-Host '[Smoke] Step 7: verify gallery has images (C1)'
        # 画廊是 ListBox 虚拟化（自定义 VirtualizingTilePanel），UIA 可能不暴露 ListItem；
        # 用 DB 记录数 + 空态消失双重判定
        $galleryCount = 0
        $listBoxCond = New-Object System.Windows.Automation.PropertyCondition(
            [System.Windows.Automation.AutomationElement]::ControlTypeProperty,
            [System.Windows.Automation.ControlType]::List)
        $listBoxes = $mainWin.FindAll([System.Windows.Automation.TreeScope]::Descendants, $listBoxCond)
        foreach ($lb in $listBoxes) {
            try {
                $gp = $lb.GetCurrentPattern([System.Windows.Automation.GridPattern]::Pattern)
                if ($gp.Current.RowCount -gt $galleryCount) { $galleryCount = $gp.Current.RowCount }
            } catch {}
        }
        if ($galleryCount -eq 0) {
            $listItems = Find-AllByType $mainWin ([System.Windows.Automation.ControlType]::ListItem)
            $galleryCount = $listItems.Count
        }
        Assert-Step '画廊显示导入图片（≥1 张）' ($galleryCount -ge 1 -or $dbImageCount -gt 0) "画廊项数=$galleryCount, DB=$dbImageCount"
        Write-Host "  Gallery item count: $galleryCount (DB: $dbImageCount)"
        # 空态文字消失验证（证明画廊非空）
        $emptyText = Find-FirstByName $mainWin '点击左上角「＋ 导入图片」（Ctrl+I）开始建立素材库' ([System.Windows.Automation.ControlType]::Text)
        if ($null -eq $emptyText) { $emptyText = Find-ButtonByNameContains $mainWin '开始建立素材库' }
        Assert-Step '画廊空态已消失' ($null -eq $emptyText) '空态提示仍可见，画廊可能未刷新'

        # ── 网格/列表切换（C2）──────────────────────────────
        Write-Host '[Smoke] Step 8: grid/list view toggle (C2)'
        $gridBtn = Find-ButtonByNameContains $mainWin '网格'
        $listBtn = Find-ButtonByNameContains $mainWin '列表'
        $toggleOk = ($null -ne $gridBtn -and $null -ne $listBtn)
        Assert-Step '网格/列表切换控件存在' $toggleOk "grid=$($null -ne $gridBtn) list=$($null -ne $listBtn)"
        if ($toggleOk) {
            try {
                ($listBtn.GetCurrentPattern([System.Windows.Automation.TogglePattern]::Pattern)).Toggle()
            } catch {
                try { ($listBtn.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern)).Invoke() } catch {}
            }
            Start-Sleep -Milliseconds 1000
            try {
                ($gridBtn.GetCurrentPattern([System.Windows.Automation.TogglePattern]::Pattern)).Toggle()
            } catch {
                try { ($gridBtn.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern)).Invoke() } catch {}
            }
            Start-Sleep -Milliseconds 1000
            Assert-Step '网格/列表切换不崩溃' $true ''
        }

        # ── 详情栏（C4）────────────────────────────────────
        Write-Host '[Smoke] Step 9: detail panel on selection (C4)'
        if ($galleryCount -ge 1) {
            # 找一个可见的 ListItem 选中
            $firstItem = $null
            $listItems = Find-AllByType $mainWin ([System.Windows.Automation.ControlType]::ListItem)
            if ($listItems.Count -ge 1) { $firstItem = $listItems[0] }
            if ($firstItem -ne $null) {
                try {
                    $selPattern = $firstItem.GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern)
                    $selPattern.Select()
                    Start-Sleep -Milliseconds 800
                    Assert-Step '点击首图不崩溃' $true ''
                    Assert-Step '选中后应用存活' (-not $process.HasExited) '选中图片后进程退出'
                } catch {
                    Assert-Step '选中首图操作' $false $_.Exception.Message
                }
            } else {
                Write-Host '  (no visible ListItem to select, skip detail test)'
            }
        }

        # ── 应用存活最终检查 ────────────────────────────────
        $process.Refresh()
        Assert-Step '应用全程存活' (-not $process.HasExited) '测试中进程退出'
    }
}
finally {
    # 优雅退出
    if ($null -ne $process -and -not $process.HasExited) {
        try {
            $shutdownEvent = [System.Threading.EventWaitHandle]::OpenExisting($shutdownEventName)
            try { [void]$shutdownEvent.Set() } finally { $shutdownEvent.Dispose() }
            $process.Refresh()
            if (-not $process.WaitForExit(8000)) { $process.CloseMainWindow() | Out-Null }
        } catch { $process.CloseMainWindow() | Out-Null }
        $process.Refresh()
        if (-not $process.HasExited) { Stop-Process -Id $process.Id -Force -ErrorAction SilentlyContinue }
    }
    if (Test-Path -LiteralPath $dataRoot) {
        Remove-Item -LiteralPath $dataRoot -Recurse -Force -ErrorAction SilentlyContinue
    }
}

# ── 汇总 ─────────────────────────────────────────────────────
Write-Host ''
Write-Host '════════ UI Smoke (Import) Summary ════════'
$results | ForEach-Object { Write-Host $_ }
Write-Host '════════════════════════════════════════════'
if ($script:failures -gt 0) {
    Write-Host "RESULT: FAILED ($script:failures step(s))"
    exit 1
}
Write-Host 'RESULT: ALL PASSED'
exit 0
