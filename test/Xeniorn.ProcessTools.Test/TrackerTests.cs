using Xeniorn.ProcessTools.Selection;
using Xeniorn.ProcessTools.Tracking;

namespace Xeniorn.ProcessTools.Test;

public class TrackerTests
{
    private static ProcessTrackerOptions Options(ProcessSelector selector, int periodTicks = 3, Aggregation aggregations = Aggregation.Min | Aggregation.Max | Aggregation.Mean | Aggregation.Last,
        bool raw = false, params ProcessMetric[] metrics) => new()
    {
        Selector = selector,
        Metrics = metrics.Length > 0 ? metrics : [ProcessMetric.PrivateBytes],
        SampleInterval = TimeSpan.FromSeconds(1),
        AggregationPeriod = TimeSpan.FromSeconds(periodTicks),
        Aggregations = aggregations,
        IncludeRawValues = raw,
    };

    [Fact]
    public async Task Reports_each_period_with_the_requested_aggregations_and_raw_values()
    {
        var platform = new FakePlatform();
        var p = platform.Add(10, "Spectronaut");
        await using var h = new TrackerHarness(Options(ProcessSelector.Name("Spectronaut"), raw: true), platform);

        p.Values[ProcessMetric.PrivateBytes] = 100;
        await h.StartAsync();
        p.Values[ProcessMetric.PrivateBytes] = 200;
        await h.TickAsync();
        p.Values[ProcessMetric.PrivateBytes] = 300;
        await h.TickAsync();

        var report = Assert.Single(h.Sink.Reports);
        var m = report.Metrics[ProcessMetric.PrivateBytes];
        Assert.Equal(100, m[Aggregation.Min]);
        Assert.Equal(300, m[Aggregation.Max]);
        Assert.Equal(200, m[Aggregation.Mean]);
        Assert.Equal(300, m[Aggregation.Last]);
        Assert.NotNull(m.RawValues);
        Assert.Equal([100.0, 200.0, 300.0], m.RawValues);
        Assert.Equal(3, report.SampleCount);
        Assert.False(report.IsPartial);
        Assert.False(m.Values.ContainsKey(Aggregation.Median));
    }

    [Fact]
    public async Task Raw_values_are_absent_unless_requested()
    {
        var platform = new FakePlatform();
        platform.Add(10, "x").Values[ProcessMetric.PrivateBytes] = 1;
        await using var h = new TrackerHarness(Options(ProcessSelector.Name("x"), periodTicks: 1), platform);
        await h.StartAsync();
        Assert.Null(Assert.Single(h.Sink.Reports).Metrics[ProcessMetric.PrivateBytes].RawValues);
    }

    [Fact]
    public async Task Reports_a_process_already_running_as_started_before_tracking()
    {
        var platform = new FakePlatform();
        platform.Add(10, "x");
        await using var h = new TrackerHarness(Options(ProcessSelector.Name("x")), platform);
        await h.StartAsync();
        var started = Assert.Single(h.Sink.Started);
        Assert.True(started.StartedBeforeTracking);
        Assert.Equal(new ProcessIdentity(10, FakePlatform.T0), started.Identity);
    }

    [Fact]
    public async Task A_process_that_starts_later_is_picked_up_and_its_first_period_is_partial()
    {
        var platform = new FakePlatform();
        await using var h = new TrackerHarness(Options(ProcessSelector.Name("late")), platform);
        await h.StartAsync();
        await h.TickAsync();

        var late = platform.Add(20, "late", start: h.Time.GetUtcNow().UtcDateTime);
        late.Values[ProcessMetric.PrivateBytes] = 5;
        await h.TickAsync(); // tick 3: picked up, the window boundary of the others is also here
        await h.TickAsync(3);

        var started = Assert.Single(h.Sink.Started);
        Assert.False(started.StartedBeforeTracking);
        Assert.True(h.Sink.Reports.First().IsPartial);
        Assert.False(h.Sink.Reports.Last().IsPartial);
    }

    [Fact]
    public async Task Exit_delivers_the_final_partial_report_then_the_exit_with_its_code()
    {
        var platform = new FakePlatform();
        var p = platform.Add(10, "x");
        p.Values[ProcessMetric.PrivateBytes] = 1;
        await using var h = new TrackerHarness(Options(ProcessSelector.Name("x")), platform);
        await h.StartAsync();
        await h.TickAsync();

        var at = h.Time.GetUtcNow().UtcDateTime;
        p.Exit(code: 7, at);
        await h.TickAsync();

        var exited = Assert.Single(h.Sink.Exited);
        Assert.Equal(ProcessEndReason.Exited, exited.Reason);
        Assert.Equal(7, exited.ExitCode);
        Assert.Equal(at, exited.ExitedAtUtc);

        var events = h.Sink.Events.ToList();
        var reportIndex = events.FindIndex(e => e is PeriodReportEvent);
        Assert.True(reportIndex >= 0 && reportIndex < events.IndexOf(exited));
        var report = Assert.Single(h.Sink.Reports);
        Assert.True(report.IsPartial);
        Assert.Equal(2, report.SampleCount);
    }

