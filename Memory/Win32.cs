using System.Runtime.InteropServices;

namespace R2PrismRuntime.Memory;

internal static class Win32
{
    public const uint PROCESS_VM_OPERATION        = 0x0008;
    public const uint PROCESS_VM_READ             = 0x0010;
    public const uint PROCESS_VM_WRITE            = 0x0020;
    public const uint PROCESS_QUERY_INFORMATION   = 0x0400;
    public const uint STILL_ACTIVE                = 259;
    public const uint MEM_COMMIT          = 0x1000;
    public const uint MEM_PRIVATE         = 0x20000;
    public const uint MEM_MAPPED          = 0x40000;
    public const uint MEM_IMAGE           = 0x1000000;
    public const uint PAGE_READONLY       = 0x02;
    public const uint PAGE_READWRITE      = 0x04;
    public const uint PAGE_WRITECOPY      = 0x08;
    public const uint PAGE_EXECUTE_READ   = 0x20;
    public const uint PAGE_EXECUTE_READWRITE = 0x40;
    public const uint PAGE_EXECUTE_WRITECOPY = 0x80;
    public const uint PAGE_GUARD          = 0x100;

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern IntPtr OpenProcess(uint access, bool inherit, int pid);

    [DllImport("kernel32.dll")]
    public static extern bool CloseHandle(IntPtr handle);

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern bool ReadProcessMemory(
        IntPtr hProcess, ulong lpBaseAddress,
        byte[] lpBuffer, int nSize, out int lpBytesRead);

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern bool WriteProcessMemory(
        IntPtr hProcess, ulong lpBaseAddress,
        byte[] lpBuffer, int nSize, out int lpBytesWritten);

    [DllImport("kernel32.dll")]
    public static extern ulong VirtualQueryEx(
        IntPtr hProcess, ulong lpAddress,
        out MEMORY_BASIC_INFORMATION lpBuffer, uint dwLength);

    [DllImport("kernel32.dll")]
    public static extern bool GetExitCodeProcess(IntPtr hProcess, out uint exitCode);

    public const uint MEM_RESERVE = 0x2000;
    public const uint MEM_FREE    = 0x10000;

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern ulong VirtualAllocEx(IntPtr hProcess, ulong address, UIntPtr size, uint type, uint protect);

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern bool VirtualProtectEx(IntPtr hProcess, ulong address, UIntPtr size, uint newProtect, out uint oldProtect);

    [DllImport("kernel32.dll")]
    public static extern bool FlushInstructionCache(IntPtr hProcess, ulong address, UIntPtr size);

    [StructLayout(LayoutKind.Sequential)]
    public struct MEMORY_BASIC_INFORMATION
    {
        public ulong BaseAddress;
        public ulong AllocationBase;
        public uint  AllocationProtect;
        public ushort PartitionId;
        public ulong RegionSize;
        public uint  State;
        public uint  Protect;
        public uint  Type;
    }
}
