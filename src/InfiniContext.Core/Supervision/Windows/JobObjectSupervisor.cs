using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Serilog;

namespace InfiniContext.Core.Supervision.Windows;

/// <summary>
/// Windows-only <see cref="IProcessSupervisor"/>: creates a Job Object with
/// <c>JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE</c> at construction, assigns each attached child to it,
/// and lets the OS terminate every descendant when the job handle closes (which happens on
/// <see cref="Dispose"/> or when the host process dies for any reason — clean exit, crash,
/// taskkill).
///
/// <para>
/// The kill flag is set <em>before</em> the first <see cref="Attach(Process)"/> call so children
/// inherit the limit immediately. Per-child assignment must happen as soon as possible after
/// <c>Process.Start()</c>; descendants spawned before assignment are not guaranteed to be in the
/// job (Windows tracks the job at the kernel object level, but the race window is ours to manage).
/// </para>
/// </summary>
[SupportedOSPlatform("windows")]
public sealed partial class JobObjectSupervisor : IProcessSupervisor
{
    private readonly ILogger _logger;
    private readonly object _gate = new();
    private nint _job;
    private bool _disposed;
    private bool _detached;

    public JobObjectSupervisor(ILogger? logger = null)
    {
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException($"{nameof(JobObjectSupervisor)} is Windows-only.");

        _logger = logger ?? Log.Logger;

        _job = CreateJobObjectW(nint.Zero, null);
        if (_job == nint.Zero)
            throw new InvalidOperationException(
                $"CreateJobObjectW failed: Win32 error {Marshal.GetLastPInvokeError()}.");

        var info = new JOBOBJECT_EXTENDED_LIMIT_INFORMATION
        {
            BasicLimitInformation = new JOBOBJECT_BASIC_LIMIT_INFORMATION
            {
                LimitFlags = JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE,
            },
        };

        if (!SetInformationJobObject(_job, JobObjectExtendedLimitInformation, in info,
                (uint)Marshal.SizeOf<JOBOBJECT_EXTENDED_LIMIT_INFORMATION>()))
        {
            var err = Marshal.GetLastPInvokeError();
            CloseHandle(_job);
            _job = nint.Zero;
            throw new InvalidOperationException(
                $"SetInformationJobObject(KILL_ON_JOB_CLOSE) failed: Win32 error {err}.");
        }

        _logger.Information("supervisor: Windows Job Object created (kill_on_close=true)");
    }

    public void Attach(Process process)
    {
        ArgumentNullException.ThrowIfNull(process);

        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);

            int pid;
            try { pid = process.Id; }
            catch (InvalidOperationException ex)
            {
                throw new InvalidOperationException(
                    "Cannot attach a process that has not been started.", ex);
            }

            nint handle;
            try { handle = process.Handle; }
            catch (InvalidOperationException ex)
            {
                throw new InvalidOperationException(
                    "Process has no OS handle (not started or already cleaned up).", ex);
            }

            if (!AssignProcessToJobObject(_job, handle))
            {
                var err = Marshal.GetLastPInvokeError();
                throw new InvalidOperationException(
                    $"AssignProcessToJobObject failed for pid={pid}: Win32 error {err}.");
            }

            _logger.Information("supervisor: attached pid={Pid}", pid);
        }
    }

    public void DetachAll()
    {
        lock (_gate)
        {
            if (_disposed || _detached || _job == nint.Zero) return;

            var info = new JOBOBJECT_EXTENDED_LIMIT_INFORMATION();
            if (!SetInformationJobObject(_job, JobObjectExtendedLimitInformation, in info,
                    (uint)Marshal.SizeOf<JOBOBJECT_EXTENDED_LIMIT_INFORMATION>()))
            {
                var err = Marshal.GetLastPInvokeError();
                throw new InvalidOperationException(
                    $"SetInformationJobObject(clear) failed: Win32 error {err}.");
            }

            _detached = true;
            _logger.Information("supervisor: detached all (kill_on_close cleared)");
        }
    }

    public void Dispose()
    {
        nint handle;
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            handle = _job;
            _job = nint.Zero;
        }

        if (handle != nint.Zero)
        {
            CloseHandle(handle);
            _logger.Information("supervisor: disposed (job handle closed)");
        }
    }

    private const int JobObjectExtendedLimitInformation = 9;
    private const uint JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE = 0x00002000;

    [LibraryImport("kernel32.dll", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    private static partial nint CreateJobObjectW(nint lpJobAttributes, string? lpName);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetInformationJobObject(
        nint hJob,
        int infoClass,
        in JOBOBJECT_EXTENDED_LIMIT_INFORMATION info,
        uint cbInfoLength);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool AssignProcessToJobObject(nint hJob, nint hProcess);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CloseHandle(nint hObject);

    [StructLayout(LayoutKind.Sequential)]
    private struct JOBOBJECT_BASIC_LIMIT_INFORMATION
    {
        public long PerProcessUserTimeLimit;
        public long PerJobUserTimeLimit;
        public uint LimitFlags;
        public nuint MinimumWorkingSetSize;
        public nuint MaximumWorkingSetSize;
        public uint ActiveProcessLimit;
        public nuint Affinity;
        public uint PriorityClass;
        public uint SchedulingClass;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct IO_COUNTERS
    {
        public ulong ReadOperationCount;
        public ulong WriteOperationCount;
        public ulong OtherOperationCount;
        public ulong ReadTransferCount;
        public ulong WriteTransferCount;
        public ulong OtherTransferCount;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct JOBOBJECT_EXTENDED_LIMIT_INFORMATION
    {
        public JOBOBJECT_BASIC_LIMIT_INFORMATION BasicLimitInformation;
        public IO_COUNTERS IoInfo;
        public nuint ProcessMemoryLimit;
        public nuint JobMemoryLimit;
        public nuint PeakProcessMemoryUsed;
        public nuint PeakJobMemoryUsed;
    }
}
