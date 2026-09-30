using System.Text.RegularExpressions;
using R2PrismRuntime.Diagnostics;
using R2PrismRuntime.Memory;

namespace R2PrismRuntime.Game;

/// <summary>One row of PrismStoneDataTable or PrismStoneMythicDataTable, read from the running game.</summary>
public sealed record LiveSegmentRow(
    string Row, int NameId, bool Mythic,
    string? DisplayName, string? Description,
    string ClassObject, string Category, bool Combo, int Rarity);

/// <summary>
/// Reads the two prism data tables straight from memory, so segments added or changed by mods
/// (e.g. BeyondHell) are known without updating the embedded catalog.
///
///   UDataTable (UE5)
///     +0x28  UScriptStruct* RowStruct       FPrismStoneDataTableRow
///     +0x30  TMap&lt;FName, uint8*&gt; RowMap   TSparseArray: Data, Num (+0x08)
///   RowMap element: +0x00 FName Key, +0x08 uint8* Row, stride 0x18
///
/// Field offsets inside the row are taken from the struct's reflection data (FProperty chain),
/// so a changed row layout doesn't need new constants.
/// </summary>
public sealed class PrismTableReader
{
    public const string MainTable = "PrismStoneDataTable";
    public const string MythicTable = "PrismStoneMythicDataTable";
    private const string RowStructName = "PrismStoneDataTableRow";

    private const ulong Table_RowStruct = 0x28;
    private const ulong Table_RowMap = 0x30;
    private const int MapElementStride = 0x18;
    private const int MaxRows = 5000;

    /// EPrismSegmentCategory, in declaration order.
    private static readonly string[] Categories =
        { "Red", "Blue", "Yellow", "RedRed", "BlueBlue", "YellowYellow", "RedBlue", "RedYellow", "BlueYellow", "None" };

    private static readonly string[] Fields =
        { "DisplayName", "ClassObject", "Rarity", "Category", "ComboSegment", "Description" };

    private readonly ProcessMemory _mem;
    private readonly FNameReader _names;
    private readonly ObjectArray _objects;
    private readonly Action<string> _trace;

    public PrismTableReader(ProcessMemory mem, FNameReader names, ObjectArray objects, Action<string>? trace = null)
    {
        _mem = mem;
        _names = names;
        _objects = objects;
        _trace = trace ?? (s => Log.Debug(s));
    }

    /// Rows of both tables, or null unless both could be read (with one table missing, the merge
    /// would retire every segment of the other).
    public List<LiveSegmentRow>? ReadAll()
    {
        var ids = _names.FindIds(new[] { MainTable, MythicTable });
        if (ids.Count == 0) { _trace("Prism tables: table names not in the name pool (not loaded yet)."); return null; }

        var hits = _objects.FindObjectsByName(ids.Values.ToHashSet());
        var rows = new List<LiveSegmentRow>();
        int tablesRead = 0;
        foreach (var (name, mythic) in new[] { (MainTable, false), (MythicTable, true) })
        {
            if (!ids.TryGetValue(name, out int id)) { _trace($"Prism tables: '{name}' not in the name pool."); continue; }
            ulong table = hits.Where(h => h.NameId == id).Select(h => h.Object).FirstOrDefault(IsDataTable);
            if (table == 0) { _trace($"Prism tables: no DataTable object named '{name}' ({hits.Count(h => h.NameId == id)} object(s) with that name)."); continue; }

            var read = ReadTable(table, name, mythic);
            if (read == null) continue;
            tablesRead++;
            rows.AddRange(read);
        }
        return tablesRead == 2 ? rows : null;
    }

    private string ObjName(ulong obj) => _names.Resolve(_mem.ReadInt32(obj + GameOffsets.UObject_Name));

    private bool IsDataTable(ulong obj)
    {
        ulong cls = _mem.ReadPointer(obj + GameOffsets.UObject_Class);
        return ProcessMemory.LooksLikePointer(cls) && ObjName(cls).Contains("DataTable", StringComparison.Ordinal);
    }

