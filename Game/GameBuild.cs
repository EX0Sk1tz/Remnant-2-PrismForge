using System.Diagnostics;
using R2PrismRuntime.Diagnostics;

namespace R2PrismRuntime.Game;

/// <summary>A game executable this app knows about. <see cref="Tested"/> builds allow writes by default.</summary>
public sealed record GameTarget(string ProcessName, string Store, bool Tested)
{
    public string ModuleName => ProcessName + ".exe";
}

/// <summary>
/// Finds the running game. Steam and Epic ship Remnant2-Win64-Shipping.exe; Game Pass / Microsoft
/// Store ships Remnant2-WinGDK-Shipping.exe (Apply and Hold confirmed by a tester, 2026-09-27).
/// A target marked untested is attached read-only and logged verbosely so a tester's logs show
/// what differs.
/// </summary>
public static class GameBuild
{
    public static readonly GameTarget Steam = new("Remnant2-Win64-Shipping", "Steam/Epic", Tested: true);
    public static readonly GameTarget GamePass = new("Remnant2-WinGDK-Shipping", "Game Pass", Tested: true);
    private static readonly GameTarget[] Known = { Steam, GamePass };

    /// Set by --allow-untested-writes: lets Apply and Hold write to an untested build.
    public static bool AllowUntestedWrites { get; set; }

    private static string _lastUnknown = "";

    /// Returns the first known game process that is running, or null.
    public static GameTarget? FindRunning()
    {
        foreach (var t in Known)
        {
            var procs = Process.GetProcessesByName(t.ProcessName);
            foreach (var p in procs) p.Dispose();
            if (procs.Length > 0) return t;
        }
        LogUnknownCandidates();
        return null;
    }

    /// Logs (once per change) processes that look like Remnant 2 but have a name this app doesn't
    /// know, so a store build with yet another exe name shows up in a tester's log.
    private static void LogUnknownCandidates()
    {
        var names = Process.GetProcesses()
            .Select(p => { using (p) return p.ProcessName; })
            .Where(n => n.Contains("Remnant", StringComparison.OrdinalIgnoreCase)
                     && !Known.Any(k => k.ProcessName.Equals(n, StringComparison.OrdinalIgnoreCase)))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(n => n)
            .ToList();
        string key = string.Join(", ", names);
        if (key == _lastUnknown) return;
        _lastUnknown = key;
        if (names.Count > 0)
            Log.Warn($"Found Remnant-like process(es) with an unknown exe name: {key}. " +
                     $"Known names: {string.Join(", ", Known.Select(k => k.ModuleName))}.");
    }

    /// Guesses the store from the install path (Steam and Epic share the exe name).
    public static string StoreFromPath(string path)
    {
        if (path.Contains("steamapps", StringComparison.OrdinalIgnoreCase)) return "Steam";
        if (path.Contains("Epic Games", StringComparison.OrdinalIgnoreCase)) return "Epic";
        if (path.Contains("WindowsApps", StringComparison.OrdinalIgnoreCase)
            || path.Contains("XboxGames", StringComparison.OrdinalIgnoreCase)
            || path.Contains("ModifiableWindowsApps", StringComparison.OrdinalIgnoreCase)) return "Game Pass / Microsoft Store";
        return "unknown";
    }
}
