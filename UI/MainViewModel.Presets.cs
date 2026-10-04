using System.Collections.ObjectModel;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using R2PrismRuntime.Diagnostics;
using R2PrismRuntime.Game;
using R2PrismRuntime.Models;

namespace R2PrismRuntime.UI;

/// <summary>
/// Prism presets: named build codes (without XP) saved in presets.json and loaded into the
/// selected prism with one click. Load doesn't ask; Undo load writes the previous layout back.
/// </summary>
public partial class MainViewModel
{
    public const int MaxPresetName = 60;

    public ObservableCollection<PrismPreset> Presets { get; } = new();

    /// Name for Save current / Save code; empty falls back to the code's note or the prism's name.
    [ObservableProperty] private string _presetName = "";

    /// Layout the prism had before the last preset load (prism found again by its data address).
    private (ulong Data, BuildSpec Before)? _presetUndo;

    [ObservableProperty] private bool _canUndoPresetLoad;
    [ObservableProperty] private string _undoPresetText = "";

    private void InitPresets()
    {
        foreach (var e in PresetStore.Load())
            Presets.Add(new PrismPreset { Name = e.Name, Code = e.Code, SavedFrom = e.SavedFrom, Created = e.Created });
        RefreshPresetDetails();
    }

    private void RefreshPresetDetails()
    {
        foreach (var x in Presets) Describe(x);
    }

    private void Describe(PrismPreset x)
    {
        string origin = $"Saved from {(x.SavedFrom.Length > 0 ? x.SavedFrom : "?")}, {x.Created:g}.";
        try
        {
            var spec = BuildCode.Decode(x.Code);
            x.Summary = $"{spec.Segments.Count} segment(s) · Lv {spec.Segments.Sum(s => (long)s.Level):N0}"
                        + (spec.Feeds.Count > 0 ? $" · {spec.Feeds.Count} fed" : "");
            x.Details = BuildCode.Describe(spec with { Note = "", Xp = null }, _catalog) + "\n" + origin;
        }
        catch (FormatException ex)
        {
            x.Summary = "Damaged code";
            x.Details = ex.Message + "\n" + origin;
        }
    }

    private bool PersistPresets()
    {
        if (PresetStore.Save(Presets.Select(x => new PresetStore.Entry(x.Name, x.Code, x.SavedFrom, x.Created)))) return true;
        SetStatus("The presets couldn't be written to disk (see the journal). They stay available until Prismforge closes.", Tone.Bad);
        return false;
    }

    private PrismPreset? FindPreset(string name) =>
        Presets.FirstOrDefault(x => x.Name.Equals(name, StringComparison.OrdinalIgnoreCase));

    private string UniquePresetName(string baseName)
    {
        string name = baseName;
        for (int n = 2; FindPreset(name) != null; n++) name = $"{baseName} ({n})";
        return name;
    }

    private string TakePresetName(string fallback)
    {
        string name = PresetName.Trim();
        if (name.Length == 0) name = UniquePresetName(fallback.Trim().Length > 0 ? fallback.Trim() : "Preset");
        return name.Length > MaxPresetName ? name[..MaxPresetName].TrimEnd() : name;
    }

    /// Adds a preset, or replaces the one with the same name after asking. False when cancelled.
    private bool StorePreset(string name, BuildSpec spec, string savedFrom)
    {
        string code = BuildCode.Encode(spec with { Note = name, Xp = null });
        var existing = FindPreset(name);
        if (existing != null)
        {
            if (MessageBox.Show($"A preset named '{existing.Name}' already exists. Replace it?", "Save preset",
                                MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No) != MessageBoxResult.Yes)
                return false;
            existing.Name = name;
            existing.Code = code;
            existing.SavedFrom = savedFrom;
            existing.Created = DateTime.Now;
            Describe(existing);
        }
        else
        {
            var x = new PrismPreset { Name = name, Code = code, SavedFrom = savedFrom, Created = DateTime.Now };
            Describe(x);
            Presets.Add(x);
        }
        PresetName = "";
        return PersistPresets();
    }

    /// Saves the selected prism's segments and fed fragments as they are in the game (not staged edits).
    [RelayCommand]
    private void SavePreset()
    {
        var p = SelectedPrism;
        if (p == null)
        {
            SetStatus("Pick a prism first.", Tone.Warn);
            return;
        }
        string name = TakePresetName(p.Name);
        if (!StorePreset(name, PrismWriter.ToSpec(p), $"{p.Name} {p.Numeral}")) return;
        SetStatus(p.IsDirty
            ? $"Preset '{name}' saved with the values in the game. Your staged edits aren't in it: apply them, then save again under the same name."
            : $"Preset '{name}' saved: {p.Segments.Count} segment(s), {p.Feeds.Count} fed fragment(s).", p.IsDirty ? Tone.Warn : Tone.Good);
    }

