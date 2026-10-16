using Microsoft.Extensions.Time.Testing;
using Xeniorn.ProcessTools.Tracking;

namespace Xeniorn.ProcessTools.Test;

internal sealed class RecordingSink : TrackingSinkBase
{
    private readonly object _gate = new();
    private readonly List<TrackingEvent> _events = [];

    public Func<TrackingEvent, Exception?>? Fail { get; set; }

    public IReadOnlyList<TrackingEvent> Events
    {
        get { lock (_gate) return _events.ToList(); }
    }

    public IEnumerable<ProcessStartedEvent> Started => Events.OfType<ProcessStartedEvent>();
    public IEnumerable<ProcessExitedEvent> Exited => Events.OfType<ProcessExitedEvent>();
    public IEnumerable<PeriodReport> Reports => Events.OfType<PeriodReportEvent>().Select(e => e.Report);

    public override ValueTask OnProcessStartedAsync(ProcessStartedEvent started, CancellationToken cancellationToken) => Record(started);
    public override ValueTask OnProcessExitedAsync(ProcessExitedEvent exited, CancellationToken cancellationToken) => Record(exited);
    public override ValueTask OnPeriodReportAsync(PeriodReportEvent report, CancellationToken cancellationToken) => Record(report);
    public override ValueTask OnProcessDumpedAsync(ProcessDumpEvent dump, CancellationToken cancellationToken) => Record(dump);

    private ValueTask Record(TrackingEvent e)
    {
        if (Fail?.Invoke(e) is { } ex)
            throw ex;
        lock (_gate)
            _events.Add(e);
        return ValueTask.CompletedTask;
    }
}

/// <summary>Runs a tracker on a fake machine with a fake clock; each <see cref="TickAsync"/> advances exactly one sample interval.</summary>
internal sealed class TrackerHarness : IAsyncDisposable
{
    private readonly CancellationTokenSource _cts = new();
    private Task? _run;
    private int _ticks;

    public TrackerHarness(ProcessTrackerOptions options, FakePlatform? platform = null, Xeniorn.ProcessTools.Dumping.IProcessDumper? dumper = null)
    {
        Platform = platform ?? new FakePlatform();
        Options = options;
        Time = new FakeTimeProvider(new DateTimeOffset(FakePlatform.T0.AddMinutes(5)));
        Tracker = new ProcessTracker(options, Platform, Time, dumper: dumper);
    }

    public FakePlatform Platform { get; }
    public ProcessTrackerOptions Options { get; }
    public FakeTimeProvider Time { get; }
    public ProcessTracker Tracker { get; }
    public RecordingSink Sink { get; } = new();

    /// <summary>Starts the loop and waits for its first sample (which happens immediately).</summary>
    public async Task StartAsync()
    {
        _run = Tracker.RunAsync(Sink, _cts.Token);
        await WaitForTicks(1);
        _ticks = 1;
    }

    public async Task TickAsync(int count = 1)
    {
        for (var i = 0; i < count; i++)
        {
            Time.Advance(Options.SampleInterval);
            await WaitForTicks(++_ticks);
        }
    }

    public async Task StopAsync()
    {
        _cts.Cancel();
        if (_run is not null)
            await _run;
        _run = null;
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync();
        _cts.Dispose();
    }

    private async Task WaitForTicks(int expected)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (Tracker.TicksCompleted < expected)
        {
            if (DateTime.UtcNow > deadline)
                throw new TimeoutException($"Tracker completed {Tracker.TicksCompleted} of {expected} ticks.");
            if (_run is { IsFaulted: true })
                await _run;
            await Task.Delay(1);
        }
    }
}
