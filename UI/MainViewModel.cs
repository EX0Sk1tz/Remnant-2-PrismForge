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
    /// Embedded rows, replaced by a fresh copy on every attach and then merged with the running
    /// game's prism tables (see LoadLiveTablesAsync), so mods that change them are picked up.
    private SegmentCatalog _catalog = SegmentCatalog.Load();
    private bool _tablesRead, _tablesLoading;
    private int _tableAttempts;
    private const int MaxTableAttempts = 10;
    private GameTarget? _target;
    private ProcessMemory? _mem;
    private FNameReader? _names;
    private PrismScanner? _scanner;
    private PrismWriter? _writer;
    private GameAllocator? _allocator;

    private readonly DispatcherTimer _timer;
    private bool _tickRunning;
    private bool _forceScan = true;
    private bool _objectsIndexed;

    /// Values first seen this session, per prism data address. Used by "Revert".
    private readonly Dictionary<ulong, Snapshot> _session = new();

    private sealed record Snapshot(float Xp, (SegmentDef Def, int Level)[] Segments, (SegmentDef Def, int Level)[] Feeds);

    /// "Make room" undo data per prism (key: prism data address), kept until the game reallocates.
    private readonly Dictionary<ulong, RoomBackup> _roomBackups = new();

    /// Set by the --diagnose command-line switch: write a diagnostic report after the first scan.
    public static bool DiagnoseOnFirstScan { get; set; }

    /// Set by --selftest-ui: off-screen instance that checks the stat pickers, then exits.
    public static bool SelfTest { get; set; }

    public Task ForceRescanAsync() => RescanAsync();

    public ObservableCollection<PrismData> Prisms { get; } = new();

    /// Choices for a segment slot (standard, fusion and legendary, in any slot) and a fed fragment.
    public ICollectionView SegmentChoices { get; }
    public ICollectionView FragmentChoices { get; }
    private readonly ObservableCollection<SegmentDef> _segmentChoices = new();
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
    public int TotalStats => _catalog.Active.Count();

    public MainViewModel()
    {
        SegmentChoices = MakeView(_segmentChoices);
        FragmentChoices = MakeView(_fragmentChoices);
        InitStatsView();
        InitShell();
        InitPresets();

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
        var target = GameBuild.FindRunning();
        if (target == null)
        {
            Link = LinkState.Searching;
            LinkText = "Looking for Remnant 2";
            LinkDetail = "";
            return;
        }
        if (target != _target) OnTargetFound(target);

        Link = LinkState.Loading;
        LinkText = "Attaching";
        string? error = null;
        var mem = new ProcessMemory();
        var names = new FNameReader(mem);
        // A previous session may have merged another game's (or another mod set's) tables.
        _catalog = SegmentCatalog.Load();
        _tablesRead = false;
        _tableAttempts = 0;
        int resolved = 0;
        string? report = null;
        int attempt = ++_attachFailures;

        await Task.Run(() =>
        {
            try
            {
                mem.Attach(target.ProcessName, target.ModuleName);
                if (!names.FindPool())
                {
                    error = "The game is still starting up.";
                    // Still failing well after start-up: record the build and every signature once,
                    // since without the name table no other diagnostic can run.
                    if (attempt == AttachReportAfter && !_attachReportWritten)
                    {
                        _attachReportWritten = true;
                        report = BuildProbe.WriteAttachReport(mem, target, $"FNamePool not found after {attempt} attempts");
                    }
                }
                else resolved = _catalog.ResolveIds(names);
            }
            catch (Exception ex)
            {
                error = ex.Message;
                Log.Debug($"Attach attempt {attempt} failed: {ex}");
            }
        });

        if (error != null)
        {
            mem.Dispose();
            Link = LinkState.Loading;
            LinkText = "Waiting for the game";
            if (report != null)
                SetStatus($"{error} A report was written to the logs folder ({System.IO.Path.GetFileName(report)}).", Tone.Warn);
            else if (Status != error) SetStatus(error, Tone.Warn);
            else Log.Debug($"Attach attempt {attempt}: {error}");
            return;
        }

        _attachFailures = 0;
        _mem = mem;
        _names = names;
        _scanner = new PrismScanner(mem, names, _catalog);
        _writer = new PrismWriter(mem, _scanner);
        var allocator = _allocator = new GameAllocator(mem);
        _writer.Allocator = allocator;
        _ = Task.Run(() =>
        {
            try { if (allocator.Find()) _forceScan = true; }   // rescan so Add segment enables
            catch (Exception ex) { Log.Error("Looking for the game's allocator failed.", ex); }
        });
        _forceScan = true;
        IsLinked = true;
        OnPropertyChanged(nameof(IsReadOnly));
        ApplyCommand.NotifyCanExecuteChanged();
        Link = LinkState.Linked;
        LinkText = "Linked";
        LinkDetail = target.Tested ? $"PID {mem.ProcessId}" : $"PID {mem.ProcessId} · {target.Store}{(IsReadOnly ? " · read-only" : "")}";
        Log.Info($"Resolved {resolved}/{_catalog.All.Count} segment names.");
        _nameRetries = 0;
        OnPropertyChanged(nameof(ResolvedStats));
        RebuildChoices();
        if (target.Tested) SetStatus("Connected to Remnant 2. Reading your prisms…", Tone.Good);
        else SetStatus($"Connected to the {target.Store} version (untested). " +
                       (IsReadOnly ? "Read-only: Apply and Hold are off. " : "Writes are enabled. ") +
                       "Diagnostics go to the logs folder.", Tone.Warn);
        await InitStatsAsync(mem, names);
    }

    /// Attach failures in a row before the build report is written (about 20 s of a running game).
    private const int AttachReportAfter = 10;
    /// Scan failures in a row on an untested build before a chain report is written (~30 s).
    private const int ChainReportAfter = 15;

    private int _attachFailures, _scanFailures;
    private bool _attachReportWritten, _chainReportWritten, _untestedNoticed;

    /// Apply and Hold are refused on an untested build unless --allow-untested-writes was given.
    public bool IsReadOnly => _target is { Tested: false } && !GameBuild.AllowUntestedWrites;

    private void OnTargetFound(GameTarget target)
    {
        _target = target;
        _attachFailures = 0;
        _attachReportWritten = _chainReportWritten = false;
        Log.Info($"Game process found: {target.ModuleName} ({target.Store}, {(target.Tested ? "tested" : "untested")}).");
        if (target.Tested || _untestedNoticed) return;

        _untestedNoticed = true;
        Log.RaiseDetail(LogLevel.Debug, $"{target.Store} build is untested");
        Log.Warn($"The {target.Store} build is untested. " + (GameBuild.AllowUntestedWrites
            ? "--allow-untested-writes is set: Apply and Hold WILL write to the game."
            : "Running read-only. Start with --allow-untested-writes to enable Apply and Hold."));
        DiagnoseOnFirstScan = true;
    }

    private void Disconnect(string reason)
    {
        if (_mem != null) Log.Info($"Disconnecting. {_mem.FailedReads} failed memory reads during this connection.");
        _mem?.Dispose();
        _mem = null;
        _names = null;
        _scanner = null;
        _writer = null;
        _allocator = null;
        _target = null;   // a restarted game gets its own attach/chain reports
        _scanFailures = 0;
        OnPropertyChanged(nameof(IsReadOnly));
        IsLinked = false;
        IsInSync = false;
        Prisms.Clear();
        SelectedPrism = null;
        _session.Clear();
        _roomBackups.Clear();
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
        _fragmentChoices.Clear();
        foreach (var d in _catalog.All.Where(d => d.IsResolved && !d.IsDisabled))
        {
            // Any slot can take any row, legendaries last in their own group: a legendary can become a
            // fusion or single stat and back.
            _segmentChoices.Add(d);
            if (d.Kind == SegmentKind.Standard) _fragmentChoices.Add(d);
        }
        // Same trigger: the catalog changed, so preset tooltips may get new (mod) names.
        RefreshPresetDetails();
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
            _scanFailures++;
            Log.Debug($"Scan failed ({_scanFailures} in a row, {_mem?.FailedReads} failed reads since attach): {failure}");
            if (Status != failure && (IsInSync || Prisms.Count == 0)) SetStatus(failure!, Tone.Warn);
            IsInSync = false;
            Link = LinkState.Loading;
            LinkText = "Waiting for character";
            // On an untested build a chain that never resolves is the interesting case: capture it.
            if (_target is { Tested: false } && _scanFailures == ChainReportAfter && !_chainReportWritten)
            {
                _chainReportWritten = true;
                var scanner = _scanner;
                var target = _target;
                string why = $"pointer chain failed {_scanFailures} times in a row: {failure}";
                _ = Task.Run(() =>
                {
                    try { scanner.RunDiagnostic(target, why); }
                    catch (Exception ex) { Log.Error("Automatic diagnostic failed.", ex); }
                });
            }
            return;
        }

        _scanFailures = 0;
        MergeScan(found);
        IsInSync = true;
        _ = LoadLiveTablesAsync();
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
            var target = _target;
            string path = await Task.Run(() => _scanner.RunDiagnostic(target, "first successful scan"));
            Log.Info($"First-scan diagnostic {System.IO.Path.GetFileName(path)} written to the Logs folder.");
        }
        Link = LinkState.Linked;
        LinkText = "Linked";
        SetStatus(found.Count switch
        {
            0 => "No prisms on this character.",
            1 => "1 prism found.",
            _ => $"{found.Count} prisms found.",
        }, found.Count > 0 ? Tone.Good : Tone.Neutral);
        HandleRoomBackups();
        RetryNameResolution();
    }

    /// After "Make room": once the game has reallocated a prism's segment array (new legendary
    /// picked), puts the extra segments that were taken off back into the new spare room.
    private void HandleRoomBackups()
    {
        if (_writer == null) return;
        foreach (var p in Prisms.ToList())
        {
            if (!_roomBackups.TryGetValue(p.DataAddress, out var b) || p.HasRoomBackup) continue;
            _roomBackups.Remove(p.DataAddress);

            if (p.SegmentsAddress == b.SegmentsAddress)
            {
                Log.Warn($"'{p.Name}': the segment count changed without a new array; Make room was abandoned. Removed entries are in the log.");
                continue;
            }
            if (b.Extras == 0)
            {
                SetStatus($"New array for '{p.Name}'. Add segment has room now.", Tone.Good);
                continue;
            }
            var (r, n) = _writer.PutBackSegments(p, b);
            _forceScan = true;
            if (r.Ok)
                SetStatus($"New array for '{p.Name}': {n} segment(s) put back. Add segment has room now."
                          + (r.Notes.Count > 0 ? " " + r.Notes[0] : ""), r.Notes.Count > 0 ? Tone.Warn : Tone.Good);
            else
                SetStatus(r.Problems[0] + " The removed segments are in the log.", Tone.Bad);
        }
    }

    /// Once per connection, after a successful scan: reads PrismStoneDataTable and
    /// PrismStoneMythicDataTable from the game and merges them into the catalog. Rows added by mods
    /// become pickable, and rows a mod points to a different segment class get that class.
    /// Retried on later scans (the tables may not be loaded yet); the embedded catalog stays in use
    /// when they can't be read.
    private async Task LoadLiveTablesAsync()
    {
        if (_tablesRead || _tablesLoading || _tableAttempts >= MaxTableAttempts || _scanner == null) return;
        _tablesLoading = true;
        _tableAttempts++;
        var scanner = _scanner;
        var catalog = _catalog;
        string? processName = _target?.ProcessName;
        List<LiveSegmentRow>? rows = null;
        string modLabel = "Mod";
        try
        {
            (rows, modLabel) = await Task.Run(() => (scanner.ReadPrismTables(), DetectModLabel(processName)));
        }
        catch (Exception ex) { Log.Error("Reading the prism tables failed.", ex); }
        finally { _tablesLoading = false; }

        if (scanner != _scanner || catalog != _catalog) return;   // disconnected meanwhile
        if (rows == null || rows.Count == 0)
        {
            Log.Warn($"Prism tables not readable (attempt {_tableAttempts}/{MaxTableAttempts}); using the built-in segment list.");
            return;
        }

        _tablesRead = true;
        // A mod adds and repoints rows but keeps the vanilla ones. If most embedded rows are missing,
        // the read went wrong: keep the embedded catalog rather than hide working segments.
        var liveRows = rows.Select(r => r.Row).ToHashSet(StringComparer.OrdinalIgnoreCase);
        int known = _catalog.All.Count(d => liveRows.Contains(d.Row));
        if (known < _catalog.All.Count * 9 / 10)
        {
            Log.Warn($"Prism tables read from the game contain only {known} of {_catalog.All.Count} known rows; " +
                     "ignored, using the built-in segment list.");
            return;
        }
        var result = _catalog.MergeLive(rows, modLabel);
        Log.Info($"Prism tables read from the game: {rows.Count} row(s). {result}. Mod label: '{modLabel}'.");
        foreach (var d in result.Added)
            Log.Info($"  added: {d.Row} '{d.Name}' ({d.Kind}, {d.Category}) class '{d.ClassObject}'");
        foreach (var c in result.ClassChanged) Log.Info($"  class changed: {c}");
        foreach (var k in result.KindChanged) Log.Warn($"  kind changed: {k}");
        foreach (var r in result.Retired) Log.Info($"  not in the game's tables: {r}");
        if (result.Disabled.Count > 0) Log.Info($"  disabled by the mod (not offered): {string.Join(", ", result.Disabled)}");
        foreach (var r in result.Duplicates) Log.Warn($"  row in both tables: {r}");

        // Snapshots from the first scan hold stand-ins for rows that were unknown until now.
        SegmentDef Remap(SegmentDef d) => d.Kind == SegmentKind.Unknown && _catalog.ByRow(d.Row) is { } live ? live : d;
        foreach (var key in _session.Keys.ToList())
        {
            var s = _session[key];
            _session[key] = s with
            {
                Segments = s.Segments.Select(x => (Remap(x.Def), x.Level)).ToArray(),
                Feeds = s.Feeds.Select(x => (Remap(x.Def), x.Level)).ToArray(),
            };
        }

        OnPropertyChanged(nameof(ResolvedStats));
        OnPropertyChanged(nameof(TotalStats));
        RebuildChoices();
        _objectsIndexed = false;   // changed classes need their default objects looked up again
        _forceScan = true;         // re-read segments so modded rows get their definitions
        if (result.Added.Count > 0 || result.ClassChanged.Count > 0)
            SetStatus($"Prism tables read from the game: {result.Added.Count} new segment(s), " +
                      $"{result.ClassChanged.Count} changed by mods.", Tone.Good);
    }

    /// Prism mods Prismforge can name, by a word in their pak file name.
    private static readonly (string PakPart, string Label)[] KnownPrismMods = { ("BeyondHell", "Beyond Hell") };

    /// Label for segments a mod changed: the known prism mod whose pak is in the game's Paks folder
    /// (Remnant2\Binaries\Win64\exe → Remnant2\Content\Paks, including LogicMods and ~mods), else "Mod".
    private static string DetectModLabel(string? processName)
    {
        try
        {
            using var proc = processName == null ? null : System.Diagnostics.Process.GetProcessesByName(processName).FirstOrDefault();
            string? exe = proc?.MainModule?.FileName;
            if (exe == null) return "Mod";
            string paks = System.IO.Path.GetFullPath(System.IO.Path.Combine(System.IO.Path.GetDirectoryName(exe)!, "..", "..", "Content", "Paks"));
            if (!System.IO.Directory.Exists(paks)) return "Mod";
            var pakNames = System.IO.Directory.EnumerateFiles(paks, "*.pak", System.IO.SearchOption.AllDirectories)
                .Select(System.IO.Path.GetFileNameWithoutExtension).ToList();
            foreach (var (part, label) in KnownPrismMods)
                if (pakNames.Any(n => n!.Contains(part, StringComparison.OrdinalIgnoreCase)))
                    return label;
        }
        catch (Exception ex) { Log.Debug($"Mod detection failed: {ex.Message}"); }
        return "Mod";
    }

    private int _nameRetries;
    private DateTime _nextNameRetry;

    /// Attaching while the game is still loading can resolve only some (or none) of the segment
    /// names, which leaves the stat pickers empty. Retries every 5 s for a few minutes.
    private void RetryNameResolution()
    {
        if (_names == null || ResolvedStats == TotalStats || _nameRetries >= 60 || DateTime.UtcNow < _nextNameRetry) return;
        _nameRetries++;
        _nextNameRetry = DateTime.UtcNow.AddSeconds(5);

        int before = ResolvedStats;
        int now = _catalog.ResolveIds(_names);
        if (now == before) return;
        Log.Info($"Resolved {now}/{TotalStats} segment names (was {before}).");
        OnPropertyChanged(nameof(ResolvedStats));
        RebuildChoices();
        _objectsIndexed = false;   // ResolveIds cleared the cached class objects
        _forceScan = true;         // re-read segments with the resolved names
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
        p.CanGrow = _allocator?.IsAvailable == true;
        // Waiting for the new legendary pick (see HandleRoomBackups for what happens after it).
        if (_roomBackups.TryGetValue(p.DataAddress, out var room))
            p.HasRoomBackup = room.SegmentsAddress == p.SegmentsAddress && room.Keep == p.Segments.Count;

        // Segments appended after session start (by the game or the editor) keep their current
        // values as the original, so Revert leaves them alone.
        if (_session.TryGetValue(p.DataAddress, out var snap)
            && snap.Segments.Length <= p.Segments.Count && snap.Feeds.Length == p.Feeds.Count)
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

    private bool CanApply() => IsLinked && IsInSync && HasChanges && !IsReadOnly;

    [RelayCommand(CanExecute = nameof(CanApply))]
    private void Apply()
    {
        if (_writer == null || IsReadOnly) return;
        var dirty = Prisms.Where(p => p.IsDirty).ToList();
        // The game applies segment bonuses when the prism is equipped, so new ones need a re-equip.
        bool segmentsChanged = dirty.Any(p => p.Segments.Any(s => s.IsDirty));
        const string reequip = " Re-equip the prism in game so the new segment bonuses take effect.";
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
            SetStatus($"Applied. {writes} value{(writes == 1 ? "" : "s")} written and confirmed in game memory."
                      + (segmentsChanged ? reequip : ""), Tone.Good);
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
        SetStatus($"Segments staged at level {SegmentTarget:N0}. Legendary left unchanged."
                  + (SegmentTarget > SegmentSlot.NormalMaxLevel
                      ? $" Standard segments stop gaining at {SegmentSlot.NormalMaxLevel}; fusions keep scaling."
                      : ""));
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

    /// Experimental: appends a segment into the spare capacity the game left in the array.
    [RelayCommand]
    private async Task AddSegmentAsync()
    {
        var p = SelectedPrism;
        if (!CanChangeSegmentCount(p)) return;

        // A fusion the prism doesn't have yet (loaded classes first, so the effect starts right away).
        var present = p!.Segments.Select(s => s.Def).ToHashSet();
        var def = _catalog.All.Where(d => d.IsResolved && !d.IsDisabled && d.Kind == SegmentKind.Fusion && !present.Contains(d))
                      .OrderByDescending(d => d.DefaultObject != 0).FirstOrDefault()
                  ?? _catalog.All.FirstOrDefault(d => d.IsResolved && !d.IsDisabled && d.Kind == SegmentKind.Standard && !present.Contains(d));
        if (def == null)
        {
            SetStatus("No segment stat left to add.", Tone.Warn);
            return;
        }

        int number = p.Segments.Count + 1;
        var r = _writer!.AddSegment(p, def, SegmentSlot.NormalMaxLevel);
        _forceScan = true;
        if (!r.Ok)
        {
            SetStatus(r.Problems[0], Tone.Bad);
            return;
        }
        await TickAsync();
        SetStatus($"Segment {number} added ({def.Name}, Lv {SegmentSlot.NormalMaxLevel}). Change its stat and level like any other segment, "
                  + "then re-equip the prism in game." + (r.Notes.Count > 0 ? " " + string.Join(" ", r.Notes) : ""),
                  r.Notes.Count > 0 ? Tone.Warn : Tone.Good);
    }

    /// Removes any segment, wherever it came from. Its slot becomes spare room for Add segment.
    [RelayCommand]
    private Task RemoveSegmentAsync(SegmentSlot? slot) =>
        RunArrayEdit(slot, p => _writer!.RemoveSegment(p, slot!.Index),
                     $"Segment {slot?.Number} ({slot?.Def.Name}) removed. Re-equip the prism in game.");

    [RelayCommand]
    private Task MoveSegmentUpAsync(SegmentSlot? slot) =>
        RunArrayEdit(slot, p => _writer!.MoveSegment(p, slot!.Index, -1), $"Segment {slot?.Number} moved up.");

    [RelayCommand]
    private Task MoveSegmentDownAsync(SegmentSlot? slot) =>
        RunArrayEdit(slot, p => _writer!.MoveSegment(p, slot!.Index, +1), $"Segment {slot?.Number} moved down.");

    [RelayCommand]
    private Task RemoveFeedAsync(FeedSlot? slot) =>
        RunArrayEdit(slot, p => _writer!.RemoveFeed(p, slot!.Index), $"Fed fragment {slot?.Number} ({slot?.Def.Name}) removed.");

    /// Empties the selected prism: no segments, no fed fragments, no XP, internal level 0.
    [RelayCommand]
    private async Task ResetPrismAsync()
    {
        var p = SelectedPrism;
        if (!CanEditArrays(p)) return;
        var answer = MessageBox.Show(
            $"Reset {p!.Name} {p.Numeral} to a blank prism?\n\n" +
            $"All {p.Segments.Count} segment(s) and {p.Feeds.Count} fed fragment(s) are removed, and XP and level go to 0. " +
            "The game saves this with your character. The old entries are written to the log, but Prismforge can't put them back.",
            "Reset prism", MessageBoxButton.OKCancel, MessageBoxImage.Warning, MessageBoxResult.Cancel);
        if (answer != MessageBoxResult.OK) return;

        var r = _writer!.ResetPrism(p);
        _forceScan = true;
        if (!r.Ok)
        {
            SetStatus(r.Problems[0], Tone.Bad);
            return;
        }
        await TickAsync();
        SetStatus("Prism reset. Unequip and re-equip it in game." + (r.Notes.Count > 0 ? " " + r.Notes[0] : ""),
                  r.Notes.Count > 0 ? Tone.Warn : Tone.Good);
    }

    /// Build code pasted on the Prisms page (from the planner web page or "Copy code").
    [ObservableProperty] private string _buildCodeText = "";

    /// Replaces the selected prism's segments, fed fragments and XP with those of a build code.
    [RelayCommand]
    private async Task ImportBuildAsync()
    {
        var p = SelectedPrism;
        if (!CanEditArrays(p)) return;
        BuildSpec spec;
        try { spec = BuildCode.Decode(BuildCodeText); }
        catch (FormatException ex)
        {
            SetStatus(ex.Message, Tone.Bad);
            return;
        }

        var answer = MessageBox.Show(
            $"Build {p!.Name} {p.Numeral} from this code?\n\n{BuildCode.Describe(spec, _catalog)}\n" +
            "This replaces all segments and fed fragments on the prism" + (spec.Xp != null ? " and its pending XP" : "") +
            ". The game saves it with the character. The old entries are written to the log.",
            "Import build code", MessageBoxButton.OKCancel, MessageBoxImage.Question, MessageBoxResult.Cancel);
        if (answer != MessageBoxResult.OK) return;

        string who = spec.Note.Length > 0 ? $" ({spec.Note})" : "";
        if (await ApplySpecAsync(p, spec, $"Build imported{who}: {spec.Segments.Count} segment(s), {spec.Feeds.Count} fed fragment(s)."))
            BuildCodeText = "";
    }

    /// Shared end of Import and preset Load: writes the build, rescans, reports. False when it failed.
    private async Task<bool> ApplySpecAsync(PrismData p, BuildSpec spec, string done)
    {
        var r = _writer!.ApplyBuild(p, spec, _catalog);
        _forceScan = true;
        if (!r.Ok)
        {
            SetStatus(r.Problems[0], Tone.Bad);
            return false;
        }
        await TickAsync();
        SetStatus(done + " Unequip and re-equip the prism in game." + (r.Notes.Count > 0 ? " " + string.Join(" ", r.Notes) : ""),
                  r.Notes.Count > 0 ? Tone.Warn : Tone.Good);
        return true;
    }

    /// Copies the selected prism's layout as a build code (can be opened in the planner or imported elsewhere).
    [RelayCommand]
    private void CopyBuildCode()
    {
        if (SelectedPrism == null) return;
        try
        {
            Clipboard.SetText(BuildCode.Encode(PrismWriter.ToSpec(SelectedPrism)));
            SetStatus("Build code of this prism copied to the clipboard.", Tone.Good);
        }
        catch (Exception ex)
        {
            Log.Error("Copying the build code failed.", ex);
            SetStatus("Copying to the clipboard failed. Try again.", Tone.Bad);
        }
    }

    [RelayCommand]
    private void OpenPlanner()
    {
        try { Process.Start(new ProcessStartInfo(AppInfo.PlannerUrl) { UseShellExecute = true }); }
        catch (Exception ex) { Log.Error("Could not open the planner page.", ex); }
    }

    /// Shared flow of the remove/move commands: checks, write, rescan, status.
    private async Task RunArrayEdit(SlotBase? slot, Func<PrismData, CommitResult> edit, string done)
    {
        var p = SelectedPrism;
        if (slot == null || !CanEditArrays(p)) return;
        var r = edit(p!);
        _forceScan = true;
        if (!r.Ok)
        {
            SetStatus(r.Problems[0], Tone.Bad);
            return;
        }
        await TickAsync();
        SetStatus(done + (r.Notes.Count > 0 ? " " + r.Notes[0] : ""), r.Notes.Count > 0 ? Tone.Warn : Tone.Good);
    }

    private bool CanEditArrays(PrismData? p)
    {
        if (!CanChangeSegmentCount(p)) return false;
        if (p!.HasRoomBackup)
        {
            SetStatus("Finish Make room first (pick the legendary in game) or press Restore legendary.", Tone.Warn);
            return false;
        }
        return true;
    }

    /// Experimental: takes the legendary off the list and marks the array full, so the game
    /// reallocates it with spare room when the legendary is picked again.
    [RelayCommand]
    private async Task MakeRoomAsync()
    {
        var p = SelectedPrism;
        if (!CanChangeSegmentCount(p)) return;

        var (r, backup) = _writer!.MakeRoom(p!);
        _forceScan = true;
        if (!r.Ok || backup == null)
        {
            SetStatus(r.Problems.FirstOrDefault() ?? "Making room failed.", Tone.Bad);
            return;
        }
        _roomBackups[p!.DataAddress] = backup;
        await TickAsync();
        SetStatus((backup.Extras > 0 ? $"Legendary and {backup.Extras} extra segment(s) taken off the list. " : "Legendary taken off the list. ")
                  + "Give the prism XP (Pending XP, Apply) and pick the legendary again in game; the editor then puts the extra segments back "
                  + "and Add segment is available. Keep Prismforge open until then. Restore legendary undoes this.", Tone.Warn);
    }

    [RelayCommand]
    private async Task RestoreRoomAsync()
    {
        var p = SelectedPrism;
        if (!CanChangeSegmentCount(p)) return;
        if (!_roomBackups.TryGetValue(p!.DataAddress, out var backup))
        {
            SetStatus("Nothing to restore.", Tone.Warn);
            return;
        }

        var r = _writer!.RestoreRoom(p, backup);
        _forceScan = true;
        _roomBackups.Remove(p.DataAddress);
        if (!r.Ok)
        {
            SetStatus(r.Problems[0], Tone.Bad);
            return;
        }
        await TickAsync();
        SetStatus("Legendary restored.", Tone.Good);
    }

    private bool CanChangeSegmentCount(PrismData? p)
    {
        if (p == null || _writer == null || !IsInSync) return false;
        if (IsReadOnly)
        {
            SetStatus("Read-only on this untested game version. Start Prismforge with --allow-untested-writes to change segments.", Tone.Warn);
            return false;
        }
        if (p.IsDirty)
        {
            SetStatus("Apply or discard this prism's pending changes first.", Tone.Warn);
            return false;
        }
        return true;
    }

    [RelayCommand(CanExecute = nameof(IsLinked))]
    private async Task DiagnosticAsync()
    {
        if (_scanner == null) return;
        SetStatus("Running diagnostic…");
        try
        {
            var target = _target;
            string path = await Task.Run(() => _scanner.RunDiagnostic(target));
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
