using System;
using System.Threading;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Config;
using Vintagestory.API.MathTools;

namespace canrpgmobs.Client
{
    // Same palette as the claims mod, so the windows read alike.
    public static class MobsColors
    {
        // heading of a block
        public static readonly double[] Section = { 0.95, 0.84, 0.52, 1.0 };
        // left-hand caption
        public static readonly double[] Label = { 0.74, 0.74, 0.74, 1.0 };
        // the value itself, apart from its caption
        public static readonly double[] Value = { 1.00, 0.88, 0.45, 1.0 };
    }

    // Text cut to the width it has, with the whole text on hover, instead of wrapping onto the next row.
    public static class UiKit
    {
        public const double RowH = 24;
        private const double TipPadding = 16;
        private const double TipMaxWidth = 320;
        private static int tipCounter;

        // lang key, else the inline English text
        public static string L(string key, string fallback, params object[] args)
            => Lang.HasTranslation(key, false, false) ? Lang.Get(key, args) : args.Length > 0 ? string.Format(fallback, args) : fallback;

        public static ElementBounds B(double x, double y, double w, double h = RowH) => ElementBounds.Fixed(x, y, w, h);

        public static CairoFont ValueFont() => CairoFont.WhiteSmallText().WithColor(MobsColors.Value);
        // the size of the editor's help lines
        public static CairoFont SmallValueFont() => CairoFont.WhiteDetailText().WithFontSize(11f).WithColor(MobsColors.Value);
        public static CairoFont LabelFont() => CairoFont.WhiteSmallText().WithColor(MobsColors.Label);
        public static CairoFont SectionFont() => CairoFont.WhiteSmallishText().WithColor(MobsColors.Section);

        // fixed width is unscaled, text extents are screen pixels
        public static string Fit(string? text, CairoFont font, double width)
        {
            if (string.IsNullOrEmpty(text)) return text ?? "";
            double max = GuiElement.scaled(width);
            if (font.GetTextExtents(text).Width <= max) return text!;
            string shortened = text!;
            while (shortened.Length > 1 && font.GetTextExtents(shortened + "...").Width > max) shortened = shortened.Substring(0, shortened.Length - 1);
            return shortened.TrimEnd() + "...";
        }

        public static GuiComposer AddFitText(this GuiComposer compo, string? text, CairoFont font, ElementBounds bounds, string? key = null)
        {
            string shown = Fit(text, font, bounds.fixedWidth);
            compo.AddStaticText(shown, font, bounds, key);
            if (shown != text) compo.AddTip(text, bounds);
            return compo;
        }

        public static GuiComposer AddFitButton(this GuiComposer compo, string? text, ActionConsumable onClick, ElementBounds bounds,
            EnumButtonStyle style = EnumButtonStyle.Small, string? key = null)
        {
            // a little room for the button's own edges
            string shown = Fit(text, CairoFont.SmallButtonText(style), bounds.fixedWidth - 8);
            compo.AddSmallButton(shown, onClick, bounds, style, key);
            if (shown != text) compo.AddTip(text, bounds);
            return compo;
        }

        // sized to its own text; the key keeps several tips on one composer apart
        public static GuiComposer AddTip(this GuiComposer compo, string? text, ElementBounds bounds)
        {
            if (string.IsNullOrEmpty(text)) return compo;
            var font = CairoFont.WhiteSmallText().WithOrientation(EnumTextOrientation.Center);
            double width = Math.Min(TipMaxWidth, font.GetTextExtents(text).Width / Scale() + TipPadding);
            return compo.AddHoverText(text, font, (int)width, bounds, "mobstip" + Interlocked.Increment(ref tipCounter));
        }

        private static double Scale() => RuntimeEnv.GUIScale <= 0 ? 1 : RuntimeEnv.GUIScale;

        // the game's own: left, right, plus, undo, dice; ours below
        public const string IconCross = "canrpgmobs:cross";
        // canrpgclasses' svg art, by file name
        public static string ClassesIcon(string name) => "canrpgclasses:" + name;

        private static readonly string[] ClassesIcons = { "skeleton", "position-marker", "dark-squad", "pouch-with-beads", "book-aura" };

        public static void RegisterIcons(ICoreClientAPI capi)
        {
            var icons = capi.Gui.Icons;
            icons.CustomIcons[IconCross] = (ctx, x, y, w, h, rgba) =>
            {
                ctx.SetSourceRGBA(rgba);
                icons.DrawCross(ctx, x + w * 0.2, y + h * 0.2, 2.5, w * 0.6);
            };
            foreach (var name in ClassesIcons)
            {
                // fetched with its data on every draw like the claims mod does, the data may be unloaded later; a missing file draws nothing
                var location = new AssetLocation("canrpgclasses:textures/icons/" + name + ".svg");
                icons.CustomIcons[ClassesIcon(name)] = (ctx, x, y, w, h, rgba) =>
                {
                    var asset = capi.Assets.TryGet(location, true);
                    if (asset?.Data == null) return;
                    int color = ColorUtil.ColorFromRgba((int)(rgba[0] * 255), (int)(rgba[1] * 255), (int)(rgba[2] * 255), (int)(rgba[3] * 255));
                    capi.Gui.DrawSvg(asset, (Cairo.ImageSurface)ctx.GetTarget(), x, y, (int)w, (int)h, color);
                };
            }
        }

        // a small square button with an icon and what it does on hover
        public static GuiComposer AddIcon(this GuiComposer compo, string icon, string? tip, ActionConsumable onClick, ElementBounds bounds)
        {
            compo.AddIconButton(icon, _ => onClick(), bounds, "mobsicon" + Interlocked.Increment(ref tipCounter));
            return compo.AddTip(tip, bounds);
        }
    }
}
