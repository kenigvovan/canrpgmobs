using System;
using System.Collections.Generic;
using System.Linq;
using canrpgmobs.Data;
using canrpgmobs.Runtime.Brain;
using canrpgmobs.Runtime.Loot;
using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;
using Vintagestory.API.Datastructures;

namespace canrpgmobs.Runtime.Behaviors
{
    // Summons, stolen items, loot.
    public partial class EBRpgMob
    {
        // also prunes dead ids
        public int AliveSummons()
        {
            int before = summons.Count;
            for (int i = summons.Count - 1; i >= 0; i--)
                if (entity.World.GetEntityById(summons[i].id) is not { Alive: true }) summons.RemoveAt(i);
            if (summons.Count != before) SaveSummons();
            return summons.Count;
        }

        public void AdoptSummon(Entity summoned, bool despawnWithOwner)
        {
            summons.Add((summoned.EntityId, despawnWithOwner));
            SaveSummons();
            var identity = MobKeys.Identity(summoned);
            if (identity == null) return;
            identity.SetLong(MobKeys.Owner, entity.EntityId);
            identity.SetBool(MobKeys.NoLoot, true);
        }

        private void DespawnSummons()
        {
            if (summons.Count == 0) return;
            foreach (var (id, despawnWithOwner) in summons)
                if (despawnWithOwner && entity.World.GetEntityById(id) is { Alive: true } summoned) summoned.Die(EnumDespawnReason.Removed);
            summons.Clear();
            SaveSummons();
        }

        // persisted so maxAlive/despawnWithOwner survive chunk reloads
        private void SaveSummons()
        {
            entity.Attributes[SummonsAttr] = new LongArrayAttribute(summons.Select(s => s.id).ToArray());
            entity.Attributes[BoundSummonsAttr] = new LongArrayAttribute(summons.Where(s => s.despawnWithOwner).Select(s => s.id).ToArray());
        }

        private void LoadSummons()
        {
            summons.Clear();
            if (entity.Attributes[SummonsAttr] is not LongArrayAttribute all || all.value == null) return;
            var bound = new HashSet<long>((entity.Attributes[BoundSummonsAttr] as LongArrayAttribute)?.value ?? Array.Empty<long>());
            foreach (long id in all.value) summons.Add((id, bound.Contains(id)));
        }

        public void AddStolen(ItemStack stack)
        {
            var tree = entity.Attributes.GetOrAddTreeAttribute(StolenTree);
            int slot = 0;
            while (tree.HasAttribute(slot.ToString())) slot++;
            tree.SetItemstack(slot.ToString(), stack);
        }

        private void DropStolen()
        {
            var tree = entity.Attributes.GetTreeAttribute(StolenTree);
            if (tree == null) return;
            entity.Attributes.RemoveAttribute(StolenTree);
            foreach (var pair in tree)
            {
                var stack = tree.GetItemstack(pair.Key);
                if (stack != null && stack.ResolveBlockOrItem(entity.World))
                    entity.World.SpawnItemEntity(stack, entity.Pos.XYZ.AddCopy(0, 0.25, 0));
            }
        }

        // once per life, uses the current catalog
        private void DropLoot(DamageSource? cause)
        {
            if (lootDropped || Mob == null || ScaledStats == null) return;
            lootDropped = true;
            if (Identity?.GetBool(MobKeys.Preview) == true) return;
            // summons drop nothing
            if (Identity?.GetBool(MobKeys.NoLoot) == true) return;
            var mod = canrpgmobsModSystem.For(entity.Api);
            if (mod == null) return;

            try
            {
                var mob = mod.Catalog.Resolve(Mob.Key) ?? Mob;
                // handlers may change the table list
                var tables = new List<string>(mob.LootTables);
                MobsApi.RaiseLootRolling(new LootRollingEvent(entity, mob, ScaledStats, tables), entity.Api.Logger);
                var drops = LootRoller.Roll(tables, mod.Catalog.LootTables, ScaledStats.Level, mod.Catalog.Tier(ScaledStats.TierId), entity.World.Rand);
                var died = new MobDiedEvent(entity, mob, ScaledStats, cause, drops);
                MobsApi.RaiseMobDied(died, entity.Api.Logger);
                LootSpawner.Spawn(entity.World, died.Drops, entity.Pos.XYZ, entity.Api.Logger);
            }
            catch (Exception e)
            {
                entity.Api.Logger.Error("[canrpgmobs] loot for {0} failed: {1}", Mob.Key, e);
            }
        }
    }
}
