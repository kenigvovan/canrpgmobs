using System;
using System.Collections.Generic;
using canrpgmobs.Data;
using Vintagestory.API.Common;
using Vintagestory.API.MathTools;

namespace canrpgmobs.Runtime.Loot
{
    // position in internal coords, keeps the dimension
    public static class LootSpawner
    {
        public static int Spawn(IWorldAccessor world, IReadOnlyList<LootDrop> drops, Vec3d position, ILogger logger)
        {
            int stacks = 0;
            foreach (var drop in drops)
            {
                var code = new AssetLocation(drop.Code);
                CollectibleObject? collectible = drop.Type == LootEntryModel.Block ? world.GetBlock(code) : (CollectibleObject?)world.GetItem(code);
                if (collectible == null)
                {
                    logger.Warning("[canrpgmobs] loot {0} '{1}' does not exist, skipped", drop.Type, drop.Code);
                    continue;
                }

                int left = drop.Count;
                int maxStack = Math.Max(1, collectible.MaxStackSize);
                while (left > 0)
                {
                    int size = Math.Min(left, maxStack);
                    var velocity = new Vec3d(world.Rand.NextDouble() * 0.1 - 0.05, 0.1, world.Rand.NextDouble() * 0.1 - 0.05);
                    world.SpawnItemEntity(new ItemStack(collectible, size), position.AddCopy(0, 0.25, 0), velocity);
                    left -= size;
                    stacks++;
                }
            }
            return stacks;
        }
    }
}
