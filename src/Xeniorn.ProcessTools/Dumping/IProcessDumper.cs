namespace Xeniorn.ProcessTools.Dumping;

/// <summary>Writes a dump of a running process to a file.</summary>
public interface IProcessDumper
{
    /// <summary>Whether this dumper can produce <paramref name="kind"/> on this machine.</summary>
    bool Supports(DumpKind kind);

    /// <summary>Writes the dump to <paramref name="filePath"/>. Never throws for an expected failure (access denied, process gone): returns it in the result.</summary>
    DumpResult Dump(ProcessInfo process, string filePath, DumpKind kind);
}

/// <summary>Outcome of a dump attempt.</summary>
/// <param name="Succeeded">Whether a dump file was written.</param>
/// <param name="SizeBytes">Size of the file, when written.</param>
/// <param name="Error">What went wrong, when it did.</param>
public sealed record DumpResult(bool Succeeded, long? SizeBytes, string? Error)
{
    /// <summary>A written dump.</summary>
    public static DumpResult Ok(long sizeBytes) => new(true, sizeBytes, null);

    /// <summary>A failure.</summary>
    public static DumpResult Fail(string error) => new(false, null, error);
}

/// <summary>Chooses the <see cref="IProcessDumper"/> for the operating system.</summary>
public static class ProcessDumper
{
    /// <summary>Windows: <c>MiniDumpWriteDump</c>. Linux: the .NET <c>createdump</c> tool (managed processes only). Elsewhere: null (no dumps).</summary>
    public static IProcessDumper? Default { get; } = Create();

    private static IProcessDumper? Create()
    {
        if (OperatingSystem.IsWindows())
            return new WindowsProcessDumper();
        if (OperatingSystem.IsLinux())
            return new LinuxProcessDumper();
        return null;
    }
}
