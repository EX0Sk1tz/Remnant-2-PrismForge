using System.IO;
using System.Text;
using R2PrismRuntime.Diagnostics;
using R2PrismRuntime.Memory;
using R2PrismRuntime.Models;

namespace R2PrismRuntime.Game;

/// <summary>Result of walking GEngine → … → InventoryComponent. Zero = not resolved.</summary>
public sealed class ChainResult
{
    public ulong GEngineStatic, GEngine, GameInstance, LocalPlayersData, LocalPlayer, PlayerController, Pawn, Inventory;
    public ulong InventoryOffset;
    public int   LocalPlayersNum;
    public string? FailReason;
    public bool Ok => Inventory != 0 && FailReason == null;
}

/// <summary>Collects report lines and mirrors every line into the log file.</summary>
internal sealed class DiagReport
{
    private readonly StringBuilder _sb = new();
    private readonly bool _keep;
    public DiagReport(bool keep) => _keep = keep;
    public void Line(string s = "")
    {
        if (_keep) _sb.AppendLine(s);
        if (s.Length > 0) Log.Debug(s, "Chain");
    }
    public override string ToString() => _sb.ToString();
}

/// <summary>Thrown when the pointer chain can't be walked; the message is shown to the user.</summary>
public sealed class ChainException(string message) : Exception(message);

public class PrismScanner
{
    private readonly ProcessMemory _mem;
    private readonly FNameReader _names;
    private readonly SegmentCatalog _catalog;

    /// Stable stand-ins for rows missing from the catalog, so re-reads don't create "changes".
    private readonly Dictionary<string, SegmentDef> _unknown = new(StringComparer.OrdinalIgnoreCase);

    /// Inventory offset that worked last time; tried first on the next walk.
    private ulong _inventoryOffset;
    /// GEngine global found on the first walk; the AOB scan is skipped afterwards.
    private ulong _gEngineStatic;

    public PrismScanner(ProcessMemory mem, FNameReader names, SegmentCatalog catalog)
    {
        _mem = mem;
        _names = names;
        _catalog = catalog;
    }

    // ══ UObject helpers ═══════════════════════════════════════════

    /// Structural UObject check: object lives outside the module (heap),
    /// its first qword (vtable) points into the game module, and ClassPrivate looks like a pointer.
    private bool IsUObject(ulong p)
    {
        if (!ProcessMemory.LooksLikePointer(p) || _mem.IsInModule(p)) return false;
        if (!_mem.TryReadPointer(p + GameOffsets.UObject_VTable, out ulong vt) || !_mem.IsInModule(vt)) return false;
        return _mem.TryReadPointer(p + GameOffsets.UObject_Class, out ulong cls) && ProcessMemory.LooksLikePointer(cls);
    }

    private string ObjName(ulong p)
        => _names.IsReady ? _names.Resolve(_mem.ReadInt32(p + GameOffsets.UObject_Name)) : "?";

    private string ClassName(ulong p)
    {
        if (!_names.IsReady) return "?";
        ulong cls = _mem.ReadPointer(p + GameOffsets.UObject_Class);
        return cls == 0 ? "?" : ObjName(cls);
    }

    /// One-line description of any value read from memory.
    private string Describe(ulong p)
    {
        if (p == 0) return "null";
        string region = _mem.DescribeAddress(p);
        if (!IsUObject(p)) return $"[{region}] (not a UObject)";
        return $"[{region}] UObject class='{ClassName(p)}' name='{ObjName(p)}'";
    }

    // ══ GEngine resolution ════════════════════════════════════════

    // Any 7-byte REX + opcode + ModRM [RIP+disp32] instruction: (modrm & 0xC7) == 0x05 means
    // mod=00, rm=101, with any destination register in the reg field.
    private static bool IsRipRelative7(byte[] b)
        => b.Length >= 7
        && (b[0] & 0xF0) == 0x40
        && (b[1] == 0x8B || b[1] == 0x8D || b[1] == 0x89)
        && (b[2] & 0xC7) == 0x05;

    private static string Hex(byte[] b) => string.Join(" ", b.Select(x => x.ToString("X2")));

