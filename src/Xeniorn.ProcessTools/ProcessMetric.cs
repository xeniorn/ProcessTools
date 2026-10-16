namespace Xeniorn.ProcessTools;

/// <summary>
/// A quantity that can be sampled for a process. The numeric values are stable: they are persisted by
/// <c>Xeniorn.ProcessTools.Persistence.Sqlite</c>, so never renumber, only add.
/// </summary>
/// <remarks>
/// Memory metrics are in bytes. Not every metric exists on every platform or for every process (for example when
/// access is denied); a metric that cannot be read is simply absent from a sample, never reported as zero.
/// </remarks>
public enum ProcessMetric
{
    /// <summary>CPU use over the last sample interval, in percent of ONE core (a process using 3 cores is 300). Needs two samples.</summary>
    CpuPercent = 1,

    /// <summary>Cumulative CPU time (user + kernel) in seconds. A counter: it only goes up.</summary>
    CpuSeconds = 2,

    /// <summary>Physical memory currently used (Windows: working set, Linux: VmRSS).</summary>
    WorkingSetBytes = 10,

    /// <summary>
    /// Private memory the process has asked for: the process's share of the system commit charge (Windows: "private bytes",
    /// Linux: VmData). This is what exhausts the commit limit, even if little of it is touched.
    /// </summary>
    PrivateBytes = 11,

    /// <summary>Total address space in use, reserved plus committed (Windows: virtual size, Linux: VmSize).</summary>
    VirtualBytes = 12,

    /// <summary>Highest working set so far (Windows: peak working set, Linux: VmHWM).</summary>
    PeakWorkingSetBytes = 13,

    /// <summary>Highest virtual size so far (Windows: peak virtual size, Linux: VmPeak).</summary>
    PeakVirtualBytes = 14,

    /// <summary>Number of threads.</summary>
    ThreadCount = 20,

    /// <summary>Open handles (Windows) or open file descriptors (Linux).</summary>
    HandleCount = 21,

    /// <summary>Cumulative bytes read through I/O calls (Windows: read transfer count, Linux: rchar). A counter.</summary>
    IoReadBytes = 30,

    /// <summary>Cumulative bytes written through I/O calls (Windows: write transfer count, Linux: wchar). A counter.</summary>
    IoWriteBytes = 31,

    /// <summary>Number of direct child processes (all children, not only those matching the selector).</summary>
    ChildCount = 40,

    /// <summary>Number of descendant processes: children, grandchildren and so on.</summary>
    DescendantCount = 41,
}

/// <summary>Facts about <see cref="ProcessMetric"/> values.</summary>
public static class ProcessMetricExtensions
{
    /// <summary>True for counters that only increase over the life of the process (CPU time, I/O bytes, peaks).</summary>
    public static bool IsCumulative(this ProcessMetric metric) => metric is
        ProcessMetric.CpuSeconds or ProcessMetric.IoReadBytes or ProcessMetric.IoWriteBytes
        or ProcessMetric.PeakWorkingSetBytes or ProcessMetric.PeakVirtualBytes;

    /// <summary>Unit of the metric, for display.</summary>
    public static string Unit(this ProcessMetric metric) => metric switch
    {
        ProcessMetric.CpuPercent => "%",
        ProcessMetric.CpuSeconds => "s",
        ProcessMetric.ThreadCount or ProcessMetric.HandleCount or ProcessMetric.ChildCount or ProcessMetric.DescendantCount => "count",
        _ => "bytes",
    };
}
