using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Windows;
using System.Windows.Data;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using R2PrismRuntime.Diagnostics;
using R2PrismRuntime.Game;
using R2PrismRuntime.Memory;
using R2PrismRuntime.Models;

namespace R2PrismRuntime.UI;

public enum LinkState { Searching, Loading, Linked, Error }
public enum Tone { Neutral, Good, Warn, Bad }

/// <summary>
/// Drives the whole window. A timer ticks every couple of seconds and moves through:
/// find the game → attach + name table → walk the chain + scan → keep live values fresh.
/// Staged edits survive rescans; writes only happen on "Apply".
/// </summary>
public partial class MainViewModel : ObservableObject
{
    private const string ProcessName = "Remnant2-Win64-Shipping";
    private const string GamePassText = "The Game Pass version of Remnant 2 isn't supported yet. This editor is tested on the Steam version.";

    private readonly SegmentCatalog _catalog = SegmentCatalog.Load();
    private ProcessMemory? _mem;
    private FNameReader? _names;
    private PrismScanner? _scanner;
    private PrismWriter? _writer;

    private readonly DispatcherTimer _timer;
    private bool _tickRunning;
    private bool _forceScan = true;
    private bool _objectsIndexed;

    /// Values first seen this session, per prism data address. Used by "Revert".
    private readonly Dictionary<ulong, Snapshot> _session = new();

    private sealed record Snapshot(float Xp, (SegmentDef Def, int Level)[] Segments, (SegmentDef Def, int Level)[] Feeds);

    /// Set by the --diagnose command-line switch: write a diagnostic report after the first scan.
    public static bool DiagnoseOnFirstScan { get; set; }

    /// Set by --selftest-ui: off-screen instance that checks the stat pickers, then exits.
    public static bool SelfTest { get; set; }

    public Task ForceRescanAsync() => RescanAsync();

    public ObservableCollection<PrismData> Prisms { get; } = new();

    /// Choices for a normal segment (standard + fusion), a legendary segment, and a fed fragment.
    public ICollectionView SegmentChoices { get; }
    public ICollectionView LegendaryChoices { get; }
    public ICollectionView FragmentChoices { get; }
    private readonly ObservableCollection<SegmentDef> _segmentChoices = new();
    private readonly ObservableCollection<SegmentDef> _legendaryChoices = new();
    private readonly ObservableCollection<SegmentDef> _fragmentChoices = new();

    public ObservableCollection<string> LogLines { get; } = new();
    private const int MaxLogLines = 800;

    // ── State ─────────────────────────────────────────────────────

    [ObservableProperty] private LinkState _link = LinkState.Searching;
    [ObservableProperty] private string _linkText = "Looking for Remnant 2";
    [ObservableProperty] private string _linkDetail = "";

