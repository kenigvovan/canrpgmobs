using System.Globalization;
using canrpgclasses.Client;
using canrpgmobs.Data;
using Newtonsoft.Json.Linq;
using Vintagestory.API.Client;
using Vintagestory.API.Config;
using static canrpgmobs.Client.Editor.Controllers.JsonDraft;

namespace canrpgmobs.Client.Zones
{
    // Shown while the wand is in hand: the open zone, the wand mode, the last message. No buttons, the mouse stays with the game.
    public sealed class ZoneWandHud : HudElement
    {
        private const double Width = 330;

        private readonly ZoneDraft draft;
        private int mode = -1;

        public ZoneWandHud(ICoreClientAPI capi, ZoneDraft draft) : base(capi)
        {
            this.draft = draft;
        }

        public override string ToggleKeyCombinationCode => null!;
        public override bool Focusable => false;

        public override void OnGuiOpened()
        {
            base.OnGuiOpened();
            Compose();
        }

        public void SetMode(int wandMode)
        {
            if (mode == wandMode) return;
            mode = wandMode;
            Refresh();
        }

        private void Compose()
        {
            var background = ElementBounds.Fixed(0, 0, Width + 20, 104);
            ClearComposers();
            SingleComposer = capi.Gui.CreateCompo("canrpgmobszonehud", ElementStdBounds.AutosizedMainDialog.WithAlignment(EnumDialogArea.LeftTop).WithFixedAlignmentOffset(10, 60))
                .AddShadedDialogBG(background, false, 5.0, 0.6f)
                .BeginChildElements(ElementBounds.Fixed(10, 8, Width, 90))
                .AddDynamicText("", UiKit.SectionFont(), ElementBounds.Fixed(0, 0, Width, 22), "title")
                .AddDynamicText("", EditorStyle.FieldLabel(), ElementBounds.Fixed(0, 24, Width, 20), "info")
                .AddDynamicText("", EditorStyle.Body(), ElementBounds.Fixed(0, 46, Width, 40), "line")
                .EndChildElements()
                .Compose();
            Refresh();
        }

        public void Refresh()
        {
            if (!IsOpened() || SingleComposer == null) return;
            SingleComposer.GetDynamicText("title")?.SetNewText(Title(), false, false, false);
            SingleComposer.GetDynamicText("info")?.SetNewText(Info(), false, false, false);
            var line = SingleComposer.GetDynamicText("line");
            if (line == null) return;
            line.Font = EditorStyle.Status(draft.StatusIsError && draft.Status.Length > 0);
            // forced, the color may change with the same text
            line.SetNewText(draft.Status.Length > 0 ? draft.Status : Hint(), false, true, false);
        }

        private string Title()
        {
            if (draft.Draft == null) return Lang.Get("canrpgmobs:zones-hud-none");
            var name = draft.Draft["name"] as JObject;
            string text = name?[Lang.CurrentLocale]?.ToString() ?? name?["en"]?.ToString() ?? "";
            string id = Short(draft.Id ?? "");
            return (draft.IsDirty ? "* " : "") + (text.Length > 0 ? $"{text}  ({id})" : id);
        }

        private string Info()
        {
            string modeName = Lang.Get("canrpgmobs:zones-wand-mode-" + (mode switch { ItemZoneWand.ModeSelect => "select", ItemZoneWand.ModeNew => "new", _ => "corners" }));
            string text = Lang.Get("canrpgmobs:zones-hud-mode", modeName);
            if (draft.Draft == null) return text;
            var corners = draft.Corners.ToArray();
            text += "  ·  " + Lang.Get("canrpgmobs:zones-hud-corners", corners.Length);
            if (corners.Length >= 3) text += "  ·  " + Lang.Get("canrpgmobs:zones-hud-area", ZoneGeometry.Area(corners).ToString("0", CultureInfo.InvariantCulture));
            if (draft.Draft["level"] is JArray { Count: 2 } level) text += "  ·  " + Lang.Get("canrpgmobs:zones-hud-level", level[0], level[1]);
            return text;
        }

        private string Hint() => mode switch
        {
            ItemZoneWand.ModeSelect => Lang.Get("canrpgmobs:zones-hud-hint-select"),
            ItemZoneWand.ModeNew => Lang.Get("canrpgmobs:zones-hud-hint-new"),
            _ => draft.Draft == null ? Lang.Get("canrpgmobs:zones-hud-hint-start") : Lang.Get("canrpgmobs:zones-hud-hint-corners")
        };
    }
}
