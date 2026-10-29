using canrpgclasses.Core;
using canrpgmobs.Runtime.Behaviors;
using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;
using Vintagestory.API.Datastructures;
using Vintagestory.GameContent;

namespace canrpgmobs.Runtime.Ai
{
    // Triggered only by low hp or by canrpgclasses fear (InstaFleeFrom on the first AiTaskFleeEntity).
    public class AiTaskRpgFlee : AiTaskFleeEntity
    {
        public const string Code = "rpgmobflee";

        private readonly float fleeBelowHp;
        private readonly float triggerRange;
        private readonly int cooldownMs;
        private EBRpgMob? mob;
        private long nextLowHpFleeMs;

        public AiTaskRpgFlee(EntityAgent entity, JsonObject taskConfig, JsonObject aiConfig)
            : base(entity, taskConfig, aiConfig)
        {
            fleeBelowHp = taskConfig["fleeBelowHp"].AsFloat(0f);
            triggerRange = taskConfig["seekingRange"].AsFloat(20f);
            cooldownMs = taskConfig["fleeCooldownMs"].AsInt(8000);
        }

        private EBRpgMob? Mob => mob ??= entity.GetBehavior<EBRpgMob>();

        public override bool ShouldExecute()
        {
            if (Mob == null || Mob.IsLeashing) return false;

            long now = entity.World.ElapsedMilliseconds;
            if (fleeBelowHp > 0f && now >= nextLowHpFleeMs && Mob.HpFraction < fleeBelowHp)
            {
                var enemy = Mob.AcquireEnemy(triggerRange);
                if (enemy != null)
                {
                    InstaFleeFrom(enemy);
                    nextLowHpFleeMs = now + cooldownMs;
                }
            }
            // passes only after InstaFleeFrom, the entity code matches nothing
            return base.ShouldExecute();
        }

        public override void StartExecute()
        {
            base.StartExecute();
            if (Mob != null) Mob.IsFleeing = true;
            RpgCombatApi.Interrupt(entity);
        }

        public override void FinishExecute(bool cancelled)
        {
            base.FinishExecute(cancelled);
            if (Mob != null) Mob.IsFleeing = false;
        }

        public override bool CanSense(Entity e, double range) => e.Pos.Dimension == entity.Pos.Dimension && base.CanSense(e, range);
    }
}