    [ObservableProperty] private string _status = "Start Remnant 2 and load a character.";
    [ObservableProperty] private Tone _statusTone = Tone.Neutral;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ApplyCommand), nameof(RescanCommand), nameof(DiagnosticCommand))]
    private bool _isLinked;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ApplyCommand))]
    private bool _isInSync;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSelection))]
    [NotifyCanExecuteChangedFor(nameof(RevertCommand), nameof(SetAllSegmentsCommand), nameof(SetAllFeedsCommand))]
    private PrismData? _selectedPrism;

    public bool HasSelection => SelectedPrism != null;

    [ObservableProperty] private int _segmentTarget = 10;
    [ObservableProperty] private int _feedTarget = 20;

    [ObservableProperty] private bool _isJournalOpen;

    public int DirtyCount => Prisms.Count(p => p.IsDirty);
    public bool HasChanges => DirtyCount > 0;
    public string PendingText => DirtyCount == 1 ? "1 prism with pending changes" : $"{DirtyCount} prisms with pending changes";

    public int ResolvedStats => _catalog.All.Count(d => d.IsResolved);
    public int TotalStats => _catalog.All.Count;

    public MainViewModel()
    {
        SegmentChoices = MakeView(_segmentChoices);
        LegendaryChoices = MakeView(_legendaryChoices);
        FragmentChoices = MakeView(_fragmentChoices);
        InitStatsView();
        InitShell();

        Log.LineWritten += OnLogLine;
        Prisms.CollectionChanged += (_, _) => NotifyDirty();

        _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
        _timer.Tick += async (_, _) => await TickAsync();
    }

    public void Start()
    {
        _timer.Start();
        _ = TickAsync();
    }

    public void Stop()
    {
        _timer.Stop();
        ReleaseHookOnExit();
        _mem?.Dispose();
    }

    // A view taken from a throw-away CollectionViewSource stops tracking its source once that
    // CollectionViewSource is garbage-collected, so items added later never showed up.
    // A ListCollectionView owned by the view model keeps its subscription.
    private static ICollectionView MakeView(ObservableCollection<SegmentDef> source)
    {
        var view = new ListCollectionView(source);
        view.GroupDescriptions.Add(new PropertyGroupDescription(nameof(SegmentDef.Group)));
        return view;
    }

    private void OnLogLine(LogLevel level, string line)
    {
        if (level < LogLevel.Info) return;
        Application.Current?.Dispatcher.BeginInvoke(() =>
        {
            LogLines.Add(line);
            while (LogLines.Count > MaxLogLines) LogLines.RemoveAt(0);
        });
    }

    private void SetStatus(string text, Tone tone = Tone.Neutral)
    {
        Status = text;
        StatusTone = tone;
        if (tone == Tone.Bad) Log.Warn(text); else Log.Info(text);
    }

    private void NotifyDirty()
    {
        OnPropertyChanged(nameof(DirtyCount));
        OnPropertyChanged(nameof(HasChanges));
        OnPropertyChanged(nameof(PendingText));
        ApplyCommand.NotifyCanExecuteChanged();
        DiscardCommand.NotifyCanExecuteChanged();
    }

    // ══ Tick: connect, scan, refresh ══════════════════════════════

    private async Task TickAsync()
    {
        if (_tickRunning) return;
        _tickRunning = true;
        try
        {
            if (_mem == null || !_mem.IsAttached)
            {
                await TryAttachAsync();
                if (_mem == null) return;
            }

            if (!_mem.IsProcessAlive)
            {
                Disconnect("Remnant 2 was closed. Waiting for it to start again.");
                return;
            }

            bool needScan = _forceScan || Prisms.Count == 0 || !IsInSync
                         || Prisms.Any(p => _scanner!.CheckStale(p) != null);
            if (needScan) await ScanAsync();
            else RefreshLive();
            RefreshStats();
        }
        catch (Exception ex)
        {
            Log.Error("Tick failed.", ex);
        }
        finally { _tickRunning = false; }
    }

    private async Task TryAttachAsync()
    {
        if (Process.GetProcessesByName(ProcessName).Length == 0)
        {
            Link = LinkState.Searching;
            LinkText = "Looking for Remnant 2";
            LinkDetail = "";
            // Game Pass / Microsoft Store build: different executable, untested offsets.
            if (Process.GetProcessesByName("Remnant2-WinGDK-Shipping").Length > 0)
            {
                Link = LinkState.Error;
                LinkText = "Unsupported version";
                if (Status != GamePassText) SetStatus(GamePassText, Tone.Warn);
            }
            return;
        }

        Link = LinkState.Loading;
        LinkText = "Attaching";
        string? error = null;
        var mem = new ProcessMemory();
        var names = new FNameReader(mem);
        int resolved = 0;

        await Task.Run(() =>
        {
            try
            {
                mem.Attach(ProcessName);
                if (!names.FindPool()) error = "The game is still starting up.";
                else resolved = _catalog.ResolveIds(names);
            }
            catch (Exception ex) { error = ex.Message; }
        });

        if (error != null)
        {
            mem.Dispose();
            Link = LinkState.Loading;
            LinkText = "Waiting for the game";
            SetStatus(error, Tone.Warn);
            return;
        }

        _mem = mem;
        _names = names;
        _scanner = new PrismScanner(mem, names, _catalog);
        _writer = new PrismWriter(mem, _scanner);
        _forceScan = true;
        IsLinked = true;
        Link = LinkState.Linked;
        LinkText = "Linked";
        LinkDetail = $"PID {mem.ProcessId}";
        Log.Info($"Resolved {resolved}/{_catalog.All.Count} segment names.");
        OnPropertyChanged(nameof(ResolvedStats));
        RebuildChoices();
        SetStatus("Connected to Remnant 2. Reading your prisms…", Tone.Good);
        await InitStatsAsync(mem, names);
    }

    private void Disconnect(string reason)
    {
        _mem?.Dispose();
        _mem = null;
        _names = null;
        _scanner = null;
        _writer = null;
        IsLinked = false;
        IsInSync = false;
        Prisms.Clear();
        SelectedPrism = null;
        _session.Clear();
        _objectsIndexed = false;
        ResetStats();
        Link = LinkState.Searching;
        LinkText = "Looking for Remnant 2";
        LinkDetail = "";
        SetStatus(reason, Tone.Warn);
    }

    private void RebuildChoices()
    {
        _segmentChoices.Clear();
        _legendaryChoices.Clear();
        _fragmentChoices.Clear();
        foreach (var d in _catalog.All.Where(d => d.IsResolved))
        {
            if (d.Kind == SegmentKind.Legendary) _legendaryChoices.Add(d);
            else _segmentChoices.Add(d);
            if (d.Kind == SegmentKind.Standard) _fragmentChoices.Add(d);
        }
    }

    private async Task ScanAsync()
    {
        if (_scanner == null) return;
        List<PrismData>? found = null;
        string? failure = null;

        await Task.Run(() =>
        {
            try { found = _scanner.ScanPrisms(); }
            catch (ChainException ex) { failure = ex.Message; }
            catch (Exception ex) { failure = "Reading the inventory failed: " + ex.Message; Log.Error("Scan failed.", ex); }
        });

        _forceScan = false;
        if (found == null)
        {
            if (IsInSync || Prisms.Count == 0) SetStatus(failure!, Tone.Warn);
            IsInSync = false;
            Link = LinkState.Loading;
            LinkText = "Waiting for character";
            return;
        }

        MergeScan(found);
        IsInSync = true;
        if (!_objectsIndexed)
        {
            _objectsIndexed = true;
            var scanner = _scanner;
            var snapshot = Prisms.ToList();
            _ = Task.Run(() =>
            {
                try { scanner.IndexDefaultObjects(snapshot); }
                catch (Exception ex) { Log.Error("Indexing class objects failed.", ex); }
            });
        }
        if (DiagnoseOnFirstScan)
        {
            DiagnoseOnFirstScan = false;
            string path = await Task.Run(() => _scanner.RunDiagnostic());
            Log.Info($"--diagnose: report {System.IO.Path.GetFileName(path)} written to the Logs folder.");
        }
        Link = LinkState.Linked;
        LinkText = "Linked";
        SetStatus(found.Count switch
        {
            0 => "No prisms on this character.",
            1 => "1 prism found.",
            _ => $"{found.Count} prisms found.",
        }, found.Count > 0 ? Tone.Good : Tone.Neutral);
    }

    /// Replaces the prism list, carrying over staged edits and session snapshots.
    private void MergeScan(List<PrismData> found)
    {
        var old = Prisms.ToDictionary(p => p.DataAddress);
        ulong? selected = SelectedPrism?.DataAddress;

        foreach (var p in found)
        {
            ApplySnapshot(p);
            if (old.TryGetValue(p.DataAddress, out var prev)) CarryEdits(prev, p);
            p.PropertyChanged += (_, e) => { if (e.PropertyName == nameof(PrismData.IsDirty)) NotifyDirty(); };
        }

        Prisms.Clear();
        foreach (var p in found) Prisms.Add(p);
        SelectedPrism = Prisms.FirstOrDefault(p => p.DataAddress == selected) ?? Prisms.FirstOrDefault();
        NotifyDirty();
    }

    private void ApplySnapshot(PrismData p)
    {
        if (_session.TryGetValue(p.DataAddress, out var snap)
            && snap.Segments.Length == p.Segments.Count && snap.Feeds.Length == p.Feeds.Count)
        {
            p.OriginalXp = snap.Xp;
            for (int i = 0; i < snap.Segments.Length; i++)
                (p.Segments[i].OriginalDef, p.Segments[i].OriginalLevel) = snap.Segments[i];
            for (int i = 0; i < snap.Feeds.Length; i++)
                (p.Feeds[i].OriginalDef, p.Feeds[i].OriginalLevel) = snap.Feeds[i];
            return;
        }
        _session[p.DataAddress] = new Snapshot(
            p.Xp,
            p.Segments.Select(s => (s.Def, s.Level)).ToArray(),
            p.Feeds.Select(f => (f.Def, f.Level)).ToArray());
    }

    private static void CarryEdits(PrismData from, PrismData to)
    {
        if (from.IsXpDirty) to.EditXp = from.EditXp;
        if (from.Segments.Count == to.Segments.Count)
            for (int i = 0; i < to.Segments.Count; i++) CarrySlot(from.Segments[i], to.Segments[i]);
        if (from.Feeds.Count == to.Feeds.Count)
            for (int i = 0; i < to.Feeds.Count; i++) CarrySlot(from.Feeds[i], to.Feeds[i]);
    }

    private static void CarrySlot(SlotBase from, SlotBase to)
    {
        if (from.IsRowDirty) to.EditDef = from.EditDef;
        if (from.IsLevelDirty) to.EditLevel = from.EditLevel;
    }

    /// Re-reads live values; slots without pending edits follow the game.
    private void RefreshLive()
    {
        if (_scanner == null) return;
        foreach (var p in Prisms)
        {
            bool xpDirty = p.IsXpDirty;
            var clean = p.Segments.Cast<SlotBase>().Concat(p.Feeds).Select(s => (s, row: !s.IsRowDirty, lvl: !s.IsLevelDirty)).ToList();
            _scanner.Reread(p);
            if (!xpDirty) p.EditXp = p.Xp;
            foreach (var (s, row, lvl) in clean)
            {
                if (row) s.EditDef = s.Def;
                if (lvl) s.EditLevel = s.Level;
            }
        }
    }

    // ══ Commands ══════════════════════════════════════════════════

    [RelayCommand(CanExecute = nameof(IsLinked))]
    private async Task RescanAsync()
    {
        _forceScan = true;
        _objectsIndexed = false;   // classes may have loaded since the last index
        SetStatus("Rescanning…");
        await TickAsync();
    }

    private bool CanApply() => IsLinked && IsInSync && HasChanges;

    [RelayCommand(CanExecute = nameof(CanApply))]
    private void Apply()
    {
        if (_writer == null) return;
        var dirty = Prisms.Where(p => p.IsDirty).ToList();
        int writes = 0;
        var problems = new List<string>();
        var notes = new List<string>();
        foreach (var p in dirty)
        {
            var r = _writer.Commit(p);
            writes += r.Writes;
            problems.AddRange(r.Problems.Select(x => dirty.Count > 1 ? $"{p.Name} {p.Numeral}: {x}" : x));
            notes.AddRange(r.Notes);
        }

        if (problems.Count == 0 && notes.Count > 0)
            SetStatus($"Applied {writes} value{(writes == 1 ? "" : "s")}. " + notes[0], Tone.Warn);
        else if (problems.Count == 0)
            SetStatus($"Applied. {writes} value{(writes == 1 ? "" : "s")} written and confirmed in game memory.", Tone.Good);
        else
        {
            SetStatus(problems[0] + (problems.Count > 1 ? $" (+{problems.Count - 1} more, see journal)" : ""), Tone.Bad);
            if (problems.Any(x => x.Contains("Rescan"))) _forceScan = true;
        }
        NotifyDirty();
    }

    private bool CanDiscard() => HasChanges;

    [RelayCommand(CanExecute = nameof(CanDiscard))]
    private void Discard()
    {
        foreach (var p in Prisms) p.Discard();
        SetStatus("Pending changes discarded.");
    }

    [RelayCommand(CanExecute = nameof(HasSelection))]
    private void Revert()
    {
        if (SelectedPrism == null) return;
        SelectedPrism.StageOriginal();
        SetStatus(SelectedPrism.IsDirty
            ? "Values from session start staged. Apply to write them."
            : "This prism already matches the session start.");
    }

    [RelayCommand(CanExecute = nameof(HasSelection))]
    private void SetAllSegments()
    {
        if (SelectedPrism == null) return;
        foreach (var s in SelectedPrism.Segments.Where(s => s.EditDef.Kind != SegmentKind.Legendary))
            s.EditLevel = SegmentTarget;
        SetStatus($"Segments staged at level {SegmentTarget}. Legendary left unchanged.");
    }

    [RelayCommand(CanExecute = nameof(HasSelection))]
    private void SetAllFeeds()
    {
        if (SelectedPrism == null) return;
        foreach (var f in SelectedPrism.Feeds) f.EditLevel = Math.Max(f.EditLevel, FeedTarget);
        SetStatus($"Fed fragments staged at a minimum of {FeedTarget}.");
    }

    [RelayCommand]
    private void StepUp(SlotBase? slot)
    {
        if (slot != null) slot.EditLevel++;
    }

    [RelayCommand]
    private void StepDown(SlotBase? slot)
    {
        if (slot != null) slot.EditLevel--;
    }

    [RelayCommand]
    private void ResetSlot(SlotBase? slot) => slot?.Discard();

    [RelayCommand(CanExecute = nameof(IsLinked))]
    private async Task DiagnosticAsync()
    {
        if (_scanner == null) return;
        SetStatus("Running diagnostic…");
        try
        {
            string path = await Task.Run(() => _scanner.RunDiagnostic());
            SetStatus("Diagnostic saved to the logs folder.", Tone.Good);
            Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            Log.Error("Diagnostic failed.", ex);
            SetStatus("Diagnostic failed: " + ex.Message, Tone.Bad);
        }
    }

    [RelayCommand]
    private void OpenLogFolder()
    {
        try
        {
            System.IO.Directory.CreateDirectory(Log.LogDirectory);
            Process.Start(new ProcessStartInfo(Log.LogDirectory) { UseShellExecute = true });
        }
        catch (Exception ex) { Log.Error("Could not open log folder.", ex); }
    }

    [RelayCommand]
    private void ToggleJournal() => IsJournalOpen = !IsJournalOpen;
}
