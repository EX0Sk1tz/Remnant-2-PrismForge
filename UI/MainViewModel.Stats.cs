using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows.Data;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using R2PrismRuntime.Diagnostics;
using R2PrismRuntime.Game;
using R2PrismRuntime.Memory;
using R2PrismRuntime.Models;

namespace R2PrismRuntime.UI;

public enum Page { Prisms, Stats }

/// <summary>Character stats page: live stat list plus values held by the stat hook.</summary>
public partial class MainViewModel
{
    private CharacterStats? _stats;
    private StatHook? _hook;
    private List<(int Id, float Value)> _adopted = new();
    private readonly Dictionary<int, StatEntry> _statsById = new();

    public ObservableCollection<StatEntry> Stats { get; } = new();
    public ObservableCollection<StatEntry> HeldStats { get; } = new();
    public ICollectionView StatsView { get; private set; } = null!;

    public string[] StatFilters { get; } = { "All", "Held", "Offense", "Defense", "Mobility", "Skills", "Caps", "Other" };

    [ObservableProperty] private Page _page = Page.Prisms;
    [ObservableProperty] private string _statSearch = "";
    [ObservableProperty] private string _statFilter = "All";
    [ObservableProperty] private HookState _hookState = HookState.Unavailable;
    [ObservableProperty] private string _hookDetail = "Not connected.";
    [ObservableProperty] private uint _hookHits;

    public bool IsHookOn => HookState == HookState.On;

    partial void OnHookStateChanged(HookState value) => OnPropertyChanged(nameof(IsHookOn));
    partial void OnStatSearchChanged(string value) => StatsView.Refresh();
    partial void OnStatFilterChanged(string value) => StatsView.Refresh();

    private void InitStatsView()
    {
        StatsView = new ListCollectionView(Stats);   // see MakeView for why not CollectionViewSource
        StatsView.Filter = o =>
        {
            if (o is not StatEntry s) return false;
            if (StatFilter == "Held" && !s.IsHeld) return false;
            if (StatFilter is not ("All" or "Held") && s.Category != StatFilter) return false;
            return StatSearch.Length == 0
                || s.Name.Contains(StatSearch.Replace(" ", ""), StringComparison.OrdinalIgnoreCase)
                || s.DisplayName.Contains(StatSearch, StringComparison.OrdinalIgnoreCase);
        };
    }

    private async Task InitStatsAsync(ProcessMemory mem, FNameReader names)
    {
        _stats = new CharacterStats(mem, names);
        _hook = new StatHook(mem);
        _adopted = await Task.Run(() => _hook.Probe());
        HookState = _hook.State;
        HookDetail = _hook.Detail;
    }

    private void ResetStats()
    {
        _stats = null;
        _hook = null;
        _adopted.Clear();
        _statsById.Clear();
        Stats.Clear();
        HeldStats.Clear();
        HookState = HookState.Unavailable;
        HookDetail = "Not connected.";
    }

    /// Reads the stat list; held stats that drifted from their target are written back directly.
    private void RefreshStats()
    {
        if (_stats == null || _scanner == null) return;
        HookHits = _hook?.Hits ?? 0;
        var rows = _stats.Read(_scanner.LastPawn);
        if (rows.Count == 0) return;

        foreach (var r in rows)
        {
            if (!_statsById.TryGetValue(r.NameId, out var s))
            {
                s = new StatEntry
                {
                    NameId = r.NameId, Name = r.Name, Category = CharacterStats.Category(r.Name),
                    Info = StatInfo.Describe(r.Name),
                };
                s.SyncTarget(r.Value);
                s.PropertyChanged += OnStatChanged;
                _statsById[r.NameId] = s;
                Stats.Add(s);
            }
            s.Address = r.Address;
            s.Live = r.Value;
            if (!s.IsHeld && !s.TargetEdited) s.SyncTarget(r.Value);
            if (s.IsHeld && Math.Abs(s.Live - s.Target) > 0.0001f) _stats.WriteValue(s.Address, s.Target, quiet: true);
        }

        if (_adopted.Count > 0)
        {
            foreach (var (id, value) in _adopted)
                if (_statsById.TryGetValue(id, out var s))
                {
                    s.SyncTarget(value);
                    s.IsHeld = true;
                    if (!HeldStats.Contains(s)) HeldStats.Add(s);
                }
            Log.Info($"Adopted {_adopted.Count} held stat(s) from a previous session.");
            _adopted.Clear();
            StatsView.Refresh();
        }
        UpdateWarnings();
    }

