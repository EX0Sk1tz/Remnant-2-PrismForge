using System.IO;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace R2PrismRuntime.Game;

public enum SegmentKind { Standard, Fusion, Legendary, Unknown }

/// <summary>
/// One row of PrismStoneDataTable / PrismStoneMythicDataTable. <see cref="Row"/> is the
/// FName stored in a segment's RowName; <see cref="NameId"/> is its live ComparisonIndex.
/// </summary>
public sealed class SegmentDef
{
    [JsonPropertyName("row")]         public string Row { get; init; } = "";
    [JsonPropertyName("name")]        public string Name { get; init; } = "";
    [JsonPropertyName("kind")]        public SegmentKind Kind { get; init; }
    [JsonPropertyName("category")]    public string Category { get; init; } = "";
    [JsonPropertyName("description")] public string Description { get; init; } = "";

    /// Blueprint class of the row (e.g. "RelicFragment_CriticalDamage_C"); empty for legendaries
    /// that work through an action instead.
    [JsonPropertyName("classObject")] public string ClassObject { get; init; } = "";

    /// FName ComparisonIndex in the running game, or -1 if not resolved yet.
    [JsonIgnore] public int NameId { get; set; } = -1;
    [JsonIgnore] public bool IsResolved => NameId > 0;

    /// Live address of the class default object that segments cache at +0x20 (0 = unknown).
    [JsonIgnore] public ulong DefaultObject { get; set; }
    [JsonIgnore] public bool HasClass => ClassObject.Length > 0;
    [JsonIgnore] public string DefaultObjectName => "Default__" + ClassObject;

    /// First and second colour of the gem (a fusion has two, e.g. "RedYellow").
    [JsonIgnore] public string Primary => Kind == SegmentKind.Legendary ? "Legendary" : SplitCategory().Item1;
    [JsonIgnore] public string Secondary => Kind == SegmentKind.Legendary ? "Legendary" : SplitCategory().Item2;

    [JsonIgnore]
    public string Group => Kind switch
    {
        SegmentKind.Fusion    => "Fusion",
        SegmentKind.Legendary => "Legendary",
        _ => Category switch { "Red" => "Offense", "Blue" => "Defense", "Yellow" => "Utility", _ => "Other" },
    };

    private (string, string) SplitCategory()
    {
        foreach (var c in new[] { "Red", "Blue", "Yellow" })
            if (Category.StartsWith(c, StringComparison.Ordinal))
            {
                string rest = Category[c.Length..];
                return (c, rest.Length > 0 ? rest : c);
            }
        return ("None", "None");
    }

    public static SegmentDef Unknown(string row) => new()
    {
        Row = row,
        Name = string.IsNullOrEmpty(row) ? "Empty" : row,
        Kind = SegmentKind.Unknown,
        Category = "None",
        Description = "Not in the prism data tables.",
    };

    public override string ToString() => Name;
}

/// <summary>Every prism segment the game knows, loaded from the embedded data table export.</summary>
public sealed class SegmentCatalog
{
    private readonly Dictionary<string, SegmentDef> _byRow;
    private readonly Dictionary<int, SegmentDef> _byId = new();

    public IReadOnlyList<SegmentDef> All { get; }

    private SegmentCatalog(List<SegmentDef> defs)
    {
        All = defs;
        _byRow = defs.ToDictionary(d => d.Row, StringComparer.OrdinalIgnoreCase);
    }

    public static SegmentCatalog Load()
    {
        var asm = Assembly.GetExecutingAssembly();
        string res = asm.GetManifestResourceNames().Single(n => n.EndsWith("SegmentCatalog.json", StringComparison.Ordinal));
        using Stream s = asm.GetManifestResourceStream(res)!;
        var opts = new JsonSerializerOptions { Converters = { new JsonStringEnumConverter() } };
        var defs = JsonSerializer.Deserialize<List<SegmentDef>>(s, opts) ?? new();
        return new SegmentCatalog(defs);
    }

    /// FName comparison is case-insensitive in Unreal, so lookups are too.
    public SegmentDef? ByRow(string row) => _byRow.TryGetValue(row, out var d) ? d : null;

    public SegmentDef? ById(int id) => _byId.TryGetValue(id, out var d) ? d : null;

    /// Looks up every row name in the live FNamePool. Returns how many were found.
    public int ResolveIds(FNameReader names)
    {
        _byId.Clear();
        foreach (var d in All) { d.NameId = -1; d.DefaultObject = 0; }
        if (!names.IsReady) return 0;

        var ids = names.FindIds(All.Select(d => d.Row));
        foreach (var d in All)
            if (ids.TryGetValue(d.Row, out int id))
            {
                d.NameId = id;
                _byId[id] = d;
            }
        return _byId.Count;
    }
}
