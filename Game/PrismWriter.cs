using R2PrismRuntime.Diagnostics;
using R2PrismRuntime.Memory;
using R2PrismRuntime.Models;

namespace R2PrismRuntime.Game;

public sealed record CommitResult(int Writes, IReadOnlyList<string> Problems, IReadOnlyList<string> Notes)
{
    public bool Ok => Problems.Count == 0;
}

/// <summary>
/// Writes a prism's staged edits to game memory, then reads them back to confirm.
/// Touched fields: XP, segment/fed RowName and level, and for segments the cached class
/// default object at +0x20 so it matches the new row (as the game itself would set it).
/// </summary>
public sealed class PrismWriter
{
    private readonly ProcessMemory _mem;
    private readonly PrismScanner _scanner;

    public PrismWriter(ProcessMemory mem, PrismScanner scanner)
    {
        _mem = mem;
        _scanner = scanner;
    }

    public CommitResult Commit(PrismData p)
    {
        var problems = new List<string>();
        var notes = new List<string>();
        string? stale = _scanner.CheckStale(p);
        if (stale != null) return new CommitResult(0, new[] { stale + " Rescan and try again." }, notes);

        Log.Info($"Commit '{p.Name}' ({Log.Hex(p.DataAddress)})");
        int writes = 0;

        if (p.IsXpDirty)
        {
            if (_mem.WriteFloat(p.DataAddress + GameOffsets.Data_Xp, p.EditXp)) writes++;
            else problems.Add("XP write failed.");
        }

        foreach (var s in p.Segments.Where(s => s.IsDirty))
        {
            if (s.IsRowDirty && s.EditDef.IsResolved)
            {
                // Same pairing the game keeps: +0x20 = default object of the row's class, or null.
                ulong obj = _scanner.DefaultObjectFor(s.EditDef);
                if (s.EditDef.HasClass && obj == 0)
                    notes.Add($"{s.EditDef.Name} isn't loaded yet, so its effect starts after you reload the character.");
                if (!_mem.WritePointer(s.Address + GameOffsets.Seg_Object, obj))
                    problems.Add($"{s.Header}: class object write failed.");
            }
            writes += WriteSlot(s, GameOffsets.Seg_RowName, GameOffsets.Seg_Level, s.Header, problems);
        }
        foreach (var f in p.Feeds.Where(f => f.IsDirty))
            writes += WriteSlot(f, GameOffsets.Feed_RowName, GameOffsets.Feed_Level, $"Fed fragment {f.Index + 1}", problems);

        // Read back and compare with what was staged.
        _scanner.Reread(p);
        if (Math.Abs(p.Xp - p.EditXp) > 0.001f) problems.Add($"XP reads back as {p.Xp:0}.");
        foreach (var s in p.Segments.Where(s => s.IsDirty))
            problems.Add($"{s.Header} reads back as {s.Def.Name} Lv {s.Level}.");
        foreach (var f in p.Feeds.Where(f => f.IsDirty))
            problems.Add($"Fed fragment {f.Index + 1} reads back as {f.Def.Name} Lv {f.Level}.");

        Log.Info($"Commit '{p.Name}': {writes} write(s), {problems.Count} problem(s).");
        foreach (var pr in problems) Log.Warn("  " + pr);
        foreach (var n in notes) Log.Info("  note: " + n);
        return new CommitResult(writes, problems.Distinct().ToList(), notes.Distinct().ToList());
    }

    private int WriteSlot(SlotBase s, ulong rowOff, ulong levelOff, string label, List<string> problems)
    {
        int writes = 0;
        if (s.IsRowDirty)
        {
            if (!s.EditDef.IsResolved)
                problems.Add($"{label}: '{s.EditDef.Name}' isn't known to the running game.");
            // FName = { int32 ComparisonIndex; int32 Number } — row names always use Number 0.
            else if (_mem.WriteInt32(s.Address + rowOff, s.EditDef.NameId)
                     && _mem.WriteInt32(s.Address + rowOff + 4, 0))
                writes++;
            else problems.Add($"{label}: stat write failed.");
        }
        if (s.IsLevelDirty)
        {
            if (_mem.WriteInt32(s.Address + levelOff, s.EditLevel)) writes++;
            else problems.Add($"{label}: level write failed.");
        }
        return writes;
    }
}