    private List<LiveSegmentRow>? ReadTable(ulong table, string name, bool mythic)
    {
        ulong rowStruct = _mem.ReadPointer(table + Table_RowStruct);
        if (!ProcessMemory.LooksLikePointer(rowStruct) || ObjName(rowStruct) != RowStructName)
        {
            _trace($"Prism tables: {name} at {Log.Hex(table)}: RowStruct is '{(ProcessMemory.LooksLikePointer(rowStruct) ? ObjName(rowStruct) : "?")}', " +
                   $"expected '{RowStructName}'.");
            return null;
        }

        var offs = FieldOffsets(rowStruct);
        if (offs == null) { _trace($"Prism tables: couldn't read the field layout of {RowStructName}."); return null; }

        ulong data = _mem.ReadPointer(table + Table_RowMap);
        int num = _mem.ReadInt32(table + Table_RowMap + 8);
        if (!ProcessMemory.LooksLikePointer(data) || num is <= 0 or > MaxRows)
        {
            _trace($"Prism tables: {name} RowMap looks invalid (Data={Log.Hex(data)}, Num={num}).");
            return null;
        }

        var buf = new byte[num * MapElementStride];
        if (!_mem.TryReadBytes(data, buf, buf.Length)) { _trace($"Prism tables: {name} RowMap unreadable."); return null; }

        var rows = new List<LiveSegmentRow>();
        for (int i = 0; i < num; i++)
        {
            int keyId = BitConverter.ToInt32(buf, i * MapElementStride);
            int keyNumber = BitConverter.ToInt32(buf, i * MapElementStride + 4);
            ulong row = BitConverter.ToUInt64(buf, i * MapElementStride + 8);
            string key = keyId > 0 ? _names.Resolve(keyId) : "";
            // Free slots of the sparse array hold stale data; a real row has a name and a row pointer.
            if (key.Length == 0 || key.StartsWith('#') || !ProcessMemory.LooksLikePointer(row)) continue;
            if (keyNumber != 0)
            {
                // Segments store only the ComparisonIndex we write; a numbered FName can't be picked safely.
                _trace($"Prism tables: {name} row '{key}_{keyNumber - 1}' has a numbered name; skipped.");
                continue;
            }

            byte cat = _mem.ReadBytes(row + (ulong)offs["Category"], 1)[0];
            rows.Add(new LiveSegmentRow(
                Row: key,
                NameId: keyId,
                Mythic: mythic,
                DisplayName: ReadText(row + (ulong)offs["DisplayName"]),
                Description: CleanDescription(ReadText(row + (ulong)offs["Description"])),
                ClassObject: ReadSoftClassName(row + (ulong)offs["ClassObject"]),
                Category: cat < Categories.Length ? Categories[cat] : "None",
                Combo: _mem.ReadBytes(row + (ulong)offs["ComboSegment"], 1)[0] != 0,
                Rarity: _mem.ReadInt32(row + (ulong)offs["Rarity"])));
        }
        _trace($"Prism tables: {name} at {Log.Hex(table)}: {rows.Count} row(s) of {num} map entries. " +
               $"Row offsets: {string.Join(", ", offs.Select(kv => $"{kv.Key}=0x{kv.Value:X}"))}.");
        return rows;
    }

    // ── Reflection ────────────────────────────────────────────────

    /// UStruct::ChildProperties, FField::Next / NamePrivate and FProperty::Offset_Internal. The UE5.2
    /// values come first; the alternatives cover neighbouring engine versions. A layout is accepted only
    /// when every expected field is found with a plausible offset.
    private static readonly ulong[] ChildPropsOffsets = { 0x50, 0x48, 0x58 };
    private static readonly (ulong Next, ulong Name)[] FieldLayouts = { (0x20, 0x28), (0x18, 0x20), (0x20, 0x30) };
    private static readonly ulong[] OffsetInternalOffsets = { 0x4C, 0x44, 0x50 };