    /// The CT dereferences the global once ([[pGEngine]] where pGEngine holds the global's
    /// address). A double dereference is kept as a fallback, validated by class name.
    private ulong ValidateGEngineStatic(ulong geStatic, DiagReport r, out string how)
    {
        how = "";
        ulong p1 = _mem.ReadPointer(geStatic);
        ulong p2 = ProcessMemory.LooksLikePointer(p1) ? _mem.ReadPointer(p1) : 0;
        r.Line($"      [static]   = {Log.Hex(p1)}  {Describe(p1)}");
        r.Line($"      [[static]] = {Log.Hex(p2)}  {Describe(p2)}");

        foreach (var (val, label) in new[] { (p1, "single deref [static]"), (p2, "double deref [[static]]") })
        {
            if (!IsUObject(val)) continue;
            string cls = ClassName(val);
            if (_names.IsReady && !cls.Contains("Engine", StringComparison.OrdinalIgnoreCase))
            {
                r.Line($"      {label} is a UObject but class '{cls}' is not an Engine — skipping.");
                continue;
            }
            how = label;
            return val;
        }
        return 0;
    }

    private ChainResult ResolveGEngine(DiagReport r)
    {
        var res = new ChainResult();

        if (_gEngineStatic != 0)
        {
            ulong cached = _mem.ReadPointer(_gEngineStatic);
            if (IsUObject(cached))
            {
                r.Line($"[1] GEngine static (cached) = {Log.Hex(_gEngineStatic)} → {Log.Hex(cached)}  {Describe(cached)}");
                res.GEngineStatic = _gEngineStatic;
                res.GEngine = cached;
                return res;
            }
            _gEngineStatic = 0;
        }

        var hits = _mem.AobScanAll(GameOffsets.AobGEngine, maxHits: 8);
        r.Line($"[1] GEngine AOB ({ProcessMemory.FormatPattern(GameOffsets.AobGEngine, null)}): {hits.Count} hit(s) " +
               string.Join(", ", hits.Select(h => $"{Log.Hex(h)} (module+0x{h - _mem.ModuleBase:X})")));

        foreach (ulong hit in hits)
        {
            // CT: getStaticAddr(aob, nOffset=7) → instruction starts at hit-7. A few other
            // distances are tried in case the compiler ordered the instructions differently.
            var tried = new HashSet<ulong>();
            foreach (int back in new[] { 7 }.Concat(Enumerable.Range(1, 24).Where(x => x != 7)))
            {
                ulong instr = hit - (ulong)back;
                byte[] b = _mem.ReadBytes(instr, 7);
                if (back == 7) r.Line($"    bytes at hit-7: {Hex(b)}  rip-relative={IsRipRelative7(b)}");
                if (!IsRipRelative7(b)) continue;

                ulong geStatic = _mem.RipRelative(instr, dispOffset: 3, instrSize: 7);
                if (!tried.Add(geStatic)) continue;
                r.Line($"    candidate @hit-{back}: {Hex(b)} → static {Log.Hex(geStatic)} ({_mem.DescribeAddress(geStatic)})");
                if (!_mem.IsInModule(geStatic)) { r.Line("      static outside module — skipped."); continue; }

                ulong ge = ValidateGEngineStatic(geStatic, r, out string how);
                if (ge != 0)
                {
                    r.Line($"[2] GEngine static = {Log.Hex(geStatic)} (module+0x{geStatic - _mem.ModuleBase:X}), resolved by {how}");
                    r.Line($"[3] GEngine        = {Log.Hex(ge)}  {Describe(ge)}");
                    res.GEngineStatic = geStatic;
                    res.GEngine = ge;
                    if (how.StartsWith("single", StringComparison.Ordinal)) _gEngineStatic = geStatic;
                    return res;
                }
            }
        }

        // Last resort: generic "mov rax,[rip+x]; test rax,rax" — only accepted when the
        // object's class name proves it is the engine (needs a working FNamePool).
        if (_names.IsReady)
        {
            r.Line("    AOB path failed — trying generic 'mov rax,[rip+x]; test rax,rax' sites validated by class name…");
            byte[] fp = { 0x48, 0x8B, 0x05, 0, 0, 0, 0, 0x48, 0x85, 0xC0 };
            bool[] fm = { true, true, true, false, false, false, false, true, true, true };
            var seen = new HashSet<ulong>();
            foreach (ulong site in _mem.AobScanAll(fp, fm, maxHits: 4000))
            {
                ulong st = _mem.RipRelative(site, 3, 7);
                if (!_mem.IsInModule(st) || !seen.Add(st)) continue;
                ulong obj = _mem.ReadPointer(st);
                if (!IsUObject(obj)) continue;
                string cls = ClassName(obj);
                if (cls.EndsWith("GameEngine", StringComparison.Ordinal))
                {
                    r.Line($"[2] GEngine static (generic fallback) = {Log.Hex(st)} (module+0x{st - _mem.ModuleBase:X})");
                    r.Line($"[3] GEngine = {Log.Hex(obj)}  {Describe(obj)}");
                    res.GEngineStatic = st;
                    res.GEngine = obj;
                    _gEngineStatic = st;
                    return res;
                }
            }
        }
        else
        {
            r.Line("    (generic fallback skipped: FNamePool not ready, cannot validate class names)");
        }

        res.FailReason = hits.Count == 0
            ? "Could not find the game engine signature. Is this the final Steam/Epic build of Remnant 2?"
            : "Engine signature found but it did not lead to a valid engine object.";
        r.Line($"FAIL: {res.FailReason}");
        return res;
    }

