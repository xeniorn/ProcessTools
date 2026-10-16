namespace Xeniorn.ProcessTools.Platform;

/// <summary>
/// Everything the library needs from the operating system: list processes, map services to processes, and open a
/// process for reading. Windows and Linux implementations ship; implement this to support another OS or to fake the
/// machine in tests.
/// </summary>
public interface IProcessPlatform
{
    /// <summary>A snapshot of all processes. Properties that cannot be read (access denied, process just exited) are null, the process is still listed.</summary>
    IReadOnlyList<ProcessInfo> ListProcesses();

    /// <summary>PIDs of running services whose name matches <paramref name="pattern"/> (case-insensitive, <c>*</c> and <c>?</c> wildcards). Empty where services are not supported.</summary>
    IReadOnlyCollection<int> GetServiceProcessIds(string pattern);

    /// <summary>
    /// Opens the process for repeated reading, or null if that is not possible (gone, access denied, or the PID now
    /// belongs to a different process than <paramref name="process"/>). Keeping the handle open is also what lets the
    /// exit code be read after the process exits.
    /// </summary>
    IProcessHandle? Open(ProcessInfo process);
}

/// <summary>An open process that can be sampled repeatedly.</summary>
public interface IProcessHandle : IDisposable
{
    /// <summary>
    /// Reads the metrics in <paramref name="wanted"/> that this handle can provide (the derived ones, <see cref="ProcessMetric.CpuPercent"/>,
    /// <see cref="ProcessMetric.ChildCount"/> and <see cref="ProcessMetric.DescendantCount"/>, are computed by the tracker).
    /// Returns null if the process has exited. A metric that cannot be read is left out.
    /// </summary>
    IReadOnlyDictionary<ProcessMetric, double>? Read(IReadOnlySet<ProcessMetric> wanted);

    /// <summary>Null while the process is running; after it exited, what is known about how.</summary>
    ProcessExitStatus? GetExitStatus();
}

/// <summary>How a process ended, as far as the platform can tell.</summary>
/// <param name="ExitCode">Exit code. Null when not readable: on Linux only the parent can read it, so processes the tracker did not start report null.</param>
/// <param name="ExitTimeUtc">When the process exited, when the platform records it (Windows does).</param>
public sealed record ProcessExitStatus(int? ExitCode, DateTime? ExitTimeUtc);
