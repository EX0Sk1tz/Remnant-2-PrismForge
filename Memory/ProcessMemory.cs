using System.Diagnostics;
using System.Text;
using R2PrismRuntime.Diagnostics;

namespace R2PrismRuntime.Memory;

/// <summary>
/// Wraps ReadProcessMemory / WriteProcessMemory and provides
/// AOB scanning across the game's executable regions.
/// </summary>
public class ProcessMemory : IDisposable
{
    private IntPtr _handle = IntPtr.Zero;
    private bool   _disposed;

    public ulong ModuleBase { get; private set; }
    public ulong ModuleSize { get; private set; }
    public int   ProcessId  { get; private set; }
    public bool  IsAttached => _handle != IntPtr.Zero;

    /// Number of ReadProcessMemory calls that failed or returned short since attach.
    /// A rising counter is a strong hint that a pointer chain walks into garbage.
    public long FailedReads => Interlocked.Read(ref _failedReads);
    private long _failedReads;

    // ── Attach ────────────────────────────────────────────────────

    public void Attach(string processName)
    {
        Detach();
        var procs = Process.GetProcessesByName(processName);
        Log.Info($"Found {procs.Length} process(es) named '{processName}'.");
        if (procs.Length == 0)
            throw new InvalidOperationException(
                $"Process '{processName}' not found. Make sure Remnant 2 is running.");
        if (procs.Length > 1)
            Log.Warn($"More than one '{processName}' process — using PID {procs[0].Id}. " +
                     $"Others: {string.Join(", ", procs.Skip(1).Select(p => p.Id))}");

        var proc = procs[0];
        ProcessId = proc.Id;
        const uint access = Win32.PROCESS_VM_READ | Win32.PROCESS_VM_WRITE
                          | Win32.PROCESS_VM_OPERATION | Win32.PROCESS_QUERY_INFORMATION;
        _handle = Win32.OpenProcess(access, false, proc.Id);
        if (_handle == IntPtr.Zero)
        {
            int err = System.Runtime.InteropServices.Marshal.GetLastWin32Error();
            Log.Error($"OpenProcess(PID {proc.Id}) failed, Win32 error {err}.");
            throw new InvalidOperationException(
                $"OpenProcess failed (Win32 error {err}). Try running this app as Administrator.");
        }
        Log.Info($"OpenProcess OK. PID {proc.Id}, handle {Log.Hex((ulong)_handle)}.");

        // Find the main module base and size
        try
        {
            foreach (ProcessModule mod in proc.Modules)
            {
                if (mod.ModuleName!.Equals("Remnant2-Win64-Shipping.exe",
                        StringComparison.OrdinalIgnoreCase))
                {
                    ModuleBase = (ulong)mod.BaseAddress.ToInt64();
                    ModuleSize = (ulong)mod.ModuleMemorySize;
                    Log.Info($"Module {mod.ModuleName}: base {Log.Hex(ModuleBase)}, size {Log.Hex(ModuleSize)}, " +
                             $"file '{mod.FileName}', version '{mod.FileVersionInfo.FileVersion}'.");
                    break;
                }
            }
        }
        catch (Exception ex)
        {
            Log.Error("Enumerating process modules failed (bitness mismatch or access denied?).", ex);
            throw;
        }

        if (ModuleBase == 0)
            throw new InvalidOperationException("Could not locate game module.");

        _failedReads = 0;
    }

    public void Detach()
    {
        if (_handle != IntPtr.Zero)
        {
            Log.Debug($"Closing process handle {Log.Hex((ulong)_handle)}.");
            Win32.CloseHandle(_handle);
            _handle = IntPtr.Zero;
        }
    }

    // ── Read ──────────────────────────────────────────────────────

    /// Reads memory. Returns false if the read failed or was short; buffer is zero-filled on failure.
    public bool TryReadBytes(ulong address, byte[] buffer, int count)
    {
        bool ok = Win32.ReadProcessMemory(_handle, address, buffer, count, out int read);
        if (ok && read == count) return true;
        Interlocked.Increment(ref _failedReads);
        if (!ok) Array.Clear(buffer, 0, count);
        return false;
    }