    [Fact]
    public async Task Several_instances_are_tracked_independently()
    {
        var platform = new FakePlatform();
        platform.Add(10, "x", start: FakePlatform.T0).Values[ProcessMetric.PrivateBytes] = 10;
        platform.Add(11, "x", start: FakePlatform.T0.AddMinutes(1)).Values[ProcessMetric.PrivateBytes] = 20;
        await using var h = new TrackerHarness(Options(ProcessSelector.Name("x"), periodTicks: 1), platform);
        await h.StartAsync();

        Assert.Equal(2, h.Sink.Started.Count());
        var byPid = h.Sink.Reports.ToDictionary(r => r.Identity.ProcessId);
        Assert.Equal(10, byPid[10].Metrics[ProcessMetric.PrivateBytes][Aggregation.Max]);
        Assert.Equal(20, byPid[11].Metrics[ProcessMetric.PrivateBytes][Aggregation.Max]);
    }

    [Fact]
    public async Task A_reused_pid_is_a_new_process()
    {
        var platform = new FakePlatform();
        platform.Add(10, "x", start: FakePlatform.T0).Values[ProcessMetric.PrivateBytes] = 1;
        await using var h = new TrackerHarness(Options(ProcessSelector.Name("x"), periodTicks: 1), platform);
        await h.StartAsync();

        // the old process vanishes and the OS hands PID 10 to a new one, between two samples
        platform.Add(10, "x", start: FakePlatform.T0.AddMinutes(3)).Values[ProcessMetric.PrivateBytes] = 2;
        await h.TickAsync();

        Assert.Equal(2, h.Sink.Started.Count());
        var exited = Assert.Single(h.Sink.Exited);
        Assert.Equal(FakePlatform.T0, exited.Identity.StartTimeUtc);
        Assert.Equal(ProcessEndReason.Vanished, exited.Reason);
        Assert.Equal(FakePlatform.T0.AddMinutes(3), h.Sink.Started.Last().Identity.StartTimeUtc);
    }

    [Fact]
    public async Task A_process_that_stops_matching_but_keeps_running_is_reported_as_such()
    {
        var platform = new FakePlatform();
        platform.Add(10, "svchost").Values[ProcessMetric.PrivateBytes] = 1;
        platform.Services["Svc"] = [10];
        await using var h = new TrackerHarness(Options(ProcessSelector.Service("Svc")), platform);
        await h.StartAsync();

        platform.Services["Svc"] = [];
        await h.TickAsync();

        Assert.Equal(ProcessEndReason.NoLongerMatched, Assert.Single(h.Sink.Exited).Reason);
    }

    [Fact]
    public async Task CpuPercent_is_derived_from_consecutive_cpu_times()
    {
        var platform = new FakePlatform();
        var p = platform.Add(10, "x");
        await using var h = new TrackerHarness(Options(ProcessSelector.Name("x"), periodTicks: 3, Aggregation.Min | Aggregation.Max, false, ProcessMetric.CpuPercent), platform);

        p.Values[ProcessMetric.CpuSeconds] = 0;
        await h.StartAsync();
        p.Values[ProcessMetric.CpuSeconds] = 1; // 1 cpu second in 1 wall second: 100 %
        await h.TickAsync();
        p.Values[ProcessMetric.CpuSeconds] = 4; // 3 cpu seconds in 1 wall second: 300 %
        await h.TickAsync();

        var m = Assert.Single(h.Sink.Reports).Metrics[ProcessMetric.CpuPercent];
        Assert.Equal(2, m.Count); // the first sample has no predecessor
        Assert.Equal(100, m[Aggregation.Min], 6);
        Assert.Equal(300, m[Aggregation.Max], 6);
    }

