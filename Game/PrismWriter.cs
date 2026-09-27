using R2PrismRuntime.Diagnostics;
using R2PrismRuntime.Memory;
using R2PrismRuntime.Models;

namespace R2PrismRuntime.Game;

/// <summary>
/// State before <see cref="PrismWriter.MakeRoom"/>: the old Num/Max, how many segments stayed, and
/// the removed entries (the extra normal segments first, then the legendary).
/// </summary>
public sealed record RoomBackup(ulong SegmentsAddress, int OldNum, int OldMax, int Keep, byte[] Tail, int Extras);

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

    /// <summary>
    /// Experimental: appends a segment into CurrentSegments' spare capacity. Only memory the game
    /// already allocated is used (Max > Num after the game grew the array itself), so the game's
    /// allocator still owns the buffer and can free, grow and save it normally. Writes the entry
    /// first, then Num, so the game never sees a count covering an unwritten slot.
    /// </summary>
    public CommitResult AddSegment(PrismData p, SegmentDef def, int level)
    {
        var notes = new List<string>();
        string? stale = _scanner.CheckStale(p);
        if (stale != null) return Fail(stale + " Rescan and try again.");
        if (!def.IsResolved) return Fail($"'{def.Name}' isn't known to the running game.");

        int num = p.Segments.Count;
        int max = _scanner.ReadSegmentCapacity(p);
        if (max > PrismScanner.MaxSegmentCapacity) return Fail($"The segment array looks invalid (Max {max}). Rescan and check the prism.");
        ulong segments = p.SegmentsAddress;
        if (num >= max)
        {
            if (Allocator is not { IsAvailable: true })
                return Fail($"No spare segment slot (Num {num}, Max {max}), and the game's allocator wasn't found.");
            var (grown, problem) = GrowSegments(p, num, max);
            if (problem != null) return Fail(problem);
            segments = grown;
            notes.Add("The segment list got a bigger buffer from the game's allocator.");
        }

        ulong obj = _scanner.DefaultObjectFor(def);
        if (def.HasClass && obj == 0)
            notes.Add($"{def.Name} isn't loaded yet, so its effect starts after you reload the character.");

        // Same shape as the game's own entries: FName {id, 0}, level, action -1, 16 zero bytes, class object.
        var entry = new byte[GameOffsets.Seg_Stride];
        BitConverter.GetBytes(def.NameId).CopyTo(entry, (int)GameOffsets.Seg_RowName);
        BitConverter.GetBytes(level).CopyTo(entry, (int)GameOffsets.Seg_Level);
        BitConverter.GetBytes(-1).CopyTo(entry, (int)GameOffsets.Seg_Action);
        BitConverter.GetBytes(obj).CopyTo(entry, (int)GameOffsets.Seg_Object);

        ulong slot = segments + (ulong)num * GameOffsets.Seg_Stride;
        Log.Info($"Add segment to '{p.Name}': slot {num} at {Log.Hex(slot)}, '{def.Row}' Lv {level}, obj {Log.Hex(obj)}");
        if (!_mem.WriteBytes(slot, entry, "new segment")) return Fail("Writing the new segment failed.");
        if (!_mem.WriteInt32(p.DataAddress + GameOffsets.Data_SegCount, num + 1)) return Fail("Raising the segment count failed.");

        if (_mem.ReadInt32(p.DataAddress + GameOffsets.Data_SegCount) != num + 1 || _mem.ReadInt32(slot) != def.NameId)
            return Fail("The new segment doesn't read back. Rescan and check the prism.");
        return new CommitResult(2, Array.Empty<string>(), notes);
    }

    /// Set once GMalloc was found; lets <see cref="AddSegment"/> grow a full segment array.
    public GameAllocator? Allocator { get; set; }

    /// Slots added per grow (the game itself went from 6 to 25 on its own growth).
    public const int GrowBy = 16;

    /// <summary>
    /// Gives CurrentSegments a bigger buffer from the game's own allocator, so the game can grow,
    /// free and save it later as if it had allocated it itself. Order, so the game only ever sees
    /// a valid array: fill the new buffer, check nothing moved, swap the Data pointer (the game now
    /// sees the new buffer with the old, smaller Max), then raise Max.
    /// The old buffer is deliberately not freed: a game thread might still be reading it, and it's
    /// only Max × 0x28 bytes. Returns the new buffer, or a problem.
    /// </summary>
    private (ulong Buffer, string? Problem) GrowSegments(PrismData p, int num, int max)
    {
        int stride = (int)GameOffsets.Seg_Stride;
        int newMax = Math.Min(num + GrowBy, PrismScanner.MaxSegmentCapacity);
        if (newMax <= num) return (0, $"The prism already has the most segments the editor allows ({num}).");

        var content = new byte[newMax * stride];
        if (num > 0 && !_mem.TryReadBytes(p.SegmentsAddress, content, num * stride)) return (0, "Reading the segments failed.");

        ulong buf = Allocator!.Malloc(content.Length);
        if (buf == 0) return (0, "The game's allocator didn't return memory. Nothing was changed.");
        if (!_mem.WriteBytes(buf, content, "grown segment buffer"))
        {
            Allocator.Free(buf);
            return (0, "Filling the new segment buffer failed. Nothing was changed.");
        }
        if (_scanner.CheckStale(p) != null || _scanner.ReadSegmentCapacity(p) != max)
        {
            Allocator.Free(buf);
            return (0, "The game changed this prism's segments meanwhile. Nothing was changed; rescan and try again.");
        }

        Log.Info($"Grow segments of '{p.Name}': {Log.Hex(p.SegmentsAddress)} (Num {num}, Max {max}) -> {Log.Hex(buf)} (Max {newMax}). " +
                 "The old buffer stays allocated on purpose.");
        if (!_mem.WritePointer(p.DataAddress + GameOffsets.Data_SegPtr, buf))
        {
            Allocator.Free(buf);
            return (0, "Switching to the new segment buffer failed. Nothing was changed.");
        }
        if (!_mem.WriteInt32(p.DataAddress + GameOffsets.Data_SegMax, newMax))
            return (0, "Raising the segment capacity failed. The prism works, but has no spare room yet; rescan.");
        if (_mem.ReadPointer(p.DataAddress + GameOffsets.Data_SegPtr) != buf || _scanner.ReadSegmentCapacity(p) != newMax)
            return (0, "The new segment buffer doesn't read back. Rescan and check the prism.");
        return (buf, null);
    }

    /// The game only adds a segment (and so reallocates the array) while a prism has at most this
    /// many normal segments: with 5 it added the legendary pick, with 6 it ignored the pick.
    public const int GameSegmentLimit = 5;

    /// <summary>
    /// Experimental: makes the game reallocate CurrentSegments with spare room. The game only grows
    /// the array when it's full and it adds a segment itself, which it does for the legendary pick,
    /// and only while the prism has at most <see cref="GameSegmentLimit"/> normal segments.
    /// So the array is reordered to [first 5 normal segments][other normal segments][legendaries],
    /// then Num and Max are set to the kept count. Lowering Max is safe: UE frees and reallocates
    /// by pointer. The removed entries stay in the old buffer (for <see cref="RestoreRoom"/>) and in
    /// the backup (for <see cref="PutBackSegments"/> once the game reallocated).
    /// </summary>
    public (CommitResult Result, RoomBackup? Backup) MakeRoom(PrismData p)
    {
        string? stale = _scanner.CheckStale(p);
        if (stale != null) return (Fail(stale + " Rescan and try again."), null);

        int num = p.Segments.Count;
        int max = _scanner.ReadSegmentCapacity(p);
        if (max != num) return (Fail($"This prism already has spare room (Num {num}, Max {max})."), null);
        if (!p.Segments.Any(s => s.Def.Kind == SegmentKind.Legendary))
            return (Fail("Making room needs a legendary segment: the game only grows the array for the legendary pick."), null);

        int stride = (int)GameOffsets.Seg_Stride;
        var all = new byte[num * stride];
        if (!_mem.TryReadBytes(p.SegmentsAddress, all, all.Length)) return (Fail("Reading the segments failed."), null);

        var normal = Enumerable.Range(0, num).Where(i => p.Segments[i].Def.Kind != SegmentKind.Legendary).ToList();
        var legendary = Enumerable.Range(0, num).Where(i => p.Segments[i].Def.Kind == SegmentKind.Legendary).ToList();
        int keep = Math.Min(GameSegmentLimit, normal.Count);
        var order = normal.Concat(legendary).ToList();
        var reordered = order.SelectMany(i => all.Skip(i * stride).Take(stride)).ToArray();
        var tail = reordered[(keep * stride)..];
        int extras = normal.Count - keep;

        Log.Info($"Make room on '{p.Name}': keep {keep}, take off {extras} extra segment(s) and {legendary.Count} legendary; " +
                 $"Num/Max {num}/{max} -> {keep}/{keep}. Removed entries: {Convert.ToHexString(tail)}");

        if (!_mem.WriteBytes(p.SegmentsAddress, reordered, "segment order"))
            return (Fail("Reordering the segments failed."), null);
        var header = BitConverter.GetBytes(keep).Concat(BitConverter.GetBytes(keep)).ToArray();
        if (!_mem.WriteBytes(p.DataAddress + GameOffsets.Data_SegCount, header, "segment Num/Max"))
            return (Fail("Lowering the segment count failed. The segments were only reordered."), null);

        if (_mem.ReadInt32(p.DataAddress + GameOffsets.Data_SegCount) != keep || _scanner.ReadSegmentCapacity(p) != keep)
            return (Fail("The segment count doesn't read back. Rescan and check the prism."), null);
        return (new CommitResult(2, Array.Empty<string>(), Array.Empty<string>()),
                new RoomBackup(p.SegmentsAddress, num, max, keep, tail, extras));
    }

    /// Undoes <see cref="MakeRoom"/> while the game hasn't reallocated the array yet.
    public CommitResult RestoreRoom(PrismData p, RoomBackup b)
    {
        string? stale = _scanner.CheckStale(p);
        if (stale != null) return Fail(stale + " Rescan and try again.");
        if (p.SegmentsAddress != b.SegmentsAddress || p.Segments.Count != b.Keep)
            return Fail("The game changed this prism's segments since (it probably added the new legendary). Nothing to restore.");

        var slots = new byte[b.Tail.Length];
        ulong addr = p.SegmentsAddress + (ulong)b.Keep * GameOffsets.Seg_Stride;
        if (!_mem.TryReadBytes(addr, slots, slots.Length) || !SameRows(slots, b.Tail))
            return Fail("The removed segments' slots were overwritten. Nothing restored.");

        Log.Info($"Restore room on '{p.Name}': Num/Max -> {b.OldNum}/{b.OldMax}");
        var header = BitConverter.GetBytes(b.OldNum).Concat(BitConverter.GetBytes(b.OldMax)).ToArray();
        if (!_mem.WriteBytes(p.DataAddress + GameOffsets.Data_SegCount, header, "segment Num/Max"))
            return Fail("Writing the segment count failed.");
        return new CommitResult(1, Array.Empty<string>(), Array.Empty<string>());
    }

    /// <summary>
    /// After the game reallocated the array (new legendary picked), appends the normal segments
    /// that <see cref="MakeRoom"/> took off into the new spare room. Returns how many were put back.
    /// </summary>
    public (CommitResult Result, int PutBack) PutBackSegments(PrismData p, RoomBackup b)
    {
        string? stale = _scanner.CheckStale(p);
        if (stale != null) return (Fail(stale + " Rescan and try again."), 0);
        if (b.Extras == 0) return (new CommitResult(0, Array.Empty<string>(), Array.Empty<string>()), 0);

        int num = p.Segments.Count;
        int max = _scanner.ReadSegmentCapacity(p);
        int fit = Math.Min(b.Extras, Math.Max(0, max - num));
        if (fit == 0) return (Fail($"No spare room to put the {b.Extras} segment(s) back (Num {num}, Max {max})."), 0);

        var entries = b.Tail[..(fit * (int)GameOffsets.Seg_Stride)];
        ulong slot = p.SegmentsAddress + (ulong)num * GameOffsets.Seg_Stride;
        Log.Info($"Put back {fit} segment(s) on '{p.Name}' at slot {num} (Max {max}).");
        if (!_mem.WriteBytes(slot, entries, "put back segments")) return (Fail("Writing the segments back failed."), 0);
        if (!_mem.WriteInt32(p.DataAddress + GameOffsets.Data_SegCount, num + fit)) return (Fail("Raising the segment count failed."), 0);
        if (_mem.ReadInt32(p.DataAddress + GameOffsets.Data_SegCount) != num + fit)
            return (Fail("The segment count doesn't read back. Rescan and check the prism."), 0);

        var notes = fit < b.Extras ? new[] { $"Only {fit} of {b.Extras} segments fit; the rest are in the log." } : Array.Empty<string>();
        return (new CommitResult(2, Array.Empty<string>(), notes), fit);
    }

    /// Compares row name and level of each entry; the action handle at +0x0C may change meanwhile.
    private static bool SameRows(byte[] a, byte[] b)
    {
        int stride = (int)GameOffsets.Seg_Stride;
        if (a.Length != b.Length) return false;
        for (int o = 0; o < a.Length; o += stride)
            if (!a.AsSpan(o, 0x0C).SequenceEqual(b.AsSpan(o, 0x0C))) return false;
        return true;
    }

    /// <summary>
    /// Removes one segment and closes the gap. Lowering Num is always safe: the buffer stays the
    /// game's, and the freed slots become spare room for Add segment in this session.
    /// </summary>
    public CommitResult RemoveSegment(PrismData p, int index)
    {
        string? stale = _scanner.CheckStale(p);
        if (stale != null) return Fail(stale + " Rescan and try again.");
        int num = p.Segments.Count;
        if (index < 0 || index >= num) return Fail("That segment no longer exists. Rescan and try again.");

        var notes = new List<string>();
        byte[] entry = _mem.ReadBytes(p.Segments[index].Address, (int)GameOffsets.Seg_Stride);
        if (BitConverter.ToInt32(entry, (int)GameOffsets.Seg_Action) != -1)
            notes.Add("That legendary had an active ability. Unequip and re-equip the prism, or reload the character, so the game drops it.");

        var order = Enumerable.Range(0, num).Where(i => i != index).ToList();
        Log.Info($"Remove segment {index + 1} of '{p.Name}' ('{p.Segments[index].Def.Row}' Lv {p.Segments[index].Level}): Num {num} -> {num - 1}.");
        string? problem = Rearrange(p.SegmentsAddress, p.DataAddress + GameOffsets.Data_SegCount, num, (int)GameOffsets.Seg_Stride, order, "segments");
        return problem == null ? new CommitResult(2, Array.Empty<string>(), notes) : Fail(problem);
    }

    /// Swaps segment <paramref name="index"/> with its neighbour (<paramref name="delta"/> = -1 or +1).
    public CommitResult MoveSegment(PrismData p, int index, int delta)
    {
        string? stale = _scanner.CheckStale(p);
        if (stale != null) return Fail(stale + " Rescan and try again.");
        int num = p.Segments.Count, other = index + delta;
        if (index < 0 || index >= num || other < 0 || other >= num) return Fail("The segment can't move further.");

        var order = Enumerable.Range(0, num).ToList();
        (order[index], order[other]) = (order[other], order[index]);
        Log.Info($"Move segment {index + 1} of '{p.Name}' to position {other + 1}.");
        string? problem = Rearrange(p.SegmentsAddress, p.DataAddress + GameOffsets.Data_SegCount, num, (int)GameOffsets.Seg_Stride, order, "segments");
        return problem == null ? new CommitResult(2, Array.Empty<string>(), Array.Empty<string>()) : Fail(problem);
    }

    /// Removes one fed fragment and closes the gap (same method as <see cref="RemoveSegment"/>).
    public CommitResult RemoveFeed(PrismData p, int index)
    {
        string? stale = _scanner.CheckStale(p);
        if (stale != null) return Fail(stale + " Rescan and try again.");
        int num = p.Feeds.Count;
        if (index < 0 || index >= num) return Fail("That fed fragment no longer exists. Rescan and try again.");

        var order = Enumerable.Range(0, num).Where(i => i != index).ToList();
        Log.Info($"Remove fed fragment {index + 1} of '{p.Name}' ('{p.Feeds[index].Def.Row}' Lv {p.Feeds[index].Level}): Num {num} -> {num - 1}.");
        string? problem = Rearrange(p.FeedAddress, p.DataAddress + GameOffsets.Data_FeedCount, num, (int)GameOffsets.Feed_Stride, order, "fed fragments");
        return problem == null ? new CommitResult(2, Array.Empty<string>(), Array.Empty<string>()) : Fail(problem);
    }

    /// <summary>
    /// Returns the prism to the state of one that was never levelled: no segments, no fed
    /// fragments, no pending XP, internal level 0 (what untouched prisms read in every diagnostic).
    /// The arrays keep their buffers, so their old capacity is spare room for Add segment.
    /// </summary>
    public CommitResult ResetPrism(PrismData p)
    {
        string? stale = _scanner.CheckStale(p);
        if (stale != null) return Fail(stale + " Rescan and try again.");

        var notes = new List<string>();
        int stride = (int)GameOffsets.Seg_Stride;
        if (p.Segments.Count > 0)
        {
            byte[] all = _mem.ReadBytes(p.SegmentsAddress, p.Segments.Count * stride);
            Log.Info($"Reset '{p.Name}': segments before reset: {Convert.ToHexString(all)}");
            for (int o = 0; o < all.Length; o += stride)
                if (BitConverter.ToInt32(all, o + (int)GameOffsets.Seg_Action) != -1)
                {
                    notes.Add("A legendary with an active ability was removed. Unequip and re-equip the prism, or reload the character, so the game drops it.");
                    break;
                }
        }
        Log.Info($"Reset '{p.Name}': Num {p.Segments.Count} segments, {p.Feeds.Count} fed, XP {p.Xp}, internal level {p.InternalLevel} -> all 0.");

        var problems = new List<string>();
        if (!_mem.WriteInt32(p.DataAddress + GameOffsets.Data_SegCount, 0)) problems.Add("Clearing the segments failed.");
        if (!_mem.WriteInt32(p.DataAddress + GameOffsets.Data_FeedCount, 0)) problems.Add("Clearing the fed fragments failed.");
        if (!_mem.WriteFloat(p.DataAddress + GameOffsets.Data_Xp, 0)) problems.Add("Clearing the XP failed.");
        if (!_mem.WriteInt32(p.DataAddress + GameOffsets.Data_Level, 0)) problems.Add("Clearing the internal level failed.");
        if (problems.Count > 0) return new CommitResult(0, problems, notes);

        if (_mem.ReadInt32(p.DataAddress + GameOffsets.Data_SegCount) != 0 || _mem.ReadInt32(p.DataAddress + GameOffsets.Data_FeedCount) != 0
            || _mem.ReadInt32(p.DataAddress + GameOffsets.Data_Level) != 0 || _mem.ReadFloat(p.DataAddress + GameOffsets.Data_Xp) != 0)
            return Fail("The reset doesn't read back. Rescan and check the prism.");
        return new CommitResult(4, Array.Empty<string>(), notes);
    }

    /// <summary>
    /// Rewrites a TArray so it holds the old entries in <paramref name="order"/> (a subset or
    /// permutation of 0..num-1). The game sees a valid prefix at every step: Num first drops to the
    /// first position that changes, then the entries behind it are written, then Num is set.
    /// Returns a problem, or null.
    /// </summary>
    private string? Rearrange(ulong data, ulong numAddr, int num, int stride, List<int> order, string what)
    {
        var all = new byte[num * stride];
        if (!_mem.TryReadBytes(data, all, all.Length)) return $"Reading the {what} failed.";
        Log.Info($"  {what} before: {Convert.ToHexString(all)}");

        int first = 0;
        while (first < order.Count && order[first] == first) first++;
        int newNum = order.Count;
        if (first == newNum && newNum == num) return null;

        var tail = order.Skip(first).SelectMany(i => all.Skip(i * stride).Take(stride)).ToArray();
        if (first < num && !_mem.WriteInt32(numAddr, first)) return $"Lowering the {what} count failed.";
        // On a failed write Num stays at the unchanged prefix: fewer entries, but all of them valid.
        if (tail.Length > 0 && !_mem.WriteBytes(data + (ulong)(first * stride), tail, what))
            return $"Writing the {what} failed. The prism keeps only its first {first}; the full list is in the log.";
        if (!_mem.WriteInt32(numAddr, newNum)) return $"Setting the {what} count failed. Rescan and check the prism.";

        // Row name and level only; the game may change a legendary's action handle meanwhile.
        var check = new byte[tail.Length];
        int key = Math.Min(stride, 0x0C);
        bool same = tail.Length == 0 || _mem.TryReadBytes(data + (ulong)(first * stride), check, check.Length);
        for (int o = 0; same && o < tail.Length; o += stride)
            same = check.AsSpan(o, key).SequenceEqual(tail.AsSpan(o, key));
        if (!same || _mem.ReadInt32(numAddr) != newNum)
            return $"The {what} don't read back. Rescan and check the prism.";
        return null;
    }

    private static CommitResult Fail(string problem)
    {
        Log.Warn(problem);
        return new CommitResult(0, new[] { problem }, Array.Empty<string>());
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
