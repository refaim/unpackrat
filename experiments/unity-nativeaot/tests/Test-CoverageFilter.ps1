#!/usr/bin/env pwsh
<#
    Focused harness for the Coverlet include filter owned by UnityProbe.Tests.csproj.

    It runs the real `dotnet test` with the project's OWN coverage configuration (it never
    overrides the Include filter on the command line) and asserts the observable contract:

      * the project still pins Threshold 100 and ThresholdType line,branch;
      * `dotnet test` (all 165 tests + CollectCoverage) exits 0;
      * the resulting Cobertura report has exactly one package, named `unpackrat-unity-probe`;
      * that package, every own class in it, and every line/branch outcome is 100% covered.

    Exit code 0 means the contract holds; any non-zero exit means a named assertion failed.

    Ownership: the harness only ever creates its output directory if it is missing and overwrites
    its own two outputs (coverage.cobertura.xml and dotnet-test.log). It never removes the caller's
    output directory or any unrelated contents inside it.

    Usage:  pwsh -File experiments/unity-nativeaot/tests/Test-CoverageFilter.ps1
#>
[CmdletBinding()]
param(
    [string]$TestProject = (Join-Path $PSScriptRoot '..\UnityProbe.Tests\UnityProbe.Tests.csproj'),
    [string]$OutputDir = (Join-Path $PSScriptRoot '..\..\..\out\unity-size\coverage-filter-project')
)

$ErrorActionPreference = 'Stop'
$repoRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..\..'))
$TestProject = [IO.Path]::GetFullPath($TestProject)
$OutputDir = [IO.Path]::GetFullPath($OutputDir)

$failures = [System.Collections.Generic.List[string]]::new()
function Assert([bool]$condition, [string]$message) {
    if ($condition) { Write-Host "PASS: $message" }
    else { $failures.Add($message); Write-Host "FAIL: $message" }
}

# Project-owned gate must stay strict: 100% line and branch, no weakening.
[xml]$project = Get-Content -Raw $TestProject
$threshold = (@($project.Project.PropertyGroup | Where-Object { $_.Threshold })[0]).Threshold
$thresholdType = (@($project.Project.PropertyGroup | Where-Object { $_.ThresholdType })[0]).ThresholdType
$include = (@($project.Project.PropertyGroup | Where-Object { $_.Include })[0]).Include
Write-Host "project Include filter: $include"
Write-Host "project Threshold/ThresholdType: $threshold / $thresholdType"
Assert ($threshold -eq '100') "Threshold property is 100 (found '$threshold')"
Assert ($thresholdType -eq 'line,branch') "ThresholdType property is 'line,branch' (found '$thresholdType')"

# Own only our two outputs: create the directory if it is missing, then overwrite those files.
# The caller's directory and everything else in it must survive untouched.
$report = Join-Path $OutputDir 'coverage.cobertura.xml'
$runLog = Join-Path $OutputDir 'dotnet-test.log'
New-Item -ItemType Directory -Force -Path $OutputDir | Out-Null
foreach ($ownedOutput in @($report, $runLog)) {
    if (Test-Path -LiteralPath $ownedOutput) { Remove-Item -LiteralPath $ownedOutput -Force }
}
$coverletOutput = (($OutputDir -replace '\\', '/').TrimEnd('/')) + '/'

Push-Location $repoRoot
try {
    Write-Host "running: dotnet test $TestProject -c Release --no-restore -p:CollectCoverage=true -p:CoverletOutput=$coverletOutput -p:CoverletOutputFormat=cobertura"
    & dotnet test $TestProject -c Release --no-restore `
        -p:CollectCoverage=true `
        "-p:CoverletOutput=$coverletOutput" `
        -p:CoverletOutputFormat=cobertura *>&1 | Out-File -FilePath $runLog -Encoding utf8
    $dotnetExit = $LASTEXITCODE
} finally {
    Pop-Location
}

Write-Host "dotnet test exit code: $dotnetExit"
Get-Content $runLog -Tail 8 | ForEach-Object { Write-Host "  | $_" }
Assert ($dotnetExit -eq 0) "dotnet test exited 0 (found $dotnetExit)"

Assert (Test-Path $report) "coverage report written: $report"
if (Test-Path $report) {
    [xml]$coverage = Get-Content -Raw $report
    $packages = @($coverage.coverage.packages.package)
    Write-Host "packages in report: $($packages.Count) -> $(($packages | ForEach-Object { $_.name }) -join ', ')"
    Assert ($packages.Count -eq 1) "report has exactly one package (found $($packages.Count))"

    $package = $packages[0]
    Assert ($package.name -eq 'unpackrat-unity-probe') "only package is 'unpackrat-unity-probe' (found '$($package.name)')"
    Assert ([double]$package.'line-rate' -eq 1) "package line-rate is 1 (found $($package.'line-rate'))"
    Assert ([double]$package.'branch-rate' -eq 1) "package branch-rate is 1 (found $($package.'branch-rate'))"

    $classes = @($package.classes.class)
    $expectedClasses = @(
        'Unpackrat.UnityProbe.Budgets',
        'Unpackrat.UnityProbe.DeclaredSourceSize',
        'Unpackrat.UnityProbe.UnitySession',
        'Unpackrat.UnityProbe.UnitySessionAbi',
        'Unpackrat.UnityProbe.UnitySource',
        'Unpackrat.UnityProbe.UnitySource/TextureAsset',
        'Unpackrat.UnityProbe.UnitySource/TextAssetAdapter',
        'Unpackrat.UnityProbe.UnityGraph'
    )
    foreach ($name in $expectedClasses) {
        Assert ($classes.name -contains $name) "own class visible and instrumented: $name"
    }

    $classesNotFull = @($classes | Where-Object { [double]$_.'line-rate' -ne 1 -or [double]$_.'branch-rate' -ne 1 })
    Assert ($classesNotFull.Count -eq 0) "every own class is 100% line and branch (non-full: $(($classesNotFull | ForEach-Object { $_.name }) -join ', '))"

    $lines = @($classes.lines.line)
    $uncovered = @($lines | Where-Object { [int]$_.hits -eq 0 })
    Assert ($uncovered.Count -eq 0) "no uncovered own line (found $($uncovered.Count) of $($lines.Count))"

    $branchLines = @($lines | Where-Object { $_.branch -eq 'true' })
    $partial = @()
    foreach ($line in $branchLines) {
        if ($line.'condition-coverage' -match '\((\d+)/(\d+)\)' -and $Matches[1] -ne $Matches[2]) {
            $partial += $line.number
        }
    }
    Assert ($partial.Count -eq 0) "no partially covered own branch point (branch points: $($branchLines.Count))"
}

if ($failures.Count -gt 0) {
    Write-Host "RESULT: FAIL ($($failures.Count) assertion(s))"
    $failures | ForEach-Object { Write-Host " - $_" }
    exit 1
}

Write-Host "RESULT: PASS (one package 'unpackrat-unity-probe' at 100% line and branch; dotnet exit 0)"
exit 0
