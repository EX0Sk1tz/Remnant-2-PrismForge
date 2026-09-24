using System.Text;
using R2PrismRuntime.Diagnostics;
using R2PrismRuntime.Memory;

namespace R2PrismRuntime.Game;

public enum HookState { Unavailable, Off, On, Foreign }

/// <summary>
/// Code hook on the spot where the game stores every computed character stat — the same
/// injection point as the CT's "Manage System Statistics" script (MoveSpeedCap+0x26):
///
///     mov [r12+08],eax        ; r12 = stat entry, eax = new value (float bits), ecx = stat FName id
///
/// The hook jumps to a cave that looks ecx up in an override table and, on a match, replaces
/// eax before the original store. The CT hard-codes four stats; the table here holds any.
///
/// Cave layout (one page, allocated within ±2 GB of the hook):
///   +0x000  "R2PEHOOK" magic        +0x008  original 5 bytes
///   +0x010  int32 count   +0x014 uint32 hit counter   +0x018  entries { int32 id; float value } × MaxEntries
///   +0x800  code
/// </summary>
public sealed class StatHook
{
    public const int MaxEntries = 200;
    private const int CaveSize = 0x1000;
    private const int TableCount = 0x10, TableEntries = 0x18, CodeOffset = 0x800;
    private static readonly byte[] Magic = Encoding.ASCII.GetBytes("R2PEHOOK");
    private static readonly byte[] Original = { 0x41, 0x89, 0x44, 0x24, 0x08 };   // mov [r12+08],eax

    // CT: aobscanmodule(MoveSpeedCap, 09 04 93 48 63 C6 48 8D 0C 80), hook at +0x26
    private static readonly byte[] Aob = { 0x09, 0x04, 0x93, 0x48, 0x63, 0xC6, 0x48, 0x8D, 0x0C, 0x80 };
    private const ulong SiteOffset = 0x26;

    private readonly ProcessMemory _mem;
    private ulong _site, _cave;

    public HookState State { get; private set; } = HookState.Unavailable;
    public string Detail { get; private set; } = "";

    public StatHook(ProcessMemory mem) => _mem = mem;

    /// Finds the hook site and works out its state. Returns overrides left by a previous
    /// session of this app (hook still installed), so they can be adopted.
    public List<(int Id, float Value)> Probe()
    {
        var adopted = new List<(int, float)>();
        var hits = _mem.AobScanAll(Aob, maxHits: 2);
        if (hits.Count != 1)
        {
            State = HookState.Unavailable;
            Detail = hits.Count == 0 ? "Stat code signature not found." : "Stat code signature is not unique.";
            Log.Warn("StatHook: " + Detail);
            return adopted;
        }

        _site = hits[0] + SiteOffset;
        byte[] cur = _mem.ReadBytes(_site, 5);
        if (cur.SequenceEqual(Original))
        {
            State = HookState.Off;
            Detail = "Ready.";
        }
        else if (cur[0] == 0xE9)
        {
            ulong target = _site + 5 + (ulong)(long)BitConverter.ToInt32(cur, 1);
            ulong cave = target - CodeOffset;
            if (_mem.ReadBytes(cave, Magic.Length).SequenceEqual(Magic))
            {
                _cave = cave;
                State = HookState.On;
                Detail = "Adopted the hook from a previous session.";
                int n = Math.Clamp(_mem.ReadInt32(cave + TableCount), 0, MaxEntries);
                byte[] t = _mem.ReadBytes(cave + TableEntries, n * 8);
                for (int i = 0; i < n; i++)
                    adopted.Add((BitConverter.ToInt32(t, i * 8), BitConverter.ToSingle(t, i * 8 + 4)));
            }
            else
            {
                State = HookState.Foreign;
                Detail = "Already patched by another tool (Cheat Engine?). Disable that script first.";
            }
        }
        else
        {
            State = HookState.Unavailable;
            Detail = $"Unexpected code at the hook site ({BitConverter.ToString(cur)}).";
        }
        Log.Info($"StatHook: site {Log.Hex(_site)} (module+0x{_site - _mem.ModuleBase:X}), state {State}. {Detail}");
        return adopted;
    }

