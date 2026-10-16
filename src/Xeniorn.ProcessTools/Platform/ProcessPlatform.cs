namespace Xeniorn.ProcessTools.Platform;

/// <summary>Chooses the <see cref="IProcessPlatform"/> for the operating system the program runs on.</summary>
public static class ProcessPlatform
{
    /// <summary>The implementation for the current OS: Windows, Linux, or a portable fallback (no parent/child, no services) elsewhere.</summary>
    public static IProcessPlatform Default { get; } = Create();

    private static IProcessPlatform Create()
    {
        if (OperatingSystem.IsWindows())
            return new WindowsProcessPlatform();
        if (OperatingSystem.IsLinux())
            return new LinuxProcessPlatform();
        return new PortableProcessPlatform();
    }
}
