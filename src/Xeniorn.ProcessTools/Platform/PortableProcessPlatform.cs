using System.Diagnostics;

namespace Xeniorn.ProcessTools.Platform;

/// <summary>
/// Fallback for operating systems without a dedicated implementation (for example macOS), built only on
/// <see cref="Process"/>: selection by PID, name and path works; parent/child relations, services and I/O counters do not.
/// </summary>
internal class PortableProcessPlatform : IProcessPlatform
{
    /// <inheritdoc />
    public virtual IReadOnlyList<ProcessInfo> ListProcesses()
    {
        var result = new List<ProcessInfo>();
        foreach (var p in Process.GetProcesses())
        {
            using (p)
            {
                string? path = null;
                DateTime? start = null;
                try { path = p.MainModule?.FileName; } catch (Exception) { /* not readable */ }
                try { start = p.StartTime.ToUniversalTime(); } catch (Exception) { /* not readable */ }
                result.Add(new ProcessInfo(p.Id, null, p.ProcessName, path, start));
            }
        }
        return result;
    }

    /// <inheritdoc />
    public virtual IReadOnlyCollection<int> GetServiceProcessIds(string pattern) => [];

    /// <inheritdoc />
    public IProcessHandle? Open(ProcessInfo process)
    {
        Process p;
        try
        {
            p = Process.GetProcessById(process.ProcessId);
        }
        catch (ArgumentException)
        {
            return null; // not running
        }

        if (process.StartTimeUtc is { } expected && !StartsAt(p, expected))
        {
            p.Dispose();
            return null; // the PID was reused by another process
        }

        return new SystemProcessHandle(p, IoReader, HandleCountReader, CreateExitWatcher(process.ProcessId));
    }

    /// <summary>A watcher holding its own handle on the process, for platforms where <see cref="Process.ExitCode"/> of a process opened by PID is not reliable; null to use <see cref="Process"/>.</summary>
    internal virtual ExitWatcher? CreateExitWatcher(int processId) => null;

    /// <summary>Platform specific I/O counters, or null when not available.</summary>
    protected virtual Func<Process, (double Read, double Write)?>? IoReader => null;

    /// <summary>Platform specific handle (descriptor) count, or null when not available.</summary>
    protected virtual Func<Process, double?>? HandleCountReader => null;

    private static bool StartsAt(Process p, DateTime expectedUtc)
    {
        try
        {
            return Math.Abs((p.StartTime.ToUniversalTime() - expectedUtc).TotalSeconds) < 5;
        }
        catch (Exception)
        {
            return true; // cannot verify; accept
        }
    }
}
