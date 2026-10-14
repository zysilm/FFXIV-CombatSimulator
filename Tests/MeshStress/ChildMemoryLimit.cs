using System.Diagnostics;
using System.Runtime.InteropServices;

// RSS polling does not protect the host against one suddenly huge native allocation.
// A Windows job also caps this child's private committed memory, including BEPU pools.
sealed class ChildMemoryLimit : IDisposable
{
    readonly IntPtr handle;
    ChildMemoryLimit(IntPtr handle)=>this.handle=handle;
    public static ChildMemoryLimit Attach(Process process,long bytes)
    {
        var job=CreateJobObject(IntPtr.Zero,null);
        var limits=new ExtendedLimits{Basic=new BasicLimits{Flags=0x100|0x2000},ProcessMemoryLimit=(UIntPtr)(ulong)bytes};
        if(job==IntPtr.Zero||!SetInformationJobObject(job,9,ref limits,(uint)Marshal.SizeOf<ExtendedLimits>())||!AssignProcessToJobObject(job,process.Handle)) {
            var error=Marshal.GetLastWin32Error();process.Kill(true);if(job!=IntPtr.Zero)CloseHandle(job);
            throw new InvalidOperationException($"Cannot safely apply child commit limit; refusing stress run. Win32 error {error}");
        }
        return new ChildMemoryLimit(job);
    }
    public void Dispose()=>CloseHandle(handle);
    [StructLayout(LayoutKind.Sequential)]struct BasicLimits { public long PerProcessTime,PerJobTime;public uint Flags;public UIntPtr MinimumWorkingSet,MaximumWorkingSet;public uint ActiveProcessLimit;public UIntPtr Affinity;public uint Priority,Scheduling; }
    [StructLayout(LayoutKind.Sequential)]struct IoCounters {public ulong ReadOperations,WriteOperations,OtherOperations,ReadBytes,WriteBytes,OtherBytes;}
    [StructLayout(LayoutKind.Sequential)]struct ExtendedLimits {public BasicLimits Basic;public IoCounters Io;public UIntPtr ProcessMemoryLimit,JobMemoryLimit,PeakProcessMemory,PeakJobMemory;}
    [DllImport("kernel32.dll",CharSet=CharSet.Unicode,SetLastError=true)]static extern IntPtr CreateJobObject(IntPtr attributes,string? name);
    [DllImport("kernel32.dll",SetLastError=true)]static extern bool SetInformationJobObject(IntPtr job,int infoClass,ref ExtendedLimits limits,uint length);
    [DllImport("kernel32.dll",SetLastError=true)]static extern bool AssignProcessToJobObject(IntPtr job,IntPtr process);
    [DllImport("kernel32.dll")]static extern bool CloseHandle(IntPtr handle);
}
