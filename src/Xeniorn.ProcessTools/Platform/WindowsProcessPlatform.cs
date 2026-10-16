using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;
using Xeniorn.ProcessTools.Selection;

namespace Xeniorn.ProcessTools.Platform;

/// <summary>
/// Windows: process list and parent relations from a Toolhelp snapshot, paths and start times through limited-rights handles
/// (so they work for most other users' processes too), services from the Service Control Manager, I/O counters from
/// <c>GetProcessIoCounters</c>.
/// </summary>
[SupportedOSPlatform("windows")]
internal sealed class WindowsProcessPlatform : PortableProcessPlatform
{
    /// <inheritdoc />
    public override IReadOnlyList<ProcessInfo> ListProcesses()
    {
        var snapshot = NativeMethods.CreateToolhelp32Snapshot(NativeMethods.Th32csSnapProcess, 0);
        if (snapshot == NativeMethods.InvalidHandleValue)
            return base.ListProcesses();

        var result = new List<ProcessInfo>();
        try
        {
            var entry = new NativeMethods.ProcessEntry32 { Size = (uint)Marshal.SizeOf<NativeMethods.ProcessEntry32>() };
            if (!NativeMethods.Process32First(snapshot, ref entry))
                return result;
            do
            {
                var pid = (int)entry.ProcessId;
                var (path, start) = ReadPathAndStart(pid);
                var name = entry.ExeFile;
                if (name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
                    name = name[..^4];
                result.Add(new ProcessInfo(pid, (int)entry.ParentProcessId, name, path, start));
            }
            while (NativeMethods.Process32Next(snapshot, ref entry));
        }
        finally
        {
            NativeMethods.CloseHandle(snapshot);
        }
        return result;
    }

    /// <inheritdoc />
    public override IReadOnlyCollection<int> GetServiceProcessIds(string pattern)
    {
        var ids = new HashSet<int>();
        foreach (var (name, display, pid) in EnumerateRunningServices())
        {
            if (pid != 0 && (Glob.IsMatch(pattern, name) || Glob.IsMatch(pattern, display)))
                ids.Add(pid);
        }
        return ids;
    }

    /// <inheritdoc />
    protected override Func<Process, (double Read, double Write)?>? IoReader => static p =>
        NativeMethods.GetProcessIoCounters(p.SafeHandle, out var io) ? (io.ReadTransferCount, io.WriteTransferCount) : null;

    /// <inheritdoc />
    protected override Func<Process, double?>? HandleCountReader => static p => p.HandleCount;

    /// <inheritdoc />
    internal override ExitWatcher? CreateExitWatcher(int processId)
    {
        var handle = NativeMethods.OpenProcess(NativeMethods.ProcessQueryLimitedInformation | NativeMethods.Synchronize, false, (uint)processId);
        return handle == IntPtr.Zero ? null : new WindowsExitWatcher(handle);
    }

    private sealed class WindowsExitWatcher(IntPtr handle) : ExitWatcher
    {
        public override ProcessExitStatus? Poll()
        {
            // WaitForSingleObject, not the STILL_ACTIVE exit code: a process may legitimately exit with 259
            if (NativeMethods.WaitForSingleObject(handle, 0) != 0)
                return null;
            int? code = NativeMethods.GetExitCodeProcess(handle, out var raw) ? unchecked((int)raw) : null;
            DateTime? exitTime = NativeMethods.GetProcessTimes(handle, out _, out var exit, out _, out _) && exit > 0 ? DateTime.FromFileTimeUtc(exit) : null;
            return new ProcessExitStatus(code, exitTime);
        }

        public override void Dispose() => NativeMethods.CloseHandle(handle);
    }

    private static (string? Path, DateTime? Start) ReadPathAndStart(int pid)
    {
        if (pid is 0 or 4)
            return (null, null); // idle and system: no image, no handle
        var handle = NativeMethods.OpenProcess(NativeMethods.ProcessQueryLimitedInformation, false, (uint)pid);
        if (handle == IntPtr.Zero)
            return (null, null);
        try
        {
            string? path = null;
            var buffer = new StringBuilder(1024);
            var size = buffer.Capacity;
            if (NativeMethods.QueryFullProcessImageName(handle, 0, buffer, ref size))
                path = buffer.ToString(0, size);

            DateTime? start = null;
            if (NativeMethods.GetProcessTimes(handle, out var creation, out _, out _, out _))
                start = DateTime.FromFileTimeUtc(creation);
            return (path, start);
        }
        finally
        {
            NativeMethods.CloseHandle(handle);
        }
    }

    private static IEnumerable<(string Name, string Display, int Pid)> EnumerateRunningServices()
    {
        var scm = NativeMethods.OpenSCManager(null, null, NativeMethods.ScManagerEnumerateService);
        if (scm == IntPtr.Zero)
            yield break;
        var buffer = IntPtr.Zero;
        try
        {
            const int bufferSize = 256 * 1024;
            buffer = Marshal.AllocHGlobal(bufferSize);
            uint resume = 0;
            var entrySize = Marshal.SizeOf<NativeMethods.EnumServiceStatusProcess>();
            while (true)
            {
                var ok = NativeMethods.EnumServicesStatusEx(scm, NativeMethods.ScEnumProcessInfo, NativeMethods.ServiceWin32,
                    NativeMethods.ServiceActive, buffer, bufferSize, out _, out var returned, ref resume, null);
                var error = Marshal.GetLastWin32Error();
                if (!ok && error != NativeMethods.ErrorMoreData)
                    yield break;

                for (var i = 0; i < returned; i++)
                {
                    var item = Marshal.PtrToStructure<NativeMethods.EnumServiceStatusProcess>(buffer + i * entrySize);
                    yield return (Marshal.PtrToStringUni(item.ServiceName) ?? "", Marshal.PtrToStringUni(item.DisplayName) ?? "", (int)item.ProcessId);
                }

                if (ok)
                    yield break;
            }
        }
        finally
        {
            if (buffer != IntPtr.Zero)
                Marshal.FreeHGlobal(buffer);
            NativeMethods.CloseServiceHandle(scm);
        }
    }

    private static class NativeMethods
    {
        public static readonly IntPtr InvalidHandleValue = new(-1);
        public const uint Th32csSnapProcess = 0x2;
        public const uint ProcessQueryLimitedInformation = 0x1000;
        public const uint Synchronize = 0x100000;
        public const uint ScManagerEnumerateService = 0x4;
        public const uint ScEnumProcessInfo = 0;
        public const uint ServiceWin32 = 0x30;
        public const uint ServiceActive = 0x1;
        public const int ErrorMoreData = 234;

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        public struct ProcessEntry32
        {
            public uint Size;
            public uint Usage;
            public uint ProcessId;
            public UIntPtr DefaultHeapId;
            public uint ModuleId;
            public uint Threads;
            public uint ParentProcessId;
            public int PriorityClassBase;
            public uint Flags;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
            public string ExeFile;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct EnumServiceStatusProcess
        {
            public IntPtr ServiceName;
            public IntPtr DisplayName;
            public uint ServiceType;
            public uint CurrentState;
            public uint ControlsAccepted;
            public uint Win32ExitCode;
            public uint ServiceSpecificExitCode;
            public uint CheckPoint;
            public uint WaitHint;
            public uint ProcessId;
            public uint ServiceFlags;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct IoCounters
        {
            public ulong ReadOperationCount;
            public ulong WriteOperationCount;
            public ulong OtherOperationCount;
            public ulong ReadTransferCount;
            public ulong WriteTransferCount;
            public ulong OtherTransferCount;
        }

        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern IntPtr CreateToolhelp32Snapshot(uint flags, uint processId);

        [DllImport("kernel32.dll", EntryPoint = "Process32FirstW", CharSet = CharSet.Unicode, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool Process32First(IntPtr snapshot, ref ProcessEntry32 entry);

        [DllImport("kernel32.dll", EntryPoint = "Process32NextW", CharSet = CharSet.Unicode, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool Process32Next(IntPtr snapshot, ref ProcessEntry32 entry);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool CloseHandle(IntPtr handle);

        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern IntPtr OpenProcess(uint access, [MarshalAs(UnmanagedType.Bool)] bool inherit, uint processId);

        [DllImport("kernel32.dll", EntryPoint = "QueryFullProcessImageNameW", CharSet = CharSet.Unicode, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool QueryFullProcessImageName(IntPtr process, uint flags, StringBuilder name, ref int size);

        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern uint WaitForSingleObject(IntPtr handle, uint milliseconds);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool GetExitCodeProcess(IntPtr process, out uint exitCode);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool GetProcessTimes(IntPtr process, out long creation, out long exit, out long kernel, out long user);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool GetProcessIoCounters(Microsoft.Win32.SafeHandles.SafeProcessHandle process, out IoCounters counters);

        public static bool GetProcessIoCounters(Microsoft.Win32.SafeHandles.SafeProcessHandle process, out (ulong ReadTransferCount, ulong WriteTransferCount) io)
        {
            var ok = GetProcessIoCounters(process, out IoCounters counters);
            io = (counters.ReadTransferCount, counters.WriteTransferCount);
            return ok;
        }

        [DllImport("advapi32.dll", EntryPoint = "OpenSCManagerW", CharSet = CharSet.Unicode, SetLastError = true)]
        public static extern IntPtr OpenSCManager(string? machine, string? database, uint access);

        [DllImport("advapi32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool CloseServiceHandle(IntPtr handle);

        [DllImport("advapi32.dll", EntryPoint = "EnumServicesStatusExW", CharSet = CharSet.Unicode, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool EnumServicesStatusEx(IntPtr scm, uint infoLevel, uint serviceType, uint serviceState,
            IntPtr services, uint bufferSize, out uint bytesNeeded, out uint servicesReturned, ref uint resumeHandle, string? groupName);
    }
}
