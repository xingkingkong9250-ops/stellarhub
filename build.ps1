# QL World build/release script.
# M0 skeleton: Prepare runs the full local gate (Release build with zero
# warnings, all tests, self-contained staging publish).
# M4-F: Finalize implements the five-piece release into dist/ and prints
# the annotated tag command (build.ps1 is read-only on Git; tag is a manual step).
# See GEMINI.md section 7 for the contract Finalize must satisfy.
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
    Prepare 门禁：restore + Release 0 警告编译 + 全绿测试 + self-contained win-x64
    ReadyToRun 发布到 artifacts\staging\QL World_v{version}\。
    Prepare 与 Finalize 共用此函数（M4-F 重构为复用入口，避免逻辑复制）。
#>
function Invoke-PrepareGate {
    param([Parameter(Mandatory = $true)][string]$Version)

    $staging = Join-Path $projectRoot "artifacts\staging\QL World_v$Version"

    # 原生命令的 stdout 必须捕获后经 Write-Host 输出：若直接执行，stdout 会进入
    # 函数输出管道，污染 return $staging 的返回值（调用方拿到「全部输出+路径」数组）。
    # 不用 2>&1：PS 5.1 下 $ErrorActionPreference='Stop' 时 stderr 重定向会抛 NativeCommandError。
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
        Write-Warning 'Finalize (five-piece dist/ release + annotated tag) is implemented in M4.'
    }

    'Finalize' {
        $version = Get-ProjectVersion -CsprojPath $csprojPath

        # Gate 1: 工作区必须干净（强制清洁，PRD §7 约定 Finalize 只读 Git 不做写操作）
        $gitStatusOutput = & git -C $projectRoot status --porcelain
        if ($LASTEXITCODE -ne 0) { throw 'git status failed (is this a git repository?).' }
        $statusText = if ($null -eq $gitStatusOutput) { '' } else { $gitStatusOutput -join "`n" }
        if ($statusText.Trim().Length -gt 0) {
            throw 'Working tree is not clean. Commit or stash changes before Finalize.'
        }

        # Gate 2: 完整 Prepare 门禁（build + test + publish staging）
        $staging = Invoke-PrepareGate -Version $version
        Write-Host "[QLWorld] Finalize v$version : staging ready at $staging"

        # 构建 ReleaseTool Exe（PS 5.1 无法用 Add-Type 加载 .NET 10 程序集，
        # 通过调用 Exe 子进程保持 PS 5.1 兼容；GEMINI.md §7 工程约束 #7）
        Write-Host "[QLWorld] Finalize v$version : building ReleaseTool"
        & dotnet build $releaseToolCsproj -c Release --nologo
        if ($LASTEXITCODE -ne 0) { throw 'ReleaseTool build failed.' }

        $releaseToolExe = Join-Path $projectRoot 'tools\ReleaseTool\bin\Release\net10.0-windows\QL.World.ReleaseTool.exe'
        if (-not (Test-Path -LiteralPath $releaseToolExe)) {
            throw "ReleaseTool executable not found at expected path: $releaseToolExe"
        }

        # 调用 ReleaseTool Exe 产出五件套；最后一行格式为 MANIFEST_SHA:<64hex>
        # 不显式传 --release-notes / --user-guide，让 ReleaseTool 用 --project-root
        # 解析默认路径 docs/release/RELEASE_NOTES.md 与 docs/USER_GUIDE.md，
        # 文件不存在时自动回退最小占位（USER_GUIDE.md 由 M4-E 并行写，可能缺失）。
        # PS 5.1 兼容：用 ; 分隔语句，不用 && / ||
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

        # 从输出末尾解析 MANIFEST_SHA:<hex>
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

        # PRD §7：build.ps1 只读 Git 不做写操作 → annotated tag 命令只打印不执行
        Write-Host ''
        Write-Host '[QLWorld] Finalize complete.'
        Write-Host "[QLWorld] Release dist dir: $distRoot\QL World_v$version"
        Write-Host "[QLWorld] Manifest sha256: $manifestSha"
        Write-Host ''
        Write-Host '[QLWorld] Run the following to tag (manual step, not executed by this script):'
        Write-Host "    git tag -a v$version -m `"release-manifest-sha256: $manifestSha`""
    }
}
