using System.Runtime.InteropServices;

namespace AlwaysOnTopWidget;

// Assigns a child process to a Windows Job Object configured with
// JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE, so the child is killed by the OS the
// moment this process's handle to the job goes away - including on a crash
// or a forced taskkill, not just a clean FormClosing. This is the belt to
// OverlayForm's explicit Process.Kill() suspenders: closing the widget used
// to leave its spawned `pwsh -File usage-widget.ps1` server running forever,
// still listening on port 8484.
sealed class JobObject : IDisposable
{
    IntPtr handle;

    public JobObject()
    {
        handle = CreateJobObject(IntPtr.Zero, null);
        if (handle == IntPtr.Zero)
        {
            Logger.Warn("JobObject: CreateJobObject failed; server child won't be auto-killed on crash.");
            return;
        }

        var info = new JOBOBJECT_BASIC_LIMIT_INFORMATION { LimitFlags = JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE };
        var extendedInfo = new JOBOBJECT_EXTENDED_LIMIT_INFORMATION { BasicLimitInformation = info };

        int length = Marshal.SizeOf<JOBOBJECT_EXTENDED_LIMIT_INFORMATION>();
        IntPtr ptr = Marshal.AllocHGlobal(length);
        try
        {
            Marshal.StructureToPtr(extendedInfo, ptr, false);
            if (!SetInformationJobObject(handle, JobObjectInfoType.ExtendedLimitInformation, ptr, (uint)length))
            {
                Logger.Warn("JobObject: SetInformationJobObject failed; server child won't be auto-killed on crash.");
            }
        }
        finally
        {
            Marshal.FreeHGlobal(ptr);
        }
    }

    public bool AddProcess(IntPtr processHandle)
    {
        if (handle == IntPtr.Zero) return false;
        if (AssignProcessToJobObject(handle, processHandle)) return true;
        Logger.Warn($"JobObject: AssignProcessToJobObject failed (Win32 error {Marshal.GetLastWin32Error()}).");
        return false;
    }

    public void Dispose()
    {
        if (handle == IntPtr.Zero) return;
        CloseHandle(handle);
        handle = IntPtr.Zero;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern IntPtr CreateJobObject(IntPtr lpJobAttributes, string? lpName);

    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool SetInformationJobObject(IntPtr hJob, JobObjectInfoType infoType, IntPtr lpJobObjectInfo, uint cbJobObjectInfoLength);

    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool AssignProcessToJobObject(IntPtr job, IntPtr process);

    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool CloseHandle(IntPtr hObject);

    enum JobObjectInfoType
    {
        ExtendedLimitInformation = 9,
    }

    const uint JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE = 0x2000;

    [StructLayout(LayoutKind.Sequential)]
    struct JOBOBJECT_BASIC_LIMIT_INFORMATION
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
    struct IO_COUNTERS
    {
        public ulong ReadOperationCount;
        public ulong WriteOperationCount;
        public ulong OtherOperationCount;
        public ulong ReadTransferCount;
        public ulong WriteTransferCount;
        public ulong OtherTransferCount;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct JOBOBJECT_EXTENDED_LIMIT_INFORMATION
    {
        public JOBOBJECT_BASIC_LIMIT_INFORMATION BasicLimitInformation;
        public IO_COUNTERS IoInfo;
        public UIntPtr ProcessMemoryLimit;
        public UIntPtr JobMemoryLimit;
        public UIntPtr PeakProcessMemoryUsed;
        public UIntPtr PeakJobMemoryUsed;
    }
}
