using System;
using System.Linq;
using canrpgclasses.Core;
using canrpgclasses.Core.Spells;
using canrpgmobs.Data;
using Vintagestory.API.Common.Entities;
using Vintagestory.GameContent;

namespace canrpgmobs.Runtime.Scaling
{
    // Single stat source, so reapplying replaces instead of stacking.
    public static class MobStatApplier
    {
        public const string Source = "canrpgmobs:scaled";

        internal static readonly string[] SpellPowerKeys = Enum.GetValues(typeof(SpellSchool)).Cast<SpellSchool>().Select(StatKeys.SpellpowerFor).ToArray();

        public static void Apply(Entity entity, MobStats stats, bool fullHeal)
        {
            var health = entity.GetBehavior<EntityBehaviorHealth>();
            if (health != null)
            {
                // MaxHealth is recomputed by the behavior on every hit, set the base
                float fraction = health.MaxHealth > 0f ? Math.Clamp(health.Health / health.MaxHealth, 0f, 1f) : 1f;
                health.BaseMaxHealth = stats.MaxHealth;
                health.MarkDirty();
                health.Health = (fullHeal ? 1f : fraction) * health.MaxHealth;
            }

            // spell/healing power are 1-based (unset blends to 1); DR and resists are read as blended - 1
            foreach (var key in SpellPowerKeys) entity.Stats.Set(key, Source, stats.SpellPower - 1f);
            entity.Stats.Set(StatKeys.HealingPower, Source, stats.HealingPower - 1f);
            entity.Stats.Set(StatKeys.DamageReduction, Source, stats.DamageReduction);
            foreach (var key in StatMath.ResistKeys)
                entity.Stats.Set("canrpgResist_" + key, Source, stats.Resists.TryGetValue(key, out var value) ? value : 0f);
        }
    }
}
