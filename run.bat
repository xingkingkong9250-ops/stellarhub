@echo off
setlocal

set "WSCRIPT_EXE=%SystemRoot%\System32\wscript.exe"
set "HIDDEN_LAUNCHER=%~dp0run-hidden.vbs"
set "POWERSHELL_RUNNER=%~dp0run.ps1"

if not exist "%WSCRIPT_EXE%" (
    echo [QLWorld] Windows Script Host was not found: %WSCRIPT_EXE%
    pause
    exit /b 1
)

if not exist "%HIDDEN_LAUNCHER%" (
    echo [QLWorld] Hidden launcher was not found: %HIDDEN_LAUNCHER%
    pause
    exit /b 1
)

if not exist "%POWERSHELL_RUNNER%" (
    echo [QLWorld] PowerShell runner was not found: %POWERSHELL_RUNNER%
    pause
    exit /b 1
)

"%WSCRIPT_EXE%" //B "%HIDDEN_LAUNCHER%" "%POWERSHELL_RUNNER%"
exit /b %ERRORLEVEL%
