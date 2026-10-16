using Xeniorn.ProcessTools.Platform;

namespace Xeniorn.ProcessTools.Selection;

/// <summary>
/// One consistent view of the machine's processes while a selector is evaluated: the process list, parent/child
/// relations and (lazily) the service-to-PID map, so a selector never sees a half-changed system.
/// </summary>
public sealed class ProcessSelectionContext
{
    private readonly IProcessPlatform _platform;
    private readonly Dictionary<int, ProcessInfo> _byPid = new();
    private readonly Dictionary<string, HashSet<int>> _serviceCache = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<ProcessSelector, HashSet<int>> _resolved = new(ReferenceEqualityComparer.Instance);

    /// <summary>Creates a context over <paramref name="processes"/>.</summary>
    public ProcessSelectionContext(IReadOnlyList<ProcessInfo> processes, IProcessPlatform platform)
    {
        _platform = platform;
        Processes = processes;
        foreach (var p in processes)
            _byPid[p.ProcessId] = p;
    }

    /// <summary>All processes in the snapshot.</summary>
    public IReadOnlyList<ProcessInfo> Processes { get; }

    /// <summary>Number of processes in the snapshot.</summary>
    public int ProcessCount => Processes.Count;

    /// <summary>The process with this PID, or null.</summary>
    public ProcessInfo? TryGet(int processId) => _byPid.GetValueOrDefault(processId);

    /// <summary>PIDs of the processes that <paramref name="selector"/> selects (cached for the life of this context).</summary>
    public IReadOnlySet<int> Resolve(ProcessSelector selector)
    {
        if (_resolved.TryGetValue(selector, out var cached))
            return cached;
        var set = Processes.Where(p => selector.Matches(p, this)).Select(p => p.ProcessId).ToHashSet();
        _resolved[selector] = set;
        return set;
    }

    /// <summary>PIDs of the processes belonging to services whose name matches <paramref name="pattern"/>.</summary>
    public IReadOnlySet<int> GetServiceProcessIds(string pattern)
    {
        if (!_serviceCache.TryGetValue(pattern, out var set))
        {
            set = _platform.GetServiceProcessIds(pattern).ToHashSet();
            _serviceCache[pattern] = set;
        }
        return set;
    }

    /// <summary>PIDs of the direct children of each process, built once for descendant counting.</summary>
    internal Dictionary<int, List<int>> BuildChildIndex()
    {
        var index = new Dictionary<int, List<int>>();
        foreach (var p in Processes)
        {
            if (p.ParentProcessId is not { } parent || parent == p.ProcessId)
                continue;
            if (!index.TryGetValue(parent, out var list))
                index[parent] = list = [];
            list.Add(p.ProcessId);
        }
        return index;
    }
}
