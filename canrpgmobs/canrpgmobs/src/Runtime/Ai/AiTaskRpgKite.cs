using System;
using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;
using Vintagestory.API.Datastructures;
using Vintagestory.API.MathTools;
using Vintagestory.GameContent;

namespace canrpgmobs.Runtime.Ai
{
    // Keeps the mob within preferredRange of its enemy, aiming for the middle.
    public class AiTaskRpgKite : AiTaskRpgWalkTo
    {
        public const string Code = "rpgmobkite";

        private readonly float minRange;
        private readonly float maxRange;
        private readonly Vec3d spot = new();

        private EntityBehaviorTaskAI? taskAi;
        private Entity? enemy;
        private bool retreating;

        public AiTaskRpgKite(EntityAgent entity, JsonObject taskConfig, JsonObject aiConfig)
            : base(entity, taskConfig, aiConfig)
        {
            var band = taskConfig["preferredRange"].AsArray<float>(null);
            minRange = band is { Length: >= 2 } ? Math.Max(0f, band[0]) : 8f;
            maxRange = band is { Length: >= 2 } ? Math.Max(minRange + 1f, band[1]) : 14f;
        }

        protected override long CheckMs => 400;
        protected override long TimeoutMs => 5000;
        protected override long FailedRetryMs => 2500;

        // stand still while aiming, otherwise the shoot task's turning fights the walk
        protected override bool Blocked => (taskAi ??= entity.GetBehavior<EntityBehaviorTaskAI>())?.TaskManager.IsTaskActive<AiTaskRpgShoot>() == true;

        private double MidRange => (minRange + maxRange) / 2.0;

        protected override (Vec3d spot, float arriveAt)? PickDestination()
        {
            var target = Mob!.AcquireEnemy(seekingRange);
            if (target == null) return null;
            double distance = entity.Pos.DistanceTo(target.Pos);
            if (distance < minRange) retreating = true;
            else if (distance > maxRange) retreating = false;
            else return null;

            enemy = target;
            double dx = entity.Pos.X - target.Pos.X, dz = entity.Pos.Z - target.Pos.Z;
            double length = Math.Sqrt(dx * dx + dz * dz);
            if (length < 0.01)
            {
                dx = Math.Sin(entity.Pos.Yaw);
                dz = Math.Cos(entity.Pos.Yaw);
                length = 1.0;
            }
            spot.Set(target.Pos.X + dx / length * MidRange, entity.Pos.InternalY, target.Pos.Z + dz / length * MidRange);
            return (spot, 1f);
        }

        protected override bool KeepWalking()
        {
            if (enemy is not { Alive: true }) return false;
            double distance = entity.Pos.DistanceTo(enemy.Pos);
            return retreating ? distance < MidRange - 0.5 : distance > MidRange + 0.5;
        }

        protected override void ClearTarget() => enemy = null;
    }
}
