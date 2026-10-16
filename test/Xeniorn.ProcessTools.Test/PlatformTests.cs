using System.Diagnostics;
using Xeniorn.ProcessTools.Platform;
using Xeniorn.ProcessTools.Selection;
using Xeniorn.ProcessTools.Tracking;

namespace Xeniorn.ProcessTools.Test;

/// <summary>Parsing of the Linux /proc files (runs everywhere, on a fake tree) and the real platform of the machine running the tests.</summary>
public class PlatformTests
{
    [Fact]
    public void Linux_stat_parsing_survives_spaces_and_parentheses_in_the_command_name()
    {
        // field layout after the name: state ppid pgrp session tty tpgid flags minflt cminflt majflt cmajflt utime stime cutime cstime priority nice threads itrealvalue starttime
        var stat = "1234 (my (odd) name) S 42 1234 1234 0 -1 4194560 100 0 0 0 5 3 0 0 20 0 7 0 987654 1000 200";
        var parsed = LinuxProcessPlatform.ParseStat(stat);
        Assert.Equal(("my (odd) name", 42, 987654L), parsed);
    }

    [Theory]
    [InlineData("0::/system.slice/nginx.service\n", "nginx.service")]
    [InlineData("1:name=systemd:/system.slice/nginx.service\n12:cpu:/\n", "nginx.service")]
    [InlineData("0::/user.slice/user-1000.slice/user@1000.service/app.slice/foo.service\n", "foo.service")]
    [InlineData("0::/user.slice/user-1000.slice/session-3.scope\n", null)]
    [InlineData("0::/\n", null)]
    public void Linux_cgroup_parsing_finds_the_service_unit(string cgroup, string? expected) =>
        Assert.Equal(expected, LinuxProcessPlatform.ParseServiceUnit(cgroup));

    [Fact]
    public void Linux_platform_reads_a_proc_tree()
    {
        var root = Path.Combine(Path.GetTempPath(), "fake-proc-" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(Path.Combine(root, "100"));
            Directory.CreateDirectory(Path.Combine(root, "200"));
            Directory.CreateDirectory(Path.Combine(root, "self")); // not a pid: ignored
            File.WriteAllText(Path.Combine(root, "stat"), "cpu  1 2 3\nbtime 1700000000\n");
            File.WriteAllText(Path.Combine(root, "100", "stat"), "100 (daemon) S 1 100 100 0 -1 0 0 0 0 0 0 0 0 0 20 0 1 0 6000 0 0");
            File.WriteAllText(Path.Combine(root, "100", "cgroup"), "0::/system.slice/mydaemon.service\n");
            File.WriteAllText(Path.Combine(root, "200", "stat"), "200 (worker) S 100 100 100 0 -1 0 0 0 0 0 0 0 0 0 20 0 1 0 12000 0 0");
            File.WriteAllText(Path.Combine(root, "200", "cgroup"), "0::/user.slice/session-1.scope\n");

            var platform = new LinuxProcessPlatform(root);
            var all = platform.ListProcesses().OrderBy(p => p.ProcessId).ToList();

            Assert.Equal([100, 200], all.Select(p => p.ProcessId));
            Assert.Equal("daemon", all[0].Name);
            Assert.Equal(100, all[1].ParentProcessId);
            // start = boot time + ticks / 100 per second
            Assert.Equal(DateTime.UnixEpoch.AddSeconds(1700000000 + 60), all[0].StartTimeUtc);
            Assert.Equal([100], platform.GetServiceProcessIds("mydaemon"));
            Assert.Equal([100], platform.GetServiceProcessIds("mydaemon.service"));
            Assert.Equal([100], platform.GetServiceProcessIds("my*"));
            Assert.Empty(platform.GetServiceProcessIds("other"));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task The_real_platform_samples_this_process()
    {
        var snapshots = await ProcessSampler.SampleAsync(
            ProcessSelector.Pid(Environment.ProcessId),
            [ProcessMetric.WorkingSetBytes, ProcessMetric.PrivateBytes, ProcessMetric.VirtualBytes, ProcessMetric.ThreadCount, ProcessMetric.CpuSeconds, ProcessMetric.HandleCount]);

        var me = Assert.Single(snapshots);
        Assert.True(me.Values[ProcessMetric.WorkingSetBytes] > 1_000_000);
        Assert.True(me.Values[ProcessMetric.PrivateBytes] > 1_000_000);
        Assert.True(me.Values[ProcessMetric.VirtualBytes] > 1_000_000);
        Assert.True(me.Values[ProcessMetric.ThreadCount] >= 1);
        Assert.True(me.Values[ProcessMetric.CpuSeconds] >= 0);
        Assert.True(me.Values[ProcessMetric.HandleCount] > 0);
        Assert.NotNull(me.Info.StartTimeUtc);
        Assert.Equal(Process.GetCurrentProcess().ProcessName, me.Info.Name);
        Assert.NotNull(me.Info.Path);
    }

    [Fact]
    public async Task The_real_platform_sees_a_child_and_its_exit_code()
    {
        using var child = StartChild(seconds: 2, exitCode: 7);

        var children = await ProcessSampler.SampleAsync(ProcessSelector.ChildrenOf(ProcessSelector.Pid(Environment.ProcessId)), [ProcessMetric.WorkingSetBytes]);
        Assert.Contains(children, c => c.Info.ProcessId == child.Id);

        var options = new ProcessTrackerOptions
        {
            Selector = ProcessSelector.Pid(child.Id),
            Metrics = [ProcessMetric.WorkingSetBytes, ProcessMetric.CpuPercent],
            SampleInterval = TimeSpan.FromMilliseconds(100),
            AggregationPeriod = TimeSpan.FromMilliseconds(300),
            Aggregations = Aggregation.Max,
        };
        var sink = new RecordingSink();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var tracker = new ProcessTracker(options);
        var run = tracker.RunAsync(sink, cts.Token);

        while (!sink.Exited.Any() && !cts.IsCancellationRequested)
            await Task.Delay(50);
        cts.Cancel();
        await run;

        var exited = Assert.Single(sink.Exited);
        Assert.Equal(child.Id, exited.Identity.ProcessId);
        Assert.Equal(ProcessEndReason.Exited, exited.Reason);
        if (OperatingSystem.IsWindows())
            Assert.Equal(7, exited.ExitCode);
        else
            Assert.True(exited.ExitCode is null or 7);
        Assert.NotEmpty(sink.Reports);
    }

    private static Process StartChild(int seconds, int exitCode)
    {
        var info = OperatingSystem.IsWindows()
            ? new ProcessStartInfo("cmd.exe", $"/c \"ping -n {seconds + 1} 127.0.0.1 >nul & exit {exitCode}\"")
            : new ProcessStartInfo("sh", $"-c \"sleep {seconds}; exit {exitCode}\"");
        info.UseShellExecute = false;
        info.CreateNoWindow = true;
        info.RedirectStandardOutput = true;
        return Process.Start(info)!;
    }
}
