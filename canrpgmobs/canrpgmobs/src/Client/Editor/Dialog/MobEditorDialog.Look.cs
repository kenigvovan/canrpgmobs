using System;
using System.Linq;
using canrpgmobs.Data;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Vintagestory.API.Client;
using static canrpgmobs.Client.UiKit;

namespace canrpgmobs.Client.Editor
{
    // Live look: pushes the draft's appearance onto the admin's last preview mob in the world.
    public partial class MobEditorDialog
    {
        private const long LookSendIntervalMs = 300;

        private bool liveLook;
        private string lastLookJson = "";
        private long lastLookMs;

        private bool LookAvailable => page == Page.Editor && session.Draft != null && session.Section is "races" or "mobs";

        private void AddLookRow(GuiComposer compo, double x, double y, double width)
        {
            if (!LookAvailable) return;
            double w = Math.Min(200, (width - 6) / 2);
            compo.AddSmallButton(L("canrpgmobs:editor-look-send", "Look to preview"), () => { SendLook(true); return true; }, B(x, y, w), EnumButtonStyle.Small)
                .AddSmallButton(liveLook ? L("canrpgmobs:editor-look-live-on", "Live look: on") : L("canrpgmobs:editor-look-live-off", "Live look: off"),
                    () => { liveLook = !liveLook; lastLookJson = ""; Compose(); return true; }, B(x + w + 6, y, w), EnumButtonStyle.Small);
        }

        private void SendLiveLookIfChanged()
        {
            if (liveLook && LookAvailable) SendLook(false);
        }

        private void SendLook(bool force)
        {
            long now = capi.World.ElapsedMilliseconds;
            // throttle before building the look, every compose calls this
            if (!force && now - lastLookMs < LookSendIntervalMs) return;
            var look = CurrentLook(out var preset);
            if (look == null) return;
            string appearanceJson = look.ToString(Formatting.None);
            string presetJson = preset?.ToString(Formatting.None) ?? "";
            string both = appearanceJson + "\n" + presetJson;
            if (!force && both == lastLookJson) return;
            lastLookJson = both;
            lastLookMs = now;
            mod.SendLook(appearanceJson, presetJson);
        }

        // preset: the one being edited, else the selected/first one
        private JObject? CurrentLook(out JObject? preset)
        {
            preset = null;
            var draft = session.Draft;
            if (draft == null) return null;

            if (session.Section is not ("races" or "mobs")) return null;

            // merged look, same as the 3D preview
            var eff = Effective(out _);
            var look = eff != null
                ? JObject.FromObject(eff.Appearance, Serializer)
                : draft["appearance"] as JObject ?? new JObject { ["kind"] = AppearanceKinds.Plm, ["model"] = AppearanceModel.Seraph };

            if (session.Current?.Node.Id == "preset" && !SimpleActive) preset = session.Current.Token;
            else if (eff?.Appearance.presets is { Count: > 0 } presets)
                preset = JObject.FromObject(presets.FirstOrDefault(p => p.id == simplePreset) ?? presets[0], Serializer);
            return look;
        }

        private bool CycleVariant(JObject map, KeyHolder holder, int step)
        {
            var variants = PickLists.Get("plmVariants", capi, mod, session, holder.Key);
            if (variants.Count == 0) return true;
            string current = map[holder.Key]?.ToString() ?? "";
            int index = variants.FindIndex(option => option.Value == current);
            int next = index < 0 ? 0 : (index + step + variants.Count) % variants.Count;
            map[holder.Key] = variants[next].Value;
            Compose();
            return true;
        }
    }
}
