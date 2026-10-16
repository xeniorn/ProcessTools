<#
.SYNOPSIS
    Writes a SMALL dump of a running process (Windows): stacks, modules, thread info and the memory map, no heap contents.
.DESCRIPTION
    A few MB to a few tens of MB however large the process is, in seconds. Shows what every thread was doing and how the
    address space is laid out (which regions are huge), which is usually what you need for a runaway process. Use -Full only
    when you have room for the whole process memory. Same flags the Xeniorn.ProcessTools library uses. Works on Windows PowerShell 5.1 and PowerShell 7.
    On Linux use the .NET tool:  createdump --normal --name out.dmp <pid>  (managed processes only).
.EXAMPLE
    ./New-ProcessDump.ps1 -Id 1234 -OutFile D:/dumps/spectronaut.dmp
.EXAMPLE
    ./New-ProcessDump.ps1 -Name Spectronaut -OutDir D:/dumps -WithHandles
#>
#Requires -Version 3.0
[CmdletBinding(DefaultParameterSetName = 'ById')]
param(
    [Parameter(ParameterSetName = 'ById', Mandatory)] [int]$Id,
    [Parameter(ParameterSetName = 'ByName', Mandatory)] [string]$Name,
    [string]$OutFile,
    [string]$OutDir = (Get-Location).Path,
    [switch]$WithHandles,
    [switch]$Full
)

if ([Environment]::OSVersion.Platform -ne [PlatformID]::Win32NT) { throw 'This script is Windows only. On Linux: createdump --normal --name <file> <pid>' }

if ($PSCmdlet.ParameterSetName -eq 'ByName') {
    $found = @(Get-Process -Name ($Name -replace '\.exe$', '') -ErrorAction SilentlyContinue)
    if ($found.Count -eq 0) { throw "No process named '$Name'." }
    if ($found.Count -gt 1) { throw "More than one process named '$Name' ($($found.Id -join ', ')); use -Id." }
    $Id = $found[0].Id
}
$process = Get-Process -Id $Id
if (-not $OutFile) { $OutFile = Join-Path $OutDir ('{0}_{1}_{2:yyyyMMdd_HHmmss}Z.dmp' -f $process.ProcessName, $Id, (Get-Date).ToUniversalTime()) }
New-Item -ItemType Directory -Force -Path (Split-Path -Parent ([IO.Path]::GetFullPath($OutFile))) | Out-Null

Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
public static class ProcessToolsDump {
    [DllImport("dbghelp.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool MiniDumpWriteDump(IntPtr process, uint processId, SafeFileHandle file, uint dumpType, IntPtr e, IntPtr u, IntPtr c);
}
'@ -ErrorAction SilentlyContinue

# MINIDUMP_TYPE: 0x20 unloaded modules, 0x100 process thread data, 0x800 full memory info, 0x1000 thread info, 0x4 handle data, 0x2 full memory
$type = 0x1000 -bor 0x20 -bor 0x100 -bor 0x800
if ($WithHandles) { $type = $type -bor 0x4 }
if ($Full) { $type = $type -bor 0x2 -bor 0x4 -bor 0x20000 }

$file = [IO.File]::Open($OutFile, 'Create', 'ReadWrite', 'None')
try {
    $ok = [ProcessToolsDump]::MiniDumpWriteDump($process.Handle, [uint32]$Id, $file.SafeFileHandle, [uint32]$type, [IntPtr]::Zero, [IntPtr]::Zero, [IntPtr]::Zero)
    $err = [Runtime.InteropServices.Marshal]::GetLastWin32Error()
} finally { $file.Dispose() }

if (-not $ok) {
    Remove-Item $OutFile -ErrorAction SilentlyContinue
    throw ('MiniDumpWriteDump failed: {0}' -f ((New-Object ComponentModel.Win32Exception $err).Message))
}
$info = Get-Item $OutFile
'{0} ({1:N1} MiB)' -f $info.FullName, ($info.Length / 1MB)
