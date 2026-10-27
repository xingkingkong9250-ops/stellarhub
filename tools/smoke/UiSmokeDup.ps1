# QL World DUP-D UIA 冒烟测试 — W1/A1-11「查重」入口 + W8 重复审核窗口骨架
# 用法: powershell -ExecutionPolicy Bypass -File tools\smoke\UiSmokeDup.ps1
# 前置: .NET 10 SDK；脚本自建 Debug 构建并用隔离数据根，不影响真实数据。
# 退出码: 0 = 全部通过；非 0 = 存在失败步骤。
# 覆盖：顶栏 A1-11 按钮存在、点击打开 W8、W8 工具栏（扫描/取消/类型/状态/刷新）、
#       左侧组列表、组动作（保留全部/删除选中）存在性、关闭窗口。
# 未覆盖：扫描进度与真实重复数据的处理结果（需构造库内重复图，留待人工验收）。

$ErrorActionPreference = 'Stop'

$projectRoot = Split-Path -Parent (Split-Path -Parent (Split-Path -Parent $PSCommandPath))
$sourceProject = Join-Path $projectRoot 'src\QL.World'
$outputDirectory = Join-Path $sourceProject 'bin\Debug\net10.0-windows'
$applicationExe = Join-Path $outputDirectory 'StellarHub.exe'
$dataRoot = Join-Path $env:TEMP ("QL_World_UiSmokeDup_" + [Guid]::NewGuid().ToString('N').Substring(0, 8))
$configPath = Join-Path $dataRoot 'config.json'
$libraryRoot = Join-Path $dataRoot 'library'
$shutdownEventName = 'Local\QL_World_UiTest_GracefulShutdown'

$env:QL_WORLD_UI_TEST_MODE = '1'
$env:QL_WORLD_DATA_ROOT = $dataRoot

# M11 起未配置库目录会先弹 W7「首次设置」向导，冒烟无法交互；
# 预置 config.json 写入 libraryRootPath/dataRootPath 以绕过向导。
New-Item -ItemType Directory -Path $libraryRoot -Force | Out-Null
$seedConfig = [ordered]@{ libraryRootPath = $libraryRoot; dataRootPath = $libraryRoot }
[IO.File]::WriteAllText(
    $configPath,
    ($seedConfig | ConvertTo-Json),
    (New-Object System.Text.UTF8Encoding($false)))

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
public class UiSmokeDupW32 {
    [DllImport("user32.dll")] public static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);
    [DllImport("user32.dll")] public static extern int GetWindowText(IntPtr hWnd, StringBuilder lpString, int nMaxCount);
    [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);
    [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr hWnd);
    public delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);
}
"@

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