    // ══ Chain walking ═════════════════════════════════════════════

    /// Reads [obj+off], logs it, and in deep mode probes neighbouring offsets.
    private ulong Step(DiagReport r, string label, ulong obj, ulong off, bool deep, string? expectClass)
    {
        _mem.TryReadPointer(obj + off, out ulong v);
        bool ok = IsUObject(v);
        r.Line($"  {label,-18} [{Log.Hex(obj)}+0x{off:X}] = {Log.Hex(v)}  {Describe(v)}  {(ok ? "OK" : "<-- BAD")}");

        if (ok && expectClass != null && _names.IsReady &&
            !ClassName(v).Contains(expectClass, StringComparison.OrdinalIgnoreCase))
            r.Line($"    WARNING: expected class containing '{expectClass}', got '{ClassName(v)}' — offset may be stale.");

        if ((!ok || deep) && expectClass != null)
            ProbeMembers(r, obj, off, expectClass);
        return ok ? v : 0;
    }

    /// Scans obj+[off-0x200 .. off+0x200] for UObject pointers whose class matches
    /// <paramref name="expectClass"/>.
    private void ProbeMembers(DiagReport r, ulong obj, ulong around, string expectClass)
    {
        if (!_names.IsReady) { r.Line("    (probe skipped: FNamePool not ready)"); return; }
        foreach (var (off, v) in FindMembers(obj, around > 0x200 ? around - 0x200 : 0, around + 0x200, expectClass))
            r.Line($"    probe: +0x{off:X} → {Log.Hex(v)} class='{ClassName(v)}' name='{ObjName(v)}'" +
                   (off == around ? "  (current offset)" : "  <-- CANDIDATE"));
    }

    private List<(ulong Offset, ulong Value)> FindMembers(ulong obj, ulong from, ulong to, string classContains)
    {
        var found = new List<(ulong, ulong)>();
        int size = (int)(to - from);
        var buf = new byte[size];
        if (!_mem.TryReadBytes(obj + from, buf, size)) return found;
        for (int i = 0; i + 8 <= size; i += 8)
        {
            ulong v = BitConverter.ToUInt64(buf, i);
            if (IsUObject(v) && ClassName(v).Contains(classContains, StringComparison.OrdinalIgnoreCase))
                found.Add((from + (ulong)i, v));
        }
        return found;
    }

    /// Finds the inventory component on the pawn: the CT's known offsets first, then a scan.
    private ulong FindInventory(DiagReport r, ulong pawn, out ulong offset)
    {
        var order = new List<ulong>();
        if (_inventoryOffset != 0) order.Add(_inventoryOffset);
        order.AddRange(GameOffsets.InventoryOffsets.Where(o => o != _inventoryOffset));

        foreach (ulong off in order)
        {
            ulong v = _mem.ReadPointer(pawn + off);
            if (!IsUObject(v)) { r.Line($"  Inventory?  [Pawn+0x{off:X}] = {Log.Hex(v)}  not a UObject"); continue; }
            if (!_names.IsReady || ClassName(v).Contains("Inventory", StringComparison.OrdinalIgnoreCase))
            {
                r.Line($"  {"Inventory",-18} [Pawn+0x{off:X}] = {Log.Hex(v)}  {Describe(v)}  OK");
                offset = off;
                return v;
            }
            r.Line($"  Inventory?  [Pawn+0x{off:X}] = {Log.Hex(v)}  class '{ClassName(v)}' — not an inventory");
        }

        if (_names.IsReady)
        {
            var hits = FindMembers(pawn, 0x400, 0x1400, "Inventory");
            foreach (var (off, v) in hits)
                r.Line($"  inventory scan: Pawn+0x{off:X} → {Log.Hex(v)} class='{ClassName(v)}'");
            if (hits.Count > 0)
            {
                offset = hits[0].Offset;
                return hits[0].Value;
            }
        }
        offset = 0;
        return 0;
    }

