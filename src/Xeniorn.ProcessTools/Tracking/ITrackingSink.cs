namespace Xeniorn.ProcessTools.Tracking;

/// <summary>Receives what a <see cref="ProcessTracker"/> observes. Calls are sequential, never concurrent.</summary>
public interface ITrackingSink
{
    /// <summary>A matching process appeared.</summary>
    ValueTask OnProcessStartedAsync(ProcessStartedEvent started, CancellationToken cancellationToken);

    /// <summary>A tracked process ended (any <see cref="ProcessEndReason"/>). Its final partial period report was delivered just before.</summary>
    ValueTask OnProcessExitedAsync(ProcessExitedEvent exited, CancellationToken cancellationToken);

    /// <summary>A period of samples of one process is complete.</summary>
    ValueTask OnPeriodReportAsync(PeriodReportEvent report, CancellationToken cancellationToken);

    /// <summary>A dump of a process was attempted (see <see cref="ProcessDumpEvent.Succeeded"/>).</summary>
    ValueTask OnProcessDumpedAsync(ProcessDumpEvent dump, CancellationToken cancellationToken);
}

/// <summary>Base class with empty implementations, for sinks that only care about some events.</summary>
public abstract class TrackingSinkBase : ITrackingSink
{
    /// <inheritdoc />
    public virtual ValueTask OnProcessStartedAsync(ProcessStartedEvent started, CancellationToken cancellationToken) => ValueTask.CompletedTask;

    /// <inheritdoc />
    public virtual ValueTask OnProcessExitedAsync(ProcessExitedEvent exited, CancellationToken cancellationToken) => ValueTask.CompletedTask;

    /// <inheritdoc />
    public virtual ValueTask OnPeriodReportAsync(PeriodReportEvent report, CancellationToken cancellationToken) => ValueTask.CompletedTask;

    /// <inheritdoc />
    public virtual ValueTask OnProcessDumpedAsync(ProcessDumpEvent dump, CancellationToken cancellationToken) => ValueTask.CompletedTask;
}

/// <summary>Delivers every event to several sinks, in order. A failing sink does not stop the others; the first exception is rethrown afterwards.</summary>
public sealed class CompositeTrackingSink(IEnumerable<ITrackingSink> sinks) : ITrackingSink
{
    private readonly ITrackingSink[] _sinks = sinks.ToArray();

    /// <inheritdoc />
    public ValueTask OnProcessStartedAsync(ProcessStartedEvent started, CancellationToken cancellationToken) =>
        ForEach(s => s.OnProcessStartedAsync(started, cancellationToken));

    /// <inheritdoc />
    public ValueTask OnProcessExitedAsync(ProcessExitedEvent exited, CancellationToken cancellationToken) =>
        ForEach(s => s.OnProcessExitedAsync(exited, cancellationToken));

    /// <inheritdoc />
    public ValueTask OnPeriodReportAsync(PeriodReportEvent report, CancellationToken cancellationToken) =>
        ForEach(s => s.OnPeriodReportAsync(report, cancellationToken));

    /// <inheritdoc />
    public ValueTask OnProcessDumpedAsync(ProcessDumpEvent dump, CancellationToken cancellationToken) =>
        ForEach(s => s.OnProcessDumpedAsync(dump, cancellationToken));

    private async ValueTask ForEach(Func<ITrackingSink, ValueTask> call)
    {
        Exception? first = null;
        foreach (var sink in _sinks)
        {
            try
            {
                await call(sink);
            }
            catch (Exception ex)
            {
                first ??= ex;
            }
        }
        if (first is not null)
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(first).Throw();
    }
}