    public byte[] ReadBytes(ulong address, int count)
    {
        var buf = new byte[count];
        TryReadBytes(address, buf, count);
        return buf;
    }

    public bool TryReadPointer(ulong address, out ulong value)
    {
        var b = new byte[8];
        bool ok = TryReadBytes(address, b, 8);
        value = ok ? BitConverter.ToUInt64(b, 0) : 0;
        return ok;
    }

    public int    ReadInt32(ulong address)
    {
        var b = ReadBytes(address, 4);
        return BitConverter.ToInt32(b, 0);
    }

    public uint   ReadUInt32(ulong address)
    {
        var b = ReadBytes(address, 4);
        return BitConverter.ToUInt32(b, 0);
    }

    public ushort ReadUInt16(ulong address)
    {
        var b = ReadBytes(address, 2);
        return BitConverter.ToUInt16(b, 0);
    }

    public float  ReadFloat(ulong address)
    {
        var b = ReadBytes(address, 4);
        return BitConverter.ToSingle(b, 0);
    }

    public ulong  ReadPointer(ulong address)
    {
        var b = ReadBytes(address, 8);
        return BitConverter.ToUInt64(b, 0);
    }

    public string ReadStringAnsi(ulong address, int maxLen = 256)
    {
        var b = ReadBytes(address, maxLen);
        int len = Array.IndexOf(b, (byte)0);
        if (len < 0) len = maxLen;
        return Encoding.ASCII.GetString(b, 0, len);
    }

    /// Reads a zero-terminated UTF-16 string. Returns null if the memory is unreadable.
    public string? ReadStringWide(ulong address, int maxChars = 128)
    {
        var b = new byte[maxChars * 2];
        if (!TryReadBytes(address, b, b.Length)) return null;
        int len = 0;
        while (len < maxChars && (b[len * 2] | b[len * 2 + 1]) != 0) len++;
        return Encoding.Unicode.GetString(b, 0, len * 2);
    }

    /// True while the attached game process is still running.
    public bool IsProcessAlive
        => _handle != IntPtr.Zero
        && Win32.GetExitCodeProcess(_handle, out uint code)
        && code == Win32.STILL_ACTIVE;

    // ── Address classification (diagnostics) ─────────────────────

    public bool IsInModule(ulong address)
        => address >= ModuleBase && address < ModuleBase + ModuleSize;

    /// Cheap plausibility check for a user-mode x64 pointer.
    public static bool LooksLikePointer(ulong v)
        => v >= 0x10000 && v < 0x00007FFF_FFFFFFFF && (v & 0x7) == 0;

    /// Returns a short human-readable description of what an address points at.
    public string DescribeAddress(ulong address)
    {
        if (address == 0) return "null";
        if (!LooksLikePointer(address)) return "NOT a valid pointer (raw data / code bytes?)";

        if (Win32.VirtualQueryEx(_handle, address, out var mbi,
                (uint)System.Runtime.InteropServices.Marshal.SizeOf<Win32.MEMORY_BASIC_INFORMATION>()) == 0)
            return "VirtualQueryEx failed";

        if (mbi.State != Win32.MEM_COMMIT) return "not committed";

        string where = IsInModule(address)
            ? $"game module +0x{address - ModuleBase:X}"
            : mbi.Type == Win32.MEM_IMAGE ? "other module (DLL)"
            : mbi.Type == Win32.MEM_PRIVATE ? "heap/private"
            : mbi.Type == Win32.MEM_MAPPED ? "mapped"
            : $"type 0x{mbi.Type:X}";
        return $"{where}, protect 0x{mbi.Protect:X}";
    }

    // ── Write ─────────────────────────────────────────────────────

    public bool WriteInt32(ulong address, int value)
        => Write(address, BitConverter.GetBytes(value), $"int32 {value}");

    public bool WriteFloat(ulong address, float value)
        => Write(address, BitConverter.GetBytes(value), $"float {value}");

    public bool WritePointer(ulong address, ulong value)
        => Write(address, BitConverter.GetBytes(value), $"ptr {Log.Hex(value)}");

    /// Write without a log line (for values re-applied every tick).
    public bool WriteQuiet(ulong address, byte[] b)
        => Win32.WriteProcessMemory(_handle, address, b, b.Length, out int written) && written == b.Length;