    /// Walks the whole pointer chain. deep=true probes neighbouring offsets at every step.
    private ChainResult WalkChain(DiagReport r, bool deep)
    {
        var res = ResolveGEngine(r);
        if (res.GEngine == 0) return res;

        r.Line();
        r.Line("=== Pointer chain ===");

        res.GameInstance = Step(r, "GameInstance", res.GEngine, GameOffsets.GEngine_GameInstance, deep, "GameInstance");
        if (res.GameInstance == 0) return Fail(r, res, "Game instance not available yet. Load into the game and try again.");

        // LocalPlayers is a TArray<ULocalPlayer*>: Data at +0x38, Num at +0x40.
        res.LocalPlayersData = _mem.ReadPointer(res.GameInstance + GameOffsets.GameInstance_LocalPlayers);
        res.LocalPlayersNum = _mem.ReadInt32(res.GameInstance + GameOffsets.GameInstance_LocalPlayers + 8);
        r.Line($"  {"LocalPlayers",-18} [{Log.Hex(res.GameInstance)}+0x38] Data={Log.Hex(res.LocalPlayersData)} Num={res.LocalPlayersNum}");
        if (res.LocalPlayersData == 0 || res.LocalPlayersNum is <= 0 or > 8)
            return Fail(r, res, "No local player yet. Load a character and try again.");

        res.LocalPlayer = Step(r, "LocalPlayer[0]", res.LocalPlayersData, 0, false, null);
        if (res.LocalPlayer == 0) return Fail(r, res, "No local player yet. Load a character and try again.");

        res.PlayerController = Step(r, "PlayerController", res.LocalPlayer, GameOffsets.LocalPlayer_PC, deep, "PlayerController");
        if (res.PlayerController == 0) return Fail(r, res, "No player controller. You are probably in the main menu.");

        res.Pawn = Step(r, "Pawn", res.PlayerController, GameOffsets.PC_Pawn, deep, "Character");
        if (res.Pawn == 0) return Fail(r, res, "Your character isn't spawned. Wait until the loading screen is gone.");

        res.Inventory = FindInventory(r, res.Pawn, out res.InventoryOffset);
        if (res.Inventory == 0) return Fail(r, res, "Couldn't find the inventory on your character. Run the diagnostic.");
        if (_inventoryOffset != res.InventoryOffset)
        {
            Log.Info($"Inventory component at Pawn+0x{res.InventoryOffset:X}.");
            _inventoryOffset = res.InventoryOffset;
        }

        r.Line("Chain OK.");
        return res;
    }

    private static ChainResult Fail(DiagReport r, ChainResult res, string reason)
    {
        res.FailReason = reason;
        r.Line($"FAIL: {reason}");
        return res;
    }

    // ══ Diagnostic ════════════════════════════════════════════════

    /// Writes every step of the pointer chain (with class names and neighbour probes) plus an
    /// inventory dump into the logs folder. Returns the report path.
    public string RunDiagnostic()
    {
        var r = new DiagReport(keep: true);
        r.Line("=== Prismforge — pointer diagnostic ===");
        r.Line($"Time: {DateTime.Now}");
        r.Line($"PID: {_mem.ProcessId}");
        r.Line($"Module base: {Log.Hex(_mem.ModuleBase)}");
        r.Line($"Module size: {Log.Hex(_mem.ModuleSize)}");
        r.Line($"FNamePool: {(_names.IsReady ? $"OK via '{_names.PoolMethod}', Blocks[] at {Log.Hex(_names.BlocksAddr)}" : "NOT READY (class names unavailable)")}");
        r.Line($"Catalog: {_catalog.All.Count(d => d.IsResolved)}/{_catalog.All.Count} segment names resolved");
        foreach (var d in _catalog.All.Where(d => !d.IsResolved))
            r.Line($"  unresolved: {d.Row} ({d.Kind})");
        long failedBefore = _mem.FailedReads;
        r.Line();

        try
        {
            var chain = WalkChain(r, deep: true);
            if (chain.Ok) DumpInventory(r, chain.Inventory);
            if (chain.Pawn != 0) DumpCharacterStats(r, chain.Pawn);
        }
        catch (Exception ex)
        {
            r.Line($"EXCEPTION during diagnostic: {ex}");
            Log.Error("Diagnostic threw.", ex);
        }

        r.Line();
        r.Line($"Failed memory reads during diagnostic: {_mem.FailedReads - failedBefore}");

        Directory.CreateDirectory(Log.LogDirectory);
        string path = Path.Combine(Log.LogDirectory, $"Prismforge_Diag_{DateTime.Now:yyyyMMdd_HHmmss}.txt");
        File.WriteAllText(path, r.ToString());
        Log.Info($"Diagnostic written: {Path.GetFileName(path)} (Logs folder)");
        return path;
    }

