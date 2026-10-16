<#
.SYNOPSIS
    Track chosen metrics of chosen processes over time: sample every few seconds, summarise every period.
.DESCRIPTION
    Samples at -IntervalSeconds and, every -PeriodSeconds, writes one row per process and metric (all times UTC, console lines marked Z) with the requested
    aggregations (Min, Max, Mean, Median, StdDev, Rms, Last), optionally with the raw samples of the period.
    Several instances of the same program are tracked independently (key: PID + start time); instances that start
    later are picked up; start and exit are reported. Output goes to the console and optionally to a CSV file.
    The quick, hands-on version of the Xeniorn.ProcessTools library (same metric and aggregation names). Ctrl+C stops it
    and writes the period in progress. Windows PowerShell 5.1+ and PowerShell 7+, Windows and Linux. No dependencies.
.EXAMPLE
    ./Watch-Process.ps1 -Name Spectronaut -Metric PrivateBytes,WorkingSetBytes,ThreadCount -IntervalSeconds 10 -PeriodSeconds 60 -OutFile spectronaut.csv
.EXAMPLE
    ./Watch-Process.ps1 -Service MyService -Metric CpuPercent -Aggregate Min,Median,Max,StdDev -IncludeRaw
.PARAMETER Aggregate
    Min, Max, Mean, Median, StdDev, Rms, Last. Default Min, Mean, Max, Last.
.PARAMETER IncludeRaw
    Add a Raw column with the samples of the period, separated by ';'.
.PARAMETER DurationMinutes
    Stop by itself after this long. Default: run until Ctrl+C.
#>
#Requires -Version 3.0
[CmdletBinding()]
param(
    [int[]]$Id,
    [string[]]$Name,
    [string[]]$Path,
    [string[]]$Service,
    [switch]$IncludeDescendants,
    [ValidateSet('CpuPercent', 'CpuSeconds', 'WorkingSetBytes', 'PrivateBytes', 'VirtualBytes', 'PeakWorkingSetBytes', 'PeakVirtualBytes', 'ThreadCount', 'HandleCount', 'ChildCount', 'DescendantCount')]
    [string[]]$Metric = @('CpuPercent', 'WorkingSetBytes', 'PrivateBytes', 'VirtualBytes', 'ThreadCount', 'HandleCount'),
    [ValidateSet('Min', 'Max', 'Mean', 'Median', 'StdDev', 'Rms', 'Last')]
    [string[]]$Aggregate = @('Min', 'Mean', 'Max', 'Last'),
    [double]$IntervalSeconds = 5,
    [double]$PeriodSeconds = 60,
    [switch]$IncludeRaw,
    [string]$OutFile,
    [double]$DurationMinutes = 0
)

. (Join-Path $PSScriptRoot 'ProcessToolsCommon.ps1')

if (-not ($Id -or $Name -or $Path -or $Service)) { throw 'Select something: -Id, -Name, -Path or -Service.' }
if ($PeriodSeconds -lt $IntervalSeconds) { throw '-PeriodSeconds must be at least -IntervalSeconds.' }
$samplesPerPeriod = [Math]::Max(1, [int][Math]::Round($PeriodSeconds / $IntervalSeconds))
$needTable = $Metric -contains 'ChildCount' -or $Metric -contains 'DescendantCount'

$tracked = @{}     # key "pid@start" -> state
$csvHeader = $false

