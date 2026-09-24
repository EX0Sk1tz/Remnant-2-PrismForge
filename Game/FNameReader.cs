using System.Diagnostics;
using System.Text;
using R2PrismRuntime.Diagnostics;
using R2PrismRuntime.Memory;

namespace R2PrismRuntime.Game;

/// <summary>
/// Locates the FNamePool and resolves FName ComparisonIndex values to strings.
///
/// UE5 FNamePool layout (64-bit):
///   FNamePool
///     +0x00  FRWLock           Lock
///     +0x08  uint32            CurrentBlock
///     +0x0C  uint32            CurrentByteCursor
///     +0x10  uint8*            Blocks[8192]     ← block pointer array
///
///   ComparisonIndex id:  block = id >> 16,  offset = id &amp; 0xFFFF
///   entry  = Blocks[block] + offset * 2          (entries are 2-byte aligned)
///   header = uint16 at entry:  bit0 = bIsWide, bits 6..15 = length
///   string = entry + 2, length chars (ANSI) or length*2 bytes (UTF-16)
///
/// Finding the pool:
///   AOB "48 03 F8 44 89 44 24 38"; the 7 bytes before the hit are a
///   RIP-relative LEA/MOV that references the pool. Several interpretations are
///   tried (see <see cref="FindPool"/>) and the one where name #0 reads "None" wins.
/// </summary>
public class FNameReader
{
    private const int BlockSizeBytes = 0x20000;   // 65536 offsets * stride 2
    private const int MaxBlocks      = 8192;

    private readonly ProcessMemory _mem;

    /// Address of Blocks[0] (the block pointer array).
    private ulong _blocks;

    public bool   IsReady    => _blocks != 0;
    public ulong  BlocksAddr => _blocks;
    public string PoolMethod { get; private set; } = "";

    public FNameReader(ProcessMemory mem) => _mem = mem;

    // ── Init ──────────────────────────────────────────────────────

    public bool FindPool()
    {
        _blocks = 0;
        _cache.Clear();
        var hits = _mem.AobScanAll(GameOffsets.AobFNamePool, maxHits: 4);
        if (hits.Count == 0)
        {
            Log.Warn("FNamePool AOB not found.");
            return false;
        }
        if (hits.Count > 1)
            Log.Warn($"FNamePool AOB is not unique ({hits.Count} hits) — trying each.");

        foreach (ulong hit in hits)
        {
            ulong instr = hit - 7;
            byte[] b = _mem.ReadBytes(instr, 7);
            ulong fnameStatic = _mem.RipRelative(instr, dispOffset: 3, instrSize: 7);
            Log.Info($"FNamePool hit {Log.Hex(hit)}; bytes at hit-7: {Hex(b)}; RIP target {Log.Hex(fnameStatic)} " +
                     $"({_mem.DescribeAddress(fnameStatic)})");

            // Candidate block-array addresses, in order of preference.
            var candidates = new List<(string Method, ulong Blocks)>
            {
                ("CT: [static+0x18] + 0x10",       SafeAdd(_mem.ReadPointer(fnameStatic + 0x18), 0x10)),
                ("UE5 inline: static + 0x10",      fnameStatic + 0x10),
                ("pointer: [static] + 0x10",       SafeAdd(_mem.ReadPointer(fnameStatic), 0x10)),
                ("CT raw: [static+0x18]",          _mem.ReadPointer(fnameStatic + 0x18)),
            };

            foreach (var (method, blocks) in candidates)
            {
                string first = TryReadEntry(blocks, 0);
                Log.Debug($"  FNamePool candidate '{method}': blocks {Log.Hex(blocks)} -> name#0 = \"{first}\"");
                if (first == "None")
                {
                    _blocks = blocks;
                    PoolMethod = method;
                    Log.Info($"FNamePool OK via '{method}'. Blocks[] at {Log.Hex(blocks)}. " +
                             $"Sample: #3=\"{TryReadEntry(blocks, 3)}\"");
                    return true;
                }
            }
        }

        Log.Error("FNamePool: no candidate produced \"None\" for name #0. Names will show as IDs.");
        return false;
    }

    private static ulong SafeAdd(ulong p, ulong add) => p == 0 ? 0 : p + add;

    private static string Hex(byte[] b) => string.Join(" ", b.Select(x => x.ToString("X2")));

    /// Reads the entry for <paramref name="id"/> using an explicit blocks array (no validation).
    private string TryReadEntry(ulong blocks, int id)
    {
        if (blocks == 0) return "<null blocks>";
        ulong block = _mem.ReadPointer(blocks + (ulong)((id >> 16) & 0xFFFF) * 8);
        if (!ProcessMemory.LooksLikePointer(block)) return $"<bad block {Log.Hex(block)}>";
        return ReadEntryAt(block + (ulong)(id & 0xFFFF) * 2) ?? "<bad entry>";
    }