    private void DumpInventory(DiagReport r, ulong inv)
    {
        r.Line();
        r.Line("=== Inventory ===");
        ulong itemBase = _mem.ReadPointer(inv + GameOffsets.Inv_DataPtr);
        int count = _mem.ReadInt32(inv + GameOffsets.Inv_Count);
        r.Line($"  Items TArray [inv+0x{GameOffsets.Inv_DataPtr:X}] Data={Log.Hex(itemBase)} Num={count}  ({_mem.DescribeAddress(itemBase)})");
        if (itemBase == 0 || count is <= 0 or > 10000) { r.Line("  Items TArray invalid."); return; }

        int prisms = 0;
        for (int i = 0; i < count; i++)
        {
            ulong item = itemBase + (ulong)i * GameOffsets.Item_Stride;
            ulong bp = _mem.ReadPointer(item + GameOffsets.Item_BlueprintPtr);
            ulong data = _mem.ReadPointer(item + GameOffsets.Item_DataPtr);
            string name = ItemClassName(bp);
            bool isPrism = IsPrismClass(name);
            if (isPrism) prisms++;
            if (i < 60 || isPrism)
                r.Line($"  [{i,4}] item={Log.Hex(item)} bp={Log.Hex(bp)} data={Log.Hex(data)} name='{name}'{(isPrism ? "  <-- PRISM" : "")}");
            if (isPrism && data != 0)
            {
                var p = ReadPrism(item, data, bp, name, prisms);
                r.Line($"         '{p.Name}' internalLv={p.InternalLevel} xp={p.Xp} segments={p.Segments.Count} fed={p.Feeds.Count}");
                foreach (var s in p.Segments)
                {
                    r.Line($"           seg[{s.Index}] {Log.Hex(s.Address)} id={s.RawNameId:X} '{_names.Resolve(s.RawNameId)}' → {s.Def.Name} ({s.Def.Kind}) Lv={s.Level}");
                    r.Line($"             +0x20 obj={Log.Hex(s.ObjectPtr)} {(s.ObjectPtr != 0 ? Describe(s.ObjectPtr) : "")}");
                    r.Line($"             raw: {Hex(_mem.ReadBytes(s.Address, (int)GameOffsets.Seg_Stride))}");
                }
                foreach (var f in p.Feeds)
                    r.Line($"           fed[{f.Index}] {Log.Hex(f.Address)} id={f.RawNameId:X} '{_names.Resolve(f.RawNameId)}' → {f.Def.Name} Lv={f.Level}");
            }
        }
        if (count > 60) r.Line($"  … {count - 60} more items (only prisms listed beyond #60)");
        r.Line($"  Prism items: {prisms}");
    }

    /// CT "Manage System Statistics": [[pawn+0x648]+0x128] = array of {FName, ?, float value}, stride 0x14.
    private void DumpCharacterStats(DiagReport r, ulong pawn)
    {
        r.Line();
        r.Line("=== Character stats (CT 'System Statistics') ===");
        ulong comp = _mem.ReadPointer(pawn + 0x648);
        ulong arr = _mem.ReadPointer(comp + 0x128);
        int n = _mem.ReadInt32(comp + 0x130);
        r.Line($"  component {Log.Hex(comp)} {Describe(comp)}  array={Log.Hex(arr)} num={n}");
        if (arr == 0 || n is <= 0 or > 2000) return;
        for (int i = 0; i < n; i++)
        {
            ulong e = arr + (ulong)i * 0x14;
            byte[] raw = _mem.ReadBytes(e, 0x14);
            r.Line($"  [{i,3}] {Log.Hex(e)} {_names.Resolve(BitConverter.ToInt32(raw, 0)),-36} {BitConverter.ToSingle(raw, 8),12:0.####}" +
                   $"   +4={BitConverter.ToInt32(raw, 4)} +C={BitConverter.ToInt32(raw, 12)}/{BitConverter.ToSingle(raw, 12):0.###} +10={BitConverter.ToInt32(raw, 16)}/{BitConverter.ToSingle(raw, 16):0.###}");
        }
    }

