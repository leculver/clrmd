# run-stress.ps1
#
# Outer hard-loop driver for the ClrMD parallel stress test (stress.exe).
#
# Responsibilities:
#   - Validate every .dmp file in C:\work\dumps using `stress.exe --validate`.
#     A dump where no CLR creates a runtime is DELETED from c:\work\dumps.
#     A dump that OOMs is added to the blocklist (NOT deleted, NOT retried).
#   - Round-robin through validated dumps; pick next, run stress.exe.
#     Per-run timeout: 15 min if dump >= 10 GB else 7.5 min.
#   - Set DOTNET_DbgEnableMiniDump=1 (type 4) so any FailFast captures a dump.
#   - Track crash-free streak + the 7-AM-local termination anchor in
#     C:\work\stress-state\state.json (anchor captured ONCE at script start
#     and preserved across reboots / script restarts).
#   - Termination (whichever comes LATER): now_local >= sevenAmTargetLocal
#     AND crash-free streak >= 8h.
#
# Usage:
#   ./run-stress.ps1                     # normal long-running mode
#   ./run-stress.ps1 -ValidateOnly       # just refresh .stress-validation.json
#   ./run-stress.ps1 -ResetState         # reset state.json (lose streak)
#   ./run-stress.ps1 -StreakHours 8      # tune streak target (default 8)
#   ./run-stress.ps1 -Reader standard    # validate/stress the standard reader instead of lockfree
param(
    [switch]$ValidateOnly,
    [switch]$ResetState,
    [int]$StreakHours = 8,
    [int]$ThreadsOverride = 0,
    [ValidateSet('standard','lockfree')]
    [string]$Reader = 'lockfree'
)

$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'

# -------- paths --------
$DumpsDir       = 'C:\work\dumps'
$StateDir       = 'C:\work\stress-state'
$CrashesDir     = Join-Path $StateDir 'crashes'
$LogFile        = Join-Path $StateDir 'stress.log'
$StateFile      = Join-Path $StateDir 'state.json'
$StatsFile      = Join-Path $StateDir 'stats.jsonl'
$ValidationFile = Join-Path $DumpsDir '.stress-validation.json'

$RepoRoot = Resolve-Path (Join-Path $PSScriptRoot '..\..\..')
$StressExe = Join-Path $RepoRoot 'artifacts\bin\ParallelStressTest\Release\net10.0\stress.exe'

New-Item -ItemType Directory -Force -Path $StateDir, $CrashesDir | Out-Null

# -------- helpers --------
function Write-Log {
    param([string]$Message)
    $ts = (Get-Date).ToString('yyyy-MM-dd HH:mm:ss.fff')
    $line = "$ts  $Message"
    Write-Host $line
    Add-Content -LiteralPath $LogFile -Value $line
}

function Set-DumpEnv {
    $env:DOTNET_DbgEnableMiniDump            = '1'
    $env:DOTNET_DbgMiniDumpType              = '4'
    $env:DOTNET_DbgMiniDumpName              = (Join-Path $CrashesDir '%e.%p.%t.dmp')
    $env:DOTNET_CreateDumpDiagnostics        = '1'
    $env:DOTNET_CreateDumpVerboseDiagnostics = '1'
    $env:DOTNET_CreateDumpLogToFile          = (Join-Path $CrashesDir 'createdump.log')
}

function Initialize-State {
    $now = Get-Date
    $todaySeven = [datetime]::new($now.Year, $now.Month, $now.Day, 7, 0, 0)
    if ($todaySeven -le $now) {
        $todaySeven = $todaySeven.AddDays(1)
    }
    return [pscustomobject]@{
        scriptStartUtc      = (Get-Date).ToUniversalTime().ToString('o')
        sevenAmTargetLocal  = $todaySeven.ToString('o')
        streakStartUtc      = (Get-Date).ToUniversalTime().ToString('o')
        lastCrashUtc        = $null
        completedRuns       = 0
        cleanRuns           = 0
        crashCount          = 0
        deletedDumps        = 0
        oomDumps            = 0
        cursor              = 0    # round-robin index into validated list
    }
}

function Load-State {
    if (-not (Test-Path $StateFile) -or $ResetState) {
        $s = Initialize-State
        Save-State $s
        Write-Log "[state] initialized (target 7AM = $($s.sevenAmTargetLocal))"
        return $s
    }
    $s = Get-Content $StateFile -Raw | ConvertFrom-Json
    return $s
}

function Save-State {
    param($State)
    $State | ConvertTo-Json -Depth 10 | Set-Content -LiteralPath $StateFile -Encoding UTF8
}

function Load-Validation {
    if (-not (Test-Path $ValidationFile)) {
        return [pscustomobject]@{
            version   = 1
            entries   = [pscustomobject]@{}
            blocklist = [pscustomobject]@{}
        }
    }
    return Get-Content $ValidationFile -Raw | ConvertFrom-Json
}

function Save-Validation {
    param($Validation)
    $Validation | ConvertTo-Json -Depth 10 | Set-Content -LiteralPath $ValidationFile -Encoding UTF8
}

