# ProcessTools

Cross-platform (Windows, Linux) process monitoring for .NET, plus quick PowerShell tools for hands-on use.

Pick processes (by PID, name, path, service, or ancestry), choose what to measure, and get per-period summaries
(min / mean / median / max / sd / rms / last, optionally with the raw samples), start and exit events with exit codes,
and, if you want, a **small dump** of a process that is about to go haywire.

| Package | What it is |
|---|---|
| `Xeniorn.ProcessTools` | The library: selectors, sampling, aggregation, tracker, dumper. No database, no hosting. |
| `Xeniorn.ProcessTools.Persistence.Sqlite` | SQLite storage (EF Core): a `DbContext` made to be inherited and extended, and a sink that fills it. |

```
dotnet add package Xeniorn.ProcessTools
dotnet add package Xeniorn.ProcessTools.Persistence.Sqlite   # optional
```

Requires .NET 10. Operating systems: Windows and Linux fully; other systems get a portable fallback (PID, name and path
selection only: no parent/child, no services). For machines without .NET 10 or with an old PowerShell, see [scripts](#powershell-tools).

## Quick start

```csharp
using Xeniorn.ProcessTools;
using Xeniorn.ProcessTools.Selection;
using Xeniorn.ProcessTools.Tracking;

var options = new ProcessTrackerOptions
{
    Selector = ProcessSelector.Name("Spectronaut"),                 // every instance, also ones that start later
    Metrics = [ProcessMetric.PrivateBytes, ProcessMetric.WorkingSetBytes, ProcessMetric.CpuPercent, ProcessMetric.DescendantCount],
    SampleInterval = TimeSpan.FromSeconds(10),
    AggregationPeriod = TimeSpan.FromMinutes(1),                    // a report every 6 samples
    Aggregations = Aggregation.Min | Aggregation.Mean | Aggregation.Max | Aggregation.Last,
    IncludeRawValues = false,
};

var tracker = new ProcessTracker(options);
await foreach (var e in tracker.StreamAsync(cancellationToken))
{
    switch (e)
    {
        case ProcessStartedEvent s: Console.WriteLine($"started {s.Identity} {s.Info.Path}"); break;
        case ProcessExitedEvent x:  Console.WriteLine($"ended {x.Identity}: {x.Reason}, exit code {x.ExitCode}"); break;
        case PeriodReportEvent r:
            var priv = r.Report.Metrics[ProcessMetric.PrivateBytes];
            Console.WriteLine($"{r.Report.Identity}: private max {priv[Aggregation.Max] / 1e9:N1} GB over {r.Report.SampleCount} samples");
            break;
        case ProcessDumpEvent d:    Console.WriteLine($"dump: {d.FilePath} ({d.Succeeded})"); break;
    }
}
```

Instead of pulling events, push them into a sink: `await tracker.RunAsync(sink, ct)` with your own `ITrackingSink`
(derive from `TrackingSinkBase` to implement only the events you need).

One-shot, no loop:

```csharp
var now = await ProcessSampler.SampleAsync(ProcessSelector.Service("MyService"), [ProcessMetric.PrivateBytes, ProcessMetric.CpuPercent], cpuWindow: TimeSpan.FromSeconds(2));
```

## Selecting processes

```csharp
ProcessSelector.Pid(1234)
ProcessSelector.Name("Spectro*")                      // case-insensitive, * and ?, with or without ".exe"
ProcessSelector.Path(@"C:\Program Files\Biognosys\*") // either slash
ProcessSelector.Service("MyService")                  // Windows service name/display name; Linux systemd unit
ProcessSelector.DescendantsOf(ProcessSelector.Name("Spectronaut"), includeRoot: true)
ProcessSelector.ChildrenOf(...)
ProcessSelector.Name("Spectronaut") | ProcessSelector.Service("MyService")        // union
ProcessSelector.Path("C:/tools/*") & !ProcessSelector.Name("*updater*")            // intersection, exclusion
```

The selector is evaluated against the whole process list on **every sample**. A process that starts later is picked up;
one that stops matching is reported as ended (`NoLongerMatched`). A tracked process is identified by **PID + start
time**, so a reused PID is a new process, and **several instances of the same program are tracked independently**.

## Metrics

| Metric | Meaning |
|---|---|
| `CpuPercent` | CPU over the last interval, percent of one core (3 busy cores = 300) |
| `CpuSeconds` | cumulative CPU time (counter) |
| `WorkingSetBytes` | physical memory in use |
| `PrivateBytes` | memory the process has asked for: its share of the **commit charge** (Windows private bytes, Linux VmData). This is what exhausts the commit limit, even when little of it is touched |
| `VirtualBytes` | address space in use, reserved plus committed |
| `PeakWorkingSetBytes`, `PeakVirtualBytes` | peaks so far |
| `ThreadCount`, `HandleCount` | threads; handles (Windows) or file descriptors (Linux) |
| `IoReadBytes`, `IoWriteBytes` | cumulative I/O bytes (counters) |
| `ChildCount`, `DescendantCount` | direct children; all descendants |

A metric that cannot be read for a process (access denied, not supported) is **absent**, never zero. Reading other
users' processes needs rights (run as administrator or a service account); the tracker keeps working with what it can read.

## Aggregations

`Min`, `Max`, `Mean`, `Median`, `StdDev` (sample, n-1), `Rms`, `Last`, combined as flags. Per period and process the report holds
the requested ones per metric, plus the **raw samples** when `IncludeRawValues` is set. The first and last period of a process
can be shorter (`IsPartial`); stopping the tracker delivers the periods in progress.

## Dumps for runaway processes

```csharp
Dump = new DumpOptions
{
    Directory = @"D:\dumps",
    Rules =
    [
        DumpRule.WhenAtLeast(ProcessMetric.PrivateBytes, 100L * 1024 * 1024 * 1024),                           // plateau: 100 GiB
        DumpRule.WhenGrowsBy(ProcessMetric.PrivateBytes, 30L * 1024 * 1024 * 1024, TimeSpan.FromMinutes(10)), // runaway: +30 GiB in 10 min
    ],
}
```

The default dump (`DumpKind.Small`) holds the **stacks of all threads, modules, thread info and the memory map, but no heap
contents**: a few MB to a few tens of MB however big the process is, written in seconds. It shows what the threads were doing
and which regions of the address space are huge. `SmallWithHandles` adds the handle table. `Full` (all memory) must be
allowed explicitly (`AllowFull`) because it is as large as the process.

Safeguards: at most `MaxDumpsPerProcess` (2) per process instance and `MinInterval` (30 min) apart, no dump when the volume has
less than `MinFreeDiskBytes` (5 GiB) free, a dump over `MaxDumpBytes` (2 GiB) is deleted again, and a rule can require several
consecutive samples (`sustainedSamples`) to ignore spikes. Failed attempts count and are reported. Every attempt is delivered
as a `ProcessDumpEvent` (and stored by the SQLite sink).

Windows uses `MiniDumpWriteDump` (needs query/read rights on the target). Linux uses the `createdump` tool that ships with
the .NET runtime (`--normal` for small), which only works for .NET processes and for the same user or root. The Linux path is
exercised by unit tests of the parsing only, not by an end-to-end test.

## SQLite persistence

```csharp
var options = ProcessTrackingDbContext.CreateOptions<ProcessTrackingDbContext>(@"D:\data\process-tracking.sqlite");
var factory = new PooledDbContextFactory<ProcessTrackingDbContext>(options);
await using (var db = factory.CreateDbContext()) await db.MigrateAsync();      // creates the file, applies migrations, WAL mode

await tracker.RunAsync(new SqliteTrackingSink<ProcessTrackingDbContext>(factory), ct);
```

Tables: `tracked_process` (one row per process instance: PID, start, name, path, first/last seen, exit code and reason),
`period` (one per process and period), `period_metric` (one per metric and period: **integer metric id, numeric `REAL` columns per
aggregation**, null when not requested, and an optional **BLOB of packed raw samples**: 8 bytes per sample, little-endian doubles,
no header, trivially readable from SQL tools, Python (`struct.unpack`), R (`readBin`) or `RawValuePacking.Unpack`), and
`process_dump`. Timestamps are UTC. Metric ids are stable (the enum values never change).

`ProcessTrackingDbContext` is **meant to be inherited**: derive from it, add your own tables in `OnModelCreating` after
`base.OnModelCreating`, and derive from `SqliteTrackingSink<TContext>` to store more next to each event with the
`On...StoredAsync` hooks. A derived context has its own migrations (scaffold them in your project with `dotnet ef`; they include
the tracking tables). Read data back with `GetSeriesAsync(metric, aggregation, ...)`, and apply retention with `PruneAsync(cutoff)`.

## PowerShell tools

`scripts/` has standalone tools for quick, hands-on work (no build, no dependencies). They run on **Windows PowerShell 5.1 and
PowerShell 7+**, on Windows and Linux, and use the same metric and aggregation names as the library.

```powershell
./scripts/Get-ProcessSnapshot.ps1 -Name Spectronaut -IncludeDescendants -Metric PrivateBytes,ThreadCount,DescendantCount
./scripts/Watch-Process.ps1 -Name Spectronaut -Metric PrivateBytes,WorkingSetBytes -IntervalSeconds 10 -PeriodSeconds 60 -Aggregate Min,Median,Max,StdDev -OutFile sn.csv
./scripts/New-ProcessDump.ps1 -Name Spectronaut -OutDir D:/dumps          # small dump (Windows)
```

## Design notes

- **Core first, adapters on top.** The tracker knows nothing about databases or hosting. It talks to the OS through
  `IProcessPlatform` and `IProcessDumper`, to time through `TimeProvider`, and to the outside through `ITrackingSink`; each can
  be faked, which is how the tracker is tested deterministically (fake machine, fake clock).
- **A failing sink never stops the monitoring**: the event is logged and dropped.
- **Exit codes**: Windows keeps its own handle on a process from the moment tracking starts, so the exit code is available after
  the process is gone. On Linux only a parent can read an exit code, so it is null for processes you did not start.
- All times are UTC (`...Utc` names, `DateTimeKind.Utc`, UTC text in SQLite, `Z` in file names and script output). The SQLite model stores UTC values as they are, converts a local `DateTime`, and **refuses one of unspecified kind** (including an unset `default(DateTime)`), because it cannot be told which it is.

## Build, test, release

```
dotnet build ProcessTools.slnx
dotnet test  ProcessTools.slnx
```

Versions come from git tags (`vX.Y.Z`, MinVer). A tag push builds, tests on Windows and Linux, and publishes both packages to
nuget.org from CI (`.github/workflows/release.yml`) with **NuGet Trusted Publishing** (GitHub OIDC; no long-lived API key). One-time setup
is described at the top of the workflow file. Nothing is ever pushed from a developer machine.

## License

MIT
