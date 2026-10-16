using Xeniorn.ProcessTools;
using Xeniorn.ProcessTools.Platform;

namespace Xeniorn.ProcessTools.Test;

/// <summary>A machine the test controls: a list of processes, scripted metric values, and exit state.</summary>
internal sealed class FakePlatform : IProcessPlatform
{
    public Dictionary<int, FakeProcess> Processes { get; } = new();
    public Dictionary<string, List<int>> Services { get; } = new(StringComparer.OrdinalIgnoreCase);

    public static readonly DateTime T0 = new(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc);

    public FakeProcess Add(int pid, string name, int? parent = null, string? path = null, DateTime? start = null)
    {
        var p = new FakeProcess(new ProcessInfo(pid, parent, name, path, start ?? T0));
        Processes[pid] = p;
        return p;
    }

    public IReadOnlyList<ProcessInfo> ListProcesses() => Processes.Values.Where(p => !p.Gone).Select(p => p.Info).ToList();

    public IReadOnlyCollection<int> GetServiceProcessIds(string pattern) =>
        Services.Where(s => Xeniorn.ProcessTools.Selection.Glob.IsMatch(pattern, s.Key)).SelectMany(s => s.Value).ToList();

    public IProcessHandle? Open(ProcessInfo process) =>
        Processes.TryGetValue(process.ProcessId, out var p) && !p.Gone && !p.DenyOpen ? new Handle(p) : null;

    private sealed class Handle(FakeProcess process) : IProcessHandle
    {
        public IReadOnlyDictionary<ProcessMetric, double>? Read(IReadOnlySet<ProcessMetric> wanted) =>
            process.Exited ? null : process.Values.Where(kv => wanted.Contains(kv.Key)).ToDictionary(kv => kv.Key, kv => kv.Value);

        public ProcessExitStatus? GetExitStatus() => process.Exited ? new ProcessExitStatus(process.ExitCode, process.ExitTimeUtc) : null;
        public void Dispose() { }
    }
}

internal sealed class FakeProcess(ProcessInfo info)
{
    public ProcessInfo Info { get; } = info;
    public Dictionary<ProcessMetric, double> Values { get; } = new();
    public bool Exited { get; set; }
    public bool Gone { get; set; }
    public bool DenyOpen { get; set; }
    public int? ExitCode { get; set; }
    public DateTime? ExitTimeUtc { get; set; }

    /// <summary>The process exits with <paramref name="code"/>: its handle reports the exit and it leaves the process list.</summary>
    public void Exit(int code, DateTime? at = null)
    {
        Exited = true;
        Gone = true;
        ExitCode = code;
        ExitTimeUtc = at;
    }
}