function Get-PropNames {
    param($Obj)
    if ($null -eq $Obj) { return @() }
    return $Obj.PSObject.Properties | ForEach-Object { $_.Name }
}

function Has-Prop {
    param($Obj, [string]$Name)
    if ($null -eq $Obj) { return $false }
    return [bool]($Obj.PSObject.Properties | Where-Object { $_.Name -eq $Name })
}

function Set-Prop {
    param($Obj, [string]$Name, $Value)
    if (Has-Prop $Obj $Name) {
        $Obj.$Name = $Value
    } else {
        $Obj | Add-Member -NotePropertyName $Name -NotePropertyValue $Value -Force
    }
}

function Update-ValidationList {
    param($Validation)

    $dumps = Get-ChildItem -LiteralPath $DumpsDir -Filter '*.dmp' -File -ErrorAction SilentlyContinue
    $entriesDirty = $false

    foreach ($dump in $dumps) {
        $name = $dump.Name
        if (Has-Prop $Validation.blocklist $name) {
            continue   # already blocklisted, skip
        }

        $existing = $null
        if (Has-Prop $Validation.entries $name) { $existing = $Validation.entries.$name }
        if ($null -ne $existing -and $existing.fileSize -eq $dump.Length -and (Has-Prop $existing 'reader') -and $existing.reader -eq $Reader) {
            continue   # already validated with matching size and reader mode
        }

        Write-Log "[validate] $name ($([math]::Round($dump.Length / 1GB, 2)) GB) reader=$Reader"
        Set-DumpEnv   # validation reads dump too; capture failures
        $output = & $StressExe --validate $dump.FullName --reader $Reader 2>&1 | Out-String
        $exit = $LASTEXITCODE
        $output = $output.Trim()
        Write-Log "[validate] exit=$exit"
        Write-Log "[validate] $output"

        switch ($exit) {
            0 {
                $entry = [pscustomobject]@{
                    fileSize    = $dump.Length
                    reader      = $Reader
                    validatedAt = (Get-Date).ToUniversalTime().ToString('o')
                    output      = $output
                }
                Set-Prop $Validation.entries $name $entry
                $entriesDirty = $true
            }
            2 {
                Write-Log "[validate] no working CLR; DELETING $($dump.FullName)"
                Remove-Item -LiteralPath $dump.FullName -Force -ErrorAction SilentlyContinue
                # purge any prior entry
                if (Has-Prop $Validation.entries $name) {
                    $Validation.entries.PSObject.Properties.Remove($name)
                    $entriesDirty = $true
                }
            }
            3 {
                Write-Log "[validate] OOM; blocklisting $name"
                Set-Prop $Validation.blocklist $name ([pscustomobject]@{
                    reason = 'oom-during-validation'
                    at     = (Get-Date).ToUniversalTime().ToString('o')
                })
                $entriesDirty = $true
            }
            default {
                Write-Log "[validate] unexpected exit=$exit; treating as bad dump (blocklisting)"
                Set-Prop $Validation.blocklist $name ([pscustomobject]@{
                    reason = "validation-exit-$exit"
                    at     = (Get-Date).ToUniversalTime().ToString('o')
                })
                $entriesDirty = $true
            }
        }

        if ($entriesDirty) { Save-Validation $Validation }
    }

    # Also drop entries whose dump file no longer exists
    foreach ($name in (Get-PropNames $Validation.entries)) {
        $path = Join-Path $DumpsDir $name
        if (-not (Test-Path -LiteralPath $path)) {
            $Validation.entries.PSObject.Properties.Remove($name)
            $entriesDirty = $true
        }
    }
    if ($entriesDirty) { Save-Validation $Validation }
}

function Get-ValidatedDumps {
    param($Validation)
    $names = Get-PropNames $Validation.entries | Sort-Object
    $paths = @()
    foreach ($n in $names) {
        $p = Join-Path $DumpsDir $n
        if (Test-Path -LiteralPath $p) { $paths += $p }
    }
    return ,$paths
}

function Should-Terminate {
    param($State)
    $now = Get-Date
    $sevenAm = [datetime]::Parse($State.sevenAmTargetLocal)
    $streakStart = [datetime]::Parse($State.streakStartUtc).ToLocalTime()
    $streakElapsed = $now - $streakStart
    return ($now -ge $sevenAm) -and ($streakElapsed.TotalSeconds -ge ($StreakHours * 3600))
}

function Format-StreakStatus {
    param($State)
    $now = Get-Date
    $sevenAm = [datetime]::Parse($State.sevenAmTargetLocal)
    $streakStart = [datetime]::Parse($State.streakStartUtc).ToLocalTime()
    $streakElapsed = $now - $streakStart
    $untilSevenAm = $sevenAm - $now
    return "streak={0:hh\:mm\:ss} until-7am={1:hh\:mm\:ss}" -f $streakElapsed, $untilSevenAm
}

# -------- main --------

if (-not (Test-Path $StressExe)) {
    Write-Log "[fatal] stress.exe not found at $StressExe -- build with: dotnet build src\LongRunningTests\ParallelStressTest\ParallelStressTest.csproj -c Release"
    exit 1
}

