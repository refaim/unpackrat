# Decision-table tests for the real Unity NativeAOT experiment targets.
#
# Every case runs the actual MSBuild engine against the REAL
# experiments/unity-nativeaot/Directory.Build.targets through the probe project
# yy-thunks-probe.proj, and asserts the concrete outcome (build exit code and
# the exact @(LinkerArg) items the target produced). Nothing is asserted from a
# hard-coded copy of the flags.

[CmdletBinding()]
param(
    [string]$MsBuild = 'C:\Program Files (x86)\Microsoft Visual Studio\18\BuildTools\MSBuild\Current\Bin\MSBuild.exe'
)

$ErrorActionPreference = 'Stop'

$TestsDir  = Split-Path -Parent $MyInvocation.MyCommand.Path
$ExpDir    = Split-Path -Parent $TestsDir
$RepoRoot  = Split-Path -Parent (Split-Path -Parent $ExpDir)

$RealTargets = Join-Path $ExpDir 'Directory.Build.targets'
$ProbeProj   = Join-Path $TestsDir 'yy-thunks-probe.proj'
$WorkDir     = Join-Path $RepoRoot 'out\unity-link-probe\tests'
New-Item -ItemType Directory -Force -Path $WorkDir | Out-Null

$YyX86 = Join-Path $RepoRoot 'out\unity-link-probe\vendor\YY-Thunks\objs\x86\YY_Thunks_for_WinXP.obj'
$YyX64 = Join-Path $RepoRoot 'out\unity-link-probe\vendor\YY-Thunks\objs\x64\YY_Thunks_for_WinXP.obj'
$Missing = Join-Path $WorkDir 'does-not-exist\YY_Thunks_for_WinXP.obj'

# Exact expected linker contributions, derived from the production policy in
# build/unpackrat-vista.props (x86: 5.01 / @12-decorated entry; x64: 5.02).
$FlagX86 = @(
    "`"$YyX86`"",
    '/ENTRY:DllMainCRTStartupForYY_Thunks@12',
    '/ALTERNATENAME:_YY_ThunksOriginalDllMainCRTStartup@12=__DllMainCRTStartup@12',
    '/SUBSYSTEM:WINDOWS,5.01',
    '/OSVERSION:5.1'
)
$FlagX64 = @(
    "`"$YyX64`"",
    '/ENTRY:DllMainCRTStartupForYY_Thunks',
    '/ALTERNATENAME:YY_ThunksOriginalDllMainCRTStartup=_DllMainCRTStartup',
    '/SUBSYSTEM:WINDOWS,5.02',
    '/OSVERSION:5.2'
)
$FlagUcrt = @(
    '/NODEFAULTLIB:ucrt.lib',
    '/DEFAULTLIB:libucrt.lib'
)

$Cases = @(
    [pscustomobject]@{ Id='x64-existing';        Arch='x64';   Pub='true';  Os='win';   NativeLib='Shared'; Obj=$YyX64;   TargetA='UnityNativeAotYYThunks'; ExpectExit='zero';    Kind='yy-x64' }
    [pscustomobject]@{ Id='x86-existing';        Arch='x86';   Pub='true';  Os='win';   NativeLib='Shared'; Obj=$YyX86;   TargetA='UnityNativeAotYYThunks'; ExpectExit='zero';    Kind='yy-x86' }
    [pscustomobject]@{ Id='x64-missing';         Arch='x64';   Pub='true';  Os='win';   NativeLib='Shared'; Obj=$Missing; TargetA='UnityNativeAotYYThunks'; ExpectExit='nonzero'; Kind='error' }
    [pscustomobject]@{ Id='x86-missing';         Arch='x86';   Pub='true';  Os='win';   NativeLib='Shared'; Obj=$Missing; TargetA='UnityNativeAotYYThunks'; ExpectExit='nonzero'; Kind='error' }
    [pscustomobject]@{ Id='x64-emptyprop';       Arch='x64';   Pub='true';  Os='win';   NativeLib='Shared'; Obj='';       TargetA='UnityNativeAotYYThunks'; ExpectExit='zero';    Kind='none' }
    [pscustomobject]@{ Id='x86-emptyprop';       Arch='x86';   Pub='true';  Os='win';   NativeLib='Shared'; Obj='';       TargetA='UnityNativeAotYYThunks'; ExpectExit='zero';    Kind='none' }
    [pscustomobject]@{ Id='x64-managed';         Arch='x64';   Pub='false'; Os='win';   NativeLib='Shared'; Obj=$YyX64;   TargetA='UnityNativeAotYYThunks'; ExpectExit='zero';    Kind='none' }
    [pscustomobject]@{ Id='x64-nonwindows';      Arch='x64';   Pub='true';  Os='linux'; NativeLib='Shared'; Obj=$YyX64;   TargetA='UnityNativeAotYYThunks'; ExpectExit='zero';    Kind='none' }
    [pscustomobject]@{ Id='x64-staticNativeLib'; Arch='x64';   Pub='true';  Os='win';   NativeLib='Static'; Obj=$YyX64;   TargetA='UnityNativeAotYYThunks'; ExpectExit='zero';    Kind='none' }
    [pscustomobject]@{ Id='arm64-unsupported';   Arch='arm64'; Pub='true';  Os='win';   NativeLib='Shared'; Obj=$YyX64;   TargetA='UnityNativeAotYYThunks'; ExpectExit='zero';    Kind='none' }
    [pscustomobject]@{ Id='staticUcrt';          Arch='x64';   Pub='true';  Os='win';   NativeLib='Shared'; Obj='';       TargetA='UnityNativeAotStaticUcrt'; ExpectExit='zero';  Kind='ucrt' }
    [pscustomobject]@{ Id='x64-yy-plus-ucrt';    Arch='x64';   Pub='true';  Os='win';   NativeLib='Shared'; Obj=$YyX64;   TargetA='UnityNativeAotYYThunks'; TargetB='UnityNativeAotStaticUcrt'; ExpectExit='zero'; Kind='yy-x64+ucrt' }
)