    private Dictionary<string, int>? FieldOffsets(ulong structPtr)
    {
        foreach (ulong cp in ChildPropsOffsets)
            foreach (var (next, name) in FieldLayouts)
                foreach (ulong offInternal in OffsetInternalOffsets)
                {
                    var found = new Dictionary<string, int>(StringComparer.Ordinal);
                    ulong field = _mem.ReadPointer(structPtr + cp);
                    for (int n = 0; n < 64 && ProcessMemory.LooksLikePointer(field); n++)
                    {
                        string fname = _names.Resolve(_mem.ReadInt32(field + name));
                        if (Fields.Contains(fname)) found[fname] = _mem.ReadInt32(field + offInternal);
                        field = _mem.ReadPointer(field + next);
                    }
                    // FTableRowBase has a vtable, so DisplayName (the first field) sits at +0x08.
                    if (found.Count == Fields.Length && found["DisplayName"] == 8
                        && found.Values.All(o => o is > 0 and < 0x400) && found.Values.Distinct().Count() == Fields.Length)
                        return found;
                }
        return null;
    }

    // ── Values ────────────────────────────────────────────────────

    /// FText: [text] = ITextData; its display string is an inline FString (Data, Num, Max). The CT's
    /// display-name read uses +0x30; neighbouring offsets are tried and validated as an FString.
    private static readonly ulong[] TextStringOffsets = { 0x30, 0x28, 0x38, 0x40, 0x20, 0x48, 0x18, 0x10 };

    private string? ReadText(ulong text)
    {
        ulong td = _mem.ReadPointer(text);
        if (!ProcessMemory.LooksLikePointer(td)) return null;
        foreach (ulong off in TextStringOffsets)
        {
            ulong str = _mem.ReadPointer(td + off);
            int num = _mem.ReadInt32(td + off + 8);
            int max = _mem.ReadInt32(td + off + 12);
            if (!ProcessMemory.LooksLikePointer(str) || num is < 2 or > 4096 || max < num) continue;
            string? s = _mem.ReadStringWide(str, num);
            if (s == null || s.Length != num - 1) continue;
            if (s.Any(c => char.IsControl(c) && c is not ('\n' or '\r' or '\t'))) continue;
            // BeyondHell wraps some texts in quotes ("\"Unknown Segment\"").
            return s.Trim().Trim('"').Trim();
        }
        return null;
    }

    /// TSoftClassPtr: FWeakObjectPtr, then FSoftObjectPath. UE5.1+ stores the path as
    /// (PackageName, AssetName) FNames; UE5.0 as one "/Game/…/Pkg.Asset_C" FName. Returns the
    /// class name (e.g. "PrismSegment_TrenchSoldier_C"), or "" when the row has no class.
    private string ReadSoftClassName(ulong ptr)
    {
        foreach (ulong o in new ulong[] { 0x08, 0x10 })
        {
            int pkgId = _mem.ReadInt32(ptr + o);
            if (pkgId <= 0) continue;
            string pkg = _names.Resolve(pkgId);
            if (!pkg.StartsWith('/')) continue;
            if (pkg.Contains('.')) return LastSegment(pkg);
            string asset = _names.Resolve(_mem.ReadInt32(ptr + o + 8));
            if (asset.Length > 0 && !asset.StartsWith('#')) return LastSegment(asset);
        }
        return "";
    }

    /// Some rows hold a full path as the asset name (seen for ConsumableDuration with BeyondHell):
    /// "/Game/…/RelicFragment_ConsumableDuration" → "RelicFragment_ConsumableDuration".
    private static string LastSegment(string path) => path[(path.LastIndexOfAny(new[] { '/', '.' }) + 1)..];

    private static readonly Regex RichTextTag = new("<[^>]*>", RegexOptions.Compiled);

    /// Drops rich-text markup ("&lt;bold&gt;…&lt;/&gt;") so the description reads as plain text.
    private static string? CleanDescription(string? s)
        => string.IsNullOrWhiteSpace(s) ? null : RichTextTag.Replace(s, "").Replace("\r\n", "\n").Trim();
}
