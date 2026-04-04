<#
.SYNOPSIS
    Parses benchmark raw output and appends a new entry to performance_history.md.
.DESCRIPTION
    Windows-native equivalent of update-history.sh.
    Usage: powershell -File benchmarks/Update-History.ps1 <raw-output.txt> [commit-sha]
#>
param(
    [Parameter(Mandatory)][string]$RawFile,
    [string]$Commit = ""
)

$ErrorActionPreference = "Stop"
$ScriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path
$HistoryFile = Join-Path $ScriptDir "performance_history.md"
$RegressionThreshold = 0.1

# Unicode chars as variables to avoid encoding issues
$EMDASH = [char]0x2014   # em-dash
$UARROW = [char]0x2191   # up arrow
$DARROW = [char]0x2193   # down arrow
$MU     = [char]0x03BC   # Greek mu
$DELTA  = [char]0x0394   # Greek Delta
$WARN   = [string][char]0x26A0 + [string][char]0xFE0F  # warning sign
$CHECK  = [char]0x2705   # check mark
$PIN    = [char]::ConvertFromUtf32(0x1F4CC)  # pushpin

if (-not (Test-Path $RawFile)) {
    Write-Host "[!] Raw file not found: $RawFile"
    exit 1
}
if (-not (Test-Path $HistoryFile)) {
    Write-Host "[!] History file not found: $HistoryFile"
    exit 1
}

# Get commit SHA
if (-not $Commit) {
    try { $Commit = (git rev-parse HEAD 2>$null).Trim() } catch { $Commit = "unknown" }
}

$DateStr = (Get-Date).ToUniversalTime().ToString("yyyy-MM-dd HH:mm") + " UTC"
$RawLines = Get-Content $RawFile

# -- Extract environment info --
$envLine = $RawLines | Where-Object { $_ -match '\[BENCHMARK_ENV\]' } | Select-Object -First 1
$Runtime = if ($envLine -match 'runtime=(\S+)') { $Matches[1] } else { "unknown" }
$OsName  = if ($envLine -match 'os=(\S+)')      { $Matches[1] } else { "unknown" }
$Cores   = if ($envLine -match 'cores=(\S+)')    { $Matches[1] } else { "?" }
$EnvFP = "${Runtime}|${OsName}|${Cores}"

# -- Parse current benchmark results --
$benchmarks = @()
foreach ($line in $RawLines) {
    if ($line -match '\[BENCHMARK\] (B\S+)\s*\|\s*([\d.]+)\s*\|\s*([\d.]+)\s*\|\s*([\d.]+)\s*\|\s*(\S+)\s*\|\s*(\S+)') {
        $benchmarks += [PSCustomObject]@{
            Name  = $Matches[1]
            VM    = [double]$Matches[2]
            CS    = [double]$Matches[3]
            Ratio = [double]$Matches[4]
            Scale = $Matches[5]
            Instr = $Matches[6]
        }
    }
}

if ($benchmarks.Count -eq 0) {
    Write-Host "[!] No benchmark data found in $RawFile"
    exit 1
}

# -- Parse previous entry from history --
$historyContent = [System.IO.File]::ReadAllText($HistoryFile, [System.Text.Encoding]::UTF8)
$prevVM = @{}
$prevRatio = @{}
$PrevEnvFP = ""

if ($historyContent -match '(?m)^> \.NET (\S+) \| (\S+) \| (\d+) cores') {
    $PrevEnvFP = "$($Matches[1])|$($Matches[2])|$($Matches[3])"
}

$historyLines = $historyContent -split "`n"

# Find the FIRST block of B-rows after HISTORY_START
$inHistory = $false
$firstBlockDone = $false
$prevBlockLines = @()
foreach ($hl in $historyLines) {
    if ($hl -match 'HISTORY_START') { $inHistory = $true; continue }
    if (-not $inHistory) { continue }
    if ($hl -match '^\| (B\d\S+)\s*\|') {
        if (-not $firstBlockDone) {
            $prevBlockLines += $hl
        }
    } elseif ($prevBlockLines.Count -gt 0) {
        $firstBlockDone = $true
    }
}

