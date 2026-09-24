using R2PrismRuntime.Diagnostics;
using R2PrismRuntime.Memory;

namespace R2PrismRuntime.Game;

/// <summary>One live row of the character's StatsComponent list.</summary>
public readonly record struct StatRow(int NameId, string Name, ulong Address, float Value);

/// <summary>
/// Reads the computed character stats (CT "Manage System Statistics"):
///   [Pawn+0x648] StatsComponent → +0x128 TArray Data, +0x130 Num; element stride 0x14:
///   +0x00 FName id, +0x08 float value.
/// </summary>
public sealed class CharacterStats
{
    public const ulong Pawn_StatsComp = 0x648;
    public const ulong Stats_Data = 0x128, Stats_Num = 0x130;
    public const ulong Row_Stride = 0x14, Row_Value = 0x08;

    private readonly ProcessMemory _mem;
    private readonly FNameReader _names;

    public CharacterStats(ProcessMemory mem, FNameReader names)
    {
        _mem = mem;
        _names = names;
    }

    public List<StatRow> Read(ulong pawn)
    {
        var rows = new List<StatRow>();
        if (pawn == 0) return rows;
        ulong comp = _mem.ReadPointer(pawn + Pawn_StatsComp);
        if (!ProcessMemory.LooksLikePointer(comp)) return rows;
        ulong data = _mem.ReadPointer(comp + Stats_Data);
        int n = _mem.ReadInt32(comp + Stats_Num);
        if (!ProcessMemory.LooksLikePointer(data) || n is <= 0 or > 4000) return rows;

        var buf = new byte[n * (int)Row_Stride];
        if (!_mem.TryReadBytes(data, buf, buf.Length)) return rows;
        for (int i = 0; i < n; i++)
        {
            int off = i * (int)Row_Stride;
            int id = BitConverter.ToInt32(buf, off);
            if (id <= 0) continue;
            rows.Add(new StatRow(id, _names.Resolve(id), data + (ulong)off, BitConverter.ToSingle(buf, off + (int)Row_Value)));
        }
        return rows;
    }

    public bool WriteValue(ulong rowAddress, float value, bool quiet)
        => quiet ? _mem.WriteQuiet(rowAddress + Row_Value, BitConverter.GetBytes(value))
                 : _mem.WriteFloat(rowAddress + Row_Value, value);

    /// Rough grouping for the filter chips.
    public static string Category(string n)
    {
        bool Has(params string[] parts) => parts.Any(p => n.Contains(p, StringComparison.OrdinalIgnoreCase));
        if (n.EndsWith("Cap", StringComparison.Ordinal)) return "Caps";
        if (Has("Resist", "Armor", "Health", "Heal", "DamageReduction", "DamageTaken", "Grey", "Shield", "Revive"))
            return "Defense";
        if (Has("Damage", "Crit", "WeakSpot", "Proc", "Recoil", "Accuracy", "Sway", "Range", "FireSpeed", "Reload", "Ammo",
                "Impact", "LongGun", "HandGun", "MeleeWeapon"))
            return "Offense";
        if (Has("Speed", "Move", "Evade", "Stamina", "Traversal", "Vault", "Glide", "Encumbrance", "Weight", "EquipLoad", "Windup"))
            return "Mobility";
        if (Has("Skill", "Nerud", "Summoner", "Casting", "Duration")) return "Skills";
        return "Other";
    }
}
