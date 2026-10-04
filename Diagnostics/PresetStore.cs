using System.IO;
using System.Text.Json;

namespace R2PrismRuntime.Diagnostics;

/// <summary>
/// Named prism presets in %LocalAppData%\Prismforge\presets.json. Each one is a build code
/// (see Game/BuildCode.cs) without XP, so presets work on any character and can be shared as codes.
/// Unlike settings, presets are user work: an unreadable file is moved aside, never overwritten.
/// </summary>
public static class PresetStore
{
    public sealed record Entry(string Name, string Code, string SavedFrom, DateTime Created);

    private sealed class FileData
    {
        public int Version { get; set; } = 1;
        public List<Entry> Presets { get; set; } = new();
    }

    public const string FileName = "presets.json";
    public static string FilePath => Path.Combine(AppInfo.DataDirectory, FileName);

    public static List<Entry> Load()
    {
        try
        {
            if (!File.Exists(FilePath)) return new();
            var data = JsonSerializer.Deserialize<FileData>(File.ReadAllText(FilePath));
            var list = (data?.Presets ?? new()).Where(e => e is { Name.Length: > 0, Code.Length: > 0 }).ToList();
            Log.Info($"Presets loaded: {list.Count}.");
            return list;
        }
        catch (Exception ex)
        {
            string aside = Path.Combine(AppInfo.DataDirectory, $"presets.bad_{DateTime.Now:yyyyMMdd_HHmmss}.json");
            try { File.Move(FilePath, aside); } catch { aside = "(could not move it)"; }
            Log.Warn($"Presets file unreadable ({ex.Message}); moved to {Path.GetFileName(aside)}, starting with no presets.");
            return new();
        }
    }

    /// Writes to a temporary file first, so a crash mid-write can't destroy the existing presets.
    public static bool Save(IEnumerable<Entry> presets)
    {
        try
        {
            Directory.CreateDirectory(AppInfo.DataDirectory);
            string tmp = FilePath + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(new FileData { Presets = presets.ToList() },
                                                            new JsonSerializerOptions { WriteIndented = true }));
            File.Move(tmp, FilePath, overwrite: true);
            return true;
        }
        catch (Exception ex)
        {
            Log.Warn($"Saving presets failed: {ex.Message}");
            return false;
        }
    }
}