foreach ($pl in $prevBlockLines) {
    if ($pl -match '^\|\s*(B\S+)\s*\|\s*([\d.]+)\s*\|\s*[\d.]+\s*\|\s*([\d.]+)x') {
        $prevVM[$Matches[1]] = [double]$Matches[2]
        $prevRatio[$Matches[1]] = [double]$Matches[3]
    }
}

# -- Determine if environment changed --
$envChanged = ($PrevEnvFP -ne "" -and $EnvFP -ne $PrevEnvFP)

# -- Build the new history entry --
$entry = [System.Collections.Generic.List[string]]::new()
$entry.Add("### ${DateStr} ${EMDASH} ``${Commit}``")
$entry.Add("")
$entry.Add("> .NET ${Runtime} | ${OsName} | ${Cores} cores")
$entry.Add("")
$entry.Add("| Benchmark | VM (${MU}s) | C# (${MU}s) | Ratio | ${DELTA} VM | ${DELTA} Ratio |")
$entry.Add("|-----------|---------|---------|-------|------|---------|")

$anyRegression = $false

foreach ($b in $benchmarks) {
    $dVM = $EMDASH
    $dR  = $EMDASH

    if ($envChanged) {
        $dVM = "${EMDASH} (env changed)"
        $dR  = "${EMDASH} (env changed)"
    } elseif ($prevVM.ContainsKey($b.Name)) {
        $dv = $b.VM - $prevVM[$b.Name]
        $pct = 0
        if ($prevVM[$b.Name] -gt 0) {
            $pct = [Math]::Abs($dv / $prevVM[$b.Name] * 100)
        }

        if ($dv -gt 0) {
            $dVM = "+{0:F1} (${UARROW}{1:F0}%)" -f $dv, $pct
        } elseif ($dv -lt 0) {
            $dVM = "{0:F1} (${DARROW}{1:F0}%)" -f $dv, $pct
        } else {
            $dVM = "0 (=)"
        }

        if ($prevRatio.ContainsKey($b.Name)) {
            $dr = $b.Ratio - $prevRatio[$b.Name]
            if ($dr -gt $RegressionThreshold) {
                $dR = "+{0:F2}x ${WARN}" -f $dr
            } elseif ($dr -gt 0) {
                $dR = "+{0:F2}x" -f $dr
            } elseif ($dr -lt (-$RegressionThreshold)) {
                $dR = "{0:F2}x ${CHECK}" -f $dr
            } elseif ($dr -lt 0) {
                $dR = "{0:F2}x" -f $dr
            } else {
                $dR = "="
            }

            if ($b.Ratio -gt ($prevRatio[$b.Name] * 1.1)) {
                $anyRegression = $true
            }
        }
    }

    $row = "| {0} | {1} | {2} | {3}x | {4} | {5} |" -f $b.Name, $b.VM, $b.CS, $b.Ratio, $dVM, $dR
    $entry.Add($row)
}

$entry.Add("")

if ($envChanged) {
    $entry.Add("${PIN} **New environment baseline**: environment changed from ``${PrevEnvFP}`` to ``${EnvFP}``. Deltas reset.")
    $entry.Add("")
} elseif ($anyRegression) {
    $entry.Add("${WARN} **Regression detected**: VM/C# ratio increased >10% on one or more benchmarks.")
    $entry.Add("")
}

# -- Insert into history file (after HISTORY_START marker) --
$histLines = [System.IO.File]::ReadAllLines($HistoryFile, [System.Text.Encoding]::UTF8)
$output = [System.Collections.Generic.List[string]]::new()
foreach ($hl in $histLines) {
    $output.Add($hl)
    if ($hl -match 'HISTORY_START') {
        $output.Add("")
        foreach ($el in $entry) {
            $output.Add($el)
        }
    }
}

[System.IO.File]::WriteAllLines($HistoryFile, $output.ToArray(), [System.Text.Encoding]::UTF8)
Write-Host "[*] History updated: $($benchmarks.Count) benchmarks recorded"
Write-Host "    Commit: $Commit"
Write-Host "    Date:   $DateStr"
if ($envChanged) {
    Write-Host "    NEW ENVIRONMENT BASELINE (deltas reset)"
} elseif ($anyRegression) {
    Write-Host "    REGRESSION DETECTED"
}
