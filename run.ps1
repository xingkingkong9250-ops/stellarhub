$ErrorActionPreference = 'Stop'

$projectRoot = Split-Path -Parent $PSCommandPath
$sourceProject = Join-Path $projectRoot 'src\QL.World'
$outputDirectory = Join-Path $sourceProject 'bin\Debug\net10.0-windows'
if (-not [string]::IsNullOrWhiteSpace($env:QL_WORLD_BUILD_OUTPUT)) {
    $outputDirectory = [System.IO.Path]::GetFullPath(
        $env:QL_WORLD_BUILD_OUTPUT)
}
$pidFile = Join-Path $env:TEMP 'QL_World.dev.pid'
$runLog = Join-Path $env:TEMP 'QL_World.run.log'
$shutdownEventName = 'Local\QL_World_GracefulShutdown'
if ($env:QL_WORLD_UI_TEST_MODE -match '^(1|true)$') {
    $shutdownEventName = 'Local\QL_World_UiTest_GracefulShutdown'
}

try {
    Start-Transcript -Path $runLog -Force | Out-Null
}
catch {
    # Logging must never prevent the application from starting.
}

if (-not (Get-Command dotnet -ErrorAction SilentlyContinue)) {
    Write-Error '.NET SDK was not found. Install .NET 10 SDK first.'
    exit 1
}

if (Test-Path -LiteralPath $pidFile) {
    $previousState = (Get-Content -LiteralPath $pidFile -Raw -ErrorAction SilentlyContinue).Trim()
    $parts = $previousState -split '\|', 2
    if ($parts.Count -eq 2 -and $parts[0] -match '^\d+$' -and $parts[1] -match '^\d+$') {
        $previous = Get-Process -Id $parts[0] -ErrorAction SilentlyContinue
        if ($previous -and
            $previous.StartTime.ToUniversalTime().Ticks -eq [long]$parts[1]) {
            $shutdownRequested = $false
            try {
                $shutdownEvent = [System.Threading.EventWaitHandle]::OpenExisting(
                    $shutdownEventName)
                try {
                    [void]$shutdownEvent.Set()
                    $shutdownRequested = $true
                }
                finally {
                    $shutdownEvent.Dispose()
                }
            }
            catch [System.Threading.WaitHandleCannotBeOpenedException] {
                # Compatibility fallback for an older build without the named signal.
                $shutdownRequested =
                    -not $previous.HasExited -and $previous.CloseMainWindow()
            }
            catch {
                Write-Error "[QLWorld] Could not request a graceful shutdown: $($_.Exception.Message)"
                exit 1
            }

            if (-not $shutdownRequested) {
                Write-Error '[QLWorld] The running app could not be closed safely. Close the window, then retry.'
                exit 1
            }

            try {
                $previous | Wait-Process -Timeout 8 -ErrorAction Stop
            }
            catch {
                Write-Error '[QLWorld] The running app did not exit cleanly within 8 seconds. No force-kill was attempted.'
                exit 1
            }
        }
    }
    Remove-Item -LiteralPath $pidFile -Force -ErrorAction SilentlyContinue
}

Write-Host '[QLWorld] Building...'
& dotnet build $sourceProject -c Debug --nologo -v q -o $outputDirectory
if ($LASTEXITCODE -ne 0) {
    Write-Error '[QLWorld] Build failed.'
    exit $LASTEXITCODE
}

$runtimeConfig = Get-ChildItem -LiteralPath $outputDirectory -Filter '*.runtimeconfig.json' -File |
    Select-Object -First 1
if ($null -eq $runtimeConfig) {
    Write-Error '[QLWorld] Debug runtime configuration was not generated.'
    exit 1
}

$assemblyBaseName = $runtimeConfig.Name -replace '\.runtimeconfig\.json$', ''
$applicationExe = Join-Path $outputDirectory "$assemblyBaseName.exe"
if (-not (Test-Path -LiteralPath $applicationExe)) {
    Write-Error "[QLWorld] Windowed application host is missing: $applicationExe"
    exit 1
}

Write-Host '[QLWorld] Starting...'
$process = Start-Process -FilePath $applicationExe `
    -WorkingDirectory $outputDirectory `
    -PassThru
Start-Sleep -Seconds 1

if ($process.HasExited) {
    Write-Error "[QLWorld] App exited during startup (exit code $($process.ExitCode)). See %APPDATA%\Qianlan_App\QL_World\logs\error.log."
    exit $process.ExitCode
}

Set-Content -LiteralPath $pidFile -Value "$($process.Id)|$($process.StartTime.ToUniversalTime().Ticks)" -NoNewline
Write-Host '[QLWorld] Started.'

try {
    Stop-Transcript | Out-Null
}
catch {
}
