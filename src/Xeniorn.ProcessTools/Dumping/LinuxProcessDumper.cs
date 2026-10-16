using System.Diagnostics;
using System.Runtime.InteropServices;

namespace Xeniorn.ProcessTools.Dumping;

/// <summary>
/// Linux dumps with <c>createdump</c>, the tool that ships next to the .NET runtime. It only works for managed
/// (.NET) processes, and needs the same user as the process or root. <see cref="DumpKind.Small"/> uses
/// <c>--normal</c> (no heap contents), <see cref="DumpKind.Full"/> uses <c>--full</c>.
/// </summary>
internal sealed class LinuxProcessDumper(string? createDumpPath = null) : IProcessDumper
{
    private static readonly TimeSpan Timeout = TimeSpan.FromMinutes(5);

    private string? ResolvePath()
    {
        var candidate = createDumpPath ?? Path.Combine(RuntimeEnvironment.GetRuntimeDirectory(), "createdump");
        return File.Exists(candidate) ? candidate : null;
    }

    /// <inheritdoc />
    public bool Supports(DumpKind kind) => ResolvePath() is not null;

    /// <inheritdoc />
    public DumpResult Dump(ProcessInfo process, string filePath, DumpKind kind)
    {
        if (ResolvePath() is not { } tool)
            return DumpResult.Fail("createdump was not found next to the .NET runtime; set its path explicitly.");

        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(filePath))!);
            var info = new ProcessStartInfo(tool) { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
            info.ArgumentList.Add(kind == DumpKind.Full ? "--full" : "--normal");
            info.ArgumentList.Add("--name");
            info.ArgumentList.Add(filePath);
            info.ArgumentList.Add(process.ProcessId.ToString(System.Globalization.CultureInfo.InvariantCulture));

            using var run = Process.Start(info)!;
            var output = run.StandardError.ReadToEndAsync();
            if (!run.WaitForExit(Timeout))
            {
                run.Kill(entireProcessTree: true);
                return DumpResult.Fail($"createdump did not finish within {Timeout}.");
            }

            if (run.ExitCode != 0 || !File.Exists(filePath))
                return DumpResult.Fail($"createdump exited with {run.ExitCode} (is the target a .NET process, and are you its user or root?): {output.GetAwaiter().GetResult().Trim()}");
            return DumpResult.Ok(new FileInfo(filePath).Length);
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or IOException or UnauthorizedAccessException)
        {
            return DumpResult.Fail(ex.Message);
        }
    }
}
