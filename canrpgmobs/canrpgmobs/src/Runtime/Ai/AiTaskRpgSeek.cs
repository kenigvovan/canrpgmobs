using canrpgclasses.Core.Control;
using canrpgmobs.Runtime.Behaviors;
using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;
using Vintagestory.API.Datastructures;
using Vintagestory.GameContent;

namespace canrpgmobs.Runtime.Ai
{
    // Vanilla seek, but the target comes from EBRpgMob.AcquireEnemy.
    public class AiTaskRpgSeek : AiTaskSeekEntity, IOffensiveAiTask, IMovementAiTask
    {
        public const string Code = "rpgmobseek";

        private EBRpgMob? mob;
        private long nextSeekMs;
        private long nextRecheckMs;

        public AiTaskRpgSeek(EntityAgent entity, JsonObject taskConfig, JsonObject aiConfig)
            : base(entity, taskConfig, aiConfig)
        {
            // base leap uses Animator, which is null server-side
            leapAtTarget = false;
        }

        private EBRpgMob? Mob => mob ??= entity.GetBehavior<EBRpgMob>();

        public override bool ShouldExecute()
        {
            long now = entity.World.ElapsedMilliseconds;
            if (now < nextSeekMs || Mob == null || Mob.IsLeashing || Mob.HoldPosition || Mob.IsFleeing) return false;

            var enemy = Mob.AcquireEnemy(seekingRange);
            if (enemy == null) return false;
            NowSeekRange = seekingRange * 1.5f;
            targetEntity = enemy;
            targetPos = enemy.Pos.XYZ;
            float reach = MinDistanceToTarget();
            return entity.Pos.SquareDistanceTo(targetPos) > reach * reach;
        }

        public override bool ContinueExecute(float dt)
        {
            if (Mob == null || Mob.IsLeashing || Mob.HoldPosition || targetEntity == null) return false;

            long now = entity.World.ElapsedMilliseconds;
            if (now >= nextRecheckMs)
            {
                nextRecheckMs = now + 500;
                // enemy changed, restart
                if (Mob.AcquireEnemy(seekingRange) != targetEntity) return false;
            }
            return base.ContinueExecute(dt);
        }

        public override void FinishExecute(bool cancelled)
        {
            // back off when stuck
            bool stuck = stopNow;
            base.FinishExecute(cancelled);
            nextSeekMs = entity.World.ElapsedMilliseconds + (stuck ? 3000 : 250);
        }

        // IsEnemy also checks the dimension, vanilla does that only for players
        public override bool CanSense(Entity e, double range) => (Mob?.IsEnemy(e) ?? false) && base.CanSense(e, range);

        // IsEnemy already handles creatureHostility; vanilla's passive check needs emotion states we don't have
        public override bool CanSensePlayer(EntityPlayer eplr, double range)
            => targetablePlayerMode(eplr.Player) && entity.Pos.Dimension == eplr.Pos.Dimension;
    }
}
