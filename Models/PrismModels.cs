using System.Collections.ObjectModel;
using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;
using R2PrismRuntime.Game;

namespace R2PrismRuntime.Models;

/// <summary>
/// A prism read from the game. Every editable value exists three times:
/// the live value (last read from memory), the staged edit (what the UI shows), and the
/// value from the first scan of this session (for "revert").
/// </summary>
public partial class PrismData : ObservableObject
{
    public int Index { get; init; }
    public string Name { get; init; } = "";
    public string ClassName { get; init; } = "";

    // Addresses captured during the scan; re-checked before every write.
    public ulong ItemAddress { get; init; }
    public ulong DataAddress { get; init; }
    public ulong SegmentsAddress { get; init; }
    public ulong FeedAddress { get; init; }

    public int InternalLevel { get; set; }

    public ObservableCollection<SegmentSlot> Segments { get; } = new();
    public ObservableCollection<FeedSlot> Feeds { get; } = new();

    // ── XP ──────────────────────────────────────────────────────
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsXpDirty), nameof(IsDirty))]
    private float _xp;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsXpDirty), nameof(IsDirty))]
    private float _editXp;

    public float OriginalXp { get; set; }

    public bool IsXpDirty => Math.Abs(EditXp - Xp) > 0.001f;

    // ── Derived ─────────────────────────────────────────────────

    /// The level the game shows is the sum of segment levels (as in the save editor).
    public int Level => Segments.Sum(s => s.EditLevel);

    public bool IsDirty => IsXpDirty || Segments.Any(s => s.IsDirty) || Feeds.Any(f => f.IsDirty);

    public bool HasLegendary => Segments.Any(s => s.EditDef.Kind == SegmentKind.Legendary);

    public string Numeral => ToRoman(Index);

    /// Hooks slot change notifications so Level/IsDirty on the prism stay current.
    public void Track()
    {
        foreach (var s in Segments) s.PropertyChanged += OnChildChanged;
        foreach (var f in Feeds) f.PropertyChanged += OnChildChanged;
    }

    private void OnChildChanged(object? sender, PropertyChangedEventArgs e)
    {
        OnPropertyChanged(nameof(Level));
        OnPropertyChanged(nameof(IsDirty));
        OnPropertyChanged(nameof(HasLegendary));
    }

    public void Discard()
    {
        EditXp = Xp;
        foreach (var s in Segments) s.Discard();
        foreach (var f in Feeds) f.Discard();
    }

    /// Stages the values captured when this prism was first seen this session.
    public void StageOriginal()
    {
        EditXp = OriginalXp;
        foreach (var s in Segments) s.StageOriginal();
        foreach (var f in Feeds) f.StageOriginal();
    }

    private static string ToRoman(int n)
    {
        if (n <= 0 || n > 39) return n.ToString();
        string[] tens = { "", "X", "XX", "XXX" };
        string[] ones = { "", "I", "II", "III", "IV", "V", "VI", "VII", "VIII", "IX" };
        return tens[n / 10] + ones[n % 10];
    }
}

/// <summary>Shared behaviour of segments and fed fragments: a row (stat) and a level.</summary>
public abstract partial class SlotBase : ObservableObject
{
    public int Index { get; init; }
    public int Number => Index + 1;
    public ulong Address { get; init; }

    // Live values
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsDirty), nameof(IsRowDirty))]
    private SegmentDef _def = SegmentDef.Unknown("");

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsDirty), nameof(IsLevelDirty))]
    private int _level;

    // Staged values
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsDirty), nameof(IsRowDirty))]
    private SegmentDef _editDef = SegmentDef.Unknown("");

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsDirty), nameof(IsLevelDirty))]
    private int _editLevel;

    public int RawNameId { get; set; }
    public SegmentDef OriginalDef { get; set; } = SegmentDef.Unknown("");
    public int OriginalLevel { get; set; }

    public bool IsRowDirty => !ReferenceEquals(EditDef, Def);
    public bool IsLevelDirty => EditLevel != Level;
    public bool IsDirty => IsRowDirty || IsLevelDirty;

    public const int MaxLevel = 999;

    // A ComboBox pushes null when its selection is cleared; never let that reach the model.
    partial void OnEditDefChanged(SegmentDef value)
    {
        if (value is null) EditDef = Def;
    }

    partial void OnEditLevelChanged(int value)
    {
        if (value < 0) EditLevel = 0;
        else if (value > MaxLevel) EditLevel = MaxLevel;
    }

    public void Discard()
    {
        EditDef = Def;
        EditLevel = Level;
    }

    public void StageOriginal()
    {
        EditDef = OriginalDef;
        EditLevel = OriginalLevel;
    }
}

/// <summary>One element of CurrentSegments (stride 0x28).</summary>
public sealed partial class SegmentSlot : SlotBase
{
    /// Cached object pointer at +0x20. The CT reads the name from it for legendary segments.
    public ulong ObjectPtr { get; set; }

    public string Header => $"Segment {Index + 1}";
}

/// <summary>A computed character stat, optionally held at a fixed value by the stat hook.</summary>
public sealed partial class StatEntry : ObservableObject
{
    public int NameId { get; init; }
    public string Name { get; init; } = "";
    public string Category { get; init; } = "";
    public ulong Address { get; set; }

    /// Short explanation shown as tooltip (see Game/StatInfo.cs).
    public string Info { get; init; } = "";

    /// "WeakSpotDamageMod" → "Weak Spot Damage Mod".
    public string DisplayName => System.Text.RegularExpressions.Regex.Replace(Name, "(?<=[a-z0-9])(?=[A-Z])|(?<=[A-Z])(?=[A-Z][a-z])", " ");

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(LiveText))]
    private float _live;

    [ObservableProperty] private float _target;
    [ObservableProperty] private bool _isHeld;

    /// Soft warning for the current target (null when nothing stands out).
    [ObservableProperty] private string? _warning;

    /// True once the user typed a target; until then the target follows the live value.
    public bool TargetEdited { get; set; }
    private bool _syncing;

    partial void OnTargetChanged(float value)
    {
        if (!_syncing) TargetEdited = true;
    }

    public void SyncTarget(float value)
    {
        _syncing = true;
        Target = value;
        _syncing = false;
        TargetEdited = false;
    }

    public string LiveText => Live.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture);
}

/// <summary>One element of CurrentFeedData (stride 0xC): a fragment fed into the prism.</summary>
public sealed partial class FeedSlot : SlotBase
{
}
