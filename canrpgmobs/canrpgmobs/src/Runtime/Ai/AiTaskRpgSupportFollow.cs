using canrpgmobs.Runtime.Entities;
using canrpgmobs.Runtime.Factions;
using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;
using Vintagestory.API.Datastructures;
using Vintagestory.API.MathTools;

namespace canrpgmobs.Runtime.Ai
{
    // Out of combat, stay within followRange of the nearest ally.
    public class AiTaskRpgSupportFollow : AiTaskRpgWalkTo
    {
        public const string Code = "rpgmobfollow";

        private const float SearchRange = 24f;

        private readonly float followRange;
        private Entity? leader;

        public AiTaskRpgSupportFollow(EntityAgent entity, JsonObject taskConfig, JsonObject aiConfig)
            : base(entity, taskConfig, aiConfig)
            => followRange = taskConfig["followRange"].AsFloat(6f);

        protected override long CheckMs => 1000;
        protected override long TimeoutMs => 8000;
        protected override long FailedRetryMs => 3000;

        protected override (Vec3d spot, float arriveAt)? PickDestination()
        {
            if (Mob!.AcquireEnemy(seekingRange) != null) return null;

            var catalog = canrpgmobsModSystem.For(entity.Api)?.Catalog;
            if (catalog == null) return null;
            var nearest = entity.World.GetNearestEntity(entity.Pos.XYZ, SearchRange, SearchRange / 2f,
                e => e is EntityRpgMob && e != entity && e.Alive && e.Pos.Dimension == entity.Pos.Dimension && FactionService.AreAllies(entity, e, catalog));
            if (nearest == null || entity.Pos.DistanceTo(nearest.Pos) <= followRange) return null;

            leader = nearest;
            return (nearest.Pos.XYZ, followRange * 0.6f);
        }

        protected override bool KeepWalking()
            => leader is { Alive: true } && entity.Pos.DistanceTo(leader.Pos) > followRange * 0.7f;

        protected override void ClearTarget() => leader = null;
    }
}
