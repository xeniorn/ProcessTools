using System.Collections.Concurrent;
using Microsoft.EntityFrameworkCore;
using Xeniorn.ProcessTools.Tracking;

namespace Xeniorn.ProcessTools.Persistence.Sqlite;

/// <summary>
/// Writes what a <see cref="ProcessTracker"/> observes into a <see cref="ProcessTrackingDbContext"/> (or a context derived from it).
/// Each event is stored in its own transaction. Derive from this sink to store more alongside, using the
/// <c>*StoredAsync</c> hooks, which run with the same context before it is saved.
/// </summary>
/// <typeparam name="TContext">The context type; the base <see cref="ProcessTrackingDbContext"/> or one derived from it.</typeparam>
public class SqliteTrackingSink<TContext>(IDbContextFactory<TContext> contextFactory) : TrackingSinkBase
    where TContext : ProcessTrackingDbContext
{
    private readonly ConcurrentDictionary<ProcessIdentity, long> _ids = new();

    /// <summary>Creates a context; derived sinks can use it for their own reads and writes.</summary>
    protected TContext CreateContext() => contextFactory.CreateDbContext();

    /// <inheritdoc />
    public override async ValueTask OnProcessStartedAsync(ProcessStartedEvent started, CancellationToken cancellationToken)
    {
        await using var db = CreateContext();
        var entity = await FindOrCreateAsync(db, started.Identity, started.Info, started.ObservedAtUtc, started.StartedBeforeTracking, cancellationToken);
        entity.LastSeenUtc = started.ObservedAtUtc;
        // re-attaching after a tracker restart: the process is running again as far as we know
        entity.EndedObservedUtc = null;
        entity.EndReason = null;
        entity.ExitCode = null;
        entity.ExitedAtUtc = null;
        await OnProcessStartedStoredAsync(db, entity, started, cancellationToken);
        await db.SaveChangesAsync(cancellationToken);
        _ids[started.Identity] = entity.Id;
    }

    /// <inheritdoc />
    public override async ValueTask OnProcessExitedAsync(ProcessExitedEvent exited, CancellationToken cancellationToken)
    {
        await using var db = CreateContext();
        var entity = await FindOrCreateAsync(db, exited.Identity, exited.Info, exited.ObservedAtUtc, startedBeforeTracking: true, cancellationToken);
        entity.EndedObservedUtc = exited.ObservedAtUtc;
        entity.EndReason = exited.Reason;
        entity.ExitCode = exited.ExitCode;
        entity.ExitedAtUtc = exited.ExitedAtUtc;
        if (exited.ObservedAtUtc > entity.LastSeenUtc)
            entity.LastSeenUtc = exited.ObservedAtUtc;
        await OnProcessExitedStoredAsync(db, entity, exited, cancellationToken);
        await db.SaveChangesAsync(cancellationToken);
        _ids.TryRemove(exited.Identity, out _);
    }

    /// <inheritdoc />
    public override async ValueTask OnPeriodReportAsync(PeriodReportEvent reportEvent, CancellationToken cancellationToken)
    {
        var report = reportEvent.Report;
        await using var db = CreateContext();
        var entity = await FindOrCreateAsync(db, report.Identity, report.Info, report.FirstSampleUtc, startedBeforeTracking: true, cancellationToken);
        if (report.LastSampleUtc > entity.LastSeenUtc)
            entity.LastSeenUtc = report.LastSampleUtc;

        var period = new PeriodEntity
        {
            TrackedProcess = entity,
            FirstSampleUtc = report.FirstSampleUtc,
            LastSampleUtc = report.LastSampleUtc,
            SampleCount = report.SampleCount,
            IsPartial = report.IsPartial,
        };
        foreach (var (metric, summary) in report.Metrics)
        {
            period.Metrics.Add(new PeriodMetricEntity
            {
                Metric = metric,
                Count = summary.Count,
                Min = Get(summary, Aggregation.Min),
                Max = Get(summary, Aggregation.Max),
                Mean = Get(summary, Aggregation.Mean),
                Median = Get(summary, Aggregation.Median),
                StdDev = Get(summary, Aggregation.StdDev),
                Rms = Get(summary, Aggregation.Rms),
                Last = Get(summary, Aggregation.Last),
                RawValues = summary.RawValues is { } raw ? RawValuePacking.Pack(raw) : null,
            });
        }

        db.Periods.Add(period);
        await OnPeriodStoredAsync(db, entity, period, report, cancellationToken);
        await db.SaveChangesAsync(cancellationToken);
        _ids[report.Identity] = entity.Id;
    }

    /// <inheritdoc />
    public override async ValueTask OnProcessDumpedAsync(ProcessDumpEvent dumpEvent, CancellationToken cancellationToken)
    {
        await using var db = CreateContext();
        var entity = await FindOrCreateAsync(db, dumpEvent.Identity, dumpEvent.Info, dumpEvent.ObservedAtUtc, startedBeforeTracking: true, cancellationToken);
        var dump = new ProcessDumpEntity
        {
            TrackedProcess = entity,
            CreatedUtc = dumpEvent.ObservedAtUtc,
            Kind = dumpEvent.Kind,
            Metric = dumpEvent.Rule.Metric,
            Rule = dumpEvent.Rule.ToString(),
            ObservedValue = dumpEvent.ObservedValue,
            FilePath = dumpEvent.FilePath,
            SizeBytes = dumpEvent.SizeBytes,
            Succeeded = dumpEvent.Succeeded,
            Error = dumpEvent.Error,
        };
        db.ProcessDumps.Add(dump);
        await OnProcessDumpStoredAsync(db, entity, dump, dumpEvent, cancellationToken);
        await db.SaveChangesAsync(cancellationToken);
        _ids[dumpEvent.Identity] = entity.Id;
    }

    /// <summary>Runs after a dump row was added, before it is saved.</summary>
    protected virtual ValueTask OnProcessDumpStoredAsync(TContext db, TrackedProcessEntity process, ProcessDumpEntity dump, ProcessDumpEvent dumpEvent, CancellationToken cancellationToken) =>
        ValueTask.CompletedTask;

    /// <summary>Runs after a process row was filled for a start event, before it is saved. Add rows to <paramref name="db"/> that belong to the process.</summary>
    protected virtual ValueTask OnProcessStartedStoredAsync(TContext db, TrackedProcessEntity process, ProcessStartedEvent started, CancellationToken cancellationToken) =>
        ValueTask.CompletedTask;

    /// <summary>Runs after a process row was filled for an exit event, before it is saved.</summary>
    protected virtual ValueTask OnProcessExitedStoredAsync(TContext db, TrackedProcessEntity process, ProcessExitedEvent exited, CancellationToken cancellationToken) =>
        ValueTask.CompletedTask;

    /// <summary>Runs after a period row (with its metric rows) was added, before it is saved.</summary>
    protected virtual ValueTask OnPeriodStoredAsync(TContext db, TrackedProcessEntity process, PeriodEntity period, PeriodReport report, CancellationToken cancellationToken) =>
        ValueTask.CompletedTask;

    private async Task<TrackedProcessEntity> FindOrCreateAsync(TContext db, ProcessIdentity identity, ProcessInfo info, DateTime seenUtc, bool startedBeforeTracking, CancellationToken ct)
    {
        TrackedProcessEntity? entity = null;
        if (_ids.TryGetValue(identity, out var id))
            entity = await db.TrackedProcesses.FindAsync([id], ct);
        entity ??= await db.TrackedProcesses.FirstOrDefaultAsync(x => x.ProcessId == identity.ProcessId && x.StartTimeUtc == identity.StartTimeUtc, ct);
        if (entity is not null)
            return entity;

        entity = new TrackedProcessEntity
        {
            ProcessId = identity.ProcessId,
            StartTimeUtc = identity.StartTimeUtc,
            Name = info.Name,
            Path = info.Path,
            ParentProcessId = info.ParentProcessId,
            FirstSeenUtc = seenUtc,
            LastSeenUtc = seenUtc,
            StartedBeforeTracking = startedBeforeTracking,
        };
        db.TrackedProcesses.Add(entity);
        return entity;
    }

    private static double? Get(MetricPeriod summary, Aggregation aggregation) =>
        summary.Values.TryGetValue(aggregation, out var v) ? v : null;
}