    public bool WriteBytes(ulong address, byte[] b, string what) => Write(address, b, what);

    /// Patches code: makes the page writable, writes, restores protection, flushes the i-cache.
    public bool WriteCode(ulong address, byte[] b, string what)
    {
        if (!Win32.VirtualProtectEx(_handle, address, (UIntPtr)b.Length, Win32.PAGE_EXECUTE_READWRITE, out uint old))
        {
            Log.Error($"VirtualProtectEx {Log.Hex(address)} failed, Win32 error {System.Runtime.InteropServices.Marshal.GetLastWin32Error()}.");
            return false;
        }
        bool ok = Write(address, b, what);
        Win32.VirtualProtectEx(_handle, address, (UIntPtr)b.Length, old, out _);
        Win32.FlushInstructionCache(_handle, address, (UIntPtr)b.Length);
        return ok;
    }

    /// Allocates executable memory within ±2 GB of <paramref name="near"/> so a 5-byte
    /// relative jump can reach it. Returns 0 on failure.
    public ulong AllocateNear(ulong near, int size)
    {
        const ulong gran = 0x10000, range = 0x7000_0000;
        ulong baseAddr = near & ~(gran - 1);
        for (ulong d = gran; d < range; d += gran)
        {
            foreach (ulong cand in new[] { baseAddr - d, baseAddr + d })
            {
                if (cand < 0x10000) continue;
                if (Win32.VirtualQueryEx(_handle, cand, out var mbi,
                        (uint)System.Runtime.InteropServices.Marshal.SizeOf<Win32.MEMORY_BASIC_INFORMATION>()) == 0
                    || mbi.State != Win32.MEM_FREE) continue;
                ulong p = Win32.VirtualAllocEx(_handle, cand, (UIntPtr)size,
                    Win32.MEM_COMMIT | Win32.MEM_RESERVE, Win32.PAGE_EXECUTE_READWRITE);
                if (p != 0) { Log.Info($"Allocated {size} bytes at {Log.Hex(p)} (target {Log.Hex(near)})."); return p; }
            }
        }
        Log.Error("AllocateNear: no free memory within ±2 GB.");
        return 0;
    }

    private bool Write(ulong address, byte[] b, string what)
    {
        bool ok = Win32.WriteProcessMemory(_handle, address, b, b.Length, out int written) && written == b.Length;
        Log.Info($"Write {Log.Hex(address)} = {what}  -> {(ok ? "OK" : "FAILED")}");
        return ok;
    }

    // ── AOB Scan ──────────────────────────────────────────────────

    /// <summary>
    /// Scans the game module's committed, readable regions for a byte pattern.
    /// mask[i] == false marks pattern[i] as a wildcard.
    /// Returns the first hit, or 0.
    /// </summary>
    public ulong AobScan(byte[] pattern, bool[]? mask = null)
    {
        var hits = AobScanAll(pattern, mask, maxHits: 1);
        return hits.Count > 0 ? hits[0] : 0;
    }

