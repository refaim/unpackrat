#!/usr/bin/env pwsh
<#
    Regression for Test-CoverageFilter.ps1: running it with a caller-supplied output directory
    must preserve that directory and every unrelated entry inside it. The harness may overwrite
    only its own known outputs (coverage.cobertura.xml and dotnet-test.log); it must never remove
    the directory itself or anything the caller put there.

    It plants a sentinel file and a nested sentinel directory inside an OWNED disposable directory
    under this worktree's out tree, runs the real harness against it, and asserts both survive.
    The disposable path is verified to resolve inside the worktree out tree before the harness is
    invoked, so the check can never point the harness at a real source or root directory.

    Exit code 0 means the directory and its contents were preserved; non-zero names the failure.
#>
[CmdletBinding()]
param(
    [string]$RepoRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..\..\..'))
)

$ErrorActionPreference = 'Stop'
$repoRoot = [IO.Path]::GetFullPath($RepoRoot)
$harness = Join-Path $PSScriptRoot 'Test-CoverageFilter.ps1'

$failures = [System.Collections.Generic.List[string]]::new()
function Assert([bool]$condition, [string]$message) {
    if ($condition) { Write-Host "PASS: $message" }
    else { $failures.Add($message); Write-Host "FAIL: $message" }
}

# Owned disposable output directory under the worktree out tree; never a source or root path.
$outRoot = [IO.Path]::GetFullPath((Join-Path $repoRoot 'out')) + [IO.Path]::DirectorySeparatorChar
$disposable = [IO.Path]::GetFullPath((Join-Path $repoRoot ("out\unity-size\coverage-filter-preserve-" + [Guid]::NewGuid().ToString('N'))))
Write-Host "disposable output directory: $disposable"
if (-not $disposable.StartsWith($outRoot, [StringComparison]::OrdinalIgnoreCase)) {
    Write-Host "refusing: disposable path escapes the worktree out tree '$outRoot'"
    exit 1
}

New-Item -ItemType Directory -Force -Path $disposable | Out-Null
$identityBefore = (Get-Item -LiteralPath $disposable).FullName

$sentinel = Join-Path $disposable 'caller-sentinel.txt'
Set-Content -LiteralPath $sentinel -Value 'caller-owned content' -Encoding utf8
$nestedDir = Join-Path $disposable 'caller-nested'
New-Item -ItemType Directory -Force -Path $nestedDir | Out-Null
$nestedSentinel = Join-Path $nestedDir 'nested-sentinel.txt'
Set-Content -LiteralPath $nestedSentinel -Value 'nested caller content' -Encoding utf8

Write-Host "running harness with the caller output directory"
& $harness -OutputDir $disposable
$harnessExit = $LASTEXITCODE
Write-Host "harness exit code: $harnessExit"
Assert ($harnessExit -eq 0) "harness completed its own contract (found exit $harnessExit)"

Assert (Test-Path -LiteralPath $disposable -PathType Container) "caller output directory preserved"
Assert (Test-Path -LiteralPath $sentinel -PathType Leaf) "caller sentinel file preserved"
Assert (Test-Path -LiteralPath $nestedDir -PathType Container) "caller nested directory preserved"
Assert (Test-Path -LiteralPath $nestedSentinel -PathType Leaf) "caller nested sentinel file preserved"

if (Test-Path -LiteralPath $sentinel -PathType Leaf) {
    Assert ((Get-Content -LiteralPath $sentinel -Raw).Trim() -eq 'caller-owned content') "sentinel file content unchanged"
}
if (Test-Path -LiteralPath $nestedSentinel -PathType Leaf) {
    Assert ((Get-Content -LiteralPath $nestedSentinel -Raw).Trim() -eq 'nested caller content') "nested sentinel file content unchanged"
}
if (Test-Path -LiteralPath $disposable -PathType Container) {
    Assert ((Get-Item -LiteralPath $disposable).FullName -eq $identityBefore) "caller output directory identity unchanged"
}

if ($failures.Count -gt 0) {
    Write-Host "RESULT: FAIL ($($failures.Count) assertion(s))"
    $failures | ForEach-Object { Write-Host " - $_" }
    exit 1
}

Write-Host "RESULT: PASS (caller output directory and unrelated contents preserved)"
exit 0
