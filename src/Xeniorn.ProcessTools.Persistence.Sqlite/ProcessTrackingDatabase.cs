using Microsoft.EntityFrameworkCore;

namespace Xeniorn.ProcessTools.Persistence.Sqlite;

/// <summary>Database level helpers: creating the file, applying migrations, retention, and reading series back.</summary>
public static class ProcessTrackingDatabase
{
    /// <summary>
    /// Applies pending migrations (creating the file and its directory if needed) and switches the database to write-ahead
    /// logging, which lets other tools read it while the tracker writes.
    /// </summary>
    public static async Task MigrateAsync(this DbContext db, CancellationToken cancellationToken = default)
    {
        var connection = db.Database.GetDbConnection();
        if (connection.DataSource is { Length: > 0 } file && Path.GetDirectoryName(Path.GetFullPath(file)) is { Length: > 0 } dir)
            Directory.CreateDirectory(dir);

        await db.Database.MigrateAsync(cancellationToken);
        await db.Database.ExecuteSqlRawAsync("PRAGMA journal_mode=WAL;", cancellationToken);
    }

    /// <summary>
    /// Deletes tracked processes that ended before <paramref name="cutoffUtc"/>, with all their periods, and periods
    /// older than the cutoff of processes still running. Returns the number of period rows deleted.
    /// </summary>
    public static async Task<int> PruneAsync(this ProcessTrackingDbContext db, DateTime cutoffUtc, CancellationToken cancellationToken = default)
    {
        var periods = await db.Periods.Where(p => p.LastSampleUtc < cutoffUtc).ExecuteDeleteAsync(cancellationToken);
        await db.TrackedProcesses
            .Where(p => p.LastSeenUtc < cutoffUtc && !p.Periods.Any())
            .ExecuteDeleteAsync(cancellationToken);
        return periods;
    }

    /// <summary>
    /// One aggregated value of one metric over time, for one process or all of them: the data behind "private bytes over time".
    /// Only periods that contain the aggregation are returned.
    /// </summary>
    public static async Task<IReadOnlyList<SeriesPoint>> GetSeriesAsync(
        this ProcessTrackingDbContext db,
        ProcessMetric metric,
        Aggregation aggregation,
        long? trackedProcessId = null,
        DateTime? fromUtc = null,
        DateTime? toUtc = null,
        CancellationToken cancellationToken = default)
    {
        if (!Enum.IsDefined(aggregation) || aggregation == Aggregation.None || Aggregator.Flags(aggregation).Count() != 1)
            throw new ArgumentOutOfRangeException(nameof(aggregation), aggregation, "Pass exactly one aggregation.");

        var query = db.PeriodMetrics.Where(m => m.Metric == metric);
        if (trackedProcessId is { } id)
            query = query.Where(m => m.Period!.TrackedProcessId == id);
        if (fromUtc is { } from)
            query = query.Where(m => m.Period!.FirstSampleUtc >= from);
        if (toUtc is { } to)
            query = query.Where(m => m.Period!.FirstSampleUtc < to);

        var rows = await query
            .OrderBy(m => m.Period!.FirstSampleUtc)
            .Select(m => new
            {
                m.Period!.TrackedProcessId,
                m.Period.FirstSampleUtc,
                m.Min, m.Max, m.Mean, m.Median, m.StdDev, m.Rms, m.Last,
            })
            .ToListAsync(cancellationToken);

        var points = new List<SeriesPoint>(rows.Count);
        foreach (var r in rows)
        {
            var value = aggregation switch
            {
                Aggregation.Min => r.Min,
                Aggregation.Max => r.Max,
                Aggregation.Mean => r.Mean,
                Aggregation.Median => r.Median,
                Aggregation.StdDev => r.StdDev,
                Aggregation.Rms => r.Rms,
                Aggregation.Last => r.Last,
                _ => null,
            };
            if (value is { } v)
                points.Add(new SeriesPoint(r.TrackedProcessId, r.FirstSampleUtc, v));
        }
        return points;
    }
}

/// <summary>A point of a metric series.</summary>
/// <param name="TrackedProcessId">The process (<see cref="TrackedProcessEntity.Id"/>).</param>
/// <param name="TimeUtc">Start of the period the value summarises.</param>
/// <param name="Value">The aggregated value.</param>
public sealed record SeriesPoint(long TrackedProcessId, DateTime TimeUtc, double Value);
