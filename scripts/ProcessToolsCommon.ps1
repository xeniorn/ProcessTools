# Shared helpers for the ProcessTools scripts (dot-sourced; not meant to be run).
# Runs on Windows PowerShell 5.1 (also 3.0/4.0 as far as it was checked: no ::new(), no .NET 7+ APIs) and PowerShell 7+,
# on Windows and Linux.
# Metric and aggregation names match the C# library (Xeniorn.ProcessTools), so numbers from here and from the
# library are directly comparable.

Set-StrictMode -Version 2.0

# $IsWindows does not exist before PowerShell 6
$script:OnWindows = [Environment]::OSVersion.Platform -eq [PlatformID]::Win32NT

function Get-ElapsedSeconds {
    # Stopwatch.GetElapsedTime is .NET 7+; this works everywhere
    param([long]$From, [long]$To)
    ($To - $From) / [double][Diagnostics.Stopwatch]::Frequency
}

$script:AllMetrics = @(
    'CpuPercent', 'CpuSeconds', 'WorkingSetBytes', 'PrivateBytes', 'VirtualBytes', 'PeakWorkingSetBytes',
    'PeakVirtualBytes', 'ThreadCount', 'HandleCount', 'ChildCount', 'DescendantCount')
$script:AllAggregations = @('Min', 'Max', 'Mean', 'Median', 'StdDev', 'Rms', 'Last')

function Get-ProcessTable {
    # pid -> parent pid for every process
    $table = @{}
    if ($script:OnWindows) {
        foreach ($p in Get-CimInstance Win32_Process -Property ProcessId, ParentProcessId) { $table[[int]$p.ProcessId] = [int]$p.ParentProcessId }
    } else {
        foreach ($line in (& ps -eo 'pid=,ppid=')) {
            $f = $line.Trim() -split '\s+'
            if ($f.Count -ge 2) { $table[[int]$f[0]] = [int]$f[1] }
        }
    }
    $table
}

function Get-DescendantIds {
    param([hashtable]$Table, [int[]]$RootIds)
    $children = @{}
    foreach ($kv in $Table.GetEnumerator()) {
        if ($kv.Value -ne $kv.Key) {
            if (-not $children.ContainsKey($kv.Value)) { $children[$kv.Value] = (New-Object 'System.Collections.Generic.List[int]') }
            $children[$kv.Value].Add($kv.Key)
        }
    }
    $seen = New-Object 'System.Collections.Generic.HashSet[int]'
    $stack = New-Object 'System.Collections.Generic.Stack[int]'
    foreach ($r in $RootIds) { $stack.Push($r) }
    while ($stack.Count -gt 0) {
        $id = $stack.Pop()
        if ($children.ContainsKey($id)) {
            foreach ($c in $children[$id]) { if ($seen.Add($c)) { $stack.Push($c) } }
        }
    }
    , @($seen)
}

function Resolve-ProcessSelection {
    # Returns the live Process objects chosen by the selectors (union of all given). Descendants optionally included.
    param([int[]]$Id, [string[]]$Name, [string[]]$Path, [string[]]$Service, [switch]$IncludeDescendants)

    $selected = @{}
    foreach ($i in $Id) { try { $selected[$i] = [Diagnostics.Process]::GetProcessById($i) } catch { } }
    $all = $null
    if ($Name -or $Path) { $all = [Diagnostics.Process]::GetProcesses() }
    foreach ($pattern in $Name) {
        $bare = $pattern -replace '\.exe$', ''
        foreach ($p in $all) { if ($p.ProcessName -like $bare) { $selected[$p.Id] = $p } }
    }
    foreach ($pattern in $Path) {
        foreach ($p in $all) {
            $file = try { $p.MainModule.FileName } catch { $null }
            if ($file -and (($file -replace '\\', '/') -like ($pattern -replace '\\', '/'))) { $selected[$p.Id] = $p }
        }
    }
    foreach ($pattern in $Service) {
        if ($script:OnWindows) {
            foreach ($s in Get-CimInstance Win32_Service -Filter "State='Running'") {
                if (($s.Name -like $pattern -or $s.DisplayName -like $pattern) -and $s.ProcessId) {
                    try { $selected[[int]$s.ProcessId] = [Diagnostics.Process]::GetProcessById([int]$s.ProcessId) } catch { }
                }
            }
        } else {
            $units = & systemctl list-units --type=service --state=running --no-legend --plain 2>$null | ForEach-Object { ($_ -split '\s+')[0] }
            foreach ($u in $units) {
                if ($u -like $pattern -or ($u -replace '\.service$', '') -like $pattern) {
                    $main = [int](& systemctl show -p MainPID --value $u)
                    if ($main -gt 0) { try { $selected[$main] = [Diagnostics.Process]::GetProcessById($main) } catch { } }
                }
            }
        }
    }

    if ($IncludeDescendants -and $selected.Count -gt 0) {
        $table = Get-ProcessTable
        foreach ($d in (Get-DescendantIds -Table $table -RootIds @($selected.Keys))) {
            if (-not $selected.ContainsKey($d)) { try { $selected[$d] = [Diagnostics.Process]::GetProcessById($d) } catch { } }
        }
    }
    @($selected.Values)
}

