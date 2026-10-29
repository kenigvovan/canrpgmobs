using System;
using System.Threading;
using canrpgclasses.Core;
using canrpgclasses.Core.Spells;
using canrpgmobs.Runtime.Behaviors;
using canrpgmobs.Runtime.Entities;
using canrpgmobs.Runtime.Factions;
using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;

namespace canrpgmobs.Runtime.Scaling
{
    // Runs on every hit before canrpgclasses mitigation. Registered once per process.
    public static class MobDamageHooks
    {
        private static readonly DamageModifier Hook = OnHit;
        private static int registered;

        public static void Register()
        {
            if (Interlocked.Exchange(ref registered, 1) == 0) DamageModifiers.RegisterPersistent(Hook);
        }

        private static void OnHit(Entity victim, Entity? attacker, DamageSource source, ref float damage)
        {
            if (victim.World?.Side != EnumAppSide.Server) return;

            // vanilla projectiles only spare the shooter, not its allies
            if (attacker is EntityRpgMob && source.SourceEntity is IProjectile && victim != attacker
                && canrpgmobsModSystem.For(victim.Api)?.Catalog is { } catalog && FactionService.AreAllies(attacker, victim, catalog))
            {
                damage = 0f;
                return;
            }

            // melee swings only, not spells; ranged already uses rangedWeaponsDamage
            if (attacker is EntityRpgMob && source.SourceEntity == attacker && source.Source == EnumDamageSource.Entity && source is not CanrpgDamageSource)
                damage *= Math.Max(0f, attacker.Stats.GetBlended(StatKeys.MeleeWeaponsDamage));

            if (victim is EntityRpgMob && victim.GetBehavior<EBRpgMob>() is { ScaledStats: { } stats } mob)
            {
                damage *= stats.DamageTaken;
                if (mob.CurrentPhase is { } phase && float.IsFinite(phase.damageTakenMul) && phase.damageTakenMul >= 0f) damage *= phase.damageTakenMul;
            }
        }
    }
}
