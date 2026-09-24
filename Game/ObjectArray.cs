using System.Collections.Concurrent;
using System.Diagnostics;
using R2PrismRuntime.Diagnostics;
using R2PrismRuntime.Memory;

namespace R2PrismRuntime.Game;

/// <summary>
/// Reads GUObjectArray (UE5 FChunkedFixedUObjectArray) to find objects by name.
/// Used to locate the class default object a prism segment caches at +0x20 when the
/// user picks a stat no other segment currently uses.
///
///   ObjObjects (the field we locate)
///     +0x00  FUObjectItem** Objects      chunk table, 64K items per chunk
///     +0x14  int32          NumElements
///   FUObjectItem: +0x00 UObject* Object, stride 0x18
/// </summary>
public sealed class ObjectArray
{
    private const int ChunkSize = 0x10000;
    private readonly ProcessMemory _mem;
    private ulong _field;        // address of ObjObjects.Objects
    private int _stride = 0x18;

    public bool IsReady => _field != 0;

    public ObjectArray(ProcessMemory mem) => _mem = mem;

    // mov rax,[rip+X]; mov rcx,[rax+rcx*8]; lea rax,[rcx+rdx*8]  — the common chunk lookup.
    private static readonly (byte[] Pattern, bool[] Mask)[] Signatures =
    {
        (new byte[] { 0x48, 0x8B, 0x05, 0, 0, 0, 0, 0x48, 0x8B, 0x0C, 0xC8, 0x48, 0x8D, 0x04, 0xD1 },
         new[] { true, true, true, false, false, false, false, true, true, true, true, true, true, true, true }),
        (new byte[] { 0x48, 0x8B, 0x05, 0, 0, 0, 0, 0x48, 0x8B, 0x0C, 0xC8, 0x4C, 0x8D, 0x04, 0xD1 },
         new[] { true, true, true, false, false, false, false, true, true, true, true, true, true, true, true }),
    };

    /// Locates the array. <paramref name="known"/> are live UObjects used to validate candidates.
    public bool Find(params ulong[] known)
    {
        _field = 0;
        known = known.Where(k => k != 0).ToArray();
        if (known.Length == 0) return false;
        var sw = Stopwatch.StartNew();

        foreach (var (pattern, mask) in Signatures)
            foreach (ulong hit in _mem.AobScanAll(pattern, mask, maxHits: 8))
            {
                ulong field = _mem.RipRelative(hit, 3, 7);
                if (Validate(field, known))
                {
                    Log.Info($"GUObjectArray via signature: Objects field {Log.Hex(field)} (module+0x{field - _mem.ModuleBase:X}), " +
                             $"stride 0x{_stride:X}, {Count} objects, {sw.ElapsedMilliseconds} ms.");
                    return true;
                }
            }

        // Fallback: any qword in the module's writable data that validates as the chunk table.
        foreach (var (start, size) in _mem.ModuleDataRegions())
        {
            var buf = new byte[size];
            if (!_mem.TryReadBytes(start, buf, size)) continue;
            for (int i = 0; i + 8 <= size; i += 8)
            {
                ulong v = BitConverter.ToUInt64(buf, i);
                if (!ProcessMemory.LooksLikePointer(v) || _mem.IsInModule(v)) continue;
                if (Validate(start + (ulong)i, known))
                {
                    Log.Info($"GUObjectArray via data scan: Objects field {Log.Hex(_field)} (module+0x{_field - _mem.ModuleBase:X}), " +
                             $"stride 0x{_stride:X}, {Count} objects, {sw.ElapsedMilliseconds} ms.");
                    return true;
                }
            }
        }

        Log.Warn($"GUObjectArray not found ({sw.ElapsedMilliseconds} ms).");
        return false;
    }

    public int Count => _field == 0 ? 0 : _mem.ReadInt32(_field + 0x14);

    private bool Validate(ulong field, ulong[] known)
    {
        ulong table = _mem.ReadPointer(field);
        if (!ProcessMemory.LooksLikePointer(table)) return false;
        int num = _mem.ReadInt32(field + 0x14);
        if (num is < 1000 or > 20_000_000) return false;

        foreach (int stride in new[] { 0x18, 0x10 })
        {
            bool all = true;
            foreach (ulong obj in known)
            {
                int idx = _mem.ReadInt32(obj + 0x0C);   // UObjectBase::InternalIndex
                if (idx <= 0 || idx >= num) { all = false; break; }
                ulong chunk = _mem.ReadPointer(table + (ulong)(idx / ChunkSize) * 8);
                if (!ProcessMemory.LooksLikePointer(chunk)
                    || _mem.ReadPointer(chunk + (ulong)(idx % ChunkSize) * (ulong)stride) != obj) { all = false; break; }
            }
            if (all)
            {
                _field = field;
                _stride = stride;
                return true;
            }
        }
        return false;
    }

    /// Finds class default objects (RF_ClassDefaultObject) whose FName ComparisonIndex is in
    /// <paramref name="nameIds"/>. Returns nameId → object.
    public Dictionary<int, ulong> FindDefaultObjects(IReadOnlySet<int> nameIds)
    {
        var found = new ConcurrentDictionary<int, ulong>();
        if (!IsReady || nameIds.Count == 0) return new();
        var sw = Stopwatch.StartNew();
        ulong table = _mem.ReadPointer(_field);
        int num = Count;
        int chunks = (num + ChunkSize - 1) / ChunkSize;

        Parallel.For(0, chunks, new ParallelOptions { MaxDegreeOfParallelism = 4 }, (c, state) =>
        {
            if (found.Count == nameIds.Count) { state.Stop(); return; }
            ulong chunk = _mem.ReadPointer(table + (ulong)c * 8);
            if (chunk == 0) return;
            int items = Math.Min(ChunkSize, num - c * ChunkSize);
            var buf = new byte[items * _stride];
            if (!_mem.TryReadBytes(chunk, buf, buf.Length)) return;
            var head = new byte[0x1C];
            for (int i = 0; i < items; i++)
            {
                ulong obj = BitConverter.ToUInt64(buf, i * _stride);
                if (obj == 0 || !_mem.TryReadBytes(obj, head, head.Length)) continue;
                int name = BitConverter.ToInt32(head, 0x18);
                if (!nameIds.Contains(name)) continue;
                uint flags = BitConverter.ToUInt32(head, 0x08);
                if ((flags & 0x10) != 0) found.TryAdd(name, obj);   // RF_ClassDefaultObject
            }
        });

        Log.Info($"Default-object search: {found.Count}/{nameIds.Count} found among {num} objects in {sw.ElapsedMilliseconds} ms.");
        return new Dictionary<int, ulong>(found);
    }
}