    /// Saves the code in the Build code box as a preset without applying it.
    [RelayCommand]
    private void SaveCodeAsPreset()
    {
        BuildSpec spec;
        try { spec = BuildCode.Decode(BuildCodeText); }
        catch (FormatException ex)
        {
            SetStatus(ex.Message, Tone.Bad);
            return;
        }
        string name = TakePresetName(spec.Note.Length > 0 ? spec.Note : "Planner build");
        if (!StorePreset(name, spec, "build code")) return;
        BuildCodeText = "";
        SetStatus($"Preset '{name}' saved from the build code. Load it into a prism from the Presets list.", Tone.Good);
    }

    /// Replaces the selected prism's segments and fed fragments with the preset's. XP is left alone.
    [RelayCommand]
    private async Task LoadPresetAsync(PrismPreset? preset)
    {
        var p = SelectedPrism;
        if (preset == null) return;
        if (p == null || _writer == null || !IsInSync)
        {
            SetStatus("Load a character and pick a prism first; Prismforge has to be linked to the game.", Tone.Warn);
            return;
        }
        if (!CanEditArrays(p)) return;
        BuildSpec spec;
        try { spec = BuildCode.Decode(preset.Code) with { Xp = null }; }
        catch (FormatException ex)
        {
            SetStatus($"Preset '{preset.Name}': {ex.Message}", Tone.Bad);
            return;
        }

        var before = PrismWriter.ToSpec(p) with { Xp = null };
        string target = $"{p.Name} {p.Numeral}";
        ulong data = p.DataAddress;
        // Kept even when the write failed: a failure after the first array may have changed the prism.
        await ApplySpecAsync(p, spec, $"Preset '{preset.Name}' loaded into {target}.");
        _presetUndo = (data, before);
        UndoPresetText = $"Undo '{preset.Name}' on {target}";
        CanUndoPresetLoad = true;
    }

    private PrismData? FindPrism(ulong data) => Prisms.FirstOrDefault(x => x.DataAddress == data);

    [RelayCommand]
    private async Task UndoPresetLoadAsync()
    {
        if (_presetUndo is not { } undo) return;
        var (data, before) = undo;
        var p = FindPrism(data);
        if (p == null)
        {
            SetStatus("That prism isn't in the inventory any more (other character or save?). Nothing to undo.", Tone.Warn);
            ClearPresetUndo();
            return;
        }
        if (_writer == null || !IsInSync || !CanEditArrays(p)) return;
        if (await ApplySpecAsync(p, before, $"{p.Name} {p.Numeral} is back to its layout before the preset."))
            ClearPresetUndo();
    }

    private void ClearPresetUndo()
    {
        _presetUndo = null;
        CanUndoPresetLoad = false;
    }

    [RelayCommand]
    private void CopyPresetCode(PrismPreset? preset)
    {
        if (preset == null) return;
        try
        {
            Clipboard.SetText(preset.Code);
            SetStatus($"Build code of preset '{preset.Name}' copied to the clipboard.", Tone.Good);
        }
        catch (Exception ex)
        {
            Log.Error("Copying the preset code failed.", ex);
            SetStatus("Copying to the clipboard failed. Try again.", Tone.Bad);
        }
    }

    [RelayCommand]
    private void DeletePreset(PrismPreset? preset)
    {
        if (preset == null) return;
        if (MessageBox.Show($"Delete preset '{preset.Name}'?\n\n{preset.Details}", "Delete preset",
                            MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No) != MessageBoxResult.Yes)
            return;
        Presets.Remove(preset);
        if (PersistPresets()) SetStatus($"Preset '{preset.Name}' deleted.", Tone.Good);
    }

    [RelayCommand]
    private void BeginRenamePreset(PrismPreset? preset)
    {
        if (preset == null) return;
        foreach (var x in Presets) x.IsRenaming = false;
        preset.EditName = preset.Name;
        preset.IsRenaming = true;
    }

    [RelayCommand]
    private void CancelRenamePreset(PrismPreset? preset)
    {
        if (preset != null) preset.IsRenaming = false;
    }

    [RelayCommand]
    private void CommitRenamePreset(PrismPreset? preset)
    {
        if (preset is not { IsRenaming: true }) return;
        string name = preset.EditName.Trim();
        if (name.Length > MaxPresetName) name = name[..MaxPresetName].TrimEnd();
        preset.IsRenaming = false;
        if (name.Length == 0 || name == preset.Name) return;
        if (FindPreset(name) is { } other && other != preset)
        {
            SetStatus($"A preset named '{other.Name}' already exists. Pick another name.", Tone.Warn);
            return;
        }
        // The name is also the code's note, so a copied code carries it.
        try { preset.Code = BuildCode.Encode(BuildCode.Decode(preset.Code) with { Note = name }); }
        catch (FormatException) { }
        string old = preset.Name;
        preset.Name = name;
        Describe(preset);
        if (PersistPresets()) SetStatus($"Preset '{old}' renamed to '{name}'.", Tone.Good);
    }
}
