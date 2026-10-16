namespace Xeniorn.ProcessTools;

/// <summary>
/// Who a process is, independent of its numbers. A PID alone is not an identity (PIDs are reused), so a tracked
/// process is identified by PID plus start time.
/// </summary>
public readonly record struct ProcessIdentity(int ProcessId, DateTime StartTimeUtc)
{
    /// <inheritdoc />
    public override string ToString() => $"{ProcessId}@{StartTimeUtc:yyyy-MM-dd HH:mm:ss}Z";
}

/// <summary>Descriptive facts about a running process, as listed by the platform.</summary>
/// <param name="ProcessId">PID.</param>
/// <param name="ParentProcessId">PID of the parent, when the platform can say (it may no longer exist).</param>
/// <param name="Name">Executable name without directory and without ".exe" (like <c>Process.ProcessName</c>).</param>
/// <param name="Path">Full path of the executable, when readable.</param>
/// <param name="StartTimeUtc">When the process started, when readable.</param>
public sealed record ProcessInfo(int ProcessId, int? ParentProcessId, string Name, string? Path, DateTime? StartTimeUtc);