    // ══ Scan ══════════════════════════════════════════════════════

    private string ItemClassName(ulong bp)
    {
        if (bp == 0) return "";
        ulong cls = _mem.ReadPointer(bp + GameOffsets.BP_Class);
        return cls != 0 ? _names.Resolve(_mem.ReadInt32(cls + GameOffsets.Class_NameId)) : "";
    }

    private static bool IsPrismClass(string name)
        => name.Contains("Default__PrismOf", StringComparison.OrdinalIgnoreCase);

    public List<PrismData> ScanPrisms()
    {
        if (!_names.IsReady)
            throw new ChainException("The game's name table wasn't found, so prisms can't be recognised.");

        var chain = WalkChain(new DiagReport(keep: false), deep: false);
        if (!chain.Ok) throw new ChainException(chain.FailReason ?? "Pointer chain failed.");
        _lastChain = chain;

        ulong inv = chain.Inventory;
        ulong itemBase = _mem.ReadPointer(inv + GameOffsets.Inv_DataPtr);
        int count = _mem.ReadInt32(inv + GameOffsets.Inv_Count);
        Log.Info($"Inventory {Log.Hex(inv)}: items Data={Log.Hex(itemBase)} Num={count}");
        if (itemBase == 0 || count is <= 0 or > 10000)
            throw new ChainException("The inventory looks empty or unreadable. Try again in a moment.");

        var results = new List<PrismData>();
        for (int i = 0; i < count; i++)
        {
            ulong itemAddr = itemBase + (ulong)i * GameOffsets.Item_Stride;
            ulong bp = _mem.ReadPointer(itemAddr + GameOffsets.Item_BlueprintPtr);
            ulong data = _mem.ReadPointer(itemAddr + GameOffsets.Item_DataPtr);
            if (bp == 0 || data == 0) continue;

            string cls = ItemClassName(bp);
            Log.Trace($"item[{i}] {Log.Hex(itemAddr)} '{cls}'");
            if (!IsPrismClass(cls)) continue;

            var prism = ReadPrism(itemAddr, data, bp, cls, results.Count + 1);
            Log.Info($"Prism {prism.Numeral} '{prism.Name}' data={Log.Hex(data)} Lv={prism.Level} XP={prism.Xp} " +
                     $"segments={prism.Segments.Count} fed={prism.Feeds.Count}");
            foreach (var s in prism.Segments)
                Log.Debug($"    seg[{s.Index}] {Log.Hex(s.Address)} '{s.Def.Row}' ({s.Def.Kind}) Lv={s.Level} obj={Log.Hex(s.ObjectPtr)}");
            foreach (var f in prism.Feeds)
                Log.Debug($"    fed[{f.Index}] {Log.Hex(f.Address)} '{f.Def.Row}' Lv={f.Level}");
            results.Add(prism);
        }
        Log.Info($"Scan done: {results.Count} prism(s) out of {count} items.");
        return results;
    }

    private PrismData ReadPrism(ulong itemAddr, ulong data, ulong bp, string cls, int index)
    {
        var prism = new PrismData
        {
            Index = index,
            Name = ReadDisplayName(bp) ?? FallbackName(cls),
            ClassName = cls,
            ItemAddress = itemAddr,
            DataAddress = data,
            SegmentsAddress = _mem.ReadPointer(data + GameOffsets.Data_SegPtr),
            FeedAddress = _mem.ReadPointer(data + GameOffsets.Data_FeedPtr),
        };
        prism.InternalLevel = _mem.ReadInt32(data + GameOffsets.Data_Level);
        prism.Xp = prism.EditXp = prism.OriginalXp = _mem.ReadFloat(data + GameOffsets.Data_Xp);

        int segCount = _mem.ReadInt32(data + GameOffsets.Data_SegCount);
        if (prism.SegmentsAddress != 0 && segCount is > 0 and <= 16)
            for (int j = 0; j < segCount; j++)
            {
                var s = new SegmentSlot { Index = j, Address = prism.SegmentsAddress + (ulong)j * GameOffsets.Seg_Stride };
                ReadSegment(s);
                s.EditDef = s.OriginalDef = s.Def;
                s.EditLevel = s.OriginalLevel = s.Level;
                prism.Segments.Add(s);
            }
        else if (segCount != 0)
            Log.Warn($"Prism '{prism.Name}': segments array looks invalid (Data={Log.Hex(prism.SegmentsAddress)}, Num={segCount}).");

        int feedCount = _mem.ReadInt32(data + GameOffsets.Data_FeedCount);
        if (prism.FeedAddress != 0 && feedCount is > 0 and <= 128)
            for (int j = 0; j < feedCount; j++)
            {
                var f = new FeedSlot { Index = j, Address = prism.FeedAddress + (ulong)j * GameOffsets.Feed_Stride };
                ReadFeed(f);
                f.EditDef = f.OriginalDef = f.Def;
                f.EditLevel = f.OriginalLevel = f.Level;
                prism.Feeds.Add(f);
            }
        else if (feedCount != 0)
            Log.Warn($"Prism '{prism.Name}': fed fragment array looks invalid (Data={Log.Hex(prism.FeedAddress)}, Num={feedCount}).");

        prism.Track();
        return prism;
    }

