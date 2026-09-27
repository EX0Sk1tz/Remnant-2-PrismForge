using R2PrismRuntime.Diagnostics;
using R2PrismRuntime.Memory;

namespace R2PrismRuntime.Game;

/// <summary>
/// Calls the game's own allocator (UE's GMalloc) so an array can be given a bigger buffer that
/// the game can later grow, free and save like its own. Memory from VirtualAllocEx can't be used
/// for that: the game would free it through GMalloc and crash.
///
/// GMalloc is found without a fixed offset: shipping builds inline
///     mov rcx,[rip+GMalloc]; test rcx,rcx; jne …   (then GMalloc->Malloc/Free through the vtable)
/// at hundreds of sites. Of the globals loaded that way, GMalloc is the one whose vtable has
/// FMalloc's default TryMalloc/TryRealloc, which just forward to the neighbouring slot:
///     +0x28  mov rax,[rcx]; jmp [rax+20]      (TryMalloc → Malloc)
///     +0x38  mov rax,[rcx]; jmp [rax+30]      (TryRealloc → Realloc)
/// Slots used: +0x20 Malloc(Count, Alignment), +0x40 Free(Original). Confirmed on Steam, 2026-09-28
/// (GMalloc at module+0x78EBF08, 361 sites; call sites pass the matching arguments).
///
/// Calls run on a short-lived thread in the game through a stub:
///     rcx = parameter block { +0 &GMalloc, +8 vtable offset, +10 arg1, +18 arg2, +20 result }
/// FMalloc is thread-safe, so this doesn't have to run on the game thread.
/// </summary>
public sealed class GameAllocator
{
    public const ulong Slot_Malloc = 0x20, Slot_Free = 0x40;

    private static readonly byte[] TryMallocBody  = { 0x48, 0x8B, 0x01, 0x48, 0xFF, 0x60, 0x20 };
    private static readonly byte[] TryReallocBody = { 0x48, 0x8B, 0x01, 0x48, 0xFF, 0x60, 0x30 };

    private static readonly byte[] Stub =
    {
        0x53,                         // push rbx
        0x48, 0x83, 0xEC, 0x20,       // sub rsp,20h          (rsp 16-aligned at the call)
        0x48, 0x8B, 0xD9,             // mov rbx,rcx          parameter block
        0x48, 0x8B, 0x03,             // mov rax,[rbx]        &GMalloc
        0x48, 0x8B, 0x08,             // mov rcx,[rax]        GMalloc
        0x48, 0x85, 0xC9,             // test rcx,rcx
        0x74, 0x15,                   // jz done
        0x48, 0x8B, 0x53, 0x10,       // mov rdx,[rbx+10h]    arg1
        0x4C, 0x8B, 0x43, 0x18,       // mov r8,[rbx+18h]     arg2
        0x48, 0x8B, 0x01,             // mov rax,[rcx]        vtable
        0x48, 0x03, 0x43, 0x08,       // add rax,[rbx+8]      + slot
        0xFF, 0x10,                   // call [rax]
        0x48, 0x89, 0x43, 0x20,       // mov [rbx+20h],rax    result
        // done:
        0x48, 0x83, 0xC4, 0x20,       // add rsp,20h
        0x5B,                         // pop rbx
        0x33, 0xC0,                   // xor eax,eax
        0xC3,                         // ret
    };
    private const int ParamOffset = 0x100, PageSize = 0x1000;

    private readonly ProcessMemory _mem;
    private readonly object _lock = new();
    private ulong _page;

    /// Address of the GMalloc global in the game module (0 until found).
    public ulong GMallocGlobal { get; private set; }
    public bool IsAvailable => GMallocGlobal != 0;

    public GameAllocator(ProcessMemory mem) => _mem = mem;

    /// Finds GMalloc. Takes a module scan (a second or two); run it off the UI thread.
    public bool Find()
    {
        byte?[] pat = { 0x48, 0x8B, 0x0D, null, null, null, null, 0x48, 0x85, 0xC9 };
        var hits = _mem.AobScanAll(pat.Select(b => b ?? 0).ToArray(), pat.Select(b => b != null).ToArray(), maxHits: 100_000);
        var globals = hits.GroupBy(h => _mem.RipRelative(h, 3, 7))
                          .Where(g => _mem.IsInModule(g.Key))
                          .OrderByDescending(g => g.Count())
                          .Take(40);
        foreach (var g in globals)
        {
            ulong obj = _mem.ReadPointer(g.Key);
            ulong vt = obj != 0 ? _mem.ReadPointer(obj) : 0;
            if (!_mem.IsInModule(vt)) continue;
            if (!StartsWith(_mem.ReadPointer(vt + 0x28), TryMallocBody) || !StartsWith(_mem.ReadPointer(vt + 0x38), TryReallocBody)) continue;
            GMallocGlobal = g.Key;
            Log.Info($"GMalloc: global module+0x{g.Key - _mem.ModuleBase:X} ({g.Count()} sites), object {Log.Hex(obj)}, " +
                     $"vtable module+0x{vt - _mem.ModuleBase:X}.");
            return true;
        }
        Log.Warn($"GMalloc not found ({hits.Count} candidate sites checked). Add segment needs spare room from the game.");
        return false;
    }

    /// GMalloc->Malloc(size, alignment). Returns 0 on failure.
    public ulong Malloc(int size, int alignment = 0)
    {
        ulong p = Call(Slot_Malloc, (ulong)size, (ulong)alignment);
        Log.Info($"GMalloc->Malloc({size}, {alignment}) = {Log.Hex(p)}");
        return p;
    }

    /// GMalloc->Free(ptr). Only for memory from <see cref="Malloc"/> that the game never saw.
    public void Free(ulong ptr)
    {
        Call(Slot_Free, ptr, 0);
        Log.Info($"GMalloc->Free({Log.Hex(ptr)})");
    }

    private ulong Call(ulong slot, ulong arg1, ulong arg2)
    {
        if (!IsAvailable) return 0;
        lock (_lock)
        {
            if (_page == 0)
            {
                _page = _mem.Allocate(PageSize);
                if (_page == 0 || !_mem.WriteBytes(_page, Stub, "allocator stub")) { _page = 0; return 0; }
            }
            var block = new byte[0x28];
            BitConverter.GetBytes(GMallocGlobal).CopyTo(block, 0x00);
            BitConverter.GetBytes(slot).CopyTo(block, 0x08);
            BitConverter.GetBytes(arg1).CopyTo(block, 0x10);
            BitConverter.GetBytes(arg2).CopyTo(block, 0x18);
            ulong param = _page + ParamOffset;
            if (!_mem.WriteQuiet(param, block)) return 0;
            if (!_mem.RunRemote(_page, param)) return 0;
            return _mem.ReadPointer(param + 0x20);
        }
    }

    private bool StartsWith(ulong addr, byte[] body) =>
        _mem.IsInModule(addr) && _mem.ReadBytes(addr, body.Length).AsSpan().SequenceEqual(body);
}
