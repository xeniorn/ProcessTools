using Xeniorn.ProcessTools.Platform;

namespace Xeniorn.ProcessTools.Selection;

/// <summary>
/// Says which processes to track. Evaluated against the full process list on every sample, so processes that start
/// later (or restart under a new PID) are picked up automatically. Build with the factory methods and combine with
/// <c>|</c>, <c>&amp;</c> and <c>!</c>:
/// <code>
/// ProcessSelector.Name("Spectronaut") | ProcessSelector.Service("MyService")
/// ProcessSelector.Path(@"C:\Program Files\Biognosys\*") &amp; !ProcessSelector.Name("*updater*")
/// </code>
/// </summary>
public abstract class ProcessSelector
{
    /// <summary>True when <paramref name="process"/> is selected.</summary>
    public abstract bool Matches(ProcessInfo process, ProcessSelectionContext context);

    /// <summary>Human-readable description, used in logs.</summary>
    public abstract override string ToString();

    /// <summary>A process by PID. Matches whatever has that PID, so prefer name/path/service for long-running tracking.</summary>
    public static ProcessSelector Pid(int processId) => new PidSelector(processId);

    /// <summary>
    /// Processes whose executable name matches <paramref name="pattern"/>: case-insensitive, <c>*</c> and <c>?</c>
    /// wildcards, with or without a trailing ".exe" ("Spectronaut" and "Spectronaut.exe" are the same).
    /// </summary>
    public static ProcessSelector Name(string pattern) => new NameSelector(pattern);

    /// <summary>
    /// Processes whose executable path matches <paramref name="pattern"/>: case-insensitive, <c>*</c> and <c>?</c> wildcards,
    /// either slash accepted. Processes whose path cannot be read never match.
    /// </summary>
    public static ProcessSelector Path(string pattern) => new PathSelector(pattern);

    /// <summary>
    /// Processes belonging to a service whose name matches <paramref name="pattern"/> (wildcards allowed).
    /// Windows: the Service Control Manager's service name or display name. Linux: the systemd unit, with or without ".service".
    /// Services sharing a host process (svchost) all resolve to the same PID.
    /// </summary>
    public static ProcessSelector Service(string pattern) => new ServiceSelector(pattern);

    /// <summary>Processes that are descendants (children, grandchildren, ...) of the processes <paramref name="root"/> selects.</summary>
    /// <param name="root">Selects the ancestors.</param>
    /// <param name="includeRoot">Also select the ancestors themselves.</param>
    public static ProcessSelector DescendantsOf(ProcessSelector root, bool includeRoot = false) => new DescendantSelector(root, includeRoot);

    /// <summary>Direct children of the processes <paramref name="parent"/> selects.</summary>
    public static ProcessSelector ChildrenOf(ProcessSelector parent) => new ChildSelector(parent);

    /// <summary>Every process (mostly useful as the left side of an exclusion: <c>All() &amp; !Name("x")</c>).</summary>
    public static ProcessSelector All() => new AllSelector();

    /// <summary>Selects what any of <paramref name="selectors"/> selects.</summary>
    public static ProcessSelector AnyOf(params ProcessSelector[] selectors) => new AnyOfSelector(selectors);

    /// <summary>Selects what all of <paramref name="selectors"/> select.</summary>
    public static ProcessSelector AllOf(params ProcessSelector[] selectors) => new AllOfSelector(selectors);

    /// <summary>Union.</summary>
    public static ProcessSelector operator |(ProcessSelector left, ProcessSelector right) => AnyOf(left, right);

    /// <summary>Intersection.</summary>
    public static ProcessSelector operator &(ProcessSelector left, ProcessSelector right) => AllOf(left, right);

    /// <summary>Complement.</summary>
    public static ProcessSelector operator !(ProcessSelector selector) => new NotSelector(selector);

    private sealed class PidSelector(int processId) : ProcessSelector
    {
        public override bool Matches(ProcessInfo process, ProcessSelectionContext context) => process.ProcessId == processId;
        public override string ToString() => $"pid {processId}";
    }

    private sealed class NameSelector(string pattern) : ProcessSelector
    {
        private readonly string _pattern = StripExe(pattern);
        public override bool Matches(ProcessInfo process, ProcessSelectionContext context) => Glob.IsMatch(_pattern, process.Name);
        public override string ToString() => $"name '{pattern}'";
        private static string StripExe(string p) => p.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? p[..^4] : p;
    }

    private sealed class PathSelector(string pattern) : ProcessSelector
    {
        public override bool Matches(ProcessInfo process, ProcessSelectionContext context) =>
            process.Path is not null && Glob.IsMatch(Normalize(pattern), Normalize(process.Path));
        public override string ToString() => $"path '{pattern}'";
        private static string Normalize(string p) => p.Replace('\\', '/');
    }

    private sealed class ServiceSelector(string pattern) : ProcessSelector
    {
        public override bool Matches(ProcessInfo process, ProcessSelectionContext context) =>
            context.GetServiceProcessIds(pattern).Contains(process.ProcessId);
        public override string ToString() => $"service '{pattern}'";
    }

    private sealed class DescendantSelector(ProcessSelector root, bool includeRoot) : ProcessSelector
    {
        public override bool Matches(ProcessInfo process, ProcessSelectionContext context)
        {
            var roots = context.Resolve(root);
            if (includeRoot && roots.Contains(process.ProcessId))
                return true;
            // walk up from the process; bounded by the number of processes so a parent-id cycle cannot hang
            var current = process.ParentProcessId;
            for (var hops = 0; current is { } pid && hops < context.ProcessCount; hops++)
            {
                if (roots.Contains(pid))
                    return true;
                current = context.TryGet(pid)?.ParentProcessId;
            }
            return false;
        }

        public override string ToString() => $"descendants of ({root}){(includeRoot ? " incl. root" : "")}";
    }

    private sealed class ChildSelector(ProcessSelector parent) : ProcessSelector
    {
        public override bool Matches(ProcessInfo process, ProcessSelectionContext context) =>
            process.ParentProcessId is { } ppid && context.Resolve(parent).Contains(ppid);
        public override string ToString() => $"children of ({parent})";
    }

    private sealed class AllSelector : ProcessSelector
    {
        public override bool Matches(ProcessInfo process, ProcessSelectionContext context) => true;
        public override string ToString() => "all processes";
    }

    private sealed class AnyOfSelector(ProcessSelector[] selectors) : ProcessSelector
    {
        public override bool Matches(ProcessInfo process, ProcessSelectionContext context) => selectors.Any(s => s.Matches(process, context));
        public override string ToString() => string.Join(" OR ", selectors.Select(s => $"({s})"));
    }

    private sealed class AllOfSelector(ProcessSelector[] selectors) : ProcessSelector
    {
        public override bool Matches(ProcessInfo process, ProcessSelectionContext context) => selectors.All(s => s.Matches(process, context));
        public override string ToString() => string.Join(" AND ", selectors.Select(s => $"({s})"));
    }

    private sealed class NotSelector(ProcessSelector inner) : ProcessSelector
    {
        public override bool Matches(ProcessInfo process, ProcessSelectionContext context) => !inner.Matches(process, context);
        public override string ToString() => $"NOT ({inner})";
    }
}
