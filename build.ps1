# QL World build/release script.
# M0 skeleton: Prepare runs the full local gate (Release build with zero
# warnings, all tests, self-contained staging publish).
# Finalize (five-piece release into dist/, annotated tag) lands in M4;
# see GEMINI.md section 7 for the contract it must satisfy.
[CmdletBinding()]
param(
    [ValidateSet('Prepare', 'Finalize')]
    [string]$Mode = 'Prepare'
)

$ErrorActionPreference = 'Stop'

$projectRoot = Split-Path -Parent $PSCommandPath
$solutionPath = Join-Path $projectRoot 'QL.World.sln'
$csprojPath = Join-Path $projectRoot 'src\QL.World\QL.World.csproj'

Import-Module (Join-Path $projectRoot 'tools\release\ReleaseContract.psm1')

switch ($Mode) {
    'Prepare' {
        $version = Get-ProjectVersion -CsprojPath $csprojPath
        $staging = Join-Path $projectRoot "artifacts\staging\QL World_v$version"

        Write-Host "[QLWorld] Prepare v$version : restore"
        & dotnet restore $solutionPath --nologo
        if ($LASTEXITCODE -ne 0) { throw 'dotnet restore failed.' }

        Write-Host "[QLWorld] Prepare v$version : Release build (zero-warning gate)"
        & dotnet build $solutionPath -c Release --nologo
        if ($LASTEXITCODE -ne 0) { throw 'Release build failed.' }

        Write-Host "[QLWorld] Prepare v$version : tests"
        & dotnet test $solutionPath -c Release --nologo --no-build --verbosity minimal
        if ($LASTEXITCODE -ne 0) { throw 'Tests failed.' }

        Write-Host "[QLWorld] Prepare v$version : self-contained publish (win-x64, ReadyToRun)"
        if (Test-Path -LiteralPath $staging) {
            Remove-Item -LiteralPath $staging -Recurse -Force
        }
        & dotnet publish $csprojPath `
            -c Release -r win-x64 --self-contained true `
            -p:PublishReadyToRun=true `
            --nologo -o $staging
        if ($LASTEXITCODE -ne 0) { throw 'dotnet publish failed.' }

        Write-Host "[QLWorld] Prepare complete: $staging"
        Write-Warning 'Finalize (five-piece dist/ release + annotated tag) is implemented in M4.'
    }

    'Finalize' {
        throw 'Finalize is not implemented yet (planned for M4; see GEMINI.md section 7).'
    }
}
