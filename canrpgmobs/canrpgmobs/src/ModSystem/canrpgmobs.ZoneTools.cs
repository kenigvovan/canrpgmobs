using System;
using canrpgmobs.Client.Zones;
using canrpgmobs.Data;
using canrpgmobs.Net;
using canrpgmobs.Runtime.Spawning;
using Vintagestory.API.Client;
using Vintagestory.API.Config;
using Vintagestory.API.MathTools;
using Vintagestory.API.Server;
using Vintagestory.GameContent;

namespace canrpgmobs
{
    // client: zone markup (draft, panel, outlines, hotkeys)
    public partial class canrpgmobsModSystem
    {
        private ZoneHighlighter? zoneHighlighter;
        private ZonePanelDialog? zonePanel;
        private ZoneWandHud? zoneHud;
        private long wandTickId;

        public ZoneDraft? ZoneDraft { get; private set; }
        public bool ZoneOutlinesWanted { get; private set; }
        // from the server's settings
        public bool ZonesOnMapForPlayers { get; private set; }
        // check page of the panel
        public ZoneStatsPacket? LastZoneStats { get; private set; }
        public bool ShowZoneAttempts { get; set; }
        public bool ShowZoneRing { get; set; }

        private void StartZoneTools(ICoreClientAPI api)
        {
            ZoneDraft = new ZoneDraft(api, this);
            zoneHighlighter = new ZoneHighlighter(api, this);
            zoneHud = new ZoneWandHud(api, ZoneDraft);
            ZoneDraft.Changed += () =>
            {
                zoneHighlighter?.Apply();
                zoneHud?.Refresh();
            };
            wandTickId = api.Event.RegisterGameTickListener(_ => WatchWand(api), 200);
            ZoneStatsReceived += packet =>
            {
                LastZoneStats = packet;
                zoneHighlighter?.Apply();
            };

            api.Input.RegisterHotKey("canrpgmobszones", Lang.Get("canrpgmobs:zones-hotkey"), GlKeys.Z, HotkeyType.GUIOrOtherControls, ctrlPressed: true, shiftPressed: true);
            api.Input.SetHotKeyHandler("canrpgmobszones", _ => ToggleZonePanel(api));
            // only while marking up, otherwise the keys stay free
            api.Input.RegisterHotKey("canrpgmobszoneundo", Lang.Get("canrpgmobs:zones-hotkey-undo"), GlKeys.Z, HotkeyType.GUIOrOtherControls, ctrlPressed: true);
            api.Input.SetHotKeyHandler("canrpgmobszoneundo", _ => ZoneKey(d => d.Undo()));
            api.Input.RegisterHotKey("canrpgmobszoneredo", Lang.Get("canrpgmobs:zones-hotkey-redo"), GlKeys.Y, HotkeyType.GUIOrOtherControls, ctrlPressed: true);
            api.Input.SetHotKeyHandler("canrpgmobszoneredo", _ => ZoneKey(d => d.Redo()));
        }

        // wand in hand = markup mode with the HUD, no panel needed
        private void WatchWand(ICoreClientAPI api)
        {
            if (ZoneDraft == null || zoneHud == null) return;
            var stack = api.World.Player?.InventoryManager?.ActiveHotbarSlot?.Itemstack;
            bool held = stack?.Collectible is ItemZoneWand && api.World.Player!.HasPrivilege(Privilege.controlserver);
            ZoneDraft.SetWandHeld(held);
            if (held)
            {
                if (!zoneHud.IsOpened()) zoneHud.TryOpen();
                zoneHud.SetMode(ItemZoneWand.Mode(stack));
            }
            else if (zoneHud.IsOpened()) zoneHud.TryClose();
        }

        private bool ZoneKey(Action<ZoneDraft> act)
        {
            if (ZoneDraft is not { Active: true, Draft: not null } draft) return false;
            act(draft);
            return true;
        }

        public bool ToggleZonePanel(ICoreClientAPI api)
        {
            if (api.World.Player?.HasPrivilege(Privilege.controlserver) != true)
            {
                api.ShowChatMessage(Lang.Get("canrpgmobs:editor-no-privilege"));
                return true;
            }
            if (ZoneDraft == null) return true;
            zonePanel ??= new ZonePanelDialog(api, this, ZoneDraft);
            if (zonePanel.IsOpened()) zonePanel.TryClose();
            else zonePanel.TryOpen();
            return true;
        }

        // the wand opens it on first use
        public void OpenZonePanel(ICoreClientAPI api)
        {
            if (zonePanel?.IsOpened() != true) ToggleZonePanel(api);
        }

        // from the editor: markup mode with that zone, the world map centered on it
        public void ShowZoneOnMap(ICoreClientAPI api, string id)
        {
            if (ZoneDraft == null) return;
            OpenZonePanel(api);
            if (ZoneDraft.OpenedId != id && ZoneDraft.WarnUnsaved()) return;
            if (ZoneDraft.OpenedId != id) ZoneDraft.Open(id);
            var map = api.ModLoader.GetModSystem<WorldMapManager>();
            if (map == null) return;
            if (map.worldMapDlg?.IsOpened() != true || map.worldMapDlg.DialogType != EnumDialogType.Dialog) map.ToggleMap(EnumDialogType.Dialog);
            if (Catalog.ZoneIndex.Get(id) is { } shape && map.worldMapDlg?.SingleComposer?.GetElement("mapElem") is GuiElementMap element)
            {
                var (x, z) = ZoneGeometry.LabelPoint(shape.Corners);
                element.CenterMapTo(new BlockPos((int)x, 0, (int)z));
            }
        }

        public void SetZoneOutlines(bool on)
        {
            ZoneOutlinesWanted = on;
            zoneHighlighter?.SetEnabled(on);
        }

        private void DisposeZoneTools()
        {
            BEMobSpawner.ClientLoaded.Clear();
            if (wandTickId != 0) Api?.Event.UnregisterGameTickListener(wandTickId);
            wandTickId = 0;
            zoneHud?.TryClose();
            zoneHud?.Dispose();
            zoneHud = null;
            zonePanel?.TryClose();
            zonePanel?.Dispose();
            zonePanel = null;
            ZoneDraft?.Dispose();
            ZoneDraft = null;
            zoneHighlighter?.Dispose();
            zoneHighlighter = null;
        }
    }
}
