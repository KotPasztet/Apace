using System.Diagnostics;
using System.Runtime.InteropServices;
using Serilog;

namespace Apace.Common.Utils;

/// <summary>
/// Ties child processes to the lifetime of the current process on Windows.
/// Processes added here are put into a job object with
/// <c>JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE</c>, so the OS kills them (and their
/// own children) when this process exits for any reason, including a crash or
/// being killed. Without this, java servers outlive their launcher and keep
/// holding ports and world files.
/// </summary>
public static partial class ChildProcessJob
{
    private const int JobObjectExtendedLimitInformation = 9;
    private const uint JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE = 0x2000;

    private static readonly Lazy<nint> _job = new Lazy<nint>(CreateJob);

    public static void Add(Process process)
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        nint job = _job.Value;
        if (job == 0)
        {
            return;
        }

        try
        {
            if (!AssignProcessToJobObject(job, process.Handle))
            {
                Log.Warning($"Could not tie process {process.Id} to the launcher lifetime (error {Marshal.GetLastPInvokeError()})");
            }
        }
        catch (InvalidOperationException)
        {
            // process has already exited
        }
    }

    private static nint CreateJob()
    {
        if (!OperatingSystem.IsWindows())
        {
            return 0;
        }

        nint job = CreateJobObjectW(0, null);
        if (job == 0)
        {
            Log.Warning($"Could not create job object (error {Marshal.GetLastPInvokeError()})");
            return 0;
        }

        var info = new JOBOBJECT_EXTENDED_LIMIT_INFORMATION();
        info.BasicLimitInformation.LimitFlags = JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE;

        if (!SetInformationJobObject(job, JobObjectExtendedLimitInformation, ref info, (uint)Marshal.SizeOf<JOBOBJECT_EXTENDED_LIMIT_INFORMATION>()))
        {
            Log.Warning($"Could not configure job object (error {Marshal.GetLastPInvokeError()})");
            CloseHandle(job);
            return 0;
        }

        // The handle is intentionally never closed: it is released by the OS
        // when this process exits, which is what kills the children.
        return job;
    }

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

    [LibraryImport("kernel32.dll", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    private static partial nint CreateJobObjectW(nint lpJobAttributes, string? lpName);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetInformationJobObject(nint hJob, int infoClass, ref JOBOBJECT_EXTENDED_LIMIT_INFORMATION lpInfo, uint cbInfoLength);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool AssignProcessToJobObject(nint hJob, nint hProcess);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CloseHandle(nint hObject);
}