Write-Log "================================================================"
Write-Log "[boot] run-stress.ps1 starting"
Write-Log "[boot] stressExe = $StressExe"
Write-Log "[boot] dumps     = $DumpsDir"
Write-Log "[boot] crashes   = $CrashesDir"
Write-Log "[boot] state     = $StateFile"
Write-Log "[boot] stats     = $StatsFile"
Write-Log "[boot] reader    = $Reader"

$state = Load-State
$validation = Load-Validation

Write-Log "[boot] target-7am-local = $($state.sevenAmTargetLocal)"
Write-Log "[boot] streak-since     = $($state.streakStartUtc)"
Write-Log "[boot] streak-target    = ${StreakHours}h"
Write-Log "[boot] " + (Format-StreakStatus $state)

Update-ValidationList -Validation $validation

if ($ValidateOnly) {
    $count = (Get-PropNames $validation.entries).Count
    $blocked = (Get-PropNames $validation.blocklist).Count
    Write-Log "[validate-only] done: $count valid, $blocked blocklisted"
    exit 0
}

# Main hard loop
while ($true) {
    # Refresh validation in case new dumps appeared.
    Update-ValidationList -Validation $validation

    $dumps = Get-ValidatedDumps -Validation $validation
    if ($dumps.Count -eq 0) {
        Write-Log "[fatal] no validated dumps available; exiting"
        exit 2
    }

    if ($state.cursor -ge $dumps.Count) { $state.cursor = 0 }
    $dumpPath = $dumps[$state.cursor]
    $state.cursor = ($state.cursor + 1) % $dumps.Count
    Save-State $state

    $size = (Get-Item -LiteralPath $dumpPath).Length
    if ($size -ge 10GB) { $timeoutSec = 15 * 60 } else { $timeoutSec = 450 }
    $sizeGb = [math]::Round($size / 1GB, 2)

    Write-Log "----------------------------------------------------------------"
    Write-Log "[run]  dump=$dumpPath size=${sizeGb}GB timeout=${timeoutSec}s"
    Write-Log "[run]  " + (Format-StreakStatus $state)

    Set-DumpEnv
    $crashesBefore = @(Get-ChildItem -LiteralPath $CrashesDir -Filter '*.dmp' -ErrorAction SilentlyContinue).Count

    $startUtc = (Get-Date).ToUniversalTime()
    $argList = @($dumpPath, '--timeout', $timeoutSec, '--stats-file', $StatsFile, '--reader', $Reader)
    if ($ThreadsOverride -gt 0) { $argList += '--threads'; $argList += $ThreadsOverride }

    & $StressExe @argList
    $exitCode = $LASTEXITCODE
    $endUtc = (Get-Date).ToUniversalTime()
    $crashesAfter = @(Get-ChildItem -LiteralPath $CrashesDir -Filter '*.dmp' -ErrorAction SilentlyContinue).Count
    $newCrash = $crashesAfter - $crashesBefore

    $state.completedRuns += 1
    $dumpName = Split-Path $dumpPath -Leaf

    switch ($exitCode) {
        0 {
            $state.cleanRuns += 1
            Write-Log "[ok]   $dumpName exit=0 newDumps=$newCrash"
        }
        2 {
            # stress.exe says no working CLR. Delete dump and refresh validation.
            Write-Log "[del]  $dumpName exit=2 (no-working-clr); DELETING dump"
            Remove-Item -LiteralPath $dumpPath -Force -ErrorAction SilentlyContinue
            if (Has-Prop $validation.entries $dumpName) {
                $validation.entries.PSObject.Properties.Remove($dumpName)
                Save-Validation $validation
            }
            $state.deletedDumps += 1
        }
        3 {
            # OOM. Blocklist.
            Write-Log "[oom]  $dumpName exit=3 (oom); blocklisting"
            Set-Prop $validation.blocklist $dumpName ([pscustomobject]@{
                reason = 'oom-during-stress'
                at     = (Get-Date).ToUniversalTime().ToString('o')
            })
            if (Has-Prop $validation.entries $dumpName) {
                $validation.entries.PSObject.Properties.Remove($dumpName)
            }
            Save-Validation $validation
            $state.oomDumps += 1
        }
        default {
            # Anything else is a crash / FailFast / unexpected exit.
            Write-Log "[CRASH] $dumpName exit=$exitCode newDumps=$newCrash -- streak RESET"
            $state.crashCount += 1
            $state.lastCrashUtc = (Get-Date).ToUniversalTime().ToString('o')
            $state.streakStartUtc = (Get-Date).ToUniversalTime().ToString('o')
        }
    }

    Save-State $state

    if (Should-Terminate -State $state) {
        Write-Log "================================================================"
        Write-Log "[SUCCESS] both termination conditions met:"
        Write-Log "  now >= sevenAmTargetLocal ($($state.sevenAmTargetLocal))"
        Write-Log "  streak >= ${StreakHours}h"
        Write-Log "  runs=$($state.completedRuns) clean=$($state.cleanRuns) crashes=$($state.crashCount) deleted=$($state.deletedDumps) oom=$($state.oomDumps)"
        Write-Log "================================================================"
        exit 0
    }
}
