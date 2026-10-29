using System;
using canrpgclasses.Core.Control;
using canrpgmobs.Data;
using canrpgmobs.Runtime.Behaviors;
using canrpgmobs.Runtime.Entities;
using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;

namespace canrpgmobs.Runtime.Scaling
{
    // canrpgclasses CC hook (API level 5): tier and phase scale CC duration, phases can grant immunity.
    public static class MobControlHooks
    {
        public static float Modify(Entity target, ControlType type, float seconds, Entity? source)
        {
            if (target is not EntityRpgMob || target.Api.Side != EnumAppSide.Server) return seconds;
            var mob = target.GetBehavior<EBRpgMob>();
            var catalog = canrpgmobsModSystem.For(target.Api)?.Catalog;
            if (mob?.ScaledStats == null || catalog == null) return seconds;

            float mul = catalog.Tier(mob.ScaledStats.TierId)?.ccDurationMul ?? 1f;
            if (mob.CurrentPhase is { } phase)
            {
                if (phase.ccImmune != null && phase.ccImmune.Contains(Name(type))) return 0f;
                mul *= phase.ccDurationMul;
            }
            return float.IsFinite(mul) ? seconds * Math.Max(0f, mul) : seconds;
        }

        private static string Name(ControlType type) => type switch
        {
            ControlType.Stun => ControlNames.Stun,
            ControlType.Fear => ControlNames.Fear,
            ControlType.Silence => ControlNames.Silence,
            ControlType.Root => ControlNames.Root,
            _ => ControlNames.Transmute
        };
    }
}