function Write-Period {
    param($T, [bool]$Partial)
    $rows = foreach ($m in $Metric) {
        $vals = [double[]]@($T.Window[$m])
        if ($vals.Count -eq 0) { continue }
        $row = [ordered]@{
            PeriodStartUtc = $T.First.ToString('o'); PeriodEndUtc = $T.Last.ToString('o'); Id = $T.Id; Name = $T.Name
            StartedUtc = $T.Started.ToString('o'); Metric = $m; Count = $vals.Count; Partial = $Partial
        }
        foreach ($a in $Aggregate) { $row[$a] = Get-Aggregate -Values $vals -Aggregation $a }
        if ($IncludeRaw) { $row['Raw'] = ($vals | ForEach-Object { $_.ToString([Globalization.CultureInfo]::InvariantCulture) }) -join ';' }
        [pscustomobject]$row
    }
    foreach ($r in $rows) {
        $line = "[{0}Z] {1}#{2} {3}{4}: " -f $r.PeriodEndUtc.Substring(11, 8), $r.Name, $r.Id, $r.Metric, $(if ($r.Partial) { ' (partial)' } else { '' })
        $line += ($Aggregate | ForEach-Object { "$_=$(Format-Metric $r.Metric $r.$_)" }) -join '  '
        Write-Host $line
    }
    if ($OutFile -and $rows) {
        $rows | Export-Csv -Path $OutFile -Append -NoTypeInformation -Encoding UTF8
    }
    foreach ($m in $Metric) { $T.Window[$m] = (New-Object 'System.Collections.Generic.List[double]') }
    $T.First = $null
}

$stopAt = if ($DurationMinutes -gt 0) { [DateTime]::UtcNow.AddMinutes($DurationMinutes) } else { [DateTime]::MaxValue }
$round = 0
Write-Host ("Tracking every {0}s, summarising every {1}s ({2} samples). Ctrl+C to stop." -f $IntervalSeconds, $PeriodSeconds, $samplesPerPeriod)
try {
    while ([DateTime]::UtcNow -lt $stopAt) {
        $tickStart = [Diagnostics.Stopwatch]::GetTimestamp()
        $now = [DateTime]::UtcNow
        $processes = Resolve-ProcessSelection -Id $Id -Name $Name -Path $Path -Service $Service -IncludeDescendants:$IncludeDescendants
        $table = if ($needTable) { Get-ProcessTable } else { @{} }

        $liveKeys = @{}
        foreach ($p in $processes) {
            $start = try { $p.StartTime.ToUniversalTime() } catch { $now }
            $key = "$($p.Id)@$($start.ToString('o'))"
            $liveKeys[$key] = $true
            if (-not $tracked.ContainsKey($key)) {
                $t = @{ Id = $p.Id; Name = $p.ProcessName; Started = $start; Process = $p; State = @{}; Window = @{}; First = $null; Last = $null }
                foreach ($m in $Metric) { $t.Window[$m] = (New-Object 'System.Collections.Generic.List[double]') }
                $tracked[$key] = $t
                Write-Host ("[{0:HH:mm:ss}Z] started: {1}#{2} (since {3:u}){4}" -f $now, $p.ProcessName, $p.Id, $start, $(if ($round -eq 0) { ' [already running]' } else { '' }))
            }
        }

        foreach ($key in @($tracked.Keys)) {
            $t = $tracked[$key]
            if (-not $liveKeys.ContainsKey($key)) {
                if ($t.First) { Write-Period -T $t -Partial $true }
                $code = try { $t.Process.Refresh(); if ($t.Process.HasExited) { $t.Process.ExitCode } else { $null } } catch { $null }
                Write-Host ("[{0:HH:mm:ss}Z] ended: {1}#{2}, exit code {3}" -f $now, $t.Name, $t.Id, $(if ($null -ne $code) { $code } else { 'unknown' }))
                $tracked.Remove($key)
                continue
            }
            $values = Read-ProcessMetrics -Process $t.Process -Metric $Metric -ProcessTable $table -State $t.State
            if ($null -eq $values) { continue }
            if (-not $t.First) { $t.First = $now }
            $t.Last = $now
            foreach ($m in $Metric) { if ($values.Contains($m)) { $t.Window[$m].Add([double]$values[$m]) } }
        }

        $round++
        if ($round % $samplesPerPeriod -eq 0) {
            foreach ($t in $tracked.Values) { if ($t.First) { Write-Period -T $t -Partial $false } }
        }

        $spent = Get-ElapsedSeconds -From $tickStart -To ([Diagnostics.Stopwatch]::GetTimestamp())
        if ($spent -lt $IntervalSeconds) { Start-Sleep -Milliseconds ([int](($IntervalSeconds - $spent) * 1000)) }
    }
}
finally {
    foreach ($t in $tracked.Values) { if ($t.First) { Write-Period -T $t -Partial $true } }
}
