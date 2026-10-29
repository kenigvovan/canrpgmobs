using System;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;
using Vintagestory.API.Config;
using Vintagestory.API.MathTools;
using Vintagestory.API.Server;

namespace canrpgmobs.Client.Zones
{
    // Modes on F. Corners: right click adds into the nearest edge, Shift removes, Ctrl picks a corner up and the next click puts it down.
    // Select: right click opens the zone there. New: right click starts a zone. Save, Undo and Panel act at once and keep the mode.
    // Left click: Shift saves, Ctrl undoes. All of it edits the client's zone draft, the HUD shows it.
    public class ItemZoneWand : Item
    {
        public const string ClassName = "canrpgmobs.ItemZoneWand";
        private const double ReachBlocks = 1.6;
        private const string ModeKey = "zoneWandMode";

        public const int ModeCorners = 0, ModeSelect = 1, ModeNew = 2;
        private const int ActSave = 3, ActUndo = 4, ActPanel = 5;
        private static readonly string[] ModeCodes = { "corners", "select", "new", "save", "undo", "panel" };
        private static readonly string[] ModeLetters = { "+", "?", "N", "S", "<", "=" };

        private int carried = -1;
        private SkillItem[]? modes;
        private WorldInteraction[]? help;

        public override void OnLoaded(ICoreAPI api)
        {
            base.OnLoaded(api);
            if (api is not ICoreClientAPI capi) return;
            modes = new SkillItem[ModeCodes.Length];
            for (int i = 0; i < ModeCodes.Length; i++)
                modes[i] = new SkillItem { Code = new AssetLocation(ModeCodes[i]), Name = Lang.Get("canrpgmobs:zones-wand-mode-" + ModeCodes[i]) }
                    .WithLetterIcon(capi, ModeLetters[i]);
        }

        public override void OnUnloaded(ICoreAPI api)
        {
            if (modes != null)
                foreach (var mode in modes) mode.Dispose();
            modes = null;
            base.OnUnloaded(api);
        }

        public override SkillItem[]? GetToolModes(ItemSlot slot, IClientPlayer forPlayer, BlockSelection blockSel)
            => forPlayer.HasPrivilege(Privilege.controlserver) ? modes : null;

        public override int GetToolMode(ItemSlot slot, IPlayer byPlayer, BlockSelection blockSelection) => Mode(slot.Itemstack);

        public static int Mode(ItemStack? stack) => Math.Clamp(stack?.Attributes.GetInt(ModeKey) ?? 0, ModeCorners, ModeNew);

        // actions don't stick, the previous mode stays
        public override void SetToolMode(ItemSlot slot, IPlayer byPlayer, BlockSelection blockSelection, int toolMode)
        {
            if (toolMode <= ModeNew)
            {
                carried = -1;
                slot.Itemstack?.Attributes.SetInt(ModeKey, toolMode);
                if (Draft() is { } changed) changed.Notify("", false);
                return;
            }
            if (Draft() is not { } draft || api is not ICoreClientAPI capi) return;
            switch (toolMode)
            {
                case ActSave: Save(draft); break;
                case ActUndo: Undo(draft); break;
                case ActPanel: canrpgmobsModSystem.For(capi)?.ToggleZonePanel(capi); break;
            }
        }

