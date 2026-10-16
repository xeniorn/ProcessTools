using System.Diagnostics;

namespace Xeniorn.ProcessTools.Platform;

/// <summary>
/// <see cref="IProcessHandle"/> over <see cref="System.Diagnostics.Process"/>, shared by all platforms. Platform
/// specifics (I/O counters, handle count) are injected as delegates.
/// </summary>
internal sealed class SystemProcessHandle(
    Process process,
    Func<Process, (double Read, double Write)?>? ioReader,
    Func<Process, double?>? handleCountReader,
    ExitWatcher? exitWatcher = null) : IProcessHandle
{
    /// <inheritdoc />
    public IReadOnlyDictionary<ProcessMetric, double>? Read(IReadOnlySet<ProcessMetric> wanted)
    {
        try
        {
            process.Refresh();
            if (process.HasExited)
                return null;
        }
        catch (InvalidOperationException)
        {
            return null;
        }

        var values = new Dictionary<ProcessMetric, double>();
        // each read is separate: access is often partial, and one denied property must not hide the others
        Add(values, wanted, ProcessMetric.CpuSeconds, () => process.TotalProcessorTime.TotalSeconds);
        Add(values, wanted, ProcessMetric.WorkingSetBytes, () => process.WorkingSet64);
        Add(values, wanted, ProcessMetric.PrivateBytes, () => process.PrivateMemorySize64);
        Add(values, wanted, ProcessMetric.VirtualBytes, () => process.VirtualMemorySize64);
        Add(values, wanted, ProcessMetric.PeakWorkingSetBytes, () => process.PeakWorkingSet64);
        Add(values, wanted, ProcessMetric.PeakVirtualBytes, () => process.PeakVirtualMemorySize64);
        Add(values, wanted, ProcessMetric.ThreadCount, () => process.Threads.Count);

        if (wanted.Contains(ProcessMetric.HandleCount) && handleCountReader is not null)
        {
            var handles = Try(() => handleCountReader(process));
            if (handles is { } h)
                values[ProcessMetric.HandleCount] = h;
        }

        if ((wanted.Contains(ProcessMetric.IoReadBytes) || wanted.Contains(ProcessMetric.IoWriteBytes)) && ioReader is not null)
        {
            var io = Try(() => ioReader(process));
            if (io is { } counters)
            {
                if (wanted.Contains(ProcessMetric.IoReadBytes))
                    values[ProcessMetric.IoReadBytes] = counters.Read;
                if (wanted.Contains(ProcessMetric.IoWriteBytes))
                    values[ProcessMetric.IoWriteBytes] = counters.Write;
            }
        }

        return values;
    }

    /// <inheritdoc />
    public ProcessExitStatus? GetExitStatus()
    {
        // a watcher holds its own handle from the time tracking began, which is what keeps the exit code readable
        if (exitWatcher is not null)
            return exitWatcher.Poll();

        try
        {
            process.Refresh();
            if (!process.HasExited)
                return null;
        }
        catch (Exception)
        {
            // cannot even ask: treat as unknown rather than running
            return new ProcessExitStatus(null, null);
        }

        int? exitCode = Try(() => (int?)process.ExitCode);
        DateTime? exitTime = Try(() => (DateTime?)process.ExitTime.ToUniversalTime());
        return new ProcessExitStatus(exitCode, exitTime);
    }

    /// <inheritdoc />
    public void Dispose()
    {
        process.Dispose();
        exitWatcher?.Dispose();
    }

    private static void Add(Dictionary<ProcessMetric, double> values, IReadOnlySet<ProcessMetric> wanted, ProcessMetric metric, Func<double> read)
    {
        if (!wanted.Contains(metric))
            return;
        var value = Try(() => (double?)read());
        if (value is { } v)
            values[metric] = v;
    }

    private static T? Try<T>(Func<T?> read)
    {
        try
        {
            return read();
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception or NotSupportedException
                                       or UnauthorizedAccessException or IOException or PlatformNotSupportedException)
        {
            return default;
        }
    }
}

/// <summary>Watches one process for its exit through a handle opened while it was running, so the exit code can be read afterwards.</summary>
internal abstract class ExitWatcher : IDisposable
{
    /// <summary>Null while the process runs; after it exited, how.</summary>
    public abstract ProcessExitStatus? Poll();

    /// <inheritdoc />
    public abstract void Dispose();
}
