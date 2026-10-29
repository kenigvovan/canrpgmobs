using System.Collections.Generic;
using PlayerModelLib;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;

namespace canrpgmobs.Runtime.Appearance
{
    // PlayerModelLib skin for NPCs: no camera/GUI side effects, no random skins.
    public class NpcSkinBehavior : PlayerSkinBehavior
    {
        public const string Name = "canrpgmobs:skin";

        public NpcSkinBehavior(Entity entity) : base(entity) { }

        // base changes the local camera znear for any entity
        protected override void SetZNear() { }

        public override void UpdateEntityProperties()
        {
            // statics used by the player's GUI preview, don't let NPCs overwrite them
            float guiScale = OtherPatches.CurrentModelGuiScale;
            float modelScale = OtherPatches.CurrentModelScale;
            try { base.UpdateEntityProperties(); }
            finally
            {
                OtherPatches.CurrentModelGuiScale = guiScale;
                OtherPatches.CurrentModelScale = modelScale;
            }

            // base applies the box only through the Player object
            var collision = entity.Properties.CollisionBoxSize;
            if (collision != null) entity.SetCollisionBox(collision.X, collision.Y);
            var selection = entity.Properties.SelectionBoxSize ?? collision;
            if (selection != null) entity.SetSelectionBox(selection.X, selection.Y);
        }

        // base redraws only on model change, but the editor changes parts live
        protected override void OnSkinConfigChanged()
        {
            base.OnSkinConfigChanged();
            ReplaceEntityShape();
        }

        // skin comes from the preset written before spawn
        public override bool RandomizeSkin(Entity entity, Dictionary<string, string> preSelection, bool playVoice = true) => false;

        // traits are for players only
        protected override void ApplyTraitAttributes(string modelCode) { }

        // the renderer uses this source, so gear textures are layered on top here
        public override ITexPositionSource? GetTextureSource(ref EnumHandling handling)
        {
            var skin = base.GetTextureSource(ref handling);
            return skin != null && entity.GetBehavior<EBMobGear>() is { } gear ? gear.Wrap(skin) : skin;
        }
    }
}
