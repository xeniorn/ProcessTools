using Microsoft.EntityFrameworkCore;
using Xeniorn.ProcessTools.Persistence.Sqlite;
using Xeniorn.ProcessTools.Selection;
using Xeniorn.ProcessTools.Tracking;

namespace Xeniorn.ProcessTools.Test;

public sealed class PersistenceTests : IAsyncLifetime
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "processtools-test-" + Guid.NewGuid().ToString("N"));
    private readonly string _dbPath;
    private readonly TestFactory<ProcessTrackingDbContext> _factory;

    public PersistenceTests()
    {
        _dbPath = Path.Combine(_dir, "data.sqlite");
        _factory = new TestFactory<ProcessTrackingDbContext>(ProcessTrackingDbContext.CreateOptions<ProcessTrackingDbContext>(_dbPath), o => new ProcessTrackingDbContext(o));
    }

    public async Task InitializeAsync()
    {
        await using var db = _factory.CreateDbContext();
        await db.MigrateAsync();
    }

    public Task DisposeAsync()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        if (Directory.Exists(_dir))
            Directory.Delete(_dir, recursive: true);
        return Task.CompletedTask;
    }

    private static ProcessTrackerOptions Options(bool raw) => new()
    {
        Selector = ProcessSelector.Name("x"),
        Metrics = [ProcessMetric.PrivateBytes, ProcessMetric.ThreadCount],
        SampleInterval = TimeSpan.FromSeconds(1),
        AggregationPeriod = TimeSpan.FromSeconds(2),
        Aggregations = Aggregation.Min | Aggregation.Max | Aggregation.Median,
        IncludeRawValues = raw,
    };

    [Fact]
    public async Task A_tracked_process_with_its_periods_and_exit_is_stored_and_queryable()
    {
        var platform = new FakePlatform();
        var p = platform.Add(10, "x", path: @"C:\a\x.exe");
        p.Values[ProcessMetric.PrivateBytes] = 100;
        p.Values[ProcessMetric.ThreadCount] = 4;
        await using (var h = new TrackerHarness(Options(raw: true), platform))
        {
            var sink = new SqliteTrackingSink<ProcessTrackingDbContext>(_factory);
            var tracker = new ProcessTracker(h.Options, platform, h.Time);
            using var cts = new CancellationTokenSource();
            var run = tracker.RunAsync(sink, cts.Token);

            await WaitAsync(() => tracker.TicksCompleted >= 1);
            h.Time.Advance(h.Options.SampleInterval);
            await WaitAsync(() => tracker.TicksCompleted >= 2);
            p.Exit(3, FakePlatform.T0.AddHours(1));
            h.Time.Advance(h.Options.SampleInterval);
            await WaitAsync(() => tracker.TicksCompleted >= 3);
            cts.Cancel();
            await run;
        }

        await using var db = _factory.CreateDbContext();
        var process = await db.TrackedProcesses.Include(x => x.Periods).ThenInclude(x => x.Metrics).SingleAsync();
        Assert.Equal(10, process.ProcessId);
        Assert.Equal(@"C:\a\x.exe", process.Path);
        Assert.Equal(DateTimeKind.Utc, process.StartTimeUtc.Kind);
        Assert.Equal(FakePlatform.T0, process.StartTimeUtc);
        Assert.Equal(3, process.ExitCode);
        Assert.Equal(ProcessEndReason.Exited, process.EndReason);
        Assert.NotNull(process.EndedObservedUtc);

        var period = Assert.Single(process.Periods);
        var metric = period.Metrics.Single(m => m.Metric == ProcessMetric.PrivateBytes);
        Assert.Equal(100, metric.Max);
        Assert.Null(metric.Mean); // not requested: null, not zero
        Assert.Equal(2, RawValuePacking.Unpack(metric.RawValues!).Length);

        var series = await db.GetSeriesAsync(ProcessMetric.PrivateBytes, Aggregation.Max);
        Assert.Equal(100, Assert.Single(series).Value);
    }

    [Fact]
    public async Task Restarting_the_tracker_reattaches_to_the_same_process_row()
    {
        var platform = new FakePlatform();
        platform.Add(10, "x").Values[ProcessMetric.PrivateBytes] = 1;

        for (var run = 0; run < 2; run++)
        {
            var sink = new SqliteTrackingSink<ProcessTrackingDbContext>(_factory);
            await using var h = new TrackerHarness(Options(raw: false), platform);
            var tracker = new ProcessTracker(h.Options, platform, h.Time);
            using var cts = new CancellationTokenSource();
            var task = tracker.RunAsync(sink, cts.Token);
            await WaitAsync(() => tracker.TicksCompleted >= 1);
            cts.Cancel();
            await task;
        }

        await using var db = _factory.CreateDbContext();
        Assert.Equal(1, await db.TrackedProcesses.CountAsync());
        Assert.Equal(2, await db.Periods.CountAsync()); // one partial period per run
    }

    [Fact]
    public async Task Pruning_removes_old_periods_and_processes_without_data()
    {
        await using (var db = _factory.CreateDbContext())
        {
            var old = new TrackedProcessEntity { ProcessId = 1, StartTimeUtc = FakePlatform.T0, Name = "old", FirstSeenUtc = FakePlatform.T0, LastSeenUtc = FakePlatform.T0 };
            old.Periods.Add(new PeriodEntity { FirstSampleUtc = FakePlatform.T0, LastSampleUtc = FakePlatform.T0, SampleCount = 1, Metrics = { new PeriodMetricEntity { Metric = ProcessMetric.PrivateBytes, Max = 1 } } });
            var recent = new TrackedProcessEntity { ProcessId = 2, StartTimeUtc = FakePlatform.T0, Name = "recent", FirstSeenUtc = FakePlatform.T0.AddDays(10), LastSeenUtc = FakePlatform.T0.AddDays(10) };
            recent.Periods.Add(new PeriodEntity { FirstSampleUtc = FakePlatform.T0.AddDays(10), LastSampleUtc = FakePlatform.T0.AddDays(10), SampleCount = 1, Metrics = { new PeriodMetricEntity { Metric = ProcessMetric.PrivateBytes, Max = 2 } } });
            db.TrackedProcesses.AddRange(old, recent);
            await db.SaveChangesAsync();
        }

        await using (var db = _factory.CreateDbContext())
            Assert.Equal(1, await db.PruneAsync(FakePlatform.T0.AddDays(5)));

        await using (var db = _factory.CreateDbContext())
        {
            Assert.Equal(["recent"], await db.TrackedProcesses.Select(p => p.Name).ToListAsync());
            Assert.Equal(1, await db.PeriodMetrics.CountAsync());
        }
    }

    [Fact]
    public async Task Times_are_stored_as_utc_whatever_kind_they_arrive_in()
    {
        var utc = new DateTime(2026, 3, 29, 1, 30, 0, DateTimeKind.Utc);   // an instant near a DST change
        var local = utc.ToLocalTime();
        var unspecified = DateTime.SpecifyKind(utc, DateTimeKind.Unspecified);

        await using (var db = _factory.CreateDbContext())
        {
            foreach (var (pid, time) in new[] { (1, utc), (2, local), (3, unspecified) })
                db.TrackedProcesses.Add(new TrackedProcessEntity { ProcessId = pid, StartTimeUtc = time, Name = "t", FirstSeenUtc = time, LastSeenUtc = time });
            await db.SaveChangesAsync();
        }

        await using var read = _factory.CreateDbContext();
        var rows = await read.TrackedProcesses.OrderBy(p => p.ProcessId).ToListAsync();
        Assert.All(rows, r => Assert.Equal(DateTimeKind.Utc, r.StartTimeUtc.Kind));
        Assert.All(rows, r => Assert.Equal(utc, r.StartTimeUtc));   // local converted, unspecified taken as UTC, nothing shifted

        // the file holds UTC text, readable by any tool
        var text = await read.Database.SqlQueryRaw<string>("SELECT StartTimeUtc AS Value FROM tracked_process WHERE ProcessId = 2").SingleAsync();
        Assert.StartsWith("2026-03-29 01:30:00", text);

        // queries with a local-kind bound compare as the same instant
        Assert.Equal(3, await read.TrackedProcesses.CountAsync(p => p.StartTimeUtc >= local.AddMinutes(-1) && p.StartTimeUtc <= local.AddMinutes(1)));
    }

    [Fact]
    public void Raw_values_round_trip_and_have_a_fixed_layout()
    {
        double[] values = [1.5, -2, double.MaxValue, 0];
        var packed = RawValuePacking.Pack(values);
        Assert.Equal(32, packed.Length);
        Assert.Equal(values, RawValuePacking.Unpack(packed));
        // 1.5 as little-endian IEEE-754: 0x3FF8000000000000
        Assert.Equal([0, 0, 0, 0, 0, 0, 0xF8, 0x3F], packed[..8]);
        Assert.Throws<ArgumentException>(() => RawValuePacking.Unpack(new byte[7]));
    }

    private static async Task WaitAsync(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (!condition())
        {
            if (DateTime.UtcNow > deadline)
                throw new TimeoutException();
            await Task.Delay(2);
        }
    }

    private sealed class TestFactory<T>(DbContextOptions<T> options, Func<DbContextOptions<T>, T> create) : IDbContextFactory<T> where T : DbContext
    {
        public T CreateDbContext() => create(options);
    }
}
