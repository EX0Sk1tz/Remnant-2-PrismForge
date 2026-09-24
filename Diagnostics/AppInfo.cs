using System.IO;
using System.Reflection;
using System.Text.Json;

namespace R2PrismRuntime.Diagnostics;

/// <summary>Version, links, credits and per-user storage locations.</summary>
public static class AppInfo
{
    public const string Name = "Prismforge";
    public const string Tagline = "Live prism & stat editor for Remnant II";

    /// Fill these in once the pages exist; empty links are hidden in the About panel.
    public const string NexusUrl = "";
    public const string SourceUrl = "https://github.com/EX0Sk1tz/Remnant-2-PrismForge";

    public static string Version { get; } =
        Assembly.GetExecutingAssembly().GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
            .Split('+')[0] ?? "?";

    /// %LocalAppData%\Prismforge — writable even when the exe sits somewhere read-only.
    public static string DataDirectory { get; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Prismforge");

    public static string LogDirectory => Path.Combine(DataDirectory, "logs");
    private static string SettingsPath => Path.Combine(DataDirectory, "settings.json");

    public sealed record Credit(string Who, string What);

    public static readonly Credit[] Credits =
    {
        new("Paul44", "Remnant 2 Cheat Engine table: pointer chain, prism layout, stat hook point"),
        new("kiamchyearktng", "R2PrismEditor save editor: prism segment data tables and names this tool uses"),
        new("Andrew Savinykh, t1nky, crackedmind", "lib.remnant2.saves (MIT), the save parser R2PrismEditor is built on"),
        new("Gunfire Games / Gearbox Publishing", "Remnant II. This tool is unofficial and not affiliated with them."),
        new("EX0Sk1tz", "Prismforge. MIT License."),
    };

    // ── Settings ──────────────────────────────────────────────────

    public sealed class Settings
    {
        public string AcceptedNoticeVersion { get; set; } = "";
    }

    public static Settings LoadSettings()
    {
        try
        {
            if (File.Exists(SettingsPath))
                return JsonSerializer.Deserialize<Settings>(File.ReadAllText(SettingsPath)) ?? new();
        }
        catch (Exception ex) { Log.Warn($"Settings unreadable, using defaults: {ex.Message}"); }
        return new();
    }

    public static void SaveSettings(Settings s)
    {
        try
        {
            Directory.CreateDirectory(DataDirectory);
            File.WriteAllText(SettingsPath, JsonSerializer.Serialize(s, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch (Exception ex) { Log.Warn($"Saving settings failed: {ex.Message}"); }
    }
}
