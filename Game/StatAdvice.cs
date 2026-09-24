namespace R2PrismRuntime.Game;

/// <summary>
/// Soft warnings for character stat targets. Nothing here blocks a value; the rules come from
/// the caps the game exposes as stats and from how the stat kinds behave (see DOCUMENTATION 5b).
/// None of the thresholds are verified in-game.
/// </summary>
public static class StatAdvice
{
    // Stats that describe progression rather than power; they likely feed enemy scaling.
    private static readonly HashSet<string> Progression = new(StringComparer.OrdinalIgnoreCase)
    {
        "Level", "Value", "ExperienceValue", "Grit", "Skill", "Spirit", "Vigor",
        "ItemLevel", "PowerLevel", "ArchetypeLevel", "CombinedArchetypeLevel",
    };

    /// <param name="capOf">Returns the live value of a cap stat by name, or null if there is none.</param>
    public static string? Check(string name, float target, Func<string, float?> capOf)
    {
        if (Progression.Contains(name))
            return "Progression value. It probably feeds enemy scaling and matchmaking; holding it isn't recommended.";

        // A matching "<Name>Cap" stat limits this one.
        if (!name.EndsWith("Cap", StringComparison.Ordinal) && capOf(name + "Cap") is float cap && target > cap)
            return $"The game caps this at {cap:0.###} ({name}Cap). Hold the cap as well.";

        if (name.Contains("Chance", StringComparison.Ordinal) && target > 100)
            return "Above 100 has no further effect.";

        if ((name.Contains("Resistance", StringComparison.Ordinal) || name.StartsWith("DamageReduction", StringComparison.Ordinal)
             || name.EndsWith("DamageReduction", StringComparison.Ordinal)) && target >= 100)
            return "100 or more may zero out or invert damage taken. Try 90 first.";

        if (name.Contains("Cooldown", StringComparison.Ordinal) && name.EndsWith("Mod", StringComparison.Ordinal) && target < -100)
            return "Below -100 makes cooldowns negative.";

        if (name is "HealthMax" or "StaminaMax" && target <= 0)
            return "0 or less kills the character or disables stamina.";

        if (IsMultiplier(name))
        {
            if (target <= 0) return "Multiplier at 0 or below can freeze or break the character.";
            if (target > 5) return "Multipliers above 5 often cause animation or physics glitches.";
        }
        return null;
    }

    /// Stats with a neutral value of 1 (speed and scalar multipliers).
    private static bool IsMultiplier(string n)
        => n.EndsWith("Scalar", StringComparison.Ordinal)
        || (n.EndsWith("Speed", StringComparison.Ordinal) && !n.EndsWith("ReviveSpeed", StringComparison.Ordinal));
}