        public override void OnHeldInteractStart(ItemSlot slot, EntityAgent byEntity, BlockSelection blockSel, EntitySelection entitySel, bool firstEvent, ref EnumHandHandling handling)
        {
            handling = EnumHandHandling.PreventDefault;
            if (!firstEvent || blockSel == null || !Allowed(byEntity) || Draft() is not { } draft) return;

            var pos = blockSel.Position;
            int x = pos.X, z = pos.Z;
            switch (Mode(slot.Itemstack))
            {
                case ModeSelect:
                    SelectAt(draft, pos);
                    return;
                case ModeNew:
                    NewAt(draft, pos);
                    return;
            }

            if (draft.Draft == null)
            {
                NewAt(draft, pos);
                return;
            }
            if (carried >= 0)
            {
                draft.BeginDrag();
                draft.MoveCorner(carried, x, z);
                draft.Notify(Lang.Get("canrpgmobs:zones-wand-placed", carried + 1), false);
                carried = -1;
                return;
            }

            int near = draft.NearestCorner(x + 0.5, z + 0.5, ReachBlocks);
            if (byEntity.Controls.CtrlKey)
            {
                if (near < 0) draft.Notify(Lang.Get("canrpgmobs:zones-wand-nothing"), true);
                else
                {
                    carried = near;
                    draft.Notify(Lang.Get("canrpgmobs:zones-wand-carrying", near + 1), false);
                }
                return;
            }
            if (byEntity.Controls.ShiftKey)
            {
                if (near < 0) draft.Notify(Lang.Get("canrpgmobs:zones-wand-nothing"), true);
                else draft.RemoveCornerIfAllowed(near);
                return;
            }
            if (near >= 0)
            {
                draft.Notify(Lang.Get("canrpgmobs:zones-wand-taken", near + 1), true);
                return;
            }
            draft.AddCorner(x, z);
        }

        // never breaks blocks, a missed save click would otherwise dig
        public override void OnHeldAttackStart(ItemSlot slot, EntityAgent byEntity, BlockSelection blockSel, EntitySelection entitySel, ref EnumHandHandling handling)
        {
            handling = EnumHandHandling.PreventDefault;
            if (!Allowed(byEntity) || Draft() is not { } draft) return;
            if (byEntity.Controls.ShiftKey) Save(draft);
            else if (byEntity.Controls.CtrlKey) Undo(draft);
        }

        public override WorldInteraction[] GetHeldInteractionHelp(ItemSlot inSlot)
            => help ??= new[]
            {
                new WorldInteraction { ActionLangCode = "canrpgmobs:zones-wand-help-modes", HotKeyCode = "toolmodeselect", MouseButton = EnumMouseButton.None },
                new WorldInteraction { ActionLangCode = "canrpgmobs:zones-wand-help-save", MouseButton = EnumMouseButton.Left, HotKeyCode = "shift" },
                new WorldInteraction { ActionLangCode = "canrpgmobs:zones-wand-help-undo", MouseButton = EnumMouseButton.Left, HotKeyCode = "ctrl" }
            };

        private void SelectAt(ZoneDraft draft, BlockPos pos)
        {
            var mod = canrpgmobsModSystem.For((ICoreClientAPI)api);
            if (mod?.Catalog.ZoneIndex.At(pos.dimension, pos.X + 0.5, pos.Y + 1, pos.Z + 0.5) is not { } shape)
            {
                draft.Notify(Lang.Get("canrpgmobs:zones-wand-no-zone"), true);
                return;
            }
            string id = shape.Model.id;
            if (draft.OpenedId == id || draft.WarnUnsaved()) return;
            carried = -1;
            draft.Open(id);
        }

        private void NewAt(ZoneDraft draft, BlockPos pos)
        {
            if (draft.WarnUnsaved()) return;
            carried = -1;
            draft.New(pos.X, pos.Z);
            if (pos.dimension != 0) draft.Edit(d => d["dimension"] = pos.dimension);
        }

        private void Save(ZoneDraft draft)
        {
            carried = -1;
            if (draft.Draft == null) draft.Notify(Lang.Get("canrpgmobs:zones-none-open-short"), true);
            else if (!draft.IsDirty) draft.Notify(Lang.Get("canrpgmobs:zones-status-clean"), false);
            else
            {
                draft.Save();
                draft.Notify(draft.Status, draft.StatusIsError);
            }
        }

        private void Undo(ZoneDraft draft)
        {
            carried = -1;
            if (draft.CanUndo) draft.Undo();
            else draft.Notify(Lang.Get("canrpgmobs:zones-wand-no-undo"), true);
        }

        private bool Allowed(EntityAgent byEntity)
            => api is ICoreClientAPI && byEntity is EntityPlayer { Player: { } player } && player.HasPrivilege(Privilege.controlserver);

        private ZoneDraft? Draft() => api is ICoreClientAPI capi ? canrpgmobsModSystem.For(capi)?.ZoneDraft : null;
    }
}
