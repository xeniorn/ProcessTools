using Xeniorn.ProcessTools.Dumping;
using Xeniorn.ProcessTools.Selection;

namespace Xeniorn.ProcessTools.Tracking;

/// <summary>What to track, how often to sample, and how to summarise.</summary>
public sealed class ProcessTrackerOptions
{
    /// <summary>The metrics most useful for finding leaks and runaway processes.</summary>
    public static IReadOnlyList<ProcessMetric> DefaultMetrics { get; } =
    [
        ProcessMetric.CpuPercent,
        ProcessMetric.WorkingSetBytes,
        ProcessMetric.PrivateBytes,
        ProcessMetric.VirtualBytes,
        ProcessMetric.ThreadCount,
        ProcessMetric.HandleCount,
    ];

    /// <summary>Which processes to track. Re-evaluated on every sample.</summary>
    public required ProcessSelector Selector { get; init; }

    /// <summary>Metrics to sample. Default: <see cref="DefaultMetrics"/>.</summary>
    public IReadOnlyList<ProcessMetric> Metrics { get; init; } = DefaultMetrics;

    /// <summary>Time between two samples. Default 5 seconds.</summary>
    public TimeSpan SampleInterval { get; init; } = TimeSpan.FromSeconds(5);

    /// <summary>
    /// Length of one reporting period. A report is produced every <c>AggregationPeriod / SampleInterval</c> samples
    /// (rounded, at least 1), so the period is a whole number of intervals. Default 1 minute.
    /// </summary>
    public TimeSpan AggregationPeriod { get; init; } = TimeSpan.FromMinutes(1);

    /// <summary>Summaries to compute per metric and period. Default: min, mean and max.</summary>
    public Aggregation Aggregations { get; init; } = Aggregation.Min | Aggregation.Mean | Aggregation.Max;

    /// <summary>Also return the individual samples of each period, unaggregated.</summary>
    public bool IncludeRawValues { get; init; }

    /// <summary>
    /// Optional: take a dump of a process when one of the rules fires, so there is something to analyse when a process
    /// is about to go haywire. Null (default) never dumps.
    /// </summary>
    public DumpOptions? Dump { get; init; }

    /// <summary>Number of samples in a full period.</summary>
    public int SamplesPerPeriod => Math.Max(1, (int)Math.Round(AggregationPeriod / SampleInterval));

    /// <summary>Throws <see cref="ArgumentException"/> when the combination makes no sense.</summary>
    public void Validate()
    {
        if (SampleInterval <= TimeSpan.Zero)
            throw new ArgumentException("SampleInterval must be positive.", nameof(SampleInterval));
        if (AggregationPeriod < SampleInterval)
            throw new ArgumentException("AggregationPeriod must be at least one SampleInterval.", nameof(AggregationPeriod));
        if (Metrics.Count == 0)
            throw new ArgumentException("At least one metric is required.", nameof(Metrics));
        if (Aggregations == Aggregation.None && !IncludeRawValues)
            throw new ArgumentException("Choose at least one aggregation or IncludeRawValues, otherwise reports would be empty.", nameof(Aggregations));
        Dump?.Validate();
    }
}
