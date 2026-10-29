using System.Linq;
using canrpgmobs.Client;
using canrpgmobs.Client.Editor;
using canrpgmobs.Client.Zones;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Config;
using Vintagestory.API.Server;
using Vintagestory.GameContent;

namespace canrpgmobs
{
    public partial class canrpgmobsModSystem
    {
        private MobEditorDialog? editor;

        public override void StartClientSide(ICoreClientAPI api)
        {
            if (!coreCompatible) return;
            StartClientNetwork(api);
            UiKit.RegisterIcons(api);
            api.Input.RegisterHotKey("canrpgmobseditor", Lang.Get("canrpgmobs:editor-hotkey"), GlKeys.M, HotkeyType.GUIOrOtherControls, ctrlPressed: true, shiftPressed: true);
            api.Input.SetHotKeyHandler("canrpgmobseditor", _ => ToggleEditor(api));
            api.ModLoader.GetModSystem<WorldMapManager>()?.RegisterMapLayer<ZoneMapLayer>(ZoneMapLayer.Code, 0.6);
            StartZoneTools(api);
            api.ChatCommands.Create("canrpgmobsc")
                .WithDescription("canrpgmobs client tools")
                .BeginSubCommand("editor")
                    .WithDescription("Open the mob content editor (needs controlserver)")
                    .HandleWith(_ => { ToggleEditor(api); return TextCommandResult.Success(); })
                .EndSubCommand()
                .BeginSubCommand("anims")
                    .WithDescription("List the animations of the shape of the entity you look at, for writing animation sets")
                    .HandleWith(_ => ListAnimations(api))
                .EndSubCommand()
                .BeginSubCommand("zones")
                    .WithDescription("Mob zones: zones edit opens the markup panel, zones on|off shows outlines on the ground around you")
                    .WithArgs(api.ChatCommands.Parsers.OptionalWordRange("mode", "edit", "on", "off"))
                    .HandleWith(args => ZoneCommand(api, args[0] as string))
                .EndSubCommand();
        }

        private TextCommandResult ZoneCommand(ICoreClientAPI api, string? mode)
        {
            if (zoneHighlighter == null) return TextCommandResult.Error("Not ready yet.");
            if (api.World.Player?.HasPrivilege(Privilege.controlserver) != true) return TextCommandResult.Error(Lang.Get("canrpgmobs:editor-no-privilege"));
            if (mode == "edit")
            {
                ToggleZonePanel(api);
                return TextCommandResult.Success();
            }
            bool on = mode == null ? !ZoneOutlinesWanted : mode == "on";
            SetZoneOutlines(on);
            return TextCommandResult.Success(Lang.Get(on ? "canrpgmobs:zones-outlines-on" : "canrpgmobs:zones-outlines-off"));
        }

        // for the zone panel
        public void ShowInEditor(string section, string id)
        {
            if (Api is not ICoreClientAPI capi) return;
            editor ??= new MobEditorDialog(capi, this);
            editor.ShowEntry(section, id);
        }

        // cosmetic only, the server re-checks controlserver on every request
        private bool ToggleEditor(ICoreClientAPI api)
        {
            if (api.World.Player?.HasPrivilege(Privilege.controlserver) != true)
            {
                api.ShowChatMessage(Lang.Get("canrpgmobs:editor-no-privilege"));
                return true;
            }
            editor ??= new MobEditorDialog(api, this);
            if (editor.IsOpened()) editor.TryClose();
            else editor.TryOpen();
            return true;
        }

        private static TextCommandResult ListAnimations(ICoreClientAPI capi)
        {
            var target = capi.World.Player?.CurrentEntitySelection?.Entity;
            if (target == null) return TextCommandResult.Error("Look at an entity first.");

            var inShape = target.Properties.Client?.LoadedShapeForEntity?.Animations?
                .Select(a => a.Code).Where(c => !string.IsNullOrEmpty(c)).Distinct().OrderBy(c => c).ToList();
            var configured = target.Properties.Client?.Animations?
                .Select(a => a.Code == a.Animation ? a.Code : $"{a.Code} -&gt; {a.Animation}").OrderBy(c => c).ToList();

            return TextCommandResult.Success(
                $"{target.GetName()} ({target.Code})\n"
                + $"shape animations: {(inShape is { Count: > 0 } ? string.Join(", ", inShape) : "-")}\n"
                + $"animation codes: {(configured is { Count: > 0 } ? string.Join(", ", configured) : "-")}");
        }
    }
}
