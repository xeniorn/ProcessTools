using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Microsoft.Win32.SafeHandles;

namespace Xeniorn.ProcessTools.Dumping;

/// <summary>Windows dumps through <c>MiniDumpWriteDump</c> (dbghelp), which suspends the process only for the time it takes to read it.</summary>
[SupportedOSPlatform("windows")]
internal sealed class WindowsProcessDumper : IProcessDumper
{
    // MINIDUMP_TYPE flags
    private const uint WithFullMemory = 0x2;
    private const uint WithHandleData = 0x4;
    private const uint WithUnloadedModules = 0x20;
    private const uint WithProcessThreadData = 0x100;
    private const uint WithFullMemoryInfo = 0x800;
    private const uint WithThreadInfo = 0x1000;
    private const uint IgnoreInaccessibleMemory = 0x20000;

    // Normal (0) already holds the stacks and the module list. The additions are small: thread/TEB data, the memory map
    // (one record per region) and unloaded modules. Nothing here copies heap contents.
    private const uint Small = WithThreadInfo | WithUnloadedModules | WithProcessThreadData | WithFullMemoryInfo;

    /// <inheritdoc />
    public bool Supports(DumpKind kind) => true;

    /// <inheritdoc />
    public DumpResult Dump(ProcessInfo process, string filePath, DumpKind kind)
    {
        var type = kind switch
        {
            DumpKind.Small => Small,
            DumpKind.SmallWithHandles => Small | WithHandleData,
            DumpKind.Full => WithFullMemory | WithHandleData | WithThreadInfo | WithUnloadedModules | WithFullMemoryInfo | IgnoreInaccessibleMemory,
            _ => Small,
        };

        try
        {
            using var target = Process.GetProcessById(process.ProcessId);
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(filePath))!);
            using var file = new FileStream(filePath, FileMode.Create, FileAccess.ReadWrite, FileShare.None);
            if (!MiniDumpWriteDump(target.SafeHandle, (uint)process.ProcessId, file.SafeFileHandle, type, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero))
            {
                var error = Marshal.GetLastWin32Error();
                file.Dispose();
                TryDelete(filePath);
                return DumpResult.Fail($"MiniDumpWriteDump failed: {new System.ComponentModel.Win32Exception(error).Message} (0x{error:X8})");
            }
            file.Flush();
            return DumpResult.Ok(file.Length);
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception
                                       or UnauthorizedAccessException or IOException)
        {
            TryDelete(filePath);
            return DumpResult.Fail(ex.Message);
        }
    }

    private static void TryDelete(string path)
    {
        try { File.Delete(path); } catch (IOException) { /* leave it */ } catch (UnauthorizedAccessException) { /* leave it */ }
    }

    [DllImport("dbghelp.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool MiniDumpWriteDump(SafeProcessHandle process, uint processId, SafeFileHandle file, uint dumpType,
        IntPtr exceptionParam, IntPtr userStreamParam, IntPtr callbackParam);
}
