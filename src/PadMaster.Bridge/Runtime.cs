using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace PadMaster;

public static class SafeDiagnostics
{
    public static string Error(Exception error) => $"{error.GetType().Name} code=0x{error.HResult:X8}" + (error is Win32Exception native ? $" native={native.NativeErrorCode}" : "");
}

public static class RotatingLog
{
    public const int Limit = 1024 * 1024;
    static readonly object Gate = new();
    public static void Write(string path, string text)
    {
        try
        {
            lock (Gate)
            {
                var line = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} {text}{Environment.NewLine}";
                if (new FileInfo(path) is { Exists: true } info && info.Length + System.Text.Encoding.UTF8.GetByteCount(line) > Limit)
                {
                    try { File.Move(path, path + ".1", true); } catch { return; }
                }
                File.AppendAllText(path, line);
            }
        }
        catch { }
    }
}

public sealed class HostGate
{
    int _bigBox, _reported;
    public bool AllowsAssistant(bool bigBox, Action<string> log)
    {
        if (bigBox) Interlocked.Exchange(ref _bigBox, 1);
        if (Volatile.Read(ref _bigBox) == 0) return true;
        if (Interlocked.Exchange(ref _reported, 1) == 0) log("Big Box: assistant not available");
        return false;
    }
}

public static class ChildJobs
{
    static readonly object Gate = new();
    static JobHandle? _lifetime;
    static bool _closed;
    static int _reported;
    public static void Join(Process process, Action<string> log)
    {
        try
        {
            lock (Gate)
            {
                if (_closed) { if (!process.HasExited) process.Kill(true); return; }
                _lifetime ??= Create();
                Assign(_lifetime, process);
            }
        }
        catch (Exception error) { if (Interlocked.Exchange(ref _reported, 1) == 0) log("assistant job unavailable: " + SafeDiagnostics.Error(error)); }
    }
    public static void Shutdown() { lock (Gate) { _closed = true; _lifetime?.Dispose(); _lifetime = null; } }
    public static JobHandle Create()
    {
        var job = CreateJobObject(IntPtr.Zero, null);
        if (job.IsInvalid) { job.Dispose(); throw new Win32Exception(Marshal.GetLastWin32Error()); }
        var limits = new ExtendedLimits { Basic = new BasicLimits { Flags = 0x2000 } };
        if (SetInformationJobObject(job, 9, ref limits, (uint)Marshal.SizeOf<ExtendedLimits>())) return job;
        var error = Marshal.GetLastWin32Error(); job.Dispose(); throw new Win32Exception(error);
    }
    public static void Assign(JobHandle job, Process process) { if (!AssignProcessToJobObject(job, process.Handle)) throw new Win32Exception(Marshal.GetLastWin32Error()); }
    public sealed class JobHandle : SafeHandleZeroOrMinusOneIsInvalid
    {
        public JobHandle() : base(true) { }
        protected override bool ReleaseHandle() => CloseHandle(handle);
    }
    [StructLayout(LayoutKind.Sequential)] struct BasicLimits { public long ProcessTime, JobTime; public uint Flags; public UIntPtr MinWorkingSet, MaxWorkingSet; public uint ActiveProcesses; public UIntPtr Affinity; public uint Priority, Scheduling; }
    [StructLayout(LayoutKind.Sequential)] struct IoCounters { public ulong ReadOperations, WriteOperations, OtherOperations, ReadBytes, WriteBytes, OtherBytes; }
    [StructLayout(LayoutKind.Sequential)] struct ExtendedLimits { public BasicLimits Basic; public IoCounters Io; public UIntPtr ProcessMemory, JobMemory, PeakProcessMemory, PeakJobMemory; }
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] static extern JobHandle CreateJobObject(IntPtr attributes, string? name);
    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] static extern bool SetInformationJobObject(JobHandle job, int kind, ref ExtendedLimits value, uint length);
    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] static extern bool AssignProcessToJobObject(JobHandle job, IntPtr process);
    [DllImport("kernel32.dll")] [return: MarshalAs(UnmanagedType.Bool)] static extern bool CloseHandle(IntPtr handle);
}