    /// Recomputes the soft warnings. Only stats the user is changing or holding get one.
    private void UpdateWarnings()
    {
        float? CapOf(string capName)
            => _statsById.Values.FirstOrDefault(c => c.Name == capName) is { } c ? (c.IsHeld ? c.Target : c.Live) : null;

        foreach (var s in Stats)
            s.Warning = s.IsHeld || (s.TargetEdited && Math.Abs(s.Target - s.Live) > 0.0001f)
                ? StatAdvice.Check(s.Name, s.Target, CapOf)
                : null;
    }

    private void OnStatChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(StatEntry.Target) or nameof(StatEntry.IsHeld)) UpdateWarnings();

        // Editing the target of a held stat updates the hook table right away.
        if (e.PropertyName == nameof(StatEntry.Target) && sender is StatEntry { IsHeld: true } s)
        {
            PushOverrides();
            _stats?.WriteValue(s.Address, s.Target, quiet: false);
        }
    }

    private void PushOverrides()
        => _hook?.SetOverrides(HeldStats.Select(s => (s.NameId, s.Target)).ToList());

    // ── Commands ─────────────────────────────────────────────────

    [RelayCommand]
    private void ShowPrisms() => Page = Page.Prisms;

    [RelayCommand]
    private void ShowStats() => Page = Page.Stats;

    [RelayCommand]
    private void SetStatFilter(string? filter) => StatFilter = filter ?? "All";

    [RelayCommand]
    private void ToggleHold(StatEntry? s)
    {
        if (s == null || _hook == null || _stats == null) return;

        if (s.IsHeld)
        {
            s.IsHeld = false;
            HeldStats.Remove(s);
            PushOverrides();
            if (HeldStats.Count == 0) _hook.Uninstall();
            SetStatus($"{s.DisplayName} released. The game recalculates it on the next stat update.");
        }
        else
        {
            if (!_hook.Install())
            {
                SetStatus("Stat hook unavailable: " + _hook.Detail, Tone.Bad);
                SyncHook();
                return;
            }
            if (HeldStats.Count >= StatHook.MaxEntries)
            {
                SetStatus($"At most {StatHook.MaxEntries} stats can be held.", Tone.Warn);
                return;
            }
            s.IsHeld = true;
            HeldStats.Add(s);
            PushOverrides();
            _stats.WriteValue(s.Address, s.Target, quiet: false);
            SetStatus($"{s.DisplayName} held at {s.Target:0.###}.", Tone.Good);
        }
        SyncHook();
        if (StatFilter == "Held") StatsView.Refresh();
    }

    [RelayCommand]
    private void ReleaseAll()
    {
        if (_hook == null) return;
        foreach (var s in HeldStats.ToList()) s.IsHeld = false;
        HeldStats.Clear();
        PushOverrides();
        _hook.Uninstall();
        SyncHook();
        StatsView.Refresh();
        SetStatus("All stats released; original game code restored.");
    }

    [RelayCommand]
    private void ResetTarget(StatEntry? s)
    {
        if (s == null || s.IsHeld) return;
        s.SyncTarget(s.Live);
    }

    private void SyncHook()
    {
        if (_hook == null) return;
        HookState = _hook.State;
        HookDetail = _hook.Detail;
    }

    /// With nothing held the game code is restored. Held values stay in force after the app
    /// closes and are picked up again on the next start.
    private void ReleaseHookOnExit()
    {
        if (SelfTest) return;   // a test instance must never touch another instance's hook
        try
        {
            if (_hook?.State != HookState.On || _mem?.IsProcessAlive != true) return;
            if (HeldStats.Count == 0) _hook.Uninstall();
            else Log.Info($"Leaving the stat hook in place with {HeldStats.Count} held stat(s).");
        }
        catch (Exception ex) { Log.Error("Releasing the stat hook failed.", ex); }
    }
}
