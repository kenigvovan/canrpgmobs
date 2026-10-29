using System;
using System.Collections.Generic;

namespace canrpgmobs.Data
{
    // Reach is the item's attack range, used both for approach distance and swing range.
    public readonly record struct WeaponStats(string Code, float AttackPower, int Tier, float Reach = StatMath.BareHandReach);

    public sealed class MobStats
    {
        public int Level { get; init; } = 1;
        public string TierId { get; init; } = StatMath.DefaultTier;
        public float MaxHealth { get; init; }
        public float MeleeDamage { get; init; }
        public int MeleeTier { get; init; }
        public string? Weapon { get; init; }
        // blocks between bodies, the mob also stops this far short when walking up
        public float MeleeReach { get; init; } = StatMath.BareHandReach;
        // also applied to bow and arrow damage
        public float DamageFactor { get; init; } = 1f;
        public float SpellPower { get; init; } = 1f;
        public float HealingPower { get; init; } = 1f;
        public float DamageReduction { get; init; }
        public float DamageTaken { get; init; } = 1f;
        public float SizeMul { get; init; } = 1f;
        public IReadOnlyDictionary<string, float> Resists { get; init; } = new Dictionary<string, float>();
    }

    public static class StatMath
    {
        public const string DefaultTier = "canrpgmobs:normal";
        public const float MaxResist = 0.9f;
        public const float MaxDamageReduction = 0.9f;
        // vanilla bare hand range
        public const float BareHandReach = 1.5f;
        // read by the core as canrpgResist_<key>, "magic" adds to every magic school
        public static readonly string[] ResistKeys = { "magic", "physical", "fire", "frost", "arcane", "holy", "nature", "shadow", "fel" };

        // Weapon attack power replaces the race meleeDamage, but a template's own meleeDamage wins.
        public static MobStats Compute(ResolvedMob mob, int level, TierModel? tier, ScalingModel? scaling, WeaponStats? weapon = null)
        {
            scaling ??= new ScalingModel();
            if (mob.MeleeOverridden) weapon = null;
            float baseMelee = weapon is { } held ? held.AttackPower * mob.MeleeMul + mob.MeleeAdd : mob.MeleeDamage;
            int finalLevel = Math.Clamp(level, 1, Math.Max(1, scaling.maxLevel));
            int above = finalLevel - 1;

            var resists = new Dictionary<string, float>(StringComparer.Ordinal);
            foreach (var pair in mob.Resists) resists[pair.Key] = pair.Value;
            if (scaling.resistPerLevel > 0f && above > 0)
                resists["magic"] = (resists.TryGetValue("magic", out var magic) ? magic : 0f) + scaling.resistPerLevel * above;
            foreach (var key in ResistKeys)
                if (resists.TryGetValue(key, out var value)) resists[key] = Math.Clamp(value, 0f, MaxResist);

            float spellFactor = Factor(scaling.spellPowerPerLevel, above) * (tier?.spellPower ?? 1f);
            return new MobStats
            {
                Level = finalLevel,
                TierId = tier?.id ?? DefaultTier,
                MaxHealth = Math.Max(1f, mob.MaxHealth * Factor(scaling.hpPerLevel, above) * (tier?.hp ?? 1f)),
                MeleeDamage = Math.Max(0f, baseMelee * Factor(scaling.damagePerLevel, above) * (tier?.damage ?? 1f)),
                // weak weapon never lowers the tier
                MeleeTier = weapon is { } tiered ? Math.Max(mob.MeleeTier, tiered.Tier) : mob.MeleeTier,
                Weapon = weapon?.Code,
                MeleeReach = mob.Ai.melee?.reach > 0f ? mob.Ai.melee!.reach : weapon?.Reach ?? BareHandReach,
                DamageFactor = Factor(scaling.damagePerLevel, above) * (tier?.damage ?? 1f),
                SpellPower = Math.Max(0.1f, mob.SpellPower * spellFactor),
                HealingPower = Math.Max(0f, mob.HealingPower * spellFactor),
                DamageReduction = Math.Clamp(mob.DamageReduction, 0f, MaxDamageReduction),
                DamageTaken = Math.Max(0f, tier?.damageTaken ?? 1f),
                SizeMul = tier?.size ?? 1f,
                Resists = resists
            };
        }

        public static float Factor(float perLevel, int levelsAbove) => 1f + Math.Max(0f, perLevel) * Math.Max(0, levelsAbove);
    }
}