    private void ReadSegment(SegmentSlot s)
    {
        s.RawNameId = _mem.ReadInt32(s.Address + GameOffsets.Seg_RowName);
        s.Level = _mem.ReadInt32(s.Address + GameOffsets.Seg_Level);
        s.ObjectPtr = _mem.ReadPointer(s.Address + GameOffsets.Seg_Object);
        s.Def = DefFor(s.RawNameId, s.ObjectPtr);
    }

    private void ReadFeed(FeedSlot f)
    {
        f.RawNameId = _mem.ReadInt32(f.Address + GameOffsets.Feed_RowName);
        f.Level = _mem.ReadInt32(f.Address + GameOffsets.Feed_Level);
        f.Def = DefFor(f.RawNameId, 0);
    }

    /// RowName first; for legendaries the CT reads the name from the cached object instead,
    /// so that is the fallback (e.g. "Default__PrismSegment_MasterKiller_C" → "MasterKiller").
    private SegmentDef DefFor(int rowId, ulong obj)
    {
        var def = _catalog.ById(rowId);
        if (def != null) return def;

        string row = _names.Resolve(rowId);
        def = _catalog.ByRow(row);
        if (def != null) return def;

        if (ProcessMemory.LooksLikePointer(obj))
        {
            string objName = StripAffixes(_names.Resolve(_mem.ReadInt32(obj + GameOffsets.UObject_Name)));
            def = _catalog.ByRow(objName);
            if (def != null) return def;
            if (string.IsNullOrEmpty(row)) row = objName;
        }

        if (!_unknown.TryGetValue(row, out def))
            _unknown[row] = def = SegmentDef.Unknown(row);
        return def;
    }

    private static string StripAffixes(string n)
    {
        foreach (var p in new[] { "Default__", "PrismSegment_", "RelicFragment_" })
            if (n.StartsWith(p, StringComparison.OrdinalIgnoreCase)) n = n[p.Length..];
        return n.EndsWith("_C", StringComparison.Ordinal) ? n[..^2] : n;
    }

    /// CT: wide string at [[[bp+0x110]+0x300]+0x30]. Validated, since it isn't confirmed.
    private string? ReadDisplayName(ulong bp)
    {
        ulong cls = _mem.ReadPointer(bp + GameOffsets.BP_Class);
        if (!ProcessMemory.LooksLikePointer(cls)) return null;
        ulong text = _mem.ReadPointer(cls + GameOffsets.Class_DisplayText);
        if (!ProcessMemory.LooksLikePointer(text)) return null;
        ulong str = _mem.ReadPointer(text + GameOffsets.Text_StringPtr);
        if (!ProcessMemory.LooksLikePointer(str)) return null;
        string? s = _mem.ReadStringWide(str, 64);
        if (string.IsNullOrWhiteSpace(s) || s.Length < 2 || s.Any(c => char.IsControl(c))) return null;
        return s.Trim();
    }

    private static string FallbackName(string cls)
    {
        string n = cls.Replace("Default__", "", StringComparison.OrdinalIgnoreCase);
        if (n.EndsWith("_C", StringComparison.Ordinal)) n = n[..^2];
        if (n.StartsWith("PrismOf", StringComparison.OrdinalIgnoreCase)) n = "Prism of " + n[7..];
        // "Prism of TheVoid" → "Prism of The Void"
        return System.Text.RegularExpressions.Regex.Replace(n, "(?<=[a-z])(?=[A-Z])", " ");
    }

