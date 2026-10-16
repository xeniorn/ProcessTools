namespace Xeniorn.ProcessTools.Tracking;

/// <summary>Something the tracker observed. One of <see cref="ProcessStartedEvent"/>, <see cref="ProcessExitedEvent"/> or <see cref="PeriodReportEvent"/>.</summary>
/// <param name="ObservedAtUtc">When the tracker saw it (not necessarily when it happened: processes can start and end between samples).</param>
public abstract record TrackingEvent(DateTime ObservedAtUtc);

/// <summary>A process matching the selector appeared.</summary>
/// <param name="ObservedAtUtc">When the tracker saw the process.</param>
/// <param name="Identity">PID plus start time.</param>
/// <param name="Info">What is known about the process.</param>
/// <param name="StartedBeforeTracking">True when it was already running when the tracker first saw it, so the real start was earlier than the observation.</param>
public sealed record ProcessStartedEvent(DateTime ObservedAtUtc, ProcessIdentity Identity, ProcessInfo Info, bool StartedBeforeTracking)
    : TrackingEvent(ObservedAtUtc);

/// <summary>A tracked process is gone, or stopped matching the selector.</summary>
/// <param name="ObservedAtUtc">When the tracker noticed.</param>
/// <param name="Identity">PID plus start time.</param>
/// <param name="Info">What was known about the process.</param>
/// <param name="Reason">Why tracking of it ended.</param>
/// <param name="ExitCode">Exit code, when the platform allowed reading it (see <see cref="Platform.ProcessExitStatus"/>).</param>
/// <param name="ExitedAtUtc">When the process exited, when the platform records it; otherwise null.</param>
public sealed record ProcessExitedEvent(DateTime ObservedAtUtc, ProcessIdentity Identity, ProcessInfo Info, ProcessEndReason Reason, int? ExitCode, DateTime? ExitedAtUtc)
    : TrackingEvent(ObservedAtUtc);

/// <summary>A period of samples of one process, summarised.</summary>
public sealed record PeriodReportEvent(DateTime ObservedAtUtc, PeriodReport Report) : TrackingEvent(ObservedAtUtc);

/// <summary>The tracker tried to take a dump of a process because a <see cref="Dumping.DumpRule"/> fired.</summary>
/// <param name="ObservedAtUtc">When the rule fired.</param>
/// <param name="Identity">PID plus start time.</param>
/// <param name="Info">What is known about the process.</param>
/// <param name="Rule">The rule that fired.</param>
/// <param name="ObservedValue">The value of the rule's metric that made it fire.</param>
/// <param name="Kind">What was dumped.</param>
/// <param name="FilePath">Where the dump was written (null if no file was produced).</param>
/// <param name="SizeBytes">Size of the dump file, when written.</param>
/// <param name="Succeeded">Whether a dump file was written and kept.</param>
/// <param name="Error">Why not, when it failed or was skipped.</param>
public sealed record ProcessDumpEvent(
    DateTime ObservedAtUtc,
    ProcessIdentity Identity,
    ProcessInfo Info,
    Dumping.DumpRule Rule,
    double ObservedValue,
    Dumping.DumpKind Kind,
    string? FilePath,
    long? SizeBytes,
    bool Succeeded,
    string? Error) : TrackingEvent(ObservedAtUtc);

/// <summary>Why tracking of a process ended.</summary>
public enum ProcessEndReason
{
    /// <summary>The process exited and the platform confirmed it (exit code may still be unknown).</summary>
    Exited = 0,

    /// <summary>The process disappeared from the process list without the tracker being able to confirm how (no handle).</summary>
    Vanished = 1,

    /// <summary>The process is still running but no longer matches the selector.</summary>
    NoLongerMatched = 2,
}

/// <summary>The samples of one process over one period, summarised per metric.</summary>
/// <param name="Identity">PID plus start time.</param>
/// <param name="Info">What is known about the process.</param>
/// <param name="FirstSampleUtc">Time of the first sample in the period.</param>
/// <param name="LastSampleUtc">Time of the last sample in the period.</param>
/// <param name="SampleCount">Number of sampling rounds in the period (a metric can have fewer values, for example CPU needs two samples).</param>
/// <param name="IsPartial">True when the period is shorter than configured: the process started or ended mid-period, or tracking stopped.</param>
/// <param name="Metrics">Per metric summary.</param>
public sealed record PeriodReport(
    ProcessIdentity Identity,
    ProcessInfo Info,
    DateTime FirstSampleUtc,
    DateTime LastSampleUtc,
    int SampleCount,
    bool IsPartial,
    IReadOnlyDictionary<ProcessMetric, MetricPeriod> Metrics);

/// <summary>One metric over one period.</summary>
/// <param name="Values">The requested aggregations.</param>
/// <param name="RawValues">The samples in order, when requested (otherwise null).</param>
public sealed record MetricPeriod(IReadOnlyDictionary<Aggregation, double> Values, double[]? RawValues)
{
    /// <summary>One aggregated value; throws <see cref="KeyNotFoundException"/> if it was not requested.</summary>
    public double this[Aggregation aggregation] => Values[aggregation];

    /// <summary>Number of samples that went into this metric in this period.</summary>
    public int Count { get; init; }
}
