using System.Threading.Channels;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Xeniorn.ProcessTools.Dumping;
using Xeniorn.ProcessTools.Platform;
using Xeniorn.ProcessTools.Selection;

namespace Xeniorn.ProcessTools.Tracking;

/// <summary>
/// Watches the processes a selector picks, sampling the chosen metrics at a fixed rate. Every period it delivers, per
/// process, the requested min / mean / median / max / sd / rms / last (and optionally the raw samples), and it reports
/// when processes start and exit. Several instances of the same program are tracked independently (identity is PID
/// plus start time), and the selector is re-evaluated each sample, so instances that start later are picked up.
/// Optionally it takes a dump of a process that is about to go haywire (see <see cref="ProcessTrackerOptions.Dump"/>).
/// </summary>
/// <remarks>
/// Use <see cref="RunAsync"/> to push events into an <see cref="ITrackingSink"/>, or <see cref="StreamAsync"/> to
/// pull them with <c>await foreach</c>. One tracker runs one loop; create several trackers for several selections.
/// </remarks>
public sealed class ProcessTracker
{
    private readonly ProcessTrackerOptions _options;
    private readonly IProcessPlatform _platform;
    private readonly IProcessDumper? _dumper;
    private readonly TimeProvider _time;
    private readonly ILogger _logger;
    private readonly IReadOnlySet<ProcessMetric> _handleMetrics;
    private readonly IReadOnlySet<ProcessMetric> _allMetrics;
    private readonly IReadOnlySet<ProcessMetric> _reportedMetrics;
    private readonly bool _needsChildIndex;
    private readonly Dictionary<int, Tracked> _tracked = new();
    private int _samplesInWindow;
    private DateTime? _lastTickUtc;
    private int _ticksCompleted;

    /// <summary>Number of completed sampling rounds; lets tests wait for a tick instead of sleeping.</summary>
    internal int TicksCompleted => Volatile.Read(ref _ticksCompleted);

    /// <summary>Creates a tracker.</summary>
    /// <param name="options">What and how to track.</param>
    /// <param name="platform">The OS access; defaults to <see cref="ProcessPlatform.Default"/>. Pass a fake in tests.</param>
    /// <param name="timeProvider">Clock and timer; defaults to <see cref="TimeProvider.System"/>. Pass a fake in tests.</param>
    /// <param name="logger">Optional logger.</param>
    /// <param name="dumper">Writes dumps when <see cref="ProcessTrackerOptions.Dump"/> is set; defaults to <see cref="ProcessDumper.Default"/>.</param>
    public ProcessTracker(ProcessTrackerOptions options, IProcessPlatform? platform = null, TimeProvider? timeProvider = null,
        ILogger<ProcessTracker>? logger = null, IProcessDumper? dumper = null)
    {
        options.Validate();
        _options = options;
        _platform = platform ?? ProcessPlatform.Default;
        _time = timeProvider ?? TimeProvider.System;
        _logger = logger ?? NullLogger<ProcessTracker>.Instance;
        _dumper = options.Dump is null ? null : dumper ?? ProcessDumper.Default;
        if (options.Dump is not null && _dumper is null)
            _logger.LogWarning("Dump rules are configured but this operating system has no dumper; no dumps will be taken");

        _reportedMetrics = options.Metrics.ToHashSet();
        // rule metrics are read every sample even when not reported
        var all = _reportedMetrics.ToHashSet();
        foreach (var rule in options.Dump?.Rules ?? [])
            all.Add(rule.Metric);
        _allMetrics = all;

        var wanted = all.ToHashSet();
        if (wanted.Contains(ProcessMetric.CpuPercent))
            wanted.Add(ProcessMetric.CpuSeconds); // percent is derived from consecutive CPU times
        _handleMetrics = wanted;
        _needsChildIndex = all.Contains(ProcessMetric.ChildCount) || all.Contains(ProcessMetric.DescendantCount);
    }