    public bool Install()
    {
        if (State == HookState.On) return true;
        if (State != HookState.Off) return false;

        if (_cave == 0)
        {
            _cave = _mem.AllocateNear(_site, CaveSize);
            if (_cave == 0) { Detail = "Could not allocate memory near the game code."; return false; }
        }

        var page = new byte[CaveSize];
        Magic.CopyTo(page, 0);
        Original.CopyTo(page, 8);
        byte[] code = BuildCode(_cave + TableCount, _cave + CodeOffset, _site + 5);
        code.CopyTo(page, CodeOffset);
        if (!_mem.WriteBytes(_cave, page, "stat hook cave")) { Detail = "Writing the code cave failed."; return false; }

        long rel = (long)(_cave + CodeOffset) - (long)(_site + 5);
        var jmp = new byte[5];
        jmp[0] = 0xE9;
        BitConverter.GetBytes((int)rel).CopyTo(jmp, 1);
        if (!_mem.WriteCode(_site, jmp, "stat hook jmp")) { Detail = "Patching the game code failed."; return false; }

        State = HookState.On;
        Detail = "Active.";
        Log.Info($"StatHook installed: {Log.Hex(_site)} → {Log.Hex(_cave + CodeOffset)}.");
        return true;
    }

    public bool Uninstall()
    {
        if (State != HookState.On) return true;
        // Empty the table first so a thread already inside the cave changes nothing.
        _mem.WriteInt32(_cave + TableCount, 0);
        if (!_mem.WriteCode(_site, Original, "stat hook restore")) { Detail = "Restoring the game code failed."; return false; }
        // The cave stays allocated: a game thread could still be executing in it.
        State = HookState.Off;
        Detail = "Ready.";
        Log.Info("StatHook removed; original code restored.");
        return true;
    }

    /// How many times the game has run through the hook (proves it is live).
    public uint Hits => State == HookState.On ? _mem.ReadUInt32(_cave + TableCount + 4) : 0;

    /// Replaces the override table. Entries are written before the count grows and after it shrinks,
    /// so the hook never reads a half-written entry.
    public bool SetOverrides(IReadOnlyList<(int Id, float Value)> list)
    {
        if (State != HookState.On) return false;
        int n = Math.Min(list.Count, MaxEntries);
        var t = new byte[n * 8];
        for (int i = 0; i < n; i++)
        {
            BitConverter.GetBytes(list[i].Id).CopyTo(t, i * 8);
            BitConverter.GetBytes(list[i].Value).CopyTo(t, i * 8 + 4);
        }
        int old = _mem.ReadInt32(_cave + TableCount);
        if (n < old) _mem.WriteInt32(_cave + TableCount, n);
        bool ok = n == 0 || _mem.WriteQuiet(_cave + TableEntries, t);
        if (n >= old) _mem.WriteInt32(_cave + TableCount, n);
        Log.Info($"StatHook table: {n} override(s).");
        return ok;
    }

    /// x64 code for the cave. Flags and the two scratch registers are preserved.
    private static byte[] BuildCode(ulong table, ulong codeAddr, ulong returnAddr)
    {
        var c = new List<byte>();
        c.Add(0x9C);                                     // pushfq
        c.Add(0x52);                                     // push rdx
        c.AddRange(new byte[] { 0x41, 0x53 });           // push r11
        c.AddRange(new byte[] { 0x49, 0xBB });           // mov r11, imm64 (table)
        c.AddRange(BitConverter.GetBytes(table));
        c.AddRange(new byte[] { 0xF0, 0x41, 0xFF, 0x43, 0x04 }); // lock inc dword [r11+4]   hit counter
        c.AddRange(new byte[] { 0x41, 0x8B, 0x13 });     // mov edx,[r11]        count
        c.AddRange(new byte[] { 0x49, 0x83, 0xC3, 0x08 });// add r11,8           first entry
        // loop:
        c.AddRange(new byte[] { 0x85, 0xD2 });           // test edx,edx
        c.AddRange(new byte[] { 0x74, 0x13 });           // jz done
        c.AddRange(new byte[] { 0x41, 0x3B, 0x0B });     // cmp ecx,[r11]        stat id
        c.AddRange(new byte[] { 0x75, 0x06 });           // jne next
        c.AddRange(new byte[] { 0x41, 0x8B, 0x43, 0x04 });// mov eax,[r11+4]     override value
        c.AddRange(new byte[] { 0xEB, 0x08 });           // jmp done
        // next:
        c.AddRange(new byte[] { 0x49, 0x83, 0xC3, 0x08 });// add r11,8
        c.AddRange(new byte[] { 0xFF, 0xCA });           // dec edx
        c.AddRange(new byte[] { 0xEB, 0xE9 });           // jmp loop
        // done:
        c.AddRange(new byte[] { 0x41, 0x5B });           // pop r11
        c.Add(0x5A);                                     // pop rdx
        c.Add(0x9D);                                     // popfq
        c.AddRange(Original);                            // mov [r12+08],eax
        c.Add(0xE9);                                     // jmp back
        long rel = (long)returnAddr - (long)(codeAddr + (ulong)c.Count + 4);
        c.AddRange(BitConverter.GetBytes((int)rel));
        return c.ToArray();
    }
}
