using System;
using System.Globalization;
using canrpgclasses.Client;
using canrpgmobs.Client.Editor.Controllers;
using Newtonsoft.Json.Linq;
using Vintagestory.API.Client;
using static canrpgmobs.Client.Editor.Controllers.JsonDraft;
using static canrpgmobs.Client.UiKit;

namespace canrpgmobs.Client.Editor
{
    // shared row widgets
    public partial class MobEditorDialog
    {
        // each input writes the whole pair
        private void RangeInputs(GuiComposer compo, JObject token, string key, FieldSpec spec, double x, double y, double width, double gap)
        {
            var pair = token[key] as JArray;
            string Part(int i) => pair != null && pair.Count > i ? ReadNumber(pair[i]) : "";
            string[] parts = { Part(0), Part(1) };
            NumberInput(compo, B(x, y, width), parts[0], v => { parts[0] = v; SetRange(token, key, parts, spec); });
            NumberInput(compo, B(x + width + gap, y, width), parts[1], v => { parts[1] = v; SetRange(token, key, parts, spec); });
        }

        // arrows at the ends do nothing instead of recomposing
        private GuiComposer Pager(GuiComposer compo, double x, double y, int page, int pages, Action<int> setPage, int? total = null, double width = 0)
        {
            string label = total == null ? $"{page + 1}/{pages}" : $"{page + 1}/{pages}  ({total})";
            double w = width > 0 ? width : total == null ? 132 : 204;

            bool Flip(int target)
            {
                if (target < 0 || target >= pages) return true;
                setPage(target);
                Compose();
                return true;
            }

            return compo
                .AddIcon("left", L("canrpgmobs:editor-tip-prev", "Previous page"), () => Flip(page - 1), B(x, y, 30))
                .AddStaticText(label, UiKit.LabelFont().WithOrientation(EnumTextOrientation.Center), B(x + 34, y + 4, Math.Max(40, w - 68)))
                .AddIcon("right", L("canrpgmobs:editor-tip-next", "Next page"), () => Flip(page + 1), B(x + w - 30, y, 30));
        }

        // caller keeps the typed text so it survives recompose; auto-applies after a pause (see FollowTyping)
        private GuiComposer SearchRow(GuiComposer compo, double x, double y, double inputW, string draft, Action<string> onTyped,
            string buttonText, double buttonX, double buttonW, Action apply)
        {
            string key = NextKey();
            compo.AddTextInput(B(x, y, inputW), value =>
                {
                    onTyped(value);
                    if (filling) return;
                    searchTypedMs = capi.World.ElapsedMilliseconds;
                    searchKey = key;
                }, CairoFont.WhiteDetailText(), key)
                .AddSmallButton(buttonText, () => { apply(); searchTypedMs = 0; Compose(); return true; }, B(buttonX, y, buttonW), EnumButtonStyle.Small);
            fills.Add((key, draft, false));
            return compo;
        }

        // default value = key removed from the draft
        private void YesNoToggle(GuiComposer compo, JObject token, string key, bool fallback, double x, double y, double width, bool markDefault = false)
        {
            bool written = token[key] is JValue { Type: JTokenType.Boolean };
            bool value = written ? (bool)token[key]! : fallback;
            string text = value ? L("canrpgmobs:editor-yes", "yes") : L("canrpgmobs:editor-no", "no");
            if (markDefault && !written) text += " " + L("canrpgmobs:editor-default", "(default)");
            compo.AddSmallButton(text, () =>
            {
                if (!value == fallback) token.Remove(key);
                else token[key] = !value;
                Compose();
                return true;
            }, B(x, y, width), EnumButtonStyle.Small);
        }

        // cross at the right edge of a panel row
        private GuiComposer RemoveButton(GuiComposer compo, double x, double y, Action remove) =>
            compo.AddIcon(UiKit.IconCross, L("canrpgmobs:editor-tip-remove", "Remove"), () => { remove(); Compose(); return true; }, B(x + PanelW - 30, y, 26));

