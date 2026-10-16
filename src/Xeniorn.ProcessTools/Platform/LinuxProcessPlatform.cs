using System.Diagnostics;
using System.Globalization;
using Xeniorn.ProcessTools.Selection;

namespace Xeniorn.ProcessTools.Platform;

/// <summary>
/// Linux: everything from <c>/proc</c>. Parent and start time from <c>/proc/&lt;pid&gt;/stat</c>, path from the <c>exe</c> link,
/// services from the systemd unit in <c>/proc/&lt;pid&gt;/cgroup</c> (no systemctl call), I/O from <c>/proc/&lt;pid&gt;/io</c>,
/// descriptors from <c>/proc/&lt;pid&gt;/fd</c>.
/// </summary>
internal sealed class LinuxProcessPlatform : PortableProcessPlatform
{
    // USER_HZ is 100 on every Linux architecture that matters; sysconf(_SC_CLK_TCK) is not reachable without P/Invoke.
    private const double ClockTicksPerSecond = 100;

    private readonly string _procRoot;

    /// <summary>Reads the real <c>/proc</c>.</summary>
    public LinuxProcessPlatform() : this("/proc")
    {
    }

    /// <summary>Reads a <c>/proc</c> laid out under <paramref name="procRoot"/> (tests use a fake tree).</summary>
    internal LinuxProcessPlatform(string procRoot)
    {
        _procRoot = procRoot;
    }

    /// <inheritdoc />
    public override IReadOnlyList<ProcessInfo> ListProcesses()
    {
        var result = new List<ProcessInfo>();
        var bootTimeUtc = ReadBootTimeUtc();
        foreach (var dir in Directory.EnumerateDirectories(_procRoot))
        {
            if (!int.TryParse(Path.GetFileName(dir), NumberStyles.None, CultureInfo.InvariantCulture, out var pid))
                continue;
            try
            {
                var stat = ParseStat(File.ReadAllText(Path.Combine(dir, "stat")));
                if (stat is null)
                    continue;
                var path = ReadExePath(dir);
                var name = path is not null ? Path.GetFileNameWithoutExtension(path) : stat.Value.Comm;
                // comm is truncated to 15 characters; the exe name is not, so prefer it. Scripts and interpreters keep their comm.
                if (name.Length == 0)
                    name = stat.Value.Comm;
                DateTime? start = bootTimeUtc is { } boot ? boot.AddSeconds(stat.Value.StartTicks / ClockTicksPerSecond) : null;
                result.Add(new ProcessInfo(pid, stat.Value.ParentPid, name, path, start));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // the process ended while listing, or is not readable: skip it
            }
        }
        return result;
    }

    /// <inheritdoc />
    public override IReadOnlyCollection<int> GetServiceProcessIds(string pattern)
    {
        var ids = new List<int>();
        foreach (var dir in Directory.EnumerateDirectories(_procRoot))
        {
            if (!int.TryParse(Path.GetFileName(dir), NumberStyles.None, CultureInfo.InvariantCulture, out var pid))
                continue;
            try
            {
                var unit = ParseServiceUnit(File.ReadAllText(Path.Combine(dir, "cgroup")));
                if (unit is null)
                    continue;
                var bare = unit[..^".service".Length];
                if (Glob.IsMatch(pattern, unit) || Glob.IsMatch(pattern, bare))
                    ids.Add(pid);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // gone or unreadable
            }
        }
        return ids;
    }

    /// <inheritdoc />
    protected override Func<Process, (double Read, double Write)?>? IoReader => process =>
    {
        double? read = null, write = null;
        foreach (var line in File.ReadLines(Path.Combine(_procRoot, process.Id.ToString(CultureInfo.InvariantCulture), "io")))
        {
            if (line.StartsWith("rchar:", StringComparison.Ordinal))
                read = double.Parse(line.AsSpan(6).Trim(), CultureInfo.InvariantCulture);
            else if (line.StartsWith("wchar:", StringComparison.Ordinal))
                write = double.Parse(line.AsSpan(6).Trim(), CultureInfo.InvariantCulture);
        }
        return read is { } r && write is { } w ? (r, w) : null;
    };

    /// <inheritdoc />
    protected override Func<Process, double?>? HandleCountReader => process =>
        Directory.EnumerateFileSystemEntries(Path.Combine(_procRoot, process.Id.ToString(CultureInfo.InvariantCulture), "fd")).Count();

    /// <summary>Parses <c>/proc/&lt;pid&gt;/stat</c>: the command name is in parentheses and may itself contain spaces and parentheses.</summary>
    internal static (string Comm, int ParentPid, long StartTicks)? ParseStat(string stat)
    {
        var open = stat.IndexOf('(');
        var close = stat.LastIndexOf(')');
        if (open < 0 || close < open)
            return null;
        var comm = stat.Substring(open + 1, close - open - 1);
        // after ")": state(3) ppid(4) ... starttime(22); field n is at index n-3 of what follows
        var rest = stat[(close + 1)..].Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (rest.Length < 20)
            return null;
        return (comm, int.Parse(rest[1], CultureInfo.InvariantCulture), long.Parse(rest[19], CultureInfo.InvariantCulture));
    }

    /// <summary>The systemd service unit (the last <c>*.service</c> path segment) in the content of a cgroup file, or null.</summary>
    internal static string? ParseServiceUnit(string cgroup)
    {
        string? unit = null;
        foreach (var line in cgroup.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            // "0::/system.slice/nginx.service" (v2) or "1:name=systemd:/system.slice/nginx.service" (v1)
            var path = line[(line.LastIndexOf(':') + 1)..];
            foreach (var segment in path.Split('/', StringSplitOptions.RemoveEmptyEntries))
            {
                if (segment.EndsWith(".service", StringComparison.Ordinal))
                    unit = segment;
            }
        }
        return unit;
    }

    private DateTime? ReadBootTimeUtc()
    {
        try
        {
            foreach (var line in File.ReadLines(Path.Combine(_procRoot, "stat")))
            {
                if (line.StartsWith("btime ", StringComparison.Ordinal))
                    return DateTime.UnixEpoch.AddSeconds(long.Parse(line.AsSpan(6).Trim(), CultureInfo.InvariantCulture));
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // fall through
        }
        return null;
    }

    private static string? ReadExePath(string processDir)
    {
        try
        {
            var target = File.ResolveLinkTarget(Path.Combine(processDir, "exe"), returnFinalTarget: false)?.FullName;
            const string deleted = " (deleted)";
            return target is not null && target.EndsWith(deleted, StringComparison.Ordinal) ? target[..^deleted.Length] : target;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null; // kernel threads and other users' processes
        }
    }
}