    /// <summary>Runs until <paramref name="cancellationToken"/> is cancelled, delivering events to <paramref name="sink"/>. On cancellation the periods in progress are delivered as partial reports.</summary>
    public async Task RunAsync(ITrackingSink sink, CancellationToken cancellationToken)
    {
        _logger.LogInformation("Tracking {Selector}: every {Interval} sample {Metrics}, report every {Samples} samples ({Aggregations}{Raw}){Dumps}",
            _options.Selector, _options.SampleInterval, string.Join(",", _options.Metrics), _options.SamplesPerPeriod,
            _options.Aggregations, _options.IncludeRawValues ? " + raw" : "",
            _options.Dump is { } d && _dumper is not null ? $", dump when {string.Join(" or ", d.Rules)}" : "");

        using var timer = new PeriodicTimer(_options.SampleInterval, _time);
        try
        {
            do
            {
                await TickAsync(sink, cancellationToken);
            }
            while (await timer.WaitForNextTickAsync(cancellationToken));
        }
        catch (OperationCanceledException)
        {
            // normal shutdown
        }
        finally
        {
            // deliver what is buffered; the token is cancelled, so use none: the sink decides how long this may take
            await FlushAllAsync(sink, CancellationToken.None);
            foreach (var t in _tracked.Values)
                t.Handle?.Dispose();
            _tracked.Clear();
        }
    }

