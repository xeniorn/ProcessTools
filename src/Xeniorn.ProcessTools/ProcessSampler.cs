using Xeniorn.ProcessTools.Platform;
using Xeniorn.ProcessTools.Selection;
using Xeniorn.ProcessTools.Tracking;

namespace Xeniorn.ProcessTools;

/// <summary>The metrics of one process at one moment.</summary>
/// <param name="Info">Which process.</param>
/// <param name="Values">Metric values that could be read.</param>
public sealed record ProcessSnapshot(ProcessInfo Info, IReadOnlyDictionary<ProcessMetric, double> Values);

/// <summary>One-shot sampling, for scripts and quick checks: "what are the processes matching X using right now?"</summary>
public static class ProcessSampler
{
    /// <summary>
    /// Reads the metrics of every process the selector picks, once.
    /// <see cref="ProcessMetric.CpuPercent"/> needs a time span: pass <paramref name="cpuWindow"/> to wait that long and
    /// measure over it; without it, CPU percent is left out (the cumulative <see cref="ProcessMetric.CpuSeconds"/> is available).
    /// </summary>
    public static async Task<IReadOnlyList<ProcessSnapshot>> SampleAsync(
        ProcessSelector selector,
        IEnumerable<ProcessMetric>? metrics = null,
        TimeSpan? cpuWindow = null,
        IProcessPlatform? platform = null,
        CancellationToken cancellationToken = default)
    {
        platform ??= ProcessPlatform.Default;
        var wanted = (metrics ?? ProcessTrackerOptions.DefaultMetrics).ToHashSet();
        var wantsPercent = wanted.Remove(ProcessMetric.CpuPercent);
        var readSet = new HashSet<ProcessMetric>(wanted);
        if (wantsPercent && cpuWindow is not null)
            readSet.Add(ProcessMetric.CpuSeconds);

        var all = platform.ListProcesses();
        var context = new ProcessSelectionContext(all, platform);
        var childIndex = wanted.Contains(ProcessMetric.ChildCount) || wanted.Contains(ProcessMetric.DescendantCount) ? context.BuildChildIndex() : null;
        var selected = all.Where(p => selector.Matches(p, context)).ToList();

        var opened = new List<(ProcessInfo Info, IProcessHandle Handle, double? Cpu1)>();
        try
        {
            var timestamp = System.Diagnostics.Stopwatch.GetTimestamp();
            foreach (var info in selected)
            {
                var handle = platform.Open(info);
                if (handle is null)
                    continue;
                var first = wantsPercent && cpuWindow is not null && handle.Read(readSet) is { } v && v.TryGetValue(ProcessMetric.CpuSeconds, out var cpu) ? cpu : (double?)null;
                opened.Add((info, handle, first));
            }

            if (wantsPercent && cpuWindow is { } window)
                await Task.Delay(window, cancellationToken);
            var elapsed = System.Diagnostics.Stopwatch.GetElapsedTime(timestamp).TotalSeconds;

            var result = new List<ProcessSnapshot>();
            foreach (var (info, handle, cpu1) in opened)
            {
                var values = handle.Read(readSet);
                if (values is null)
                    continue;
                var copy = new Dictionary<ProcessMetric, double>(values);
                if (wantsPercent && cpuWindow is not null && cpu1 is { } c1 && values.TryGetValue(ProcessMetric.CpuSeconds, out var c2) && elapsed > 0)
                    copy[ProcessMetric.CpuPercent] = Math.Max(0, (c2 - c1) / elapsed * 100);
                if (!wanted.Contains(ProcessMetric.CpuSeconds))
                    copy.Remove(ProcessMetric.CpuSeconds);
                if (childIndex is not null)
                {
                    if (wanted.Contains(ProcessMetric.ChildCount))
                        copy[ProcessMetric.ChildCount] = childIndex.TryGetValue(info.ProcessId, out var kids) ? kids.Count : 0;
                    if (wanted.Contains(ProcessMetric.DescendantCount))
                        copy[ProcessMetric.DescendantCount] = ProcessTracker.CountDescendants(childIndex, info.ProcessId);
                }
                result.Add(new ProcessSnapshot(info, copy));
            }
            return result;
        }
        finally
        {
            foreach (var (_, handle, _) in opened)
                handle.Dispose();
        }
    }
}
