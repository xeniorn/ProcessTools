using System.Diagnostics;
using Microsoft.EntityFrameworkCore;
using Xeniorn.ProcessTools.Dumping;
using Xeniorn.ProcessTools.Persistence.Sqlite;
using Xeniorn.ProcessTools.Selection;
using Xeniorn.ProcessTools.Tracking;

namespace Xeniorn.ProcessTools.Test;

public sealed class DumpTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "processtools-dumps-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        if (Directory.Exists(_dir))
            Directory.Delete(_dir, recursive: true);
    }

    private sealed class FakeDumper(DumpResult? result = null) : IProcessDumper
    {
        public List<(int Pid, string Path, DumpKind Kind)> Calls { get; } = [];
        public bool Supports(DumpKind kind) => true;

        public DumpResult Dump(ProcessInfo process, string filePath, DumpKind kind)
        {
            Calls.Add((process.ProcessId, filePath, kind));
            if (result is not null)
                return result;
            Directory.CreateDirectory(Path.GetDirectoryName(filePath)!);
            File.WriteAllBytes(filePath, new byte[1234]);
            return DumpResult.Ok(1234);
        }
    }

    private ProcessTrackerOptions Options(Action<DumpOptionsBuilder>? configure = null, params DumpRule[] rules)
    {
        var b = new DumpOptionsBuilder { Rules = rules.Length > 0 ? rules : [DumpRule.WhenAtLeast(ProcessMetric.PrivateBytes, 1000)] };
        configure?.Invoke(b);
        return new ProcessTrackerOptions
        {
            Selector = ProcessSelector.Name("x"),
            Metrics = [ProcessMetric.WorkingSetBytes], // the rule metric is deliberately not a reported one
            SampleInterval = TimeSpan.FromSeconds(1),
            AggregationPeriod = TimeSpan.FromSeconds(10),
            Dump = new DumpOptions
            {
                Directory = _dir,
                Rules = b.Rules,
                Kind = b.Kind,
                AllowFull = b.AllowFull,
                MaxDumpsPerProcess = b.MaxDumps,
                MinInterval = b.MinInterval,
                MinFreeDiskBytes = b.MinFreeDiskBytes,
                MaxDumpBytes = b.MaxDumpBytes,
            },
        };
    }

    private sealed class DumpOptionsBuilder
    {
        public IReadOnlyList<DumpRule> Rules { get; set; } = [];
        public DumpKind Kind { get; set; } = DumpKind.Small;
        public bool AllowFull { get; set; }
        public int MaxDumps { get; set; } = 2;
        public TimeSpan MinInterval { get; set; } = TimeSpan.FromMinutes(30);
        public long MinFreeDiskBytes { get; set; } = 1;
        public long MaxDumpBytes { get; set; } = 2L * 1024 * 1024 * 1024;
    }

    private static FakeProcess Machine(FakePlatform platform, double privateBytes)
    {
        var p = platform.Add(10, "x");
        p.Values[ProcessMetric.PrivateBytes] = privateBytes;
        p.Values[ProcessMetric.WorkingSetBytes] = 1;
        return p;
    }

    [Fact]
    public async Task A_threshold_rule_takes_a_dump_even_when_its_metric_is_not_reported()
    {
        var platform = new FakePlatform();
        var p = Machine(platform, 10);
        var dumper = new FakeDumper();
        var tracker = Run(Options(), platform, dumper, out var h);
        await using (h)
        {
            await h.StartAsync();
            Assert.Empty(dumper.Calls);

            p.Values[ProcessMetric.PrivateBytes] = 5000;
            await h.TickAsync();

            var call = Assert.Single(dumper.Calls);
            Assert.Equal(10, call.Pid);
            Assert.Equal(DumpKind.Small, call.Kind);
            Assert.StartsWith(_dir, call.Path);
            Assert.EndsWith("_PrivateBytes.dmp", call.Path);

            var dump = Assert.Single(h.Sink.Events.OfType<ProcessDumpEvent>());
            Assert.True(dump.Succeeded);
            Assert.Equal(5000, dump.ObservedValue);
            Assert.Equal(1234, dump.SizeBytes);
            Assert.Equal(call.Path, dump.FilePath);
        }
        _ = tracker;
    }

    [Fact]
    public async Task A_growth_rule_fires_on_a_runaway_but_not_on_a_high_plateau()
    {
        var platform = new FakePlatform();
        var p = Machine(platform, 1_000_000); // already high: a threshold rule would fire at once
        var dumper = new FakeDumper();
        var options = Options(rules: DumpRule.WhenGrowsBy(ProcessMetric.PrivateBytes, 500, TimeSpan.FromSeconds(10)));
        Run(options, platform, dumper, out var h);
        await using (h)
        {
            await h.StartAsync();
            await h.TickAsync(3);
            Assert.Empty(dumper.Calls); // plateau

            p.Values[ProcessMetric.PrivateBytes] = 1_000_300;
            await h.TickAsync();
            Assert.Empty(dumper.Calls); // growing, not enough yet

            p.Values[ProcessMetric.PrivateBytes] = 1_000_600;
            await h.TickAsync();
            Assert.Single(dumper.Calls);
        }
    }

    [Fact]
    public async Task Growth_older_than_the_window_does_not_count()
    {
        var platform = new FakePlatform();
        var p = Machine(platform, 0);
        var dumper = new FakeDumper();
        Run(Options(rules: DumpRule.WhenGrowsBy(ProcessMetric.PrivateBytes, 500, TimeSpan.FromSeconds(3))), platform, dumper, out var h);
        await using (h)
        {
            await h.StartAsync();
            p.Values[ProcessMetric.PrivateBytes] = 400;
            await h.TickAsync();
            await h.TickAsync(5); // the 400 now sits outside the 3 s window as the baseline
            p.Values[ProcessMetric.PrivateBytes] = 800; // +400 within the window only
            await h.TickAsync();
            Assert.Empty(dumper.Calls);
        }
    }

    [Fact]
    public async Task A_rule_can_demand_several_consecutive_samples()
    {
        var platform = new FakePlatform();
        var p = Machine(platform, 10);
        var dumper = new FakeDumper();
        Run(Options(rules: DumpRule.WhenAtLeast(ProcessMetric.PrivateBytes, 1000, sustainedSamples: 3)), platform, dumper, out var h);
        await using (h)
        {
            await h.StartAsync();
            p.Values[ProcessMetric.PrivateBytes] = 5000;
            await h.TickAsync(2);
            Assert.Empty(dumper.Calls);

            p.Values[ProcessMetric.PrivateBytes] = 10; // spike over: the count starts again
            await h.TickAsync();
            p.Values[ProcessMetric.PrivateBytes] = 5000;
            await h.TickAsync(2);
            Assert.Empty(dumper.Calls);
            await h.TickAsync();
            Assert.Single(dumper.Calls);
        }
    }

    [Fact]
    public async Task Dumps_per_process_and_their_spacing_are_limited()
    {
        var platform = new FakePlatform();
        Machine(platform, 5000);
        var dumper = new FakeDumper();
        var options = Options(b => { b.MaxDumps = 2; b.MinInterval = TimeSpan.FromSeconds(5); });
        Run(options, platform, dumper, out var h);
        await using (h)
        {
            await h.StartAsync();       // dump 1 at t=0
            await h.TickAsync(3);       // inside the interval
            Assert.Single(dumper.Calls);
            await h.TickAsync(3);       // t=6: dump 2
            Assert.Equal(2, dumper.Calls.Count);
            await h.TickAsync(20);      // limit reached
            Assert.Equal(2, dumper.Calls.Count);
        }
    }

    [Fact]
    public async Task A_failed_dump_is_reported_and_counts_toward_the_limit()
    {
        var platform = new FakePlatform();
        Machine(platform, 5000);
        var dumper = new FakeDumper(DumpResult.Fail("access denied"));
        Run(Options(b => b.MaxDumps = 1), platform, dumper, out var h);
        await using (h)
        {
            await h.StartAsync();
            await h.TickAsync(3);

            var dump = Assert.Single(h.Sink.Events.OfType<ProcessDumpEvent>());
            Assert.False(dump.Succeeded);
            Assert.Equal("access denied", dump.Error);
            Assert.Null(dump.FilePath);
            Assert.Single(dumper.Calls);
        }
    }

    [Fact]
    public async Task No_dump_is_taken_when_the_disk_is_too_full()
    {
        var platform = new FakePlatform();
        Machine(platform, 5000);
        var dumper = new FakeDumper();
        Run(Options(b => b.MinFreeDiskBytes = long.MaxValue), platform, dumper, out var h);
        await using (h)
        {
            await h.StartAsync();
            Assert.Empty(dumper.Calls);
            var dump = Assert.Single(h.Sink.Events.OfType<ProcessDumpEvent>());
            Assert.False(dump.Succeeded);
            Assert.Contains("free", dump.Error);
        }
    }

    [Fact]
    public async Task A_dump_over_the_size_limit_is_deleted_and_reported_as_failed()
    {
        var platform = new FakePlatform();
        Machine(platform, 5000);
        var dumper = new FakeDumper();
        Run(Options(b => b.MaxDumpBytes = 100), platform, dumper, out var h);
        await using (h)
        {
            await h.StartAsync();
            var dump = Assert.Single(h.Sink.Events.OfType<ProcessDumpEvent>());
            Assert.False(dump.Succeeded);
            Assert.False(File.Exists(dumper.Calls[0].Path));
        }
    }

    [Fact]
    public void A_full_dump_needs_explicit_permission()
    {
        var rules = new[] { DumpRule.WhenAtLeast(ProcessMetric.PrivateBytes, 1, kind: DumpKind.Full) };
        Assert.Throws<ArgumentException>(() => new ProcessTracker(Options(rules: rules)));
        Assert.Throws<ArgumentException>(() => new ProcessTracker(Options(b => b.Kind = DumpKind.Full)));
        _ = new ProcessTracker(Options(b => b.AllowFull = true, rules)); // fine when allowed
    }

    [Fact]
    public async Task Dumps_are_persisted_with_the_rule_and_the_observed_value()
    {
        var platform = new FakePlatform();
        Machine(platform, 5000);
        var dumper = new FakeDumper();
        var factory = new Factory(ProcessTrackingDbContext.CreateOptions<ProcessTrackingDbContext>(Path.Combine(_dir, "db", "t.sqlite")));
        await using (var db = factory.CreateDbContext())
            await db.MigrateAsync();

        var options = Options();
        var time = new Microsoft.Extensions.Time.Testing.FakeTimeProvider(new DateTimeOffset(FakePlatform.T0.AddMinutes(5)));
        var tracker = new ProcessTracker(options, platform, time, dumper: dumper);
        using var cts = new CancellationTokenSource();
        var run = tracker.RunAsync(new SqliteTrackingSink<ProcessTrackingDbContext>(factory), cts.Token);
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (tracker.TicksCompleted < 1 && DateTime.UtcNow < deadline)
            await Task.Delay(2);
        cts.Cancel();
        await run;

        await using var read = factory.CreateDbContext();
        var row = await read.ProcessDumps.Include(d => d.TrackedProcess).SingleAsync();
        Assert.True(row.Succeeded);
        Assert.Equal(ProcessMetric.PrivateBytes, row.Metric);
        Assert.Equal(5000, row.ObservedValue);
        Assert.Equal(1234, row.SizeBytes);
        Assert.Equal(10, row.TrackedProcess!.ProcessId);
        Assert.Contains("PrivateBytes", row.Rule);
    }

    [Fact]
    public async Task The_real_dumper_writes_a_small_valid_dump_of_a_real_process()
    {
        if (!OperatingSystem.IsWindows())
            return; // Linux dumps need a .NET target and createdump: covered by the docs, not by a portable test

        using var child = Process.Start(new ProcessStartInfo("cmd.exe", "/c ping -n 20 127.0.0.1 >nul")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
        })!;
        try
        {
            var info = new ProcessInfo(child.Id, Environment.ProcessId, "cmd", null, null);
            var path = Path.Combine(_dir, "real.dmp");
            var result = ProcessDumper.Default!.Dump(info, path, DumpKind.Small);

            Assert.True(result.Succeeded, result.Error);
            Assert.InRange(result.SizeBytes!.Value, 1, 100L * 1024 * 1024);
            var header = new byte[4];
            await using (var f = File.OpenRead(path))
                await f.ReadExactlyAsync(header);
            Assert.Equal("MDMP", System.Text.Encoding.ASCII.GetString(header));
        }
        finally
        {
            child.Kill(entireProcessTree: true);
            child.WaitForExit();
        }
    }

    [Fact]
    public void Dumping_a_process_that_does_not_exist_fails_cleanly()
    {
        var dumper = ProcessDumper.Default;
        if (dumper is null)
            return;
        var result = dumper.Dump(new ProcessInfo(int.MaxValue - 1, null, "ghost", null, null), Path.Combine(_dir, "ghost.dmp"), DumpKind.Small);
        Assert.False(result.Succeeded);
        Assert.False(File.Exists(Path.Combine(_dir, "ghost.dmp")));
    }

    private static ProcessTracker Run(ProcessTrackerOptions options, FakePlatform platform, IProcessDumper dumper, out TrackerHarness harness)
    {
        harness = new TrackerHarness(options, platform, dumper);
        return harness.Tracker;
    }

    private sealed class Factory(DbContextOptions<ProcessTrackingDbContext> options) : IDbContextFactory<ProcessTrackingDbContext>
    {
        public ProcessTrackingDbContext CreateDbContext() => new(options);
    }
}