function Invoke-Probe {
    param([pscustomobject]$Case, [string]$MsBuildPath)
    $msBuild = $MsBuildPath
    $probeOut = Join-Path $WorkDir ("capture-" + $Case.Id + ".txt")
    Remove-Item -LiteralPath $probeOut -ErrorAction SilentlyContinue
    $taskArgs = @(
        $ProbeProj,
        '/nologo', '/v:m', '/t:CaptureFlags',
        "/p:RealTargets=$RealTargets",
        "/p:ProbeTargetA=$($Case.TargetA)",
        "/p:ProbeTargetB=$($Case.TargetB)",
        "/p:ProbeOut=$probeOut",
        "/p:PublishAot=$($Case.Pub)",
        "/p:_targetOS=$($Case.Os)",
        "/p:_targetArchitecture=$($Case.Arch)",
        "/p:NativeLib=$($Case.NativeLib)",
        "/p:UnityYYThunksObject=$($Case.Obj)"
    )
    $output = & $msBuild @taskArgs 2>&1 | Out-String
    $exit = $LASTEXITCODE
    $lines = @()
    if (Test-Path -LiteralPath $probeOut) {
        $lines = @(Get-Content -LiteralPath $probeOut | Where-Object { $_ -ne '' })
    }
    return [pscustomobject]@{ Exit=$exit; Output=$output; Lines=$lines }
}

function Test-SortedEqual {
    param([string[]]$Actual, [string[]]$Expected)
    if ($Actual.Count -ne $Expected.Count) { return $false }
    $a = $Actual | Sort-Object
    $b = $Expected | Sort-Object
    for ($i = 0; $i -lt $a.Count; $i++) { if ($a[$i] -cne $b[$i]) { return $false } }
    return $true
}

$failures = 0
foreach ($case in $Cases) {
    $r = Invoke-Probe -Case $case -MsBuildPath $MsBuild
    $problems = New-Object System.Collections.Generic.List[string]

    if ($case.ExpectExit -eq 'zero' -and $r.Exit -ne 0) {
        $problems.Add("expected exit 0, got $($r.Exit)")
    }
    if ($case.ExpectExit -eq 'nonzero' -and $r.Exit -eq 0) {
        $problems.Add('expected nonzero exit, got 0')
    }

    switch ($case.Kind) {
        'error' {
            if ($r.Output -notmatch 'does not exist') {
                $problems.Add('expected a missing-object diagnostic, none found')
            }
        }
        'yy-x64' {
            if (-not (Test-SortedEqual -Actual $r.Lines -Expected $FlagX64)) {
                $problems.Add('x64 YY flags mismatch')
            }
        }
        'yy-x86' {
            if (-not (Test-SortedEqual -Actual $r.Lines -Expected $FlagX86)) {
                $problems.Add('x86 YY flags mismatch')
            }
        }
        'none' {
            if ($r.Lines.Count -ne 0) {
                $problems.Add("expected no LinkerArg items, got $($r.Lines.Count)")
            }
        }
        'ucrt' {
            if (-not (Test-SortedEqual -Actual $r.Lines -Expected $FlagUcrt)) {
                $problems.Add('staticUCRT flags mismatch (must keep libucrt pair, no dynamic ucrt)')
            }
        }
        'yy-x64+ucrt' {
            if (-not (Test-SortedEqual -Actual $r.Lines -Expected ($FlagX64 + $FlagUcrt))) {
                $problems.Add('combined x64 YY + staticUCRT flags mismatch')
            }
        }
    }

    if ($problems.Count -eq 0) {
        Write-Host ("PASS  {0,-20} exit={1} lines={2}" -f $case.Id, $r.Exit, $r.Lines.Count)
    }
    else {
        $failures++
        Write-Host ("FAIL  {0,-20} exit={1} :: {2}" -f $case.Id, $r.Exit, ($problems -join '; '))
        ($r.Output -split "`n" | Where-Object { $_ -match 'error|FAIL' } | Select-Object -First 3) | ForEach-Object { Write-Host "        $_" }
    }
}

if ($failures -gt 0) {
    Write-Host "DECISION-TABLE: $failures/$($Cases.Count) cases failed"
    exit 1
}
Write-Host "DECISION-TABLE: all $($Cases.Count) cases passed"
exit 0