    /// Like <see cref="AobScan"/> but returns up to <paramref name="maxHits"/> hits.
    /// Useful for checking that a signature is unique.
    public List<ulong> AobScanAll(byte[] pattern, bool[]? mask = null, int maxHits = 16)
    {
        var sw = Stopwatch.StartNew();
        var hits = new List<ulong>();
        const int chunkSize = 0x10000;
        ulong addr = ModuleBase;
        ulong end  = ModuleBase + ModuleSize;
        var   buf  = new byte[chunkSize + pattern.Length];

        while (addr < end && hits.Count < maxHits)
        {
            if (Win32.VirtualQueryEx(_handle, addr, out var mbi,
                    (uint)System.Runtime.InteropServices.Marshal.SizeOf<Win32.MEMORY_BASIC_INFORMATION>()) == 0)
                break;

            // Strip modifier flags (PAGE_GUARD, PAGE_NOCACHE, ...) before comparing.
            uint prot = mbi.Protect & 0xFF;
            bool guard = (mbi.Protect & Win32.PAGE_GUARD) != 0;
            bool readable = mbi.State == Win32.MEM_COMMIT && !guard
                && (prot == Win32.PAGE_EXECUTE_READ
                 || prot == Win32.PAGE_EXECUTE_READWRITE
                 || prot == Win32.PAGE_EXECUTE_WRITECOPY
                 || prot == Win32.PAGE_READONLY
                 || prot == Win32.PAGE_READWRITE
                 || prot == Win32.PAGE_WRITECOPY);

            if (readable)
            {
                ulong regionEnd = mbi.BaseAddress + mbi.RegionSize;
                ulong scanEnd   = Math.Min(regionEnd, end);
                ulong cur       = Math.Max(mbi.BaseAddress, addr);

                while (cur < scanEnd && hits.Count < maxHits)
                {
                    int toRead = (int)Math.Min((ulong)buf.Length, scanEnd - cur);
                    Win32.ReadProcessMemory(_handle, cur, buf, toRead, out int read);
                    if (read < pattern.Length) { cur += (ulong)toRead; continue; }

                    // Only accept hits that start before the overlap tail so a match
                    // straddling two chunks is not reported twice.
                    int advance = Math.Max(1, read - pattern.Length + 1);
                    int start = 0;
                    while (hits.Count < maxHits)
                    {
                        int hit = FindPattern(buf, read, pattern, mask, start);
                        if (hit < 0 || hit >= advance) break;
                        hits.Add(cur + (ulong)hit);
                        start = hit + 1;
                    }

                    cur += (ulong)advance;
                }
            }

            addr = mbi.BaseAddress + mbi.RegionSize;
        }

        Log.Debug($"AOB [{FormatPattern(pattern, mask)}] -> {hits.Count} hit(s) " +
                  $"{string.Join(", ", hits.Take(16).Select(Log.Hex))}{(hits.Count > 16 ? ", …" : "")} " +
                  $"in {sw.ElapsedMilliseconds} ms");
        return hits;
    }

    /// Committed read/write regions inside the game module (.data, .bss): where globals live.
    public List<(ulong Start, int Size)> ModuleDataRegions()
    {
        var list = new List<(ulong, int)>();
        ulong addr = ModuleBase, end = ModuleBase + ModuleSize;
        while (addr < end)
        {
            if (Win32.VirtualQueryEx(_handle, addr, out var mbi,
                    (uint)System.Runtime.InteropServices.Marshal.SizeOf<Win32.MEMORY_BASIC_INFORMATION>()) == 0)
                break;
            uint prot = mbi.Protect & 0xFF;
            if (mbi.State == Win32.MEM_COMMIT && (mbi.Protect & Win32.PAGE_GUARD) == 0
                && (prot == Win32.PAGE_READWRITE || prot == Win32.PAGE_WRITECOPY))
            {
                ulong s = Math.Max(mbi.BaseAddress, addr), e = Math.Min(mbi.BaseAddress + mbi.RegionSize, end);
                if (e > s && e - s < int.MaxValue) list.Add((s, (int)(e - s)));
            }
            addr = mbi.BaseAddress + mbi.RegionSize;
        }
        return list;
    }

    public static string FormatPattern(byte[] pattern, bool[]? mask)
        => string.Join(" ", pattern.Select((b, i) => mask != null && !mask[i] ? "??" : b.ToString("X2")));

    private static int FindPattern(byte[] haystack, int len, byte[] needle, bool[]? mask, int start)
    {
        int end = len - needle.Length;
        for (int i = start; i <= end; i++)
        {
            bool ok = true;
            for (int j = 0; j < needle.Length; j++)
            {
                bool isWild = mask != null && !mask[j];
                if (!isWild && haystack[i + j] != needle[j]) { ok = false; break; }
            }
            if (ok) return i;
        }
        return -1;
    }

    // ── RIP-relative helper ───────────────────────────────────────

    /// Reads the 4-byte signed displacement at instrAddr+dispOffset,
    /// returns the absolute target: instrAddr + instrSize + displacement.
    public ulong RipRelative(ulong instrAddr, int dispOffset, int instrSize)
    {
        int disp = ReadInt32(instrAddr + (ulong)dispOffset);
        return instrAddr + (ulong)instrSize + (ulong)(long)disp;
    }

    // ── IDisposable ───────────────────────────────────────────────

    public void Dispose()
    {
        if (!_disposed) { Detach(); _disposed = true; }
    }
}