    private string? ReadEntryAt(ulong entryAddr)
    {
        ushort header = _mem.ReadUInt16(entryAddr);
        int length = header >> 6;
        bool wide = (header & 1) != 0;
        if (length <= 0 || length > 1024) return null;

        if (wide)
        {
            var bytes = _mem.ReadBytes(entryAddr + 2, length * 2);
            return Encoding.Unicode.GetString(bytes);
        }
        return Encoding.ASCII.GetString(_mem.ReadBytes(entryAddr + 2, length));
    }

    // ── Resolve ───────────────────────────────────────────────────

    /// <summary>
    /// Resolves a FName ComparisonIndex (int32) to its string.
    /// Returns "" for id 0 ("None") and "#HEX" if the entry can't be read.
    /// </summary>
    public string Resolve(int key)
    {
        if (!IsReady || key == 0) return "";
        // Pool entries are never freed or moved, so a resolved name stays valid for the session.
        if (_cache.TryGetValue(key, out var cached)) return cached;
        try
        {
            ulong block = _mem.ReadPointer(_blocks + (ulong)((key >> 16) & 0xFFFF) * 8);
            if (!ProcessMemory.LooksLikePointer(block)) return $"#{key:X}";
            string? name = ReadEntryAt(block + (ulong)(key & 0xFFFF) * 2);
            if (name == null) return $"#{key:X}";
            _cache[key] = name;
            return name;
        }
        catch
        {
            return $"#{key:X}";
        }
    }

    private readonly System.Collections.Concurrent.ConcurrentDictionary<int, string> _cache = new();

    /// <summary>
    /// Scans the pool for a string matching <paramref name="target"/>.
    /// Returns the ComparisonIndex if found, -1 otherwise.
    /// Prefer <see cref="FindIds"/> for multiple names — it walks the pool once.
    /// </summary>
    public int FindId(string target)
        => FindIds(new[] { target }).TryGetValue(target.Trim(), out int id) ? id : -1;

    /// <summary>
    /// Walks the whole pool once (one ReadProcessMemory per 128 KB block) and returns
    /// the ComparisonIndex of every requested name that was found.
    /// </summary>
    public Dictionary<string, int> FindIds(IEnumerable<string> targets)
    {
        // FNames compare case-insensitively; the pool keeps whichever casing was registered first.
        var result = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        if (!IsReady) return result;

        var wanted = new HashSet<string>(targets.Select(t => t.Trim()), StringComparer.OrdinalIgnoreCase);
        var lengths = new HashSet<int>(wanted.Select(w => w.Length));
        var sw = Stopwatch.StartNew();
        var buf = new byte[BlockSizeBytes];
        int blocksRead = 0, entries = 0;

        for (int block = 0; block < MaxBlocks && result.Count < wanted.Count; block++)
        {
            ulong blockPtr = _mem.ReadPointer(_blocks + (ulong)block * 8);
            if (blockPtr == 0) break;   // blocks are allocated sequentially
            blocksRead++;

            // The last block may be partially committed; fall back to smaller reads if needed.
            int size = BlockSizeBytes;
            while (!_mem.TryReadBytes(blockPtr, buf, size) && size > 0x1000) size /= 2;

            int offs = 0;
            while (offs + 2 <= size)
            {
                ushort header = BitConverter.ToUInt16(buf, offs);
                int length = header >> 6;
                if (length == 0) break;   // end of used entries in this block
                bool wide = (header & 1) != 0;
                int byteLen = wide ? length * 2 : length;
                if (offs + 2 + byteLen > size) break;
                entries++;

                if (!wide && lengths.Contains(length))
                {
                    string name = Encoding.ASCII.GetString(buf, offs + 2, length);
                    if (wanted.Contains(name) && !result.ContainsKey(name))
                        result[name] = (block << 16) | (offs / 2);
                }

                // Entries are 2-byte aligned (FNameEntryAllocator::Stride == 2).
                offs += (2 + byteLen + 1) & ~1;
            }
        }

        Log.Info($"FindIds: {result.Count}/{wanted.Count} names found; walked {blocksRead} blocks, " +
                 $"{entries} entries in {sw.ElapsedMilliseconds} ms.");
        foreach (var missing in wanted.Where(w => !result.ContainsKey(w)))
            Log.Debug($"  FName not found in pool: {missing}");
        return result;
    }
}
