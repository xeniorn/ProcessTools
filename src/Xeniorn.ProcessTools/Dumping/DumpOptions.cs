namespace Xeniorn.ProcessTools.Dumping;

/// <summary>What a dump contains. The default, <see cref="Small"/>, stays in the megabytes however large the process is.</summary>
public enum DumpKind
{
    /// <summary>
    /// Stacks of all threads, loaded modules, thread information and the full memory map (which region of the address
    /// space is what), but NOT the contents of the heap. Typically a few MB to a few tens of MB, written in seconds, even
    /// for a process with hundreds of GB committed. Shows what the threads were doing and how the address space is laid out.
    /// </summary>
    Small = 0,

    /// <summary><see cref="Small"/> plus the table of open handles. Larger when the process leaks handles.</summary>
    SmallWithHandles = 1,

    /// <summary>The complete memory of the process: as large as the process. Only taken when <see cref="DumpOptions.AllowFull"/> is set.</summary>
    Full = 2,
}

/// <summary>
/// A condition on one metric that makes the tracker take a dump of the process ("about to go haywire"). Create with
/// <see cref="WhenAtLeast"/> or <see cref="WhenGrowsBy"/>.
/// </summary>
public sealed record DumpRule
{
    private DumpRule()
    {
    }

    /// <summary>The metric watched. It is read on every sample whether or not it is in the tracker's reported metrics.</summary>
    public ProcessMetric Metric { get; private init; }

    /// <summary>The rule fires when the metric is at least this.</summary>
    public double? AtLeast { get; private init; }

    /// <summary>The rule fires when the metric rose by at least this much within <see cref="GrowthWindow"/>.</summary>
    public double? GrowthBy { get; private init; }

    /// <summary>Window for <see cref="GrowthBy"/>.</summary>
    public TimeSpan GrowthWindow { get; private init; }

    /// <summary>The condition must hold on this many consecutive samples before the rule fires (filters spikes). Default 1.</summary>
    public int SustainedSamples { get; private init; } = 1;

    /// <summary>What to dump; null uses <see cref="DumpOptions.Kind"/>.</summary>
    public DumpKind? Kind { get; private init; }

    /// <summary>Fires when <paramref name="metric"/> reaches <paramref name="threshold"/> (for memory: bytes).</summary>
    public static DumpRule WhenAtLeast(ProcessMetric metric, double threshold, int sustainedSamples = 1, DumpKind? kind = null) =>
        new() { Metric = metric, AtLeast = threshold, SustainedSamples = Math.Max(1, sustainedSamples), Kind = kind };

    /// <summary>Fires when <paramref name="metric"/> grew by <paramref name="delta"/> within <paramref name="window"/>: a runaway rather than a high plateau.</summary>
    public static DumpRule WhenGrowsBy(ProcessMetric metric, double delta, TimeSpan window, int sustainedSamples = 1, DumpKind? kind = null) =>
        new() { Metric = metric, GrowthBy = delta, GrowthWindow = window, SustainedSamples = Math.Max(1, sustainedSamples), Kind = kind };

    /// <inheritdoc />
    public override string ToString() => AtLeast is { } at
        ? $"{Metric} >= {at:G6}"
        : $"{Metric} grew by >= {GrowthBy:G6} within {GrowthWindow}";
}

/// <summary>When and where the tracker takes dumps, and the limits that keep that from becoming a problem of its own.</summary>
public sealed class DumpOptions
{
    /// <summary>Directory the dump files are written to (created if missing). Put it on a volume with room.</summary>
    public required string Directory { get; init; }

    /// <summary>The conditions; the first one that fires takes the dump.</summary>
    public required IReadOnlyList<DumpRule> Rules { get; init; }

    /// <summary>What to dump unless a rule says otherwise. Default <see cref="DumpKind.Small"/>.</summary>
    public DumpKind Kind { get; init; } = DumpKind.Small;

    /// <summary>Allow <see cref="DumpKind.Full"/>. Off by default: a full dump of a large process fills disks.</summary>
    public bool AllowFull { get; init; }

    /// <summary>At most this many dumps per process instance (failed attempts count). Default 2.</summary>
    public int MaxDumpsPerProcess { get; init; } = 2;

    /// <summary>Minimum time between two dumps of the same process. Default 30 minutes.</summary>
    public TimeSpan MinInterval { get; init; } = TimeSpan.FromMinutes(30);

    /// <summary>No dump is attempted when the volume has less free space than this. Default 5 GiB.</summary>
    public long MinFreeDiskBytes { get; init; } = 5L * 1024 * 1024 * 1024;

    /// <summary>A dump file larger than this is deleted again and reported as failed. Default 2 GiB (ignored for <see cref="DumpKind.Full"/>).</summary>
    public long MaxDumpBytes { get; init; } = 2L * 1024 * 1024 * 1024;

    /// <summary>Throws <see cref="ArgumentException"/> for settings that make no sense.</summary>
    public void Validate()
    {
        if (string.IsNullOrWhiteSpace(Directory))
            throw new ArgumentException("A dump directory is required.", nameof(Directory));
        if (Rules.Count == 0)
            throw new ArgumentException("At least one dump rule is required.", nameof(Rules));
        if (MaxDumpsPerProcess < 1)
            throw new ArgumentException("MaxDumpsPerProcess must be at least 1.", nameof(MaxDumpsPerProcess));
        if (!AllowFull && (Kind == DumpKind.Full || Rules.Any(r => r.Kind == DumpKind.Full)))
            throw new ArgumentException("A rule asks for a full dump but AllowFull is not set.", nameof(AllowFull));
    }
}