        // where an inherited value comes from
        private string FromLabel(bool fromRace) => fromRace
            ? L("canrpgmobs:editor-gear-from-race", "from race")
            : L("canrpgmobs:editor-gear-from-role", "from role");

        private readonly record struct NumberPreset(string LangKey, string Fallback, float Value);

        private static readonly NumberPreset[] SpeedPresets =
        {
            new("canrpgmobs:wiz-pace-slow", "Slow", 0.025f), new("canrpgmobs:wiz-pace-normal", "Normal", 0.03f), new("canrpgmobs:wiz-pace-fast", "Fast", 0.036f)
        };

        private static readonly NumberPreset[] MulPresets = { new("", "×0.5", 0.5f), new("", "×1", 1f), new("", "×1.5", 1.5f), new("", "×2", 2f) };
        private static readonly NumberPreset[] SpeedMulPresets = { new("", "×0.8", 0.8f), new("", "×1", 1f), new("", "×1.2", 1.2f) };

        // preset buttons, then the exact number for whoever needs it
        private FormRow PresetStatRow(double x, JObject d, string objectKey, string key, string label, FieldSpec spec, NumberPreset[] presets, Func<string?>? result)
            => new(1, (c, y) =>
            {
                c.AddFitText(label, UiKit.LabelFont(), B(x, y + 4, SimpleLabelW - 6));
                string current = ReadNumber((d[objectKey] as JObject)?[key]);
                bool known = TryParse(current, out float value);
                double each = (SimpleControlW - (presets.Length - 1) * 3) / presets.Length;
                double bx = x + SimpleLabelW;
                foreach (var preset in presets)
                {
                    float set = preset.Value;
                    bool on = known && Math.Abs(value - set) < 0.0005f;
                    string text = preset.LangKey.Length > 0 ? L(preset.LangKey, preset.Fallback) : preset.Fallback;
                    c.AddSmallButton((on ? "> " : "") + text, () =>
                    {
                        if (d[objectKey] is not JObject target) d[objectKey] = target = new JObject();
                        SetNumber(target, key, set.ToString(CultureInfo.InvariantCulture), spec);
                        Compose();
                        return true;
                    }, B(bx, y, each), EnumButtonStyle.Small);
                    bx += each + 3;
                }
                double ix = x + SimpleLabelW + SimpleControlW + 6;
                NumberInput(c, B(ix, y, 64), current, v =>
                {
                    if (d[objectKey] is not JObject target)
                    {
                        if (v.Trim().Length == 0) return;
                        d[objectKey] = target = new JObject();
                    }
                    SetNumber(target, key, v, spec);
                    RemoveIfEmpty(d, objectKey);
                });
                double rx = ix + 70;
                if (Inherits(objectKey) && HasOwn(d, objectKey, key))
                {
                    ResetButton(c, rx, y, d, objectKey, key);
                    rx += ResetW + 4;
                }
                if (result != null)
                    LiveText(c, B(rx, y + 4, PanelW - (rx - x), RowStep), UiKit.SmallValueFont(),
                        () => result() is { } r ? ResultText(d, objectKey, key, r) : "");
            });

        // one wording for every "race and role ..." switch
        private void InheritToggle(GuiComposer c, double x, double y, string label, string helpKey, bool used, Action toggle)
        {
            SimpleLabel(c, x, y, label, helpKey);
            c.AddSmallButton(used ? L("canrpgmobs:editor-inherit-used", "Used, plus own") : L("canrpgmobs:editor-inherit-unused", "Not used, own only"), () =>
            {
                toggle();
                Compose();
                return true;
            }, B(x + SimpleLabelW, y, SimpleControlW), EnumButtonStyle.Small);
        }

        // unsaved changes: first click warns, second click on the same target goes through. true = stop
        private bool ConfirmDiscard(ref string? pending, string target)
        {
            if (!session.IsDirty || pending == target) return false;
            pending = target;
            SetStatus(L("canrpgmobs:editor-unsaved", "Unsaved changes: click again to discard them."), true);
            Compose();
            return true;
        }
    }
}
