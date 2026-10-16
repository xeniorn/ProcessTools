using Xeniorn.ProcessTools.Dumping;
using Xeniorn.ProcessTools.Tracking;

namespace Xeniorn.ProcessTools.Persistence.Sqlite;

/// <summary>One process instance that was tracked: identified by PID plus start time, with how its tracking ended.</summary>
public class TrackedProcessEntity
{
    /// <summary>Surrogate key.</summary>
    public long Id { get; set; }

    /// <summary>PID.</summary>
    public int ProcessId { get; set; }

    /// <summary>Start time of the process (with <see cref="ProcessId"/> the identity of the instance).</summary>
    public DateTime StartTimeUtc { get; set; }

    /// <summary>Executable name without extension.</summary>
    public string Name { get; set; } = "";

    /// <summary>Full path of the executable, when it was readable.</summary>
    public string? Path { get; set; }

    /// <summary>PID of the parent at the time the process was first seen.</summary>
    public int? ParentProcessId { get; set; }

    /// <summary>When the tracker first saw the process.</summary>
    public DateTime FirstSeenUtc { get; set; }

    /// <summary>When the tracker last saw the process (updated with every period report).</summary>
    public DateTime LastSeenUtc { get; set; }

    /// <summary>True when the process was already running when tracking began, so <see cref="StartTimeUtc"/> predates <see cref="FirstSeenUtc"/> by an unknown time.</summary>
    public bool StartedBeforeTracking { get; set; }

    /// <summary>When the tracker noticed that tracking of the process ended; null while it is running (or if the tracker stopped first).</summary>
    public DateTime? EndedObservedUtc { get; set; }

    /// <summary>When the process exited according to the platform, if it records that.</summary>
    public DateTime? ExitedAtUtc { get; set; }

    /// <summary>Exit code, when the platform allowed reading it.</summary>
    public int? ExitCode { get; set; }

    /// <summary>Why tracking ended; null while it has not.</summary>
    public ProcessEndReason? EndReason { get; set; }

    /// <summary>Period summaries of this process.</summary>
    public List<PeriodEntity> Periods { get; set; } = [];

    /// <summary>Dumps taken (or attempted) of this process.</summary>
    public List<ProcessDumpEntity> Dumps { get; set; } = [];
}

/// <summary>A dump the tracker took, or tried to take, because a dump rule fired.</summary>
public class ProcessDumpEntity
{
    /// <summary>Surrogate key.</summary>
    public long Id { get; set; }

    /// <summary>The tracked process.</summary>
    public long TrackedProcessId { get; set; }

    /// <summary>The tracked process.</summary>
    public TrackedProcessEntity? TrackedProcess { get; set; }

    /// <summary>When the rule fired.</summary>
    public DateTime CreatedUtc { get; set; }

    /// <summary>What was dumped.</summary>
    public DumpKind Kind { get; set; }

    /// <summary>The metric of the rule that fired.</summary>
    public ProcessMetric Metric { get; set; }

    /// <summary>Human-readable description of the rule, for example "PrivateBytes &gt;= 1.07374e+11".</summary>
    public string Rule { get; set; } = "";

    /// <summary>The value of <see cref="Metric"/> that made the rule fire.</summary>
    public double ObservedValue { get; set; }

    /// <summary>Where the dump file is (null when none was produced).</summary>
    public string? FilePath { get; set; }

    /// <summary>Size of the dump file, when produced.</summary>
    public long? SizeBytes { get; set; }

    /// <summary>Whether a dump file was written and kept.</summary>
    public bool Succeeded { get; set; }

    /// <summary>Why it failed or was skipped.</summary>
    public string? Error { get; set; }
}

/// <summary>One reporting period of one process.</summary>
public class PeriodEntity
{
    /// <summary>Surrogate key.</summary>
    public long Id { get; set; }

    /// <summary>The tracked process.</summary>
    public long TrackedProcessId { get; set; }

    /// <summary>The tracked process.</summary>
    public TrackedProcessEntity? TrackedProcess { get; set; }

    /// <summary>Time of the first sample in the period.</summary>
    public DateTime FirstSampleUtc { get; set; }

    /// <summary>Time of the last sample in the period.</summary>
    public DateTime LastSampleUtc { get; set; }

    /// <summary>Number of sampling rounds in the period.</summary>
    public int SampleCount { get; set; }

    /// <summary>True when shorter than the configured period (process started or ended mid-period, or tracking stopped).</summary>
    public bool IsPartial { get; set; }

    /// <summary>Per-metric summaries.</summary>
    public List<PeriodMetricEntity> Metrics { get; set; } = [];
}

/// <summary>
/// One metric in one period: the aggregations as plain numeric columns (null = not requested), and optionally the raw
/// samples packed as little-endian IEEE-754 doubles (use <see cref="RawValuePacking.Unpack"/>).
/// </summary>
public class PeriodMetricEntity
{
    /// <summary>The period.</summary>
    public long PeriodId { get; set; }

    /// <summary>The period.</summary>
    public PeriodEntity? Period { get; set; }

    /// <summary>The metric (stored as its stable integer value).</summary>
    public ProcessMetric Metric { get; set; }

    /// <summary>Number of samples that went into this metric.</summary>
    public int Count { get; set; }

    /// <summary>Minimum.</summary>
    public double? Min { get; set; }

    /// <summary>Maximum.</summary>
    public double? Max { get; set; }

    /// <summary>Mean.</summary>
    public double? Mean { get; set; }

    /// <summary>Median.</summary>
    public double? Median { get; set; }

    /// <summary>Sample standard deviation.</summary>
    public double? StdDev { get; set; }

    /// <summary>Root mean square.</summary>
    public double? Rms { get; set; }

    /// <summary>Last sample of the period.</summary>
    public double? Last { get; set; }

    /// <summary>Raw samples, packed (8 bytes per sample, little-endian), when they were requested.</summary>
    public byte[]? RawValues { get; set; }
}
