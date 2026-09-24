# QL World build/release script.
# Prepare runs the full local gate (Release build with zero warnings,
# all tests, self-contained staging publish).
# Finalize implements the five-piece release into dist/ and prints
# the annotated tag command (build.ps1 is read-only on Git; tag is a manual step).
# See GEMINI.md section 7 for the contract Finalize must satisfy.
#
# NOTE: this file MUST be saved as UTF-8 WITH BOM. Windows PowerShell 5.1
# reads BOM-less UTF-8 as ANSI/GBK on zh-CN systems; CJK punctuation byte
# pairs can swallow LF newlines and silently corrupt parsing (M4-Z lesson).
[CmdletBinding()]
param(
    [ValidateSet('Prepare', 'Finalize')]
    [string]$Mode = 'Prepare'
)

$ErrorActionPreference = 'Stop'

$projectRoot = Split-Path -Parent $PSCommandPath
$solutionPath = Join-Path $projectRoot 'QL.World.sln'
$csprojPath = Join-Path $projectRoot 'src\QL.World\QL.World.csproj'
$releaseToolCsproj = Join-Path $projectRoot 'tools\ReleaseTool\ReleaseTool.csproj'
$distRoot = Join-Path $projectRoot 'dist'

Import-Module (Join-Path $projectRoot 'tools\release\ReleaseContract.psm1')

<#
    Prepare gate: restore + Release zero-warning build + all tests +
    self-contained win-x64 ReadyToRun publish into
    artifacts\staging\QL World_v{version}\.
    Shared by Prepare and Finalize (M4-F refactor, no logic duplication).
#>
function Invoke-PrepareGate {
    param([Parameter(Mandatory = $true)][string]$Version)

    $staging = Join-Path $projectRoot "artifacts\staging\QL World_v$Version"

    # Native command stdout MUST be captured and re-emitted via Write-Host:
    # un-captured stdout enters the function output pipeline and pollutes
    # the return value (caller would receive "all output + path" array).
    # Do NOT use 2>&1: on PS 5.1 with $ErrorActionPreference='Stop' a stderr
    # redirect raises NativeCommandError.
    Write-Host "[QLWorld] Prepare v$Version : restore"
    $out = & dotnet restore $solutionPath --nologo
    $out | ForEach-Object { Write-Host $_ }
    if ($LASTEXITCODE -ne 0) { throw 'dotnet restore failed.' }

    Write-Host "[QLWorld] Prepare v$Version : Release build (zero-warning gate)"
    $out = & dotnet build $solutionPath -c Release --nologo
    $out | ForEach-Object { Write-Host $_ }
    if ($LASTEXITCODE -ne 0) { throw 'Release build failed.' }

    Write-Host "[QLWorld] Prepare v$Version : tests"
    $out = & dotnet test $solutionPath -c Release --nologo --no-build --verbosity minimal
    $out | ForEach-Object { Write-Host $_ }
    if ($LASTEXITCODE -ne 0) { throw 'Tests failed.' }

    Write-Host "[QLWorld] Prepare v$Version : self-contained publish (win-x64, ReadyToRun)"
    if (Test-Path -LiteralPath $staging) {
        Remove-Item -LiteralPath $staging -Recurse -Force
    }
    $out = & dotnet publish $csprojPath `
        -c Release -r win-x64 --self-contained true `
        -p:PublishReadyToRun=true `
        --nologo -o $staging
    $out | ForEach-Object { Write-Host $_ }
    if ($LASTEXITCODE -ne 0) { throw 'dotnet publish failed.' }

    return $staging
}

switch ($Mode) {
    'Prepare' {
        $version = Get-ProjectVersion -CsprojPath $csprojPath
        $staging = Invoke-PrepareGate -Version $version
        Write-Host "[QLWorld] Prepare complete: $staging"
    }

    'Finalize' {
        $version = Get-ProjectVersion -CsprojPath $csprojPath

        # Gate 1: working tree must be clean (Finalize is read-only on Git,
        # per PRD section 7).
        $gitStatusOutput = & git -C $projectRoot status --porcelain
        if ($LASTEXITCODE -ne 0) { throw 'git status failed (is this a git repository?).' }
        $statusText = if ($null -eq $gitStatusOutput) { '' } else { $gitStatusOutput -join "`n" }
        if ($statusText.Trim().Length -gt 0) {
            throw 'Working tree is not clean. Commit or stash changes before Finalize.'
        }

        # Gate 2: full Prepare gate (build + test + publish staging).
        $staging = Invoke-PrepareGate -Version $version
        Write-Host "[QLWorld] Finalize v$version : staging ready at $staging"

        # Build the ReleaseTool exe (PS 5.1 cannot Add-Type a .NET 10 assembly,
        # so we invoke it as a child process).
        Write-Host "[QLWorld] Finalize v$version : building ReleaseTool"
        $out = & dotnet build $releaseToolCsproj -c Release --nologo
        $out | ForEach-Object { Write-Host $_ }
        if ($LASTEXITCODE -ne 0) { throw 'ReleaseTool build failed.' }

        $releaseToolExe = Join-Path $projectRoot 'tools\ReleaseTool\bin\Release\net10.0-windows\QL.World.ReleaseTool.exe'
        if (-not (Test-Path -LiteralPath $releaseToolExe)) {
            throw "ReleaseTool executable not found at expected path: $releaseToolExe"
        }

        # Produce the five artifacts. Release notes / user guide default to
        # docs\release\RELEASE_NOTES.md and docs\USER_GUIDE.md under --project-root;
        # missing files fall back to minimal placeholders.
        $toolOutput = & $releaseToolExe `
            --staging $staging `
            --version $version `
            --project-root $projectRoot `
            --dist-root $distRoot
        if ($LASTEXITCODE -ne 0) {
            $toolOutput | ForEach-Object { Write-Host $_ }
            throw "ReleaseTool failed (exit $LASTEXITCODE)."
        }
        $toolOutput | ForEach-Object { Write-Host $_ }

        # Parse the final MANIFEST_SHA:<64hex> line.
        $manifestSha = $null
        foreach ($line in $toolOutput) {
            if ($line -match '^MANIFEST_SHA:([0-9a-f]{64})$') {
                $manifestSha = $Matches[1]
                break
            }
        }
        if ($null -eq $manifestSha) {
            throw 'ReleaseTool did not emit MANIFEST_SHA line; cannot produce tag command.'
        }

        # PRD section 7: build.ps1 is read-only on Git; the annotated tag
        # command is printed, not executed.
        Write-Host ''
        Write-Host '[QLWorld] Finalize complete.'
        Write-Host "[QLWorld] Release dist dir: $distRoot\QL World_v$version"
        Write-Host "[QLWorld] Manifest sha256: $manifestSha"
        Write-Host ''
        Write-Host '[QLWorld] Run the following to tag (manual step, not executed by this script):'
        Write-Host "    git tag -a v$version -m `"release-manifest-sha256: $manifestSha`""
    }
}
