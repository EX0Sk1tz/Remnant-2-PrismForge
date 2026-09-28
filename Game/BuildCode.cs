using System.IO;
using System.IO.Compression;
using System.Text;
using System.Text.Json;

namespace R2PrismRuntime.Game;

/// <summary>A prism layout from a build code: segments and fed fragments by row name, plus XP and a note.</summary>
public sealed record BuildSpec(string Note, float? Xp, IReadOnlyList<(string Row, int Level)> Segments,
                               IReadOnlyList<(string Row, int Level)> Feeds);

/// <summary>
/// Build codes shared with the Prismforge planner web page:
///     "PF1-" + base64url( raw deflate( UTF-8 JSON ) )
///     JSON: { "v":1, "n":"note", "xp":1000, "s":[["CriticalDamage",10],…], "f":[["ModDamage",5],…] }
/// Rows are the data table row names (as in SegmentCatalog.json), so codes survive catalog changes.
/// The page encodes with the browser's CompressionStream("deflate-raw"); keep both sides in sync.
/// </summary>
public static class BuildCode
{
    public const string Prefix = "PF1-";
    public const int MaxNote = 200, MaxSegments = PrismScanner.MaxSegmentCapacity, MaxFeeds = 128;
    public const int MaxSegmentLevel = 100_000_000, MaxFeedLevel = 999;

    public static string Encode(BuildSpec b)
    {
        using var ms = new MemoryStream();
        using (var w = new Utf8JsonWriter(ms))
        {
            w.WriteStartObject();
            w.WriteNumber("v", 1);
            if (b.Note.Length > 0) w.WriteString("n", b.Note);
            if (b.Xp is float xp) w.WriteNumber("xp", xp);
            WriteList(w, "s", b.Segments);
            WriteList(w, "f", b.Feeds);
            w.WriteEndObject();
        }
        using var packed = new MemoryStream();
        using (var d = new DeflateStream(packed, CompressionLevel.SmallestSize, leaveOpen: true))
            d.Write(ms.ToArray());
        return Prefix + Convert.ToBase64String(packed.ToArray()).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    }

    /// Parses a code. Throws FormatException with a readable message when it isn't valid.
    public static BuildSpec Decode(string code)
    {
        code = new string((code ?? "").Where(c => !char.IsWhiteSpace(c)).ToArray());
        if (!code.StartsWith(Prefix, StringComparison.OrdinalIgnoreCase))
            throw new FormatException($"A build code starts with \"{Prefix}\".");
        string b64 = code[Prefix.Length..].Replace('-', '+').Replace('_', '/');
        b64 = b64.PadRight(b64.Length + (4 - b64.Length % 4) % 4, '=');

        byte[] json;
        try
        {
            using var input = new DeflateStream(new MemoryStream(Convert.FromBase64String(b64)), CompressionMode.Decompress);
            using var output = new MemoryStream();
            input.CopyTo(output);
            json = output.ToArray();
        }
        catch (Exception ex) when (ex is FormatException or InvalidDataException)
        {
            throw new FormatException("The build code is damaged or incomplete. Copy it again in full.");
        }

        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (!root.TryGetProperty("v", out var v) || v.GetInt32() != 1)
                throw new FormatException("This build code is from a newer planner. Update Prismforge.");
            string note = root.TryGetProperty("n", out var n) ? n.GetString() ?? "" : "";
            float? xp = root.TryGetProperty("xp", out var x) ? x.GetSingle() : null;
            if (xp is < 0 or float.NaN) throw new FormatException("The XP in this build code is invalid.");
            var segs = ReadList(root, "s", MaxSegments, MaxSegmentLevel, "segments");
            var feeds = ReadList(root, "f", MaxFeeds, MaxFeedLevel, "fed fragments");
            return new BuildSpec(note.Length > MaxNote ? note[..MaxNote] : note, xp, segs, feeds);
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or KeyNotFoundException)
        {
            throw new FormatException("The build code is damaged or incomplete. Copy it again in full.");
        }
    }

    private static void WriteList(Utf8JsonWriter w, string name, IReadOnlyList<(string Row, int Level)> list)
    {
        w.WriteStartArray(name);
        foreach (var (row, level) in list)
        {
            w.WriteStartArray();
            w.WriteStringValue(row);
            w.WriteNumberValue(level);
            w.WriteEndArray();
        }
        w.WriteEndArray();
    }

    private static List<(string, int)> ReadList(JsonElement root, string name, int maxCount, int maxLevel, string what)
    {
        var list = new List<(string, int)>();
        if (!root.TryGetProperty(name, out var arr)) return list;
        foreach (var e in arr.EnumerateArray())
        {
            string row = e[0].GetString() ?? "";
            int level = e[1].GetInt32();
            if (row.Length == 0 || level < 0 || level > maxLevel)
                throw new FormatException($"The build code has an invalid entry in its {what}.");
            list.Add((row, level));
        }
        if (list.Count > maxCount) throw new FormatException($"The build code has more than {maxCount} {what}.");
        return list;
    }

    /// Readable summary for the confirmation dialog.
    public static string Describe(BuildSpec b, SegmentCatalog catalog)
    {
        var sb = new StringBuilder();
        if (b.Note.Length > 0) sb.AppendLine($"Note: {b.Note}").AppendLine();
        sb.AppendLine($"{b.Segments.Count} segment(s), prism level {b.Segments.Sum(s => (long)s.Level):N0}:");
        foreach (var (row, level) in b.Segments.Take(30))
            sb.AppendLine($"  • {catalog.ByRow(row)?.Name ?? row}  Lv {level:N0}");
        if (b.Segments.Count > 30) sb.AppendLine($"  … and {b.Segments.Count - 30} more");
        sb.AppendLine($"{b.Feeds.Count} fed fragment(s)" + (b.Feeds.Count > 0
            ? ": " + string.Join(", ", b.Feeds.Select(f => $"{catalog.ByRow(f.Row)?.Name ?? f.Row} {f.Level}")) : ""));
        if (b.Xp is float xp) sb.AppendLine($"Pending XP: {xp:N0}");
        return sb.ToString();
    }
}
