using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

namespace Xeniorn.ProcessTools.Persistence.Sqlite;

/// <summary>
/// SQLite model of tracked processes and their period summaries. Use it as is, or <b>derive from it</b> to add your own
/// tables and columns next to these:
/// <code>
/// public class MyDbContext(DbContextOptions&lt;MyDbContext&gt; options) : ProcessTrackingDbContext(options)
/// {
///     public DbSet&lt;MyThingEntity&gt; MyThings =&gt; Set&lt;MyThingEntity&gt;();
///     protected override void OnModelCreating(ModelBuilder modelBuilder)
///     {
///         base.OnModelCreating(modelBuilder);   // the process tracking tables
///         modelBuilder.Entity&lt;MyThingEntity&gt;(...);
///     }
/// }
/// </code>
/// A derived context has its own migrations (one history per context) which, being scaffolded from the derived model,
/// include the tracking tables; generate them with <c>dotnet ef migrations add</c> in your project. The migrations in this
/// package belong to <see cref="ProcessTrackingDbContext"/> itself and are used when you use it directly.
/// </summary>
public class ProcessTrackingDbContext : DbContext
{
    /// <summary>For the context used directly.</summary>
    public ProcessTrackingDbContext(DbContextOptions<ProcessTrackingDbContext> options) : base(options)
    {
    }

    /// <summary>For derived contexts: pass their own <c>DbContextOptions&lt;TDerived&gt;</c>.</summary>
    protected ProcessTrackingDbContext(DbContextOptions options) : base(options)
    {
    }

    /// <summary>Tracked process instances.</summary>
    public DbSet<TrackedProcessEntity> TrackedProcesses => Set<TrackedProcessEntity>();

    /// <summary>Dumps taken because a dump rule fired.</summary>
    public DbSet<ProcessDumpEntity> ProcessDumps => Set<ProcessDumpEntity>();

    /// <summary>Reporting periods.</summary>
    public DbSet<PeriodEntity> Periods => Set<PeriodEntity>();

    /// <summary>Per metric summaries of the periods.</summary>
    public DbSet<PeriodMetricEntity> PeriodMetrics => Set<PeriodMetricEntity>();

    /// <summary>SQLite options for a database file at <paramref name="databasePath"/>.</summary>
    public static DbContextOptions<TContext> CreateOptions<TContext>(string databasePath) where TContext : ProcessTrackingDbContext =>
        new DbContextOptionsBuilder<TContext>().UseSqlite($"Data Source={databasePath}").Options;

    /// <inheritdoc />
    protected override void ConfigureConventions(ModelConfigurationBuilder configurationBuilder)
    {
        // SQLite has no timestamp type and EF reads DateTime back as Kind=Unspecified; everything stored is UTC
        configurationBuilder.Properties<DateTime>().HaveConversion<UtcDateTimeConverter>();
        configurationBuilder.Properties<DateTime?>().HaveConversion<NullableUtcDateTimeConverter>();
    }

    /// <inheritdoc />
    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<TrackedProcessEntity>(e =>
        {
            e.ToTable("tracked_process");
            e.HasKey(x => x.Id);
            e.Property(x => x.Name).HasMaxLength(260);
            e.Property(x => x.Path).HasMaxLength(1024);
            // one row per process instance; restarting the tracker re-attaches to it instead of duplicating
            e.HasIndex(x => new { x.ProcessId, x.StartTimeUtc }).IsUnique();
            e.HasIndex(x => x.Name);
        });

        modelBuilder.Entity<PeriodEntity>(e =>
        {
            e.ToTable("period");
            e.HasKey(x => x.Id);
            e.HasOne(x => x.TrackedProcess).WithMany(x => x.Periods).HasForeignKey(x => x.TrackedProcessId).OnDelete(DeleteBehavior.Cascade);
            e.HasIndex(x => new { x.TrackedProcessId, x.FirstSampleUtc });
            e.HasIndex(x => x.FirstSampleUtc);
        });

        modelBuilder.Entity<ProcessDumpEntity>(e =>
        {
            e.ToTable("process_dump");
            e.HasKey(x => x.Id);
            e.Property(x => x.Rule).HasMaxLength(256);
            e.Property(x => x.FilePath).HasMaxLength(1024);
            e.HasOne(x => x.TrackedProcess).WithMany(x => x.Dumps).HasForeignKey(x => x.TrackedProcessId).OnDelete(DeleteBehavior.Cascade);
            e.HasIndex(x => new { x.TrackedProcessId, x.CreatedUtc });
        });

        modelBuilder.Entity<PeriodMetricEntity>(e =>
        {
            e.ToTable("period_metric");
            e.HasKey(x => new { x.PeriodId, x.Metric });
            e.HasOne(x => x.Period).WithMany(x => x.Metrics).HasForeignKey(x => x.PeriodId).OnDelete(DeleteBehavior.Cascade);
        });
    }
}

/// <summary>Design-time factory so <c>dotnet ef migrations add</c> works for <see cref="ProcessTrackingDbContext"/> from this project.</summary>
internal sealed class ProcessTrackingDbContextDesignTimeFactory : IDesignTimeDbContextFactory<ProcessTrackingDbContext>
{
    public ProcessTrackingDbContext CreateDbContext(string[] args) =>
        new(ProcessTrackingDbContext.CreateOptions<ProcessTrackingDbContext>("design-time.sqlite"));
}

/// <summary>
/// DateTime stored as UTC text, read back as Kind=Utc. Every time in this model is UTC by contract: a UTC value is stored as is,
/// a local one is converted, and a value of <b>unspecified</b> kind is <b>refused</b> (it cannot be told whether it is UTC or local,
/// and guessing would silently shift it; an unset <c>default(DateTime)</c> is refused the same way).
/// </summary>
internal sealed class UtcDateTimeConverter() : ValueConverter<DateTime, DateTime>(
    v => ToUtc(v),
    v => DateTime.SpecifyKind(v, DateTimeKind.Utc))
{
    internal static DateTime ToUtc(DateTime v) => v.Kind switch
    {
        DateTimeKind.Utc => v,
        DateTimeKind.Local => v.ToUniversalTime(),
        _ => throw new ArgumentException($"A DateTime of unspecified kind ({v:O}) was given for a UTC column. Every time in this model must be UTC (DateTimeKind.Utc); an unset value (default(DateTime)) lands here too."),
    };
}

/// <summary>Nullable counterpart of <see cref="UtcDateTimeConverter"/>.</summary>
internal sealed class NullableUtcDateTimeConverter() : ValueConverter<DateTime?, DateTime?>(
    v => v.HasValue ? UtcDateTimeConverter.ToUtc(v.Value) : v,
    v => v.HasValue ? DateTime.SpecifyKind(v.Value, DateTimeKind.Utc) : v);
