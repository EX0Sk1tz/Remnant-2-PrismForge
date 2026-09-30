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
    // Settable so rows read from the running game (see SegmentCatalog.MergeLive) can update them.
    [JsonPropertyName("name")]        public string Name { get; set; } = "";
    [JsonPropertyName("kind")]        public SegmentKind Kind { get; set; }
    [JsonPropertyName("category")]    public string Category { get; set; } = "";
    [JsonPropertyName("description")] public string Description { get; set; } = "";

    /// Blueprint class of the row (e.g. "RelicFragment_CriticalDamage_C"); empty for legendaries
    /// that work through an action instead.
    [JsonPropertyName("classObject")] public string ClassObject { get; set; } = "";

    /// Row that isn't in the embedded catalog: added by a mod, read from the game's tables.
    [JsonIgnore] public bool IsAdded { get; set; }

    /// Mod that brought this segment (e.g. "Beyond Hell"): set for rows a mod added, renamed or pointed
    /// to a different segment class. Null for vanilla segments.
    [JsonIgnore] public string? ModSource { get; set; }

    /// Row a mod switched off by pointing it to PrismSegment_Invalid (BeyondHell does this for most
    /// vanilla legendaries). Still shown on prisms that have it, but not offered in the pickers.
    [JsonIgnore] public bool IsDisabled { get; set; }

    /// Embedded row that the game's tables don't contain (a mod removed it): never offered or resolved.
    [JsonIgnore] public bool NotInTables { get; set; }

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
    // Replaced, not changed, by MergeLive: background scans may be enumerating the old instances.
    private List<SegmentDef> _all;
    private Dictionary<string, SegmentDef> _byRow;
    private Dictionary<int, SegmentDef> _byId = new();

    public IReadOnlyList<SegmentDef> All => _all;

    /// Rows the game can use: everything except embedded rows a mod removed.
    public IEnumerable<SegmentDef> Active => _all.Where(d => !d.NotInTables);

    /// True once the game's own tables were merged in (see <see cref="MergeLive"/>).
    public bool IsLive { get; private set; }

    private SegmentCatalog(List<SegmentDef> defs)
    {
        _all = defs;
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

        var ids = names.FindIds(Active.Select(d => d.Row));
        foreach (var d in Active)
            if (ids.TryGetValue(d.Row, out int id))
            {
                d.NameId = id;
                _byId[id] = d;
            }
        return _byId.Count;
    }

    /// <summary>
    /// Merges the rows of the game's PrismStoneDataTable / PrismStoneMythicDataTable. The game is the
    /// authority: known rows take its names, descriptions and classes (a mod may point a row to a
    /// different segment), unknown rows are added, and embedded rows missing from the game are retired.
    /// </summary>
    /// <paramref name="modLabel"/> is stored as <see cref="SegmentDef.ModSource"/> on every row the
    /// mod added, renamed or pointed to a different class.
    public LiveMergeResult MergeLive(IReadOnlyCollection<LiveSegmentRow> rows, string modLabel = "Mod")
    {
        var result = new LiveMergeResult();
        var seen = new HashSet<SegmentDef>();
        var all = new List<SegmentDef>(_all);
        var byRow = new Dictionary<string, SegmentDef>(_byRow, StringComparer.OrdinalIgnoreCase);
        var byId = new Dictionary<int, SegmentDef>(_byId);
        foreach (var r in rows)
        {
            var kind = r.Mythic ? SegmentKind.Legendary
                     : r.Combo || IsTwoColour(r.Category) ? SegmentKind.Fusion
                     : SegmentKind.Standard;
            string category = r.Mythic ? "Legendary" : r.Category;

            if (!byRow.TryGetValue(r.Row, out var d))
            {
                d = new SegmentDef
                {
                    Row = r.Row,
                    Name = string.IsNullOrWhiteSpace(r.DisplayName) ? Humanize(r.Row) : r.DisplayName,
                    Kind = kind,
                    Category = category,
                    Description = r.Description ?? "",
                    ClassObject = WithC(r.ClassObject),
                    IsAdded = true,
                    ModSource = modLabel,
                };
                all.Add(d);
                byRow[d.Row] = d;
                result.Added.Add(d);
            }
            else
            {
                if (seen.Contains(d)) { result.Duplicates.Add(r.Row); continue; }
                bool renamed = !string.IsNullOrWhiteSpace(r.DisplayName)
                               && !string.Equals(d.Name, r.DisplayName, StringComparison.OrdinalIgnoreCase);
                if (renamed || !SameClass(d.ClassObject, r.ClassObject)) d.ModSource = modLabel;
                if (!string.IsNullOrWhiteSpace(r.DisplayName)) d.Name = r.DisplayName;
                if (r.Description != null) d.Description = r.Description;
                if (d.Kind != kind) result.KindChanged.Add($"{d.Row}: {d.Kind} → {kind}");
                d.Kind = kind;
                d.Category = category;
                if (!SameClass(d.ClassObject, r.ClassObject))
                {
                    result.ClassChanged.Add($"{d.Row}: '{d.ClassObject}' → '{r.ClassObject}'");
                    d.ClassObject = WithC(r.ClassObject);
                    d.DefaultObject = 0;
                }
                result.Updated++;
            }
            seen.Add(d);
            d.IsDisabled = StripC(r.ClassObject).Equals("PrismSegment_Invalid", StringComparison.OrdinalIgnoreCase);
            if (d.IsDisabled) result.Disabled.Add(d.Row);
            d.NotInTables = false;
            d.NameId = r.NameId;
            byId[r.NameId] = d;
        }

        foreach (var d in all.Where(d => !seen.Contains(d) && !d.NotInTables))
        {
            d.NotInTables = true;
            if (d.NameId > 0) byId.Remove(d.NameId);
            d.NameId = -1;
            result.Retired.Add(d.Row);
        }
        // The game's table has no text for fusions; the embedded ones bring theirs. New fusions get
        // one from their row name, which joins two fragment rows ("StatusDamageModDamage").
        var fragments = all.Where(d => d.Kind == SegmentKind.Standard && !d.NotInTables).ToList();
        foreach (var d in result.Added.Where(d => d.Kind == SegmentKind.Fusion && d.Description.Length == 0))
            d.Description = DescribeFusion(d.Row, fragments) ?? "";

        _all = all;
        _byRow = byRow;
        _byId = byId;
        IsLive = true;
        return result;
    }

    /// "MeleeCriticalStaminaPercent" → "Melee Critical Chance + Stamina Bonus". Each half is a fragment
    /// row or display name ("HealingEffectiveness" is the row HealingEfficacy), or the unique fragment
    /// row it starts with. A row that doesn't split names a group: "CritChance" → every fragment ending
    /// in "Critical Chance". Null when nothing matches.
    private static string? DescribeFusion(string row, List<SegmentDef> fragments)
    {
        var keys = fragments.Select(f => (Def: f, Row: Key(f.Row), Name: Key(f.Name))).ToList();

        SegmentDef? Match(string part)
        {
            string k = Key(part);
            var exact = keys.FirstOrDefault(f => f.Row == k || f.Name == k).Def;
            if (exact != null) return exact;
            var prefixed = keys.Where(f => f.Row.StartsWith(k, StringComparison.Ordinal)).Take(2).ToList();
            return prefixed.Count == 1 ? prefixed[0].Def : null;
        }

        for (int i = 1; i < row.Length; i++)
        {
            if (!char.IsUpper(row[i])) continue;
            if (Match(row[..i]) is { } a && Match(row[i..]) is { } b)
                return a == b ? $"{a.Name} ×2" : $"{a.Name} + {b.Name}";
        }

        string whole = Key(row);
        var group = keys.Where(f => f.Row.EndsWith(whole, StringComparison.Ordinal) || f.Name.EndsWith(whole, StringComparison.Ordinal))
                        .Select(f => f.Def.Name).ToList();
        return group.Count >= 2 ? string.Join(" + ", group) : null;
    }

    /// Lower-case, no spaces, "Crit" spelled out: "Melee Critical Chance" and "MeleeCritChance" compare equal.
    private static string Key(string s)
        => System.Text.RegularExpressions.Regex.Replace(s.Replace(" ", ""), "Crit(?!ical)", "Critical",
               System.Text.RegularExpressions.RegexOptions.IgnoreCase).ToLowerInvariant();

    /// Class names compare without the "_C" suffix, which some table rows leave out.
    internal static bool SameClass(string a, string b)
        => string.Equals(StripC(a), StripC(b), StringComparison.OrdinalIgnoreCase);

    private static string StripC(string s) => s.EndsWith("_C", StringComparison.OrdinalIgnoreCase) ? s[..^2] : s;

    /// Blueprint class names end in "_C" (DefaultObjectName relies on it); empty stays empty.
    private static string WithC(string s) => s.Length == 0 ? s : StripC(s) + "_C";

    private static bool IsTwoColour(string category)
        => category is "RedRed" or "BlueBlue" or "YellowYellow" or "RedBlue" or "RedYellow" or "BlueYellow";

    /// "TrenchSoldier" → "Trench Soldier", for rows whose display name can't be read.
    private static string Humanize(string row)
        => System.Text.RegularExpressions.Regex.Replace(row, "(?<=[a-z])(?=[A-Z])", " ");
}

public sealed class LiveMergeResult
{
    public int Updated;
    public List<SegmentDef> Added { get; } = new();
    public List<string> ClassChanged { get; } = new();
    public List<string> KindChanged { get; } = new();
    public List<string> Retired { get; } = new();
    public List<string> Disabled { get; } = new();
    public List<string> Duplicates { get; } = new();

    public override string ToString()
        => $"{Updated} known row(s) updated, {Added.Count} added, {ClassChanged.Count} with a different class, " +
           $"{KindChanged.Count} with a different kind, {Disabled.Count} disabled by a mod, {Retired.Count} retired";
}