    [Fact]
    public async Task Child_and_descendant_counts_come_from_the_process_tree()
    {
        var platform = new FakePlatform();
        platform.Add(10, "root");
        platform.Add(11, "child", parent: 10);
        platform.Add(12, "grandchild", parent: 11);
        platform.Add(13, "child", parent: 10);
        await using var h = new TrackerHarness(
            Options(ProcessSelector.Name("root"), periodTicks: 1, Aggregation.Max, false, ProcessMetric.ChildCount, ProcessMetric.DescendantCount), platform);
        await h.StartAsync();

        var report = Assert.Single(h.Sink.Reports);
        Assert.Equal(2, report.Metrics[ProcessMetric.ChildCount][Aggregation.Max]);
        Assert.Equal(3, report.Metrics[ProcessMetric.DescendantCount][Aggregation.Max]);
    }

    [Fact]
    public async Task A_process_that_cannot_be_opened_is_still_tracked_for_what_the_list_shows()
    {
        var platform = new FakePlatform();
        platform.Add(10, "root").DenyOpen = true;
        platform.Add(11, "child", parent: 10);
        await using var h = new TrackerHarness(
            Options(ProcessSelector.Name("root"), periodTicks: 1, Aggregation.Max, false, ProcessMetric.PrivateBytes, ProcessMetric.ChildCount), platform);
        await h.StartAsync();

        var report = Assert.Single(h.Sink.Reports);
        Assert.Equal([ProcessMetric.ChildCount], report.Metrics.Keys);
    }

    [Fact]
    public async Task Stopping_delivers_the_period_in_progress_as_partial()
    {
        var platform = new FakePlatform();
        platform.Add(10, "x").Values[ProcessMetric.PrivateBytes] = 1;
        await using var h = new TrackerHarness(Options(ProcessSelector.Name("x"), periodTicks: 5), platform);
        await h.StartAsync();
        await h.TickAsync();
        await h.StopAsync();

        var report = Assert.Single(h.Sink.Reports);
        Assert.True(report.IsPartial);
        Assert.Equal(2, report.SampleCount);
        Assert.Empty(h.Sink.Exited); // the process did not end, tracking did
    }

    [Fact]
    public async Task A_failing_sink_does_not_stop_the_tracking()
    {
        var platform = new FakePlatform();
        platform.Add(10, "x").Values[ProcessMetric.PrivateBytes] = 1;
        await using var h = new TrackerHarness(Options(ProcessSelector.Name("x"), periodTicks: 1), platform);
        h.Sink.Fail = e => e is PeriodReportEvent ? new IOException("disk full") : null;
        await h.StartAsync();
        await h.TickAsync();

        platform.Add(11, "x");
        await h.TickAsync();
        Assert.Equal(2, h.Sink.Started.Count()); // later events still arrive
    }

    [Fact]
    public async Task StreamAsync_yields_events_and_completes_on_cancellation()
    {
        var platform = new FakePlatform();
        platform.Add(10, "x").Values[ProcessMetric.PrivateBytes] = 1;
        var time = new Microsoft.Extensions.Time.Testing.FakeTimeProvider();
        var tracker = new ProcessTracker(Options(ProcessSelector.Name("x"), periodTicks: 1), platform, time);
        using var cts = new CancellationTokenSource();

        var seen = new List<TrackingEvent>();
        await foreach (var e in tracker.StreamAsync(cts.Token))
        {
            seen.Add(e);
            if (e is PeriodReportEvent)
                cts.Cancel();
        }

        Assert.Contains(seen, e => e is ProcessStartedEvent);
        Assert.Contains(seen, e => e is PeriodReportEvent);
    }

    [Fact]
    public void Invalid_options_are_rejected()
    {
        var selector = ProcessSelector.Name("x");
        Assert.Throws<ArgumentException>(() => new ProcessTracker(new ProcessTrackerOptions { Selector = selector, SampleInterval = TimeSpan.Zero }));
        Assert.Throws<ArgumentException>(() => new ProcessTracker(new ProcessTrackerOptions { Selector = selector, SampleInterval = TimeSpan.FromSeconds(10), AggregationPeriod = TimeSpan.FromSeconds(5) }));
        Assert.Throws<ArgumentException>(() => new ProcessTracker(new ProcessTrackerOptions { Selector = selector, Metrics = [] }));
        Assert.Throws<ArgumentException>(() => new ProcessTracker(new ProcessTrackerOptions { Selector = selector, Aggregations = Aggregation.None }));
    }

    [Fact]
    public void Samples_per_period_is_a_whole_number_of_intervals()
    {
        var o = new ProcessTrackerOptions { Selector = ProcessSelector.All(), SampleInterval = TimeSpan.FromSeconds(5), AggregationPeriod = TimeSpan.FromSeconds(62) };
        Assert.Equal(12, o.SamplesPerPeriod);
    }
}