function Read-ProcessMetrics {
    # One reading of the requested metrics for one process; $State carries the previous CPU sample for CpuPercent.
    param([Diagnostics.Process]$Process, [string[]]$Metric, [hashtable]$ProcessTable, [hashtable]$State)

    $v = [ordered]@{}
    try {
        $Process.Refresh()
        if ($Process.HasExited) { return $null }
    } catch { return $null }
    $now = [Diagnostics.Stopwatch]::GetTimestamp()
    foreach ($m in $Metric) {
        try {
            switch ($m) {
                'WorkingSetBytes'     { $v[$m] = [double]$Process.WorkingSet64 }
                'PrivateBytes'        { $v[$m] = [double]$Process.PrivateMemorySize64 }
                'VirtualBytes'        { $v[$m] = [double]$Process.VirtualMemorySize64 }
                'PeakWorkingSetBytes' { $v[$m] = [double]$Process.PeakWorkingSet64 }
                'PeakVirtualBytes'    { $v[$m] = [double]$Process.PeakVirtualMemorySize64 }
                'ThreadCount'         { $v[$m] = [double]$Process.Threads.Count }
                'HandleCount'         {
                    $v[$m] = if ($script:OnWindows) { [double]$Process.HandleCount } else { [double]@(Get-ChildItem "/proc/$($Process.Id)/fd" -ErrorAction Stop).Count }
                }
                'CpuSeconds'          { $v[$m] = $Process.TotalProcessorTime.TotalSeconds }
                'CpuPercent'          {
                    $cpu = $Process.TotalProcessorTime.TotalSeconds
                    if ($State.ContainsKey('cpu')) {
                        $elapsed = (Get-ElapsedSeconds -From $State['ts'] -To $now)
                        if ($elapsed -gt 0) { $v[$m] = [Math]::Max(0, ($cpu - $State['cpu']) / $elapsed * 100) }
                    }
                    $State['cpu'] = $cpu
                    $State['ts'] = $now
                }
                'ChildCount'          { $v[$m] = [double]@($ProcessTable.GetEnumerator() | Where-Object { $_.Value -eq $Process.Id -and $_.Key -ne $Process.Id }).Count }
                'DescendantCount'     { $v[$m] = [double](Get-DescendantIds -Table $ProcessTable -RootIds @($Process.Id)).Count }
            }
        } catch { }   # not readable for this process: leave the metric out, like the library does
    }
    $v
}

function Get-Aggregate {
    param([double[]]$Values, [string]$Aggregation)
    $n = $Values.Count
    switch ($Aggregation) {
        'Min'    { ($Values | Measure-Object -Minimum).Minimum }
        'Max'    { ($Values | Measure-Object -Maximum).Maximum }
        'Mean'   { ($Values | Measure-Object -Average).Average }
        'Last'   { $Values[-1] }
        'Median' {
            $s = $Values | Sort-Object
            if ($n % 2 -eq 1) { $s[[int](($n - 1) / 2)] } else { ($s[[int]($n / 2) - 1] + $s[[int]($n / 2)]) / 2 }
        }
        'StdDev' {
            if ($n -lt 2) { 0.0 } else {
                $mean = ($Values | Measure-Object -Average).Average
                $ss = 0.0; foreach ($x in $Values) { $ss += ($x - $mean) * ($x - $mean) }
                [Math]::Sqrt($ss / ($n - 1))
            }
        }
        'Rms'    { $ss = 0.0; foreach ($x in $Values) { $ss += $x * $x }; [Math]::Sqrt($ss / $n) }
    }
}

function Format-Metric {
    param([string]$Metric, [double]$Value)
    if ($Metric -like '*Bytes') {
        foreach ($unit in 'B', 'KiB', 'MiB', 'GiB', 'TiB') { if ([Math]::Abs($Value) -lt 1024 -or $unit -eq 'TiB') { return ('{0:N1} {1}' -f $Value, $unit) }; $Value /= 1024 }
    }
    if ($Metric -eq 'CpuPercent') { return ('{0:N1} %' -f $Value) }
    '{0:N1}' -f $Value
}
