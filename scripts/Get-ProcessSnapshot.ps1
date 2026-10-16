<#
.SYNOPSIS
    One-shot: what are the processes matching X using right now?
.DESCRIPTION
    Selects processes by PID, name, path or service (any combination = union), optionally with all their descendants,
    and prints the requested metrics once. Windows PowerShell 5.1+ and PowerShell 7+, Windows and Linux. No dependencies.
.EXAMPLE
    ./Get-ProcessSnapshot.ps1 -Name Spectronaut
.EXAMPLE
    ./Get-ProcessSnapshot.ps1 -Path 'C:/Program Files/Biognosys/*' -IncludeDescendants -Metric PrivateBytes,ThreadCount,DescendantCount
.EXAMPLE
    ./Get-ProcessSnapshot.ps1 -Service MyService -CpuWindowSeconds 2 -Metric CpuPercent,WorkingSetBytes
.PARAMETER Metric
    CpuPercent, CpuSeconds, WorkingSetBytes, PrivateBytes, VirtualBytes, PeakWorkingSetBytes, PeakVirtualBytes,
    ThreadCount, HandleCount, ChildCount, DescendantCount. PrivateBytes is the commit charge: what exhausts the commit limit.
.PARAMETER CpuWindowSeconds
    CpuPercent needs a time span: the script waits this long between two readings. Default 1 second when CpuPercent is requested.
.PARAMETER Raw
    Output objects with numeric values (for piping into Sort-Object, Export-Csv, ...) instead of a formatted table.
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
    [double]$CpuWindowSeconds = 1,
    [switch]$Raw
)

. (Join-Path $PSScriptRoot 'ProcessToolsCommon.ps1')

if (-not ($Id -or $Name -or $Path -or $Service)) { throw 'Select something: -Id, -Name, -Path or -Service.' }

$processes = Resolve-ProcessSelection -Id $Id -Name $Name -Path $Path -Service $Service -IncludeDescendants:$IncludeDescendants
if (-not $processes) { Write-Warning 'No process matches.'; return }

$table = if ($Metric -contains 'ChildCount' -or $Metric -contains 'DescendantCount') { Get-ProcessTable } else { @{} }
$states = @{}
foreach ($p in $processes) { $states[$p.Id] = @{} }

if ($Metric -contains 'CpuPercent') {
    foreach ($p in $processes) { $null = Read-ProcessMetrics -Process $p -Metric @('CpuPercent') -ProcessTable $table -State $states[$p.Id] }
    Start-Sleep -Milliseconds ([int]($CpuWindowSeconds * 1000))
}

$rows = foreach ($p in $processes) {
    $values = Read-ProcessMetrics -Process $p -Metric $Metric -ProcessTable $table -State $states[$p.Id]
    if ($null -eq $values) { continue }
    $row = [ordered]@{ Id = $p.Id; Name = $p.ProcessName; Started = $(try { $p.StartTime.ToUniversalTime() } catch { $null }) }
    foreach ($m in $Metric) { $row[$m] = if ($values.Contains($m)) { $values[$m] } else { $null } }
    [pscustomobject]$row
}

if ($Raw) { $rows; return }
$rows | ForEach-Object {
    $display = [ordered]@{ Id = $_.Id; Name = $_.Name; Started = $_.Started }
    foreach ($m in $Metric) { $display[$m] = if ($null -ne $_.$m) { Format-Metric $m $_.$m } else { 'n/a' } }
    [pscustomobject]$display
} | Format-Table -AutoSize
