using System;
using canrpgclasses.Core;
using canrpgmobs.Data;
using canrpgmobs.Runtime.Brain;
using Vintagestory.API.Datastructures;
using Vintagestory.API.MathTools;
using Vintagestory.GameContent;

namespace canrpgmobs.Runtime.Behaviors
{
    // Leash: walk back to the anchor and reset.
    public partial class EBRpgMob
    {
        private void StartLeash(long now)
        {
            var taskAi = entity.GetBehavior<EntityBehaviorTaskAI>();
            if (taskAi == null || anchor == null || Mob == null) return;

            IsLeashing = true;
            leashStartedMs = now;
            DropCombat();
            combatStartMs = 0;
            RpgCombatApi.Interrupt(entity);
            taskAi.TaskManager.StopTasks();
            foreach (var task in taskAi.TaskManager.GetTasks<AiTaskBaseTargetable>()) task.ClearAttacker();

            MobsApi.RaiseLeashing(new LeashEvent(entity, anchor, started: true, reachedHome: false), entity.Api.Logger);
            // anim first, NavigateTo_Async can call EndLeash synchronously
            entity.AnimManager?.StartAnimation(AnimKeys.Walk);
            taskAi.PathTraverser.NavigateTo_Async(anchor.Clone(), Math.Max(0.01f, Mob.MoveSpeed * 1.25f), LeashArrivalDistance,
                () => EndLeash(true), () => EndLeash(false), () => EndLeash(false), 5000, 1, null);
        }

        private void EndLeash(bool reachedHome)
        {
            if (!IsLeashing) return;
            IsLeashing = false;
            LifeEpoch++;
            ResetPhases();
            EndEnrage();
            DespawnSummons();
            entity.AnimManager?.StopAnimation(AnimKeys.Walk);

            if (!reachedHome)
            {
                entity.GetBehavior<EntityBehaviorTaskAI>()?.PathTraverser.Stop();
                // no path home, stay here instead of retrying
                SetAnchor(entity.Pos.XYZ);
            }

            if (Health is { } full && entity.Alive) full.Health = full.MaxHealth;
            MobsApi.RaiseLeashing(new LeashEvent(entity, anchor ?? entity.Pos.XYZ, started: false, reachedHome), entity.Api.Logger);
        }

        // leash: zone, the mob walks back once it leaves its zone's outline
        private bool OutsideOwnZone()
        {
            if (Identity?.GetString(MobKeys.Zone) is not { Length: > 0 } id) return false;
            if (canrpgmobsModSystem.For(entity.Api)?.Catalog.ZoneIndex.Get(id) is not { } shape || shape.Model.leash != ZoneRules.LeashZone) return false;
            var pos = entity.Pos;
            return pos.Dimension == shape.Model.dimension && !shape.Contains(pos.X, pos.Y, pos.Z);
        }

        private Vec3d? ReadAnchor()
        {
            var identity = Identity;
            if (identity == null || !identity.HasAttribute(MobKeys.AnchorX)) return null;
            return new Vec3d(identity.GetDouble(MobKeys.AnchorX), identity.GetDouble(MobKeys.AnchorY), identity.GetDouble(MobKeys.AnchorZ));
        }

        // server only, no sync needed
        private void SetAnchor(Vec3d position)
        {
            anchor = position;
            var identity = Identity;
            if (identity == null) return;
            identity.SetDouble(MobKeys.AnchorX, position.X);
            identity.SetDouble(MobKeys.AnchorY, position.Y);
            identity.SetDouble(MobKeys.AnchorZ, position.Z);
        }
    }
}