function Find-TopWindowByTitle {
    # WPF 模态窗口在 UIA 顶层枚举可能不可见，走 Win32 EnumWindows fallback（M2 经验）
    param([int]$OwnerPid, [string]$TitleWildcard, [int]$TimeoutSeconds = 10)
    $deadline = [DateTime]::UtcNow.AddSeconds($TimeoutSeconds)
    while ([DateTime]::UtcNow -lt $deadline) {
        $script:foundHwnd = [IntPtr]::Zero
        $callback = [UiSmokeDupW32+EnumWindowsProc]{
            param($hWnd, $lParam)
            $wpid = 0
            [UiSmokeDupW32]::GetWindowThreadProcessId($hWnd, [ref]$wpid) | Out-Null
            if ($wpid -eq $OwnerPid -and [UiSmokeDupW32]::IsWindowVisible($hWnd)) {
                $sb = New-Object System.Text.StringBuilder 256
                [UiSmokeDupW32]::GetWindowText($hWnd, $sb, 256) | Out-Null
                if ($sb.ToString() -like $TitleWildcard) { $script:foundHwnd = $hWnd }
            }
            return $true
        }
        [UiSmokeDupW32]::EnumWindows($callback, [IntPtr]::Zero) | Out-Null
        if ($script:foundHwnd -ne [IntPtr]::Zero) {
            return [System.Windows.Automation.AutomationElement]::FromHandle($script:foundHwnd)
        }
        Start-Sleep -Milliseconds 300
    }
    return $null
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
    Write-Host '[Smoke] Launch (isolated data root)'
    $app = Start-App
    $mainWin = $app.Window
    Write-Host "  First window: $($mainWin.Current.Name)"

    # W5 登录窗以模态方式先于 ShellWindow 出现（无 NAS 时关窗即本地模式）；
    # 冒烟无法交互登录，直接关闭登录窗并等待主窗口。
    if ($mainWin.Current.Name -like '*登录*') {
        try {
            ($mainWin.GetCurrentPattern([System.Windows.Automation.WindowPattern]::Pattern)).Close()
        }
        catch { }
        $mainWin = Find-TopWindowByTitle $app.Process.Id '*3D 美术图片素材管理*' 20
        if ($null -eq $mainWin) { throw '[Smoke] Shell window not found after closing login window.' }
    }
    Write-Host "  Main window: $($mainWin.Current.Name)"

    # ── 1. A1-11 顶栏入口 ────────────────────────────────────
    Write-Host '[Smoke] Step 1: A1-11 查重 button exists'
    $dupButton = Find-FirstByName $mainWin '查重' ([System.Windows.Automation.ControlType]::Button)
    Assert-Step '顶栏「查重」按钮存在 (A1-11)' ($null -ne $dupButton) '未找到 AutomationProperties.Name=查重 的按钮'

    # ── 2. 打开 W8 ──────────────────────────────────────────
    Write-Host '[Smoke] Step 2: open W8 duplicate review window'
    $dupWindow = $null
    if ($null -ne $dupButton) {
        ($dupButton.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern)).Invoke()
        $dupWindow = Find-TopWindowByTitle $app.Process.Id '*重复审核*'
    }
    Assert-Step 'W8 重复审核窗口已打开' ($null -ne $dupWindow) '等待 10 秒未找到标题含「重复审核」的窗口'

    if ($null -ne $dupWindow) {
        # ── 3. 工具栏 ─────────────────────────────────────────
        Write-Host '[Smoke] Step 3: W8 toolbar controls'
        $scanButton = Find-FirstByName $dupWindow '开始全库扫描' ([System.Windows.Automation.ControlType]::Button)
        $cancelButton = Find-FirstByName $dupWindow '取消扫描' ([System.Windows.Automation.ControlType]::Button)
        $kindCombo = Find-FirstByName $dupWindow '类型筛选' ([System.Windows.Automation.ControlType]::ComboBox)
        $statusCombo = Find-FirstByName $dupWindow '状态筛选' ([System.Windows.Automation.ControlType]::ComboBox)
        $refreshButton = Find-FirstByName $dupWindow '刷新重复组' ([System.Windows.Automation.ControlType]::Button)
        Assert-Step '「开始全库扫描」按钮存在 (W8-TB)' ($null -ne $scanButton) '缺少扫描按钮'
        Assert-Step '「取消扫描」按钮存在 (W8-TB)' ($null -ne $cancelButton) '缺少取消按钮'
        Assert-Step '「类型筛选」下拉存在 (W8-TB)' ($null -ne $kindCombo) '缺少类型筛选 ComboBox'
        Assert-Step '「状态筛选」下拉存在 (W8-TB)' ($null -ne $statusCombo) '缺少状态筛选 ComboBox'
        Assert-Step '「刷新」按钮存在 (W8-TB)' ($null -ne $refreshButton) '缺少刷新按钮'

        # ── 4. 组列表与动作区 ────────────────────────────────
        Write-Host '[Smoke] Step 4: W8 group list and action buttons'
        $groupList = Find-FirstByName $dupWindow '重复组列表' ([System.Windows.Automation.ControlType]::List)
        $keepAll = Find-FirstByName $dupWindow '保留全部' ([System.Windows.Automation.ControlType]::Button)
        $deleteSelected = Find-FirstByName $dupWindow '删除选中' ([System.Windows.Automation.ControlType]::Button)
        Assert-Step '「重复组列表」存在 (W8-L)' ($null -ne $groupList) '缺少组列表 ListBox'
        Assert-Step '「保留全部」按钮存在 (W8-ACT)' ($null -ne $keepAll) '缺少保留全部按钮'
        Assert-Step '「删除选中」按钮存在 (W8-ACT)' ($null -ne $deleteSelected) '缺少删除选中按钮'

        # ── 5. 关闭 W8 ───────────────────────────────────────
        Write-Host '[Smoke] Step 5: close W8'
        $closeButton = Find-FirstByName $dupWindow '关闭' ([System.Windows.Automation.ControlType]::Button)
        Assert-Step 'W8「关闭」按钮存在' ($null -ne $closeButton) '缺少关闭按钮'
        if ($null -ne $closeButton) {
            ($closeButton.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern)).Invoke()
            Start-Sleep -Milliseconds 800
        }
        $stillOpen = Find-TopWindowByTitle $app.Process.Id '*重复审核*' 2
        Assert-Step 'W8 关闭后窗口消失' ($null -eq $stillOpen) '窗口仍然存在'
    }
}
finally {
    Stop-App $app
    Write-Host ''
    Write-Host '── 结果 ──'
    $results | ForEach-Object { Write-Host $_ }
    Write-Host "失败 $script:failures 项"
}

if ($script:failures -gt 0) { exit 1 }
exit 0
