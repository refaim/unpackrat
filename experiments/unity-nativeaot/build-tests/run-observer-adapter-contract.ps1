#requires -Version 7.4
<#
  Behavioral contract test for UnityNativeAotObserverAdapter (Directory.Build.targets).

  Drives the real target through an empty SetupOSSpecificProps probe project and
  asserts the actual @(LinkerArg) items the target contributes for each condition of
  the decision matrix. It inspects emitted items, not source text, and fails closed
  on any mismatch. Run:  pwsh -File run-observer-adapter-contract.ps1 [-MsBuild <path>]
#>
[CmdletBinding()]
param(
    [string] $MsBuild = 'C:\Program Files (x86)\Microsoft Visual Studio\18\BuildTools\MSBuild\Current\Bin\MSBuild.exe'
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$here = Split-Path -Parent $MyInvocation.MyCommand.Path
$experiment = Split-Path -Parent $here
$root = Split-Path -Parent (Split-Path -Parent $experiment)
$project = Join-Path $here 'observer-adapter-contract.proj'

$libX64 = Join-Path $root 'out\unity-observer-adapter\native\x64\Release\UnityObserver.lib'
$libX86 = Join-Path $root 'out\unity-observer-adapter\native\Win32\Release\UnityObserver.lib'
foreach ($lib in @($libX64, $libX86)) {
    if (-not (Test-Path -LiteralPath $lib)) {
        throw "the Observer static library is missing: $lib (build UnityObserver.vcxproj first)"
    }
}

function Invoke-Probe {
    param([hashtable] $Properties)

    $arguments = [System.Collections.Generic.List[string]]::new()
    $arguments.Add($project)
    $arguments.Add('/t:ReportObserverAdapter')
    $arguments.Add('/nologo')
    $arguments.Add('/v:m')
    foreach ($pair in $Properties.GetEnumerator()) {
        $arguments.Add("/p:$($pair.Key)=$($pair.Value)")
    }
    $text = & $MsBuild @arguments 2>&1 | Out-String
    return [pscustomobject]@{ ExitCode = $LASTEXITCODE; Text = $text }
}

function Get-LinkerArgs {
    param([string] $Text)
    $match = [regex]::Match($Text, 'OBSERVER_LINKERARG=\[(?<items>.*?)\]')
    if (-not $match.Success) { return @() }
    $raw = $match.Groups['items'].Value
    if ([string]::IsNullOrWhiteSpace($raw)) { return @() }
    return @($raw -split ';')
}

# The static-UCRT swap target also hooks SetupOSSpecificProps for every Windows AOT
# build, so the matrix asserts on the Observer-specific items only.
function Get-ObserverItems {
    param([string[]] $Items)
    return @($Items | Where-Object { $_ -like '*UnityObserver.lib*' -or $_ -like '/EXPORT:*' })
}

$base = @{
    PublishAot         = 'true'
    _targetOS          = 'win'
    _targetArchitecture = 'x64'
    NativeLib          = 'Shared'
}

$failures = [System.Collections.Generic.List[string]]::new()
$count = 0

function Assert-Case {
    param([string] $Name, [hashtable] $Properties, [scriptblock] $Check)

    $script:count++
    $result = Invoke-Probe -Properties $Properties
    $items = @(Get-LinkerArgs -Text $result.Text)
    $verdict = & $Check $result $items
    if ($verdict -eq $true) {
        Write-Host ("PASS  {0}" -f $Name)
    } else {
        Write-Host ("FAIL  {0}: {1}" -f $Name, $verdict)
        $failures.Add($Name)
    }
}

function Merge-Props {
    param([hashtable] $Overrides, [switch] $WithLibrary, [string] $Library)
    $merged = @{}
    foreach ($key in $base.Keys) { $merged[$key] = $base[$key] }
    foreach ($key in $Overrides.Keys) { $merged[$key] = $Overrides[$key] }
    if ($WithLibrary) { $merged['UnityObserverLibrary'] = $Library }
    return $merged
}

# 1. Empty property: ordinary publish must be untouched.
Assert-Case 'empty-property-emits-nothing' (Merge-Props @{}) {
    param($result, $items)
    if ($result.ExitCode -ne 0) { return "exit $($result.ExitCode)" }
    $observer = @(Get-ObserverItems -Items $items)
    if ($observer.Count -ne 0) { return "unexpected items: $($observer -join ', ')" }
    return $true
}

# 2. Non-Windows target is skipped even with the property set.
Assert-Case 'non-windows-skipped' (Merge-Props @{ _targetOS = 'linux' } -WithLibrary -Library $libX64) {
    param($result, $items)
    if ($result.ExitCode -ne 0) { return "exit $($result.ExitCode)" }
    $observer = @(Get-ObserverItems -Items $items)
    if ($observer.Count -ne 0) { return "unexpected items: $($observer -join ', ')" }
    return $true
}

# 3. Non-AOT build is skipped.
Assert-Case 'non-aot-skipped' (Merge-Props @{ PublishAot = 'false' } -WithLibrary -Library $libX64) {
    param($result, $items)
    if ($result.ExitCode -ne 0) { return "exit $($result.ExitCode)" }
    $observer = @(Get-ObserverItems -Items $items)
    if ($observer.Count -ne 0) { return "unexpected items: $($observer -join ', ')" }
    return $true
}

# 4. Static native library is skipped.
Assert-Case 'static-lib-skipped' (Merge-Props @{ NativeLib = 'Static' } -WithLibrary -Library $libX64) {
    param($result, $items)
    if ($result.ExitCode -ne 0) { return "exit $($result.ExitCode)" }
    $observer = @(Get-ObserverItems -Items $items)
    if ($observer.Count -ne 0) { return "unexpected items: $($observer -join ', ')" }
    return $true
}

# 5. Unsupported architecture is skipped.
Assert-Case 'arm64-skipped' (Merge-Props @{ _targetArchitecture = 'arm64' } -WithLibrary -Library $libX64) {
    param($result, $items)
    if ($result.ExitCode -ne 0) { return "exit $($result.ExitCode)" }
    $observer = @(Get-ObserverItems -Items $items)
    if ($observer.Count -ne 0) { return "unexpected items: $($observer -join ', ')" }
    return $true
}

# 6. x64: the library plus the two undecorated exports.
Assert-Case 'x64-emits-library-and-bare-exports' (Merge-Props @{} -WithLibrary -Library $libX64) {
    param($result, $items)
    if ($result.ExitCode -ne 0) { return "exit $($result.ExitCode)" }
    $joined = $items -join ';'
    if ($joined -notlike "*$libX64*") { return "library path absent: $joined" }
    if ($items -notcontains '/EXPORT:LoadSubModule') { return "missing /EXPORT:LoadSubModule" }
    if ($items -notcontains '/EXPORT:UnloadSubModule') { return "missing /EXPORT:UnloadSubModule" }
    if ($items -contains '/EXPORT:LoadSubModule=_LoadSubModule@4') { return 'x64 must not use the stdcall alias' }
    return $true
}

# 7. x86: the library plus the stdcall-decorated exports.
Assert-Case 'x86-emits-library-and-decorated-exports' (Merge-Props @{ _targetArchitecture = 'x86' } -WithLibrary -Library $libX86) {
    param($result, $items)
    if ($result.ExitCode -ne 0) { return "exit $($result.ExitCode)" }
    $joined = $items -join ';'
    if ($joined -notlike "*$libX86*") { return "library path absent: $joined" }
    if ($items -notcontains '/EXPORT:LoadSubModule=_LoadSubModule@4') { return 'missing /EXPORT:LoadSubModule=_LoadSubModule@4' }
    if ($items -notcontains '/EXPORT:UnloadSubModule=_UnloadSubModule@0') { return 'missing /EXPORT:UnloadSubModule=_UnloadSubModule@0' }
    return $true
}

# 8. A set-but-missing library fails the build before the link, with the diagnostic.
Assert-Case 'missing-library-fails-closed' (Merge-Props @{} -WithLibrary -Library 'Z:\definitely-missing\UnityObserver.lib') {
    param($result, $items)
    if ($result.ExitCode -eq 0) { return 'expected a build failure' }
    if ($result.Text -notlike '*does not exist*') { return 'missing the fail-closed diagnostic' }
    return $true
}

Write-Host ("{0}/{1} cases passed" -f ($count - $failures.Count), $count)
if ($failures.Count -ne 0) {
    Write-Host ("FAILED: {0}" -f ($failures -join ', '))
    exit 1
}
exit 0