    // ══ Class default objects (segment +0x20) ═════════════════════

    private ObjectArray? _objects;
    private ChainResult? _lastChain;

    /// Player character from the last successful chain walk (0 if none).
    public ulong LastPawn => _lastChain is { Ok: true } c ? c.Pawn : 0;

    /// Fills <see cref="SegmentDef.DefaultObject"/> for every row: first from segments already
    /// in memory, then by searching GUObjectArray. Returns how many rows with a class are known.
    public int IndexDefaultObjects(IEnumerable<PrismData> prisms)
    {
        foreach (var s in prisms.SelectMany(p => p.Segments))
            if (s.ObjectPtr != 0 && s.Def.HasClass && s.Def.DefaultObject == 0 && IsDefaultObjectOf(s.ObjectPtr, s.Def))
                s.Def.DefaultObject = s.ObjectPtr;

        var missing = _catalog.All.Where(d => d.HasClass && d.IsResolved && d.DefaultObject == 0).ToList();
        if (missing.Count > 0)
        {
            // A class whose default object name isn't even in the name pool was never loaded.
            var ids = _names.FindIds(missing.Select(d => d.DefaultObjectName));
            if (ids.Count > 0 && _lastChain is { Ok: true } c)
            {
                _objects ??= new ObjectArray(_mem);
                if (_objects.IsReady || _objects.Find(c.GEngine, c.GameInstance, c.PlayerController, c.Pawn))
                {
                    var found = _objects.FindDefaultObjects(ids.Values.ToHashSet());
                    foreach (var d in missing)
                        if (ids.TryGetValue(d.DefaultObjectName, out int id) && found.TryGetValue(id, out ulong obj)
                            && IsDefaultObjectOf(obj, d))
                            d.DefaultObject = obj;
                }
            }
        }

        int known = _catalog.All.Count(d => d.HasClass && d.DefaultObject != 0);
        Log.Info($"Class default objects known for {known}/{_catalog.All.Count(d => d.HasClass)} rows.");
        foreach (var d in _catalog.All.Where(d => d.HasClass && d.IsResolved && d.DefaultObject == 0))
            Log.Debug($"  not loaded: {d.DefaultObjectName}");
        return known;
    }

    private bool IsDefaultObjectOf(ulong obj, SegmentDef d)
        => IsUObject(obj) && ObjName(obj).Equals(d.DefaultObjectName, StringComparison.OrdinalIgnoreCase);

    /// Default object to store at segment+0x20 for <paramref name="d"/>: re-validated, 0 if unknown.
    public ulong DefaultObjectFor(SegmentDef d)
    {
        if (!d.HasClass || d.DefaultObject == 0) return 0;
        if (IsDefaultObjectOf(d.DefaultObject, d)) return d.DefaultObject;
        d.DefaultObject = 0;
        return 0;
    }

    // ══ Validation and re-reading ═════════════════════════════════

    /// Checks that the addresses captured during the scan still describe this prism.
    /// Returns null when valid, otherwise a reason.
    public string? CheckStale(PrismData p)
    {
        if (!_mem.IsProcessAlive) return "The game is no longer running.";
        if (_mem.ReadPointer(p.ItemAddress + GameOffsets.Item_DataPtr) != p.DataAddress)
            return "The inventory changed since the last scan.";
        if (_mem.ReadPointer(p.DataAddress + GameOffsets.Data_SegPtr) != p.SegmentsAddress
            || _mem.ReadInt32(p.DataAddress + GameOffsets.Data_SegCount) != p.Segments.Count)
            return "This prism's segments changed in game.";
        if (_mem.ReadPointer(p.DataAddress + GameOffsets.Data_FeedPtr) != p.FeedAddress
            || _mem.ReadInt32(p.DataAddress + GameOffsets.Data_FeedCount) != p.Feeds.Count)
            return "This prism's fed fragments changed in game.";
        return null;
    }

    /// Refreshes the live values of an existing prism (layout must be unchanged; see CheckStale).
    public void Reread(PrismData p)
    {
        p.Xp = _mem.ReadFloat(p.DataAddress + GameOffsets.Data_Xp);
        p.InternalLevel = _mem.ReadInt32(p.DataAddress + GameOffsets.Data_Level);
        foreach (var s in p.Segments) ReadSegment(s);
        foreach (var f in p.Feeds) ReadFeed(f);
    }
}
