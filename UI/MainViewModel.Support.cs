using System.Diagnostics;
using System.Text;
using CommunityToolkit.Mvvm.Input;
using R2PrismRuntime.Diagnostics;
using R2PrismRuntime.Game;

namespace R2PrismRuntime.UI;

/// <summary>Support zip: one file a tester can send back, with a summary of the current state.</summary>
public partial class MainViewModel
{
    [RelayCommand]
    private void CreateSupportZip()
    {
        try
        {
            string path = SupportBundle.Create(BuildSupportSummary());
            SetStatus("Support zip created in the logs folder. Send that file along with what you did.", Tone.Good);
            Process.Start("explorer.exe", $"/select,\"{path}\"");
        }
        catch (Exception ex)
        {
            Log.Error("Creating the support zip failed.", ex);
            SetStatus("Creating the support zip failed: " + ex.Message, Tone.Bad);
        }
    }

    private string BuildSupportSummary()
    {
        var sb = new StringBuilder();
        void L(string s = "") => sb.AppendLine(s);

        L($"{AppInfo.Name} {AppInfo.Version} support summary, {DateTime.Now}");
        L($"Environment: {AppInfo.EnvironmentLine()}");
        L($"Arguments: {string.Join(" ", Environment.GetCommandLineArgs().Skip(1))}");
        L($"Log level: {Log.MinLevel}; allow untested writes: {GameBuild.AllowUntestedWrites}");
        L();
        L($"Game target: {(_target == null ? "none found" : $"{_target.ModuleName} ({_target.Store}, {(_target.Tested ? "tested" : "untested")})")}");
        L($"Link: {Link} '{LinkText}' {LinkDetail}");
        L($"Status: {Status}");
        L($"Read-only: {IsReadOnly}");
        L($"Attach failures in a row: {_attachFailures}; scan failures in a row: {_scanFailures}");
        if (_mem != null)
        {
            L($"Image path: {_mem.ModulePath} (store from path: {GameBuild.StoreFromPath(_mem.ModulePath)})");
            L($"Build: {_mem.Fingerprint}");
            L($"Failed memory reads since attach: {_mem.FailedReads}");
        }
        L($"FNamePool: {(_names?.IsReady == true ? $"OK via '{_names.PoolMethod}'" : "not found")}");
        L($"Segment names resolved: {ResolvedStats}/{TotalStats}");
        L();
        L($"Prisms: {Prisms.Count}, in sync: {IsInSync}, pending changes: {DirtyCount}");
        foreach (var p in Prisms)
            L($"  {p.Numeral} '{p.Name}' Lv {p.Level}, XP {p.Xp}, segments {p.Segments.Count}, fed {p.Feeds.Count}");
        L($"Presets: {Presets.Count}");
        L($"Character stats read: {Stats.Count}, held: {HeldStats.Count}");
        L($"Stat hook: {HookState} ({HookDetail}), hits {HookHits}");
        return sb.ToString();
    }
}
