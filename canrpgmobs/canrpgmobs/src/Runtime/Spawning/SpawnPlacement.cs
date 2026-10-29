using System;
using Vintagestory.API.Common;
using Vintagestory.API.MathTools;

namespace canrpgmobs.Runtime.Spawning
{
    // Two free blocks over a solid top, keeps center's dimension.
    public static class SpawnPlacement
    {
        public const string NoFreeSpot = "no free spot to stand on nearby";
        private const int Attempts = 12;

        // internal coords, null if nothing found
        public static Vec3d? Find(IBlockAccessor blocks, BlockPos center, float radius, Random rand)
        {
            var pos = new BlockPos(center.dimension);
            for (int attempt = 0; attempt < Attempts; attempt++)
            {
                int x = center.X + (int)Math.Round((rand.NextDouble() * 2.0 - 1.0) * radius);
                int z = center.Z + (int)Math.Round((rand.NextDouble() * 2.0 - 1.0) * radius);
                for (int dy = 2; dy >= -3; dy--)
                {
                    int y = center.Y + dy;
                    if (y < 1) break;
                    if (CanStand(blocks, pos.Set(x, y, z))) return CenterOf(pos);
                }
            }
            return null;
        }

        // middle of the block's floor, internal coords
        public static Vec3d CenterOf(BlockPos pos) => new Vec3d().SetWithDimension(pos).Add(0.5, 0, 0.5);

        public static float RandomYaw(Random rand) => (float)(rand.NextDouble() * GameMath.TWOPI);

        // two free blocks at pos over a solid top
        public static bool CanStand(IBlockAccessor blocks, BlockPos pos)
        {
            if (!Free(blocks, pos)) return false;
            pos.Y++;
            bool head = Free(blocks, pos);
            pos.Y -= 2;
            bool floor = blocks.GetBlock(pos).SideSolid[BlockFacing.UP.Index];
            pos.Y++;
            return head && floor;
        }

        // liquids have no collision boxes, check the fluid layer
        private static bool Free(IBlockAccessor blocks, BlockPos pos)
            => blocks.GetBlock(pos).CollisionBoxes is not { Length: > 0 } && !blocks.GetBlock(pos, BlockLayersAccess.Fluid).IsLiquid();
    }
}
