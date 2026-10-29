using canrpgmobs.Client.Spawner;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Server;

namespace canrpgmobs.Runtime.Spawning
{
    // Right-click opens the settings dialog for controlserver players.
    public class BlockMobSpawner : Block
    {
        public const string ClassName = "canrpgmobs.BlockMobSpawner";

        private static MobSpawnerDialog? openDialog;

        public override bool OnBlockInteractStart(IWorldAccessor world, IPlayer byPlayer, BlockSelection blockSel)
        {
            if (byPlayer == null || !byPlayer.HasPrivilege(Privilege.controlserver)) return base.OnBlockInteractStart(world, byPlayer, blockSel);
            if (world.Api is ICoreClientAPI capi && world.BlockAccessor.GetBlockEntity(blockSel.Position) is BEMobSpawner spawner)
            {
                openDialog?.TryClose();
                var dialog = new MobSpawnerDialog(capi, blockSel.Position, spawner.Config);
                dialog.OnClosed += () =>
                {
                    if (openDialog == dialog) openDialog = null;
                    dialog.Dispose();
                };
                openDialog = dialog;
                dialog.TryOpen();
            }
            return true;
        }
    }
}
