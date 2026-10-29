using canrpgclasses.Core.Control;
using canrpgmobs.Runtime.Behaviors;
using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;
using Vintagestory.API.Datastructures;
using Vintagestory.API.MathTools;

namespace canrpgmobs.Runtime.Ai
{
    // Base for kite/follow: pick a spot, path to it, back off after a failed path.
    public abstract class AiTaskRpgWalkTo : AiTaskBase, IMovementAiTask
    {
        protected readonly float moveSpeed;
        protected readonly float seekingRange;

        private readonly Vec3d destination = new();
        private EBRpgMob? mob;
        private float arriveAt;
        private bool arrived;
        private bool failed;
        private long startedMs;
        private long nextCheckMs;

        protected AiTaskRpgWalkTo(EntityAgent entity, JsonObject taskConfig, JsonObject aiConfig)
            : base(entity, taskConfig, aiConfig)
        {
            moveSpeed = taskConfig["movespeed"].AsFloat(0.03f);
            seekingRange = taskConfig["seekingRange"].AsFloat(20f);
        }

        protected EBRpgMob? Mob => mob ??= entity.GetBehavior<EBRpgMob>();

        protected abstract long CheckMs { get; }
        protected abstract long TimeoutMs { get; }
        protected abstract long FailedRetryMs { get; }

        // null = nothing to do now
        protected abstract (Vec3d spot, float arriveAt)? PickDestination();

        protected abstract bool KeepWalking();

        protected virtual void ClearTarget() { }

        // archer aiming etc.
        protected virtual bool Blocked => false;

        public override bool ShouldExecute()
        {
            long now = entity.World.ElapsedMilliseconds;
            if (now < nextCheckMs) return false;
            nextCheckMs = now + CheckMs;
            if (Mob is not { IsLeashing: false, HoldPosition: false, IsFleeing: false } || Blocked) return false;

            if (PickDestination() is not { } pick) return false;
            destination.Set(pick.spot);
            arriveAt = pick.arriveAt;
            return true;
        }

        public override void StartExecute()
        {
            base.StartExecute();
            arrived = false;
            failed = false;
            startedMs = entity.World.ElapsedMilliseconds;
            pathTraverser.NavigateTo_Async(destination.Clone(), moveSpeed, arriveAt, OnArrived, OnFailed, OnFailed, 2000, 1, null);
        }

        public override bool ContinueExecute(float dt)
        {
            if (arrived || failed || Mob is not { IsLeashing: false, HoldPosition: false } || Blocked) return false;
            if (entity.World.ElapsedMilliseconds - startedMs > TimeoutMs) return false;
            return KeepWalking();
        }

        public override void FinishExecute(bool cancelled)
        {
            pathTraverser.Stop();
            base.FinishExecute(cancelled);
            ClearTarget();
            if (failed) nextCheckMs = entity.World.ElapsedMilliseconds + FailedRetryMs;
        }

        private void OnArrived() => arrived = true;
        private void OnFailed() => failed = true;
    }
}
