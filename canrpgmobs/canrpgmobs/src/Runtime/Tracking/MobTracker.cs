using System.Collections.Generic;
using System.Linq;
using canrpgmobs.Runtime.Behaviors;
using canrpgmobs.Runtime.Brain;
using Vintagestory.API.Server;

namespace canrpgmobs.Runtime.Tracking
{
    // Server, in-memory list of loaded mobs. Catalog changes are applied a few mobs per tick.
    public sealed class MobTracker
    {
        // ~30s at 50ms ticks
        private const int PruneEveryTicks = 600;

        private readonly HashSet<EBRpgMob> live = new();
        private readonly Queue<EBRpgMob> pending = new();
        private readonly HashSet<EBRpgMob> queued = new();
        private int ticksSincePrune;

        public int Count => live.Count;
        public int Pending => pending.Count;

        public void Add(EBRpgMob mob) => live.Add(mob);

        public bool Contains(EBRpgMob mob) => live.Contains(mob);

        public void Remove(EBRpgMob mob)
        {
            live.Remove(mob);
            queued.Remove(mob);
        }

        public void QueueAll()
        {
            foreach (var mob in live)
                if (queued.Add(mob)) pending.Enqueue(mob);
        }

        // mobs gone without a despawn event (removed dimension)
        public int PruneMissing(IServerWorldAccessor world)
        {
            var gone = live.Where(mob => !world.LoadedEntities.TryGetValue(mob.entity.EntityId, out var loaded) || loaded != mob.entity).ToList();
            foreach (var mob in gone) Remove(mob);
            return gone.Count;
        }

        public void Tick(canrpgmobsModSystem mod, int perTick)
        {
            if (++ticksSincePrune >= PruneEveryTicks && mod.Api is ICoreServerAPI sapi)
            {
                ticksSincePrune = 0;
                int pruned = PruneMissing(sapi.World);
                if (pruned > 0) sapi.Logger.Notification("[canrpgmobs] forgot {0} mob(s) that left the world without a despawn event", pruned);
                BrainTrace.PruneMissing(sapi.World);
            }

            int done = 0;
            while (done < perTick && pending.Count > 0)
            {
                var mob = pending.Dequeue();
                if (!queued.Remove(mob) || !live.Contains(mob)) continue;
                mob.Reapply(mod);
                done++;
            }
        }
    }
}
