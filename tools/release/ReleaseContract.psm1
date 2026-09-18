# Release contract helpers shared by build.ps1 and (M4) release tests.
# Rule from GEMINI.md: the csproj <Version> is the single version source;
# scripts must never hardcode a release version.

Set-StrictMode -Version Latest

function Get-ProjectVersion {
    param(
        [Parameter(Mandatory = $true)]
        [string]$CsprojPath
    )

    if (-not (Test-Path -LiteralPath $CsprojPath)) {
        throw "Project file not found: $CsprojPath"
    }

    [xml]$xml = Get-Content -LiteralPath $CsprojPath -Raw
    $raw = $xml.Project.PropertyGroup.Version |
        Where-Object { -not [string]::IsNullOrWhiteSpace([string]$_) } |
        Select-Object -First 1
    $version = [string]$raw

    if ($version -notmatch '^\d+\.\d+\.\d+$') {
        throw "csproj <Version> must be major.minor.patch (found: '$version')."
    }

    return $version
}

Export-ModuleMember -Function Get-ProjectVersion