    /// <summary>The same as <see cref="RunAsync"/>, as a stream of events. Ends when <paramref name="cancellationToken"/> is cancelled (after the final partial reports).</summary>
    public async IAsyncEnumerable<TrackingEvent> StreamAsync([System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var channel = Channel.CreateUnbounded<TrackingEvent>(new UnboundedChannelOptions { SingleReader = true, SingleWriter = true });
        var run = Task.Run(async () =>
        {
            try
            {
                await RunAsync(new ChannelSink(channel.Writer), cancellationToken);
            }
            finally
            {
                channel.Writer.TryComplete();
            }
        }, CancellationToken.None);

        await foreach (var e in channel.Reader.ReadAllAsync(CancellationToken.None))
            yield return e;
        await run;
    }

    private async Task TickAsync(ITrackingSink sink, CancellationToken ct)
    {
        var now = _time.GetUtcNow().UtcDateTime;
        var all = _platform.ListProcesses();
        var context = new ProcessSelectionContext(all, _platform);
        var matched = new Dictionary<int, ProcessInfo>();
        foreach (var p in all)
        {
            if (_options.Selector.Matches(p, context))
                matched[p.ProcessId] = p;
        }

        // 1. ended: no longer listed (or listed with another start time = PID reused), or no longer selected
        foreach (var tracked in _tracked.Values.ToList())
        {
            matched.TryGetValue(tracked.Info.ProcessId, out var current);
            if (current is not null && !StartChanged(tracked, current))
                continue;
            var stillRunning = context.TryGet(tracked.Info.ProcessId) is { } listed && !StartChanged(tracked, listed);
            await EndAsync(tracked, now, stillRunning, sink, ct);
        }

        // 2. new
        foreach (var info in matched.Values)
        {
            if (!_tracked.ContainsKey(info.ProcessId))
                await BeginAsync(info, now, sink, ct);
        }

        // 3. sample, and check the dump rules on what was just read
        var childIndex = _needsChildIndex ? context.BuildChildIndex() : null;
        foreach (var tracked in _tracked.Values.ToList())
        {
            var current = Sample(tracked, now, childIndex);
            await EvaluateDumpRulesAsync(tracked, current, now, sink, ct);
        }
        _lastTickUtc = now;

        // 4. period boundary
        if (++_samplesInWindow >= _options.SamplesPerPeriod)
        {
            await FlushAllAsync(sink, ct);
            _samplesInWindow = 0;
        }

        Interlocked.Increment(ref _ticksCompleted);
    }

    private async Task BeginAsync(ProcessInfo info, DateTime now, ITrackingSink sink, CancellationToken ct)
    {
        var identity = new ProcessIdentity(info.ProcessId, info.StartTimeUtc ?? now);
        var handle = _platform.Open(info);
        if (handle is null)
            _logger.LogWarning("Cannot open process {Identity} ({Name}); only what the process list shows (and child counts) can be tracked", identity, info.Name);

        _tracked[info.ProcessId] = new Tracked(identity, info, handle);
        var startedBefore = _lastTickUtc is null || (info.StartTimeUtc is { } s && s < _lastTickUtc.Value - TimeSpan.FromSeconds(1));
        _logger.LogInformation("Process started: {Identity} {Name} ({Path})", identity, info.Name, info.Path);
        await DeliverAsync(() => sink.OnProcessStartedAsync(new ProcessStartedEvent(now, identity, info, startedBefore), ct), "started", ct);
    }

    private async Task EndAsync(Tracked tracked, DateTime now, bool stillRunning, ITrackingSink sink, CancellationToken ct)
    {
        await FlushAsync(tracked, isFullWindow: false, now, sink, ct);

        var status = tracked.Handle?.GetExitStatus();
        ProcessEndReason reason;
        if (stillRunning)
            reason = ProcessEndReason.NoLongerMatched;
        else if (status is not null)
            reason = ProcessEndReason.Exited;
        else
            reason = ProcessEndReason.Vanished;

        tracked.Handle?.Dispose();
        _tracked.Remove(tracked.Info.ProcessId);
        _logger.LogInformation("Process ended: {Identity} {Name}, {Reason}, exit code {ExitCode}", tracked.Identity, tracked.Info.Name, reason, status?.ExitCode);
        await DeliverAsync(() => sink.OnProcessExitedAsync(
            new ProcessExitedEvent(now, tracked.Identity, tracked.Info, reason, status?.ExitCode, status?.ExitTimeUtc), ct), "exited", ct);
    }

    /// <summary>Reads everything wanted for one process, buffers the reported metrics, and returns all values read this round (for the dump rules).</summary>
    private Dictionary<ProcessMetric, double> Sample(Tracked tracked, DateTime now, Dictionary<int, List<int>>? childIndex)
    {
        tracked.SampleRounds++;
        tracked.FirstSampleUtc ??= now;
        tracked.LastSampleUtc = now;

        var current = new Dictionary<ProcessMetric, double>();
        var values = tracked.Handle?.Read(_handleMetrics);
        var timestamp = _time.GetTimestamp();
        if (values is not null)
        {
            foreach (var metric in _allMetrics)
            {
                if (metric is ProcessMetric.CpuPercent or ProcessMetric.ChildCount or ProcessMetric.DescendantCount)
                    continue;
                if (values.TryGetValue(metric, out var v))
                    current[metric] = v;
            }

            if (_handleMetrics.Contains(ProcessMetric.CpuSeconds) && values.TryGetValue(ProcessMetric.CpuSeconds, out var cpu))
            {
                if (tracked.PreviousCpuSeconds is { } prevCpu)
                {
                    var elapsed = _time.GetElapsedTime(tracked.PreviousTimestamp, timestamp).TotalSeconds;
                    if (elapsed > 0 && _allMetrics.Contains(ProcessMetric.CpuPercent))
                        current[ProcessMetric.CpuPercent] = Math.Max(0, (cpu - prevCpu) / elapsed * 100);
                }
                tracked.PreviousCpuSeconds = cpu;
                tracked.PreviousTimestamp = timestamp;
            }
        }

        if (childIndex is not null)
        {
            if (_allMetrics.Contains(ProcessMetric.ChildCount))
                current[ProcessMetric.ChildCount] = childIndex.TryGetValue(tracked.Info.ProcessId, out var children) ? children.Count : 0;
            if (_allMetrics.Contains(ProcessMetric.DescendantCount))
                current[ProcessMetric.DescendantCount] = CountDescendants(childIndex, tracked.Info.ProcessId);
        }

        foreach (var (metric, value) in current)
        {
            if (_reportedMetrics.Contains(metric))
                tracked.Add(metric, value);
        }
        return current;
    }

    private async Task EvaluateDumpRulesAsync(Tracked tracked, Dictionary<ProcessMetric, double> current, DateTime now, ITrackingSink sink, CancellationToken ct)
    {
        var dump = _options.Dump;
        if (dump is null || _dumper is null)
            return;

        // evaluate every rule every sample so growth history and "sustained" counters stay current even while dumping is held back
        DumpRule? fired = null;
        double firedValue = 0;
        for (var i = 0; i < dump.Rules.Count; i++)
        {
            var rule = dump.Rules[i];
            if (!current.TryGetValue(rule.Metric, out var value))
            {
                tracked.Sustained[i] = 0;
                continue;
            }

            var hit = false;
            if (rule.AtLeast is { } threshold)
                hit = value >= threshold;
            if (rule.GrowthBy is { } growth)
            {
                var history = tracked.History(rule.Metric);
                history.Enqueue((now, value));
                while (history.Count > 0 && now - history.Peek().Time > rule.GrowthWindow)
                    history.Dequeue();
                hit |= value - history.Min(h => h.Value) >= growth;
            }

            tracked.Sustained[i] = hit ? tracked.Sustained.GetValueOrDefault(i) + 1 : 0;
            if (fired is null && tracked.Sustained[i] >= rule.SustainedSamples)
            {
                fired = rule;
                firedValue = value;
            }
        }

        if (fired is null)
            return;
        if (tracked.DumpCount >= dump.MaxDumpsPerProcess)
            return;
        if (tracked.LastDumpUtc is { } last && now - last < dump.MinInterval)
            return;

        var kind = fired.Kind ?? dump.Kind;
        tracked.DumpCount++;
        tracked.LastDumpUtc = now;
        var (path, result) = await TakeDumpAsync(tracked.Info, dump, kind, now, fired);

        if (result.Succeeded)
            _logger.LogWarning("Dumped {Identity} {Name} ({Rule}, value {Value:G6}) to {Path}, {Size:N0} bytes", tracked.Identity, tracked.Info.Name, fired, firedValue, path, result.SizeBytes);
        else
            _logger.LogError("Could not dump {Identity} {Name} ({Rule}, value {Value:G6}): {Error}", tracked.Identity, tracked.Info.Name, fired, firedValue, result.Error);

        var captured = fired;
        var capturedValue = firedValue;
        await DeliverAsync(() => sink.OnProcessDumpedAsync(new ProcessDumpEvent(now, tracked.Identity, tracked.Info, captured, capturedValue, kind,
            result.Succeeded ? path : null, result.SizeBytes, result.Succeeded, result.Error), ct), "dump", ct);
    }

    private async Task<(string Path, DumpResult Result)> TakeDumpAsync(ProcessInfo info, DumpOptions dump, DumpKind kind, DateTime now, DumpRule rule)
    {
        var fileName = $"{Sanitize(info.Name)}_{info.ProcessId}_{now:yyyyMMdd_HHmmss}_{rule.Metric}.dmp";
        var path = System.IO.Path.Combine(dump.Directory, fileName);

        if (kind == DumpKind.Full && !dump.AllowFull)
            return (path, DumpResult.Fail("A full dump was requested but AllowFull is not set."));
        if (!_dumper!.Supports(kind))
            return (path, DumpResult.Fail($"This machine cannot produce a {kind} dump."));
        if (FreeDiskBytes(dump.Directory) is { } free && free < dump.MinFreeDiskBytes)
            return (path, DumpResult.Fail($"Only {free:N0} bytes free on the dump volume, less than the required {dump.MinFreeDiskBytes:N0}."));

        // the dumper blocks (it reads the target process): keep it off the sampling thread's synchronisation context
        var result = await Task.Run(() => _dumper.Dump(info, path, kind));
        if (result.Succeeded && kind != DumpKind.Full && result.SizeBytes > dump.MaxDumpBytes)
        {
            try { File.Delete(path); } catch (IOException) { /* report below */ }
            return (path, DumpResult.Fail($"The dump was {result.SizeBytes:N0} bytes, over the limit of {dump.MaxDumpBytes:N0}; deleted."));
        }
        return (path, result);
    }

    private static long? FreeDiskBytes(string directory)
    {
        try
        {
            var root = System.IO.Path.GetPathRoot(System.IO.Path.GetFullPath(directory));
            return root is null ? null : new DriveInfo(root).AvailableFreeSpace;
        }
        catch (Exception ex) when (ex is IOException or ArgumentException or UnauthorizedAccessException)
        {
            return null; // cannot tell: do not block the dump on it
        }
    }

    private static string Sanitize(string name) =>
        string.Concat(name.Select(c => char.IsLetterOrDigit(c) || c is '-' or '_' ? c : '_'));

    internal static int CountDescendants(Dictionary<int, List<int>> childIndex, int processId)
    {
        var seen = new HashSet<int> { processId };
        var stack = new Stack<int>();
        stack.Push(processId);
        var count = 0;
        while (stack.Count > 0)
        {
            if (!childIndex.TryGetValue(stack.Pop(), out var children))
                continue;
            foreach (var child in children)
            {
                if (seen.Add(child))
                {
                    count++;
                    stack.Push(child);
                }
            }
        }
        return count;
    }

    private async Task FlushAllAsync(ITrackingSink sink, CancellationToken ct)
    {
        var now = _time.GetUtcNow().UtcDateTime;
        foreach (var tracked in _tracked.Values.ToList())
            await FlushAsync(tracked, isFullWindow: tracked.SampleRounds >= _options.SamplesPerPeriod, now, sink, ct);
    }

    private async Task FlushAsync(Tracked tracked, bool isFullWindow, DateTime now, ITrackingSink sink, CancellationToken ct)
    {
        if (tracked.SampleRounds == 0)
            return;

        var metrics = new Dictionary<ProcessMetric, MetricPeriod>();
        foreach (var (metric, list) in tracked.Window)
        {
            if (list.Count == 0)
                continue;
            var span = System.Runtime.InteropServices.CollectionsMarshal.AsSpan(list);
            metrics[metric] = new MetricPeriod(Aggregator.ComputeAll(span, _options.Aggregations), _options.IncludeRawValues ? span.ToArray() : null)
            {
                Count = list.Count,
            };
        }

        var report = new PeriodReport(tracked.Identity, tracked.Info, tracked.FirstSampleUtc!.Value, tracked.LastSampleUtc!.Value,
            tracked.SampleRounds, IsPartial: !isFullWindow, metrics);
        tracked.ResetWindow();

        if (metrics.Count == 0)
            return;
        await DeliverAsync(() => sink.OnPeriodReportAsync(new PeriodReportEvent(now, report), ct), "period report", ct);
    }

    private async Task DeliverAsync(Func<ValueTask> deliver, string what, CancellationToken ct)
    {
        try
        {
            await deliver();
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            // a failing sink (full disk, locked database) must not stop the monitoring itself; this event is lost
            _logger.LogError(ex, "The tracking sink failed on a {Event} event; the event is dropped", what);
        }
    }

    private static bool StartChanged(Tracked tracked, ProcessInfo current) =>
        current.StartTimeUtc is { } start && tracked.Info.StartTimeUtc is { } known && Math.Abs((start - known).TotalSeconds) > 1;

    private sealed class Tracked(ProcessIdentity identity, ProcessInfo info, IProcessHandle? handle)
    {
        private readonly Dictionary<ProcessMetric, Queue<(DateTime Time, double Value)>> _history = new();

        public ProcessIdentity Identity { get; } = identity;
        public ProcessInfo Info { get; } = info;
        public IProcessHandle? Handle { get; } = handle;
        public Dictionary<ProcessMetric, List<double>> Window { get; } = new();
        public int SampleRounds { get; set; }
        public DateTime? FirstSampleUtc { get; set; }
        public DateTime? LastSampleUtc { get; set; }
        public double? PreviousCpuSeconds { get; set; }
        public long PreviousTimestamp { get; set; }
        public Dictionary<int, int> Sustained { get; } = new();
        public int DumpCount { get; set; }
        public DateTime? LastDumpUtc { get; set; }

        public Queue<(DateTime Time, double Value)> History(ProcessMetric metric)
        {
            if (!_history.TryGetValue(metric, out var q))
                _history[metric] = q = new Queue<(DateTime, double)>();
            return q;
        }

        public void Add(ProcessMetric metric, double value)
        {
            if (!Window.TryGetValue(metric, out var list))
                Window[metric] = list = [];
            list.Add(value);
        }

        public void ResetWindow()
        {
            foreach (var list in Window.Values)
                list.Clear();
            SampleRounds = 0;
            FirstSampleUtc = null;
            LastSampleUtc = null;
        }
    }

    private sealed class ChannelSink(ChannelWriter<TrackingEvent> writer) : ITrackingSink
    {
        public ValueTask OnProcessStartedAsync(ProcessStartedEvent started, CancellationToken cancellationToken) => writer.WriteAsync(started, CancellationToken.None);
        public ValueTask OnProcessExitedAsync(ProcessExitedEvent exited, CancellationToken cancellationToken) => writer.WriteAsync(exited, CancellationToken.None);
        public ValueTask OnPeriodReportAsync(PeriodReportEvent report, CancellationToken cancellationToken) => writer.WriteAsync(report, CancellationToken.None);
        public ValueTask OnProcessDumpedAsync(ProcessDumpEvent dump, CancellationToken cancellationToken) => writer.WriteAsync(dump, CancellationToken.None);
    }
}
