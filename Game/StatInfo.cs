namespace R2PrismRuntime.Game;

/// <summary>
/// Short explanations for the character stats shown on the Attributes page.
/// "Mod" stats are percentages (40 = +40 %), "Scalar"/speed stats are multipliers (1 = normal).
/// Texts marked "(likely)" are inferred from the stat name and how the game uses similar stats;
/// they are not verified in game.
/// </summary>
public static class StatInfo
{
    private static readonly Dictionary<string, string> Texts = new(StringComparer.OrdinalIgnoreCase)
    {
        // ── N'Erud energy ────────────────────────────────────────
        ["MaxNerudEnergy"] = "Maximum N'Erud energy (used by N'Erud gear and effects).",
        ["NerudEnergyRegen"] = "N'Erud energy regenerated per second.",
        ["NerudEnergyGainMod"] = "% more N'Erud energy gained.",
        ["NerudEnergyConsumptionMod"] = "% change to N'Erud energy consumption. Negative = uses less.",

        // ── Caps ─────────────────────────────────────────────────
        ["VigorCap"] = "Upper limit for Vigor (legacy attribute).",
        ["HealthMaxCap"] = "Upper limit for maximum health.",
        ["HealthMaxModCap"] = "Upper limit for the % health bonus.",
        ["MoveSpeedCap"] = "Upper limit for the movement speed multiplier. Raise it together with Move Speed.",
        ["SprintMoveSpeedCap"] = "Upper limit for sprint speed.",
        ["CrouchMoveSpeedCap"] = "Upper limit for crouch movement speed.",
        ["AimMoveSpeedCap"] = "Upper limit for movement speed while aiming.",
        ["WoundedMoveSpeedCap"] = "Upper limit for movement speed while downed.",
        ["HeavyCarryMoveSpeedCap"] = "Upper limit for movement speed while carrying a heavy object.",
        ["HeavyCarrySprintMoveSpeedCap"] = "Upper limit for sprint speed while carrying a heavy object.",
        ["HeavyCarryAimMoveSpeedCap"] = "Upper limit for aim movement speed while carrying a heavy object.",
        ["WadeMoveSpeedCap"] = "Upper limit for movement speed in water or deep terrain.",
        ["ConsumableMoveSpeedCap"] = "Upper limit for movement speed while using a consumable.",
        ["WindupTimeModCap"] = "Limit for charge/wind-up time reduction (-80 = at most 80 % faster).",

        // ── Legacy / progression ─────────────────────────────────
        ["Level"] = "Internal level value. Progression stat, don't hold it.",
        ["Value"] = "Generic internal value (likely unused).",
        ["Grit"] = "Legacy attribute from Remnant: From the Ashes (likely unused).",
        ["Skill"] = "Legacy attribute (likely unused).",
        ["Spirit"] = "Legacy attribute (likely unused).",
        ["Vigor"] = "Legacy attribute (likely unused).",
        ["ExperienceValue"] = "Internal experience value (likely unused).",
        ["Duration"] = "Generic duration value (likely unused).",
        ["ItemLevel"] = "Combined item level of your gear. Feeds world difficulty scaling; don't hold it.",
        ["ArchetypeLevel"] = "Level of your current archetype.",
        ["CombinedArchetypeLevel"] = "Sum of your archetype levels. Progression stat, don't hold it.",
        ["PowerLevel"] = "Overall power level. Likely used for enemy scaling; don't hold it.",
        ["ExperienceMod"] = "% more experience gained.",

        // ── Health, stamina, healing ─────────────────────────────
        ["HealthMax"] = "Maximum health (before % bonuses).",
        ["HealthMaxMod"] = "% bonus to maximum health.",
        ["HealthRegen"] = "Health regenerated per second.",
        ["StaminaMax"] = "Maximum stamina (before % bonuses).",
        ["StaminaMaxMod"] = "% bonus to maximum stamina.",
        ["StaminaRegen"] = "Stamina regenerated per second.",
        ["StaminaRegenDelayScalar"] = "Multiplier for the pause before stamina starts regenerating. Lower = faster.",
        ["StaminaEmptyDelayScalar"] = "Multiplier for the pause after stamina runs out completely. Lower = faster.",
        ["StaminaPenaltyReductionMod"] = "% reduction of stamina penalties (e.g. from armor weight).",
        ["StaminaDeltaMod"] = "% change to stamina costs. Negative = actions cost less stamina.",
        ["NoAggroStaminaRegen"] = "Stamina regeneration while out of combat (likely).",
        ["NoAggroStaminaDeltaMod"] = "% change to stamina costs while out of combat (likely).",
        ["DisplayStaminaRegen"] = "Display copy: stamina regeneration shown on the character screen.",
        ["DisplayStaminaDeltaMod"] = "Display copy: stamina cost change shown on the character screen.",
        ["HealingMod"] = "% more healing received.",
        ["HealthStealEfficacyMod"] = "% more health gained from lifesteal effects.",
        ["FriendlyHealMod"] = "% more healing applied to allies.",
        ["DisplayFriendlyHealMod"] = "Display copy: ally healing bonus shown on the character screen.",
        ["GreyHealthRate"] = "Share of damage turned into grey health (0.5 = 50 %).",
        ["GreyHealthRateMod"] = "% change to how much damage becomes grey health.",
        ["GreyHealthRegen"] = "How fast grey health turns back into health.",
        ["GreyHealthRegenMod"] = "% change to grey health recovery speed.",
        ["ForcedGreyHealthPercentage"] = "Forces a share of health to be grey health (set by some effects).",
        ["DragonHeartMax"] = "Maximum Dragon Heart charges.",
        ["DragonHeartUseSpeed"] = "Dragon Heart use speed multiplier.",
        ["DragonHeartUseSpeedMod"] = "% faster Dragon Heart use.",
        ["DragonHeartHealthRegenMod"] = "% more healing from the Dragon Heart.",

        // ── Downed / revive ──────────────────────────────────────
        ["WoundedReviveSpeed"] = "% faster reviving of downed allies.",
        ["WoundedReviveSpeedMod"] = "% bonus to revive speed.",
        ["WoundedHealthMax"] = "Health pool while downed (bleed-out time).",
        ["WoundedHealthRegen"] = "Health change per second while downed. Negative = bleeding out.",
        ["ReviveAnimSpeed"] = "Revive animation speed multiplier.",

        // ── Damage ───────────────────────────────────────────────
        ["Damage"] = "Flat bonus damage (likely).",
        ["DamageMod"] = "% bonus to all damage dealt.",
        ["RangedDamageMod"] = "% bonus to ranged (firearm) damage.",
        ["MeleeDamageMod"] = "% bonus to melee damage.",
        ["ModDamageMod"] = "% bonus to weapon mod damage.",
        ["PhysicalDamageMod"] = "% bonus to physical damage.",
        ["FireDamageMod"] = "% bonus to fire (burning) damage.",
        ["ShockDamageMod"] = "% bonus to shock (overloaded) damage.",
        ["CorrosiveDamageMod"] = "% bonus to corrosive damage.",
        ["RadiationDamageMod"] = "% bonus to radiation damage (likely unused in Remnant II).",
        ["RootDamageMod"] = "% bonus to root damage (likely unused in Remnant II).",
        ["BleedDamageMod"] = "% bonus to bleed damage.",
        ["CritDamageMod"] = "% bonus damage on critical hits.",
        ["CritChance"] = "% chance to land a critical hit.",
        ["RangedCritChance"] = "% critical chance for ranged attacks.",
        ["BleedCritChance"] = "% critical chance for bleed damage (likely).",
        ["WeakSpotDamageMod"] = "% bonus damage on weak spot hits.",
        ["WeakSpotDamageScalar"] = "Multiplier for weak spot damage.",
        ["ProcChance"] = "% chance for 'on hit' effects to trigger.",
        ["StatusEffectBuildupScalar"] = "Multiplier for status effect buildup you cause (likely).",
        ["EnemyStatusEffectDurationScalar"] = "Multiplier for how long your status effects last on enemies.",
        ["ImpactScalar"] = "Multiplier for stagger/impact your hits deal.",
        ["DisplayImpactScalar"] = "Display copy: impact value shown on the character screen.",
        ["FriendlyFireDamageTakenMod"] = "% change to damage taken from allies.",
        ["SelfDamageTakenMod"] = "% change to damage you deal to yourself (e.g. explosions).",

        // ── Weapons ──────────────────────────────────────────────
        ["FireSpeed"] = "Fire rate multiplier.",
        ["FireSpeedMod"] = "% faster fire rate.",
        ["ReloadSpeed"] = "Reload speed multiplier.",
        ["ReloadSpeedMod"] = "% faster reloading.",
        ["WeaponEquipSpeed"] = "Weapon swap speed multiplier.",
        ["WeaponHandlingMod"] = "% better weapon handling (likely swap and aim speed).",
        ["WeaponRecoilMod"] = "% change to recoil. Negative = less recoil.",
        ["WeaponAccuracyMod"] = "% change to weapon spread. Negative = tighter spread.",
        ["DisplayWeaponAccuracyMod"] = "Display copy: spread change shown on the character screen.",
        ["WeaponSwayMod"] = "% change to weapon sway while aiming.",
        ["WeaponSwayScalar"] = "Multiplier for weapon sway while aiming.",
        ["WeaponEffectiveRangeMod"] = "% change to ideal range (full damage before falloff).",
        ["WeaponEffectiveRangeBonus"] = "Flat bonus to ideal range, in game units (100 ≈ 1 m, likely).",
        ["WindupSpeed"] = "Charge/wind-up speed multiplier (charged weapons and melee).",
        ["AmmoMax"] = "Base ammo capacity value.",
        ["MaxAmmoMod"] = "% more ammo reserves.",
        ["AmmoPoolMax"] = "Reserve ammo pool size (likely primary weapons).",
        ["HeavyAmmoPoolMax"] = "Reserve ammo pool size for heavy weapons (likely).",
        ["AmmoShareMod"] = "% of ammo pickups shared with allies (likely).",
        ["LongGun"] = "Power value of your equipped long gun (likely display/scaling only).",
        ["HandGun"] = "Power value of your equipped handgun (likely display/scaling only).",
        ["MeleeWeapon"] = "Power value of your equipped melee weapon (likely display/scaling only).",

        // ── Melee ────────────────────────────────────────────────
        ["MeleeAttackSpeed"] = "Melee attack speed multiplier.",
        ["MeleeChargeSpeed"] = "Charged melee speed multiplier.",
        ["MeleeChargeCost"] = "Multiplier for the stamina cost of charged melee.",
        ["MeleeChargeCostMod"] = "% change to charged melee stamina cost.",
        ["MeleeMovementStaminaCost"] = "Stamina cost of melee attacks (likely).",
        ["TotalMeleeSpeedScalar"] = "Overall melee speed multiplier.",
        ["TotalMeleeSpeedMod"] = "% bonus to overall melee speed.",

        // ── Defense ──────────────────────────────────────────────
        ["Armor"] = "Total armor value.",
        ["ArmorMod"] = "% bonus to armor.",
        ["DamageReductionMod"] = "% damage reduction from gear and effects (not armor).",
        ["DamageReductionModArmor"] = "% damage reduction coming from armor.",
        ["DisplayDamageReductionModArmor"] = "Display copy: armor damage reduction shown on the character screen.",
        ["TotalDamageReductionMod"] = "Total % damage reduction (armor + other sources).",
        ["MeleeDamageReduction"] = "% less damage taken from melee attacks.",
        ["RangedDamageReduction"] = "% less damage taken from ranged attacks.",
        ["AllResistance"] = "Bonus to all elemental resistances.",
        ["ElementalResistance"] = "Bonus to elemental resistances.",
        ["PhysicalResistance"] = "Physical resistance.",
        ["FireResistance"] = "Fire resistance.",
        ["ShockResistance"] = "Shock resistance.",
        ["AcidResistance"] = "Acid resistance.",
        ["BleedResistance"] = "Bleed resistance.",
        ["BlightResistance"] = "Blight resistance.",
        ["CorrosiveResistance"] = "Corrosive resistance (likely legacy).",
        ["RadiationResistance"] = "Radiation resistance (likely legacy).",
        ["RootResistance"] = "Root resistance (likely legacy).",
        ["DisplayElementalFireResistance"] = "Display copy: fire resistance shown on the character screen.",
        ["DisplayElementalShockResistance"] = "Display copy: shock resistance shown on the character screen.",
        ["DisplayElementalAcidResistance"] = "Display copy: acid resistance shown on the character screen.",
        ["HitReactionSpeed"] = "Speed of recovering from being hit (flinch/stagger).",
        ["HitReactionSpeedMod"] = "% faster recovery from being hit.",
        ["HitReactionSpeedModDisplay"] = "Display copy: hit recovery bonus shown on the character screen.",

        // ── Movement ─────────────────────────────────────────────
        ["MoveSpeed"] = "Walk/run speed multiplier. Limited by Move Speed Cap.",
        ["MoveSpeedMod"] = "% bonus to movement speed.",
        ["TotalMoveSpeedScalar"] = "Overall movement speed multiplier.",
        ["TotalMoveSpeedScalarMod"] = "% bonus to overall movement speed.",
        ["TotalMoveSpeedDisplay"] = "Display copy: movement speed bonus shown on the character screen.",
        ["DisplayMoveSpeedMod"] = "Display copy: movement speed change shown on the character screen.",
        ["SprintMoveSpeed"] = "Sprint speed multiplier.",
        ["SprintMoveSpeedMod"] = "% bonus to sprint speed.",
        ["AimMoveSpeed"] = "Movement speed multiplier while aiming.",
        ["AimMoveSpeedMod"] = "% bonus to movement speed while aiming.",
        ["CrouchSpeed"] = "Crouch speed multiplier.",
        ["CrouchSpeedMod"] = "% bonus to crouch speed.",
        ["CrouchMoveSpeed"] = "Crouch movement speed multiplier.",
        ["WadeMoveSpeed"] = "Movement speed multiplier in water or deep terrain.",
        ["WadeMoveSpeedMod"] = "% bonus to movement speed in water or deep terrain.",
        ["WoundedMoveSpeed"] = "Crawl speed multiplier while downed.",
        ["ConsumableMoveSpeed"] = "Movement speed multiplier while using a consumable.",
        ["TraversalSpeedMod"] = "% faster climbing, vaulting and ladders (likely).",
        ["VaultSpeed"] = "Vault speed multiplier.",
        ["VaultSpeedMod"] = "% faster vaulting.",
        ["EvadeSpeed"] = "Dodge speed multiplier.",
        ["EvadeSpeedMod"] = "% faster dodging.",
        ["EvadeDistanceMod"] = "% longer dodge distance.",
        ["EvadeStaminaScalar"] = "Multiplier for the stamina cost of dodging.",
        ["HeavyCarryMoveSpeed"] = "Movement speed multiplier while carrying a heavy object.",
        ["HeavyCarryMoveSpeedMod"] = "% bonus to movement speed while carrying a heavy object.",
        ["HeavyCarryAimMoveSpeed"] = "Aim movement speed while carrying a heavy object.",
        ["HeavyCarryAimMoveSpeedMod"] = "% bonus to aim movement speed while carrying a heavy object.",
        ["HeavyCarrySprintMoveSpeed"] = "Sprint speed while carrying a heavy object.",
        ["HeavyCarrySprintMoveSpeedMod"] = "% bonus to sprint speed while carrying a heavy object.",
        ["HeavyCarryEvadeSpeed"] = "Dodge speed while carrying a heavy object.",
        ["HeavyCarryEvadeSpeedMod"] = "% faster dodging while carrying a heavy object.",
        ["HeavyCarryEvadeDistanceMod"] = "% longer dodges while carrying a heavy object.",
        ["GlideStaminaMax"] = "Stamina pool for gliding (likely used by specific gear only).",
        ["GlideStaminaRegen"] = "Glide stamina regeneration (likely specific gear only).",
        ["GlideStaminaRegenDelay"] = "Pause before glide stamina regenerates (likely specific gear only).",
        ["GlideStaminaEmptyDelay"] = "Pause after glide stamina runs out (likely specific gear only).",

        // ── Weight ───────────────────────────────────────────────
        ["Encumbrance"] = "Current armor weight. Decides your weight class (light/medium/heavy).",
        ["EncumbranceMod"] = "% change to armor weight.",
        ["EquipLoad"] = "Base carry capacity before your weight class changes.",
        ["EquipLoadBonus"] = "Extra carry capacity (e.g. from Bulwark or traits).",
        ["WeightMax"] = "Maximum weight (likely legacy).",
        ["WeightMaxMod"] = "% bonus to maximum weight (likely legacy).",

        // ── Skills, mods, consumables ────────────────────────────
        ["SkillCooldownMod"] = "% change to archetype skill cooldowns. Negative = faster. Below -100 makes cooldowns negative.",
        ["SkillCooldownScalar"] = "Multiplier for skill cooldowns. Lower = faster.",
        ["SkillCastingSpeed"] = "Skill cast speed multiplier.",
        ["SkillCastingSpeedMod"] = "% faster skill casting.",
        ["ThrowSkillCastingSpeed"] = "Throw speed multiplier for thrown skills (e.g. grenades).",
        ["ThrowSkillCastingSpeedMod"] = "% faster throwing of thrown skills.",
        ["ThrowSpeedMod"] = "% faster throwing (likely).",
        ["ModCastingSpeedMod"] = "% faster weapon mod casting.",
        ["SkillChargesScalar"] = "Multiplier for skill charges.",
        ["SkillChargesMultiplier"] = "Multiplier for skill charges (second factor).",
        ["SkillChargesMod"] = "Extra skill charges.",
        ["SummonerExtraMinionCharges"] = "Extra minion charges for Summoner skills.",
        ["ConsumableUseSpeed"] = "Consumable use speed multiplier.",
        ["ConsumableUseSpeedMod"] = "% faster consumable use.",

        // ── Enemies, threat, awareness ───────────────────────────
        ["ThreatScalar"] = "Multiplier for how much enemies focus you (aggro) in co-op.",
        ["AggroBoost"] = "Extra threat; enemies are more likely to target you.",
        ["DisplayAggroBoost"] = "Display copy: threat bonus shown on the character screen.",
        ["VisMod"] = "% change to how visible you are to enemies (stealth). Negative = harder to spot (likely).",
        ["VisDetectMod"] = "% change to how quickly enemies detect you. Negative = slower detection (likely).",
        ["AwarenessMod"] = "% change to enemy awareness of you (likely stealth/detection).",
        ["FogOfWarMod"] = "% change to how much of the map is revealed around you (likely map fog radius).",
        ["AutoPickupRangeMod"] = "% larger radius for automatically picking up ammo, scrap and similar items (likely).",
    };

    /// Explanation for a stat, with a generic fallback based on the name.
    public static string Describe(string name)
    {
        if (Texts.TryGetValue(name, out var t)) return t;
        if (name.StartsWith("Display", StringComparison.Ordinal))
            return "Display copy for the character screen. Changing it has no gameplay effect (likely).";
        if (name.EndsWith("Cap", StringComparison.Ordinal)) return "Upper limit for another stat.";
        if (name.EndsWith("Mod", StringComparison.Ordinal)) return "Percentage modifier (likely).";
        if (name.EndsWith("Scalar", StringComparison.Ordinal)) return "Multiplier, 1 = normal (likely).";
        return "No description yet.";
    }
}
