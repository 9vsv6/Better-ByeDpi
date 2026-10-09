using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;

namespace ByeDpiPc.Core;

/// <summary>
/// A console engine (ciadpi / tun2socks) run hidden, with its output sent to the log.
/// Every child is placed in a kill-on-close job, so a crash of this app never leaves a
/// stray engine or Wintun adapter (and therefore no stale routes) behind.
/// </summary>
public sealed class ChildProcess : IDisposable
{
    private readonly Process _process;
    private readonly string _tag;

    public static string BinDirectory { get; } = Path.Combine(AppContext.BaseDirectory, "bin");

    private ChildProcess(Process process, string tag)
    {
        _process = process;
        _tag = tag;
    }

    public int Id => _process.Id;
    public bool HasExited => _process.HasExited;
    public int ExitCode => _process.ExitCode;
    public event Action? Exited;

    public static string BinPath(string exe)
    {
        var path = Path.Combine(BinDirectory, exe);
        if (!File.Exists(path))
            throw new FileNotFoundException(
                $"{exe} is missing from {BinDirectory}. Run scripts\\fetch-deps.ps1 and rebuild.", path);
        return path;
    }

    public static ChildProcess Start(string exe, IEnumerable<string> args, string tag, bool quiet = false)
    {
        var info = new ProcessStartInfo(BinPath(exe))
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            WorkingDirectory = BinDirectory,
        };
        foreach (var a in args) info.ArgumentList.Add(a);

        var process = new Process { StartInfo = info, EnableRaisingEvents = true };
        var child = new ChildProcess(process, tag);
        if (!quiet)
        {
            process.OutputDataReceived += (_, e) => { if (!string.IsNullOrWhiteSpace(e.Data)) Log.Write($"[{tag}] {e.Data}"); };
            process.ErrorDataReceived += (_, e) => { if (!string.IsNullOrWhiteSpace(e.Data)) Log.Write($"[{tag}] {e.Data}"); };
        }
        process.Exited += (_, _) => child.Exited?.Invoke();

        if (!quiet) Log.Write($"[{tag}] start: {exe} {Arguments.Join(args)}");
        process.Start();
        KillOnCloseJob.Add(process);
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        return child;
    }

    public Task WaitForExitAsync(CancellationToken token) => _process.WaitForExitAsync(token);

    public void Stop()
    {
        try
        {
            if (!_process.HasExited)
            {
                // tun2socks removes its adapter on Ctrl+C, but console signals can't reach a
                // windowless child; killing is fine because the adapter dies with its handle.
                _process.Kill(entireProcessTree: true);
                _process.WaitForExit(3000);
            }
        }
        catch (Exception e) when (e is InvalidOperationException or Win32Exception)
        {
            // Already gone.
        }
    }

    public void Dispose()
    {
        Stop();
        _process.Dispose();
    }
}

internal static class KillOnCloseJob
{
    private static readonly IntPtr Handle = Create();

    public static void Add(Process process)
    {
        if (Handle != IntPtr.Zero) AssignProcessToJobObject(Handle, process.Handle);
    }

    private static IntPtr Create()
    {
        var job = CreateJobObject(IntPtr.Zero, null);
        var info = new JOBOBJECT_EXTENDED_LIMIT_INFORMATION
        {
            BasicLimitInformation = new JOBOBJECT_BASIC_LIMIT_INFORMATION { LimitFlags = 0x2000 }, // KILL_ON_JOB_CLOSE
        };
        int length = Marshal.SizeOf(info);
        var ptr = Marshal.AllocHGlobal(length);
        try
        {
            Marshal.StructureToPtr(info, ptr, false);
            if (!SetInformationJobObject(job, 9 /* ExtendedLimitInformation */, ptr, (uint)length))
                Log.Write("Could not configure the child-process job; engines may outlive a crash.");
        }
        finally
        {
            Marshal.FreeHGlobal(ptr);
        }
        return job;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct JOBOBJECT_BASIC_LIMIT_INFORMATION
    {
        public long PerProcessUserTimeLimit;
        public long PerJobUserTimeLimit;
        public uint LimitFlags;
        public UIntPtr MinimumWorkingSetSize;
        public UIntPtr MaximumWorkingSetSize;
        public uint ActiveProcessLimit;
        public UIntPtr Affinity;
        public uint PriorityClass;
        public uint SchedulingClass;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct IO_COUNTERS
    {
        public ulong ReadOperationCount, WriteOperationCount, OtherOperationCount;
        public ulong ReadTransferCount, WriteTransferCount, OtherTransferCount;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct JOBOBJECT_EXTENDED_LIMIT_INFORMATION
    {
        public JOBOBJECT_BASIC_LIMIT_INFORMATION BasicLimitInformation;
        public IO_COUNTERS IoInfo;
        public UIntPtr ProcessMemoryLimit;
        public UIntPtr JobMemoryLimit;
        public UIntPtr PeakProcessMemoryUsed;
        public UIntPtr PeakJobMemoryUsed;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr CreateJobObject(IntPtr attributes, string? name);

    [DllImport("kernel32.dll")]
    private static extern bool SetInformationJobObject(IntPtr job, int infoClass, IntPtr info, uint length);

    [DllImport("kernel32.dll")]
    private static extern bool AssignProcessToJobObject(IntPtr job, IntPtr process);
}
