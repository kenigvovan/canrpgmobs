using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using canrpgclasses.Client;
using canrpgmobs.Catalog;
using canrpgmobs.Data;
using canrpgmobs.Runtime.Spawning;
using canrpgmobs.Runtime.Zones;
using Newtonsoft.Json;
using Vintagestory.API.Client;
using Vintagestory.API.Config;
using Vintagestory.API.MathTools;
using static canrpgmobs.Client.Editor.Controllers.JsonDraft;
using static canrpgmobs.Client.UiKit;

namespace canrpgmobs.Client.Spawner
{
    // Saves via BE packets; the server re-validates and replies in chat.
    public class MobSpawnerDialog : GuiDialog
    {
        private const string ComposerKey = "canrpgmobsspawner";
        private const double Width = 470;
        private const double LabelW = 170;
        private const double RowStep = 30;

        private readonly BlockPos pos;
        private readonly SpawnerConfig draft;
        private readonly List<(string key, string value)> fills = new();
        // mob, group or zone
        private string mode;
        private bool GroupMode => mode == "group";
        private bool filling;
        private long statusTickId;
        private int keyCounter;

        public MobSpawnerDialog(ICoreClientAPI capi, BlockPos pos, SpawnerConfig current) : base(capi)
        {
            this.pos = new BlockPos(pos.X, pos.Y, pos.Z, pos.dimension);
            draft = current.Clamped();
            mode = draft.FromZone ? "zone" : draft.GroupId != null ? "group" : "mob";
        }

        public override string ToggleKeyCombinationCode => null!;

        public override void OnGuiOpened()
        {
            base.OnGuiOpened();
            Compose();
            statusTickId = capi.Event.RegisterGameTickListener(_ => UpdateStatus(), 1000);
        }

        public override void OnGuiClosed()
        {
            base.OnGuiClosed();
            if (statusTickId != 0) capi.Event.UnregisterGameTickListener(statusTickId);
            statusTickId = 0;
        }

        private BEMobSpawner? Spawner => capi.World.BlockAccessor.GetBlockEntity(pos) as BEMobSpawner;

        private void Compose()
        {
            fills.Clear();
            keyCounter = 0;
            var catalog = canrpgmobsModSystem.For(capi)?.Catalog;
            var background = ElementBounds.Fixed(0, 0, Width + 40, 12 * RowStep + 70);

            ClearComposers();
            var compo = capi.Gui.CreateCompo(ComposerKey, ElementStdBounds.AutosizedMainDialog.WithAlignment(EnumDialogArea.CenterMiddle))
                .AddShadedDialogBG(background, true, 5.0, 0.75f)
                .AddDialogTitleBar(L("canrpgmobs:spawner-title", "Mob spawner"), () => TryClose())
                .BeginChildElements(ElementBounds.Fixed(20, 34, Width, background.fixedHeight - 34));

            double y = 0;
            Label(compo, y, L("canrpgmobs:spawner-mode", "Spawns"));
            var modes = new[] { "mob", "group", "zone" };
            compo.AddDropDown(modes, new[] { L("canrpgmobs:spawner-mode-mob", "a mob"), L("canrpgmobs:spawner-mode-group", "a group"), L("canrpgmobs:spawner-mode-zone", "as in the zone") },
                Array.IndexOf(modes, mode), (code, _) => { mode = code; Compose(); }, B(LabelW, y, 180), NextKey());

            y += RowStep;
            Label(compo, y, mode == "zone" ? L("canrpgmobs:spawner-target-zone", "Zone") : GroupMode ? L("canrpgmobs:spawner-target-group", "Group") : L("canrpgmobs:spawner-target-mob", "Mob"));
            var targets = TargetValues(catalog);
            string current = (GroupMode ? draft.GroupId : draft.MobKey) ?? "";
            if (mode == "zone")
            {
                var zone = Spawner?.ZoneHere(capi)?.Model;
                string levels = zone == null ? "" : ZoneText.Levels(zone);
                string text = zone == null
                    ? L("canrpgmobs:spawner-no-zone", "no zone covers this block")
                    : ZoneText.Name(zone, Lang.CurrentLocale) + (levels.Length > 0 ? $" ({L("canrpgmobs:zone-level", "lvl {0}", levels)})" : "");
                compo.AddStaticText(text, zone == null ? EditorStyle.Status(true) : EditorStyle.Body(), B(LabelW, y + 4, Width - LabelW));
            }
            else if (targets.Length == 0)
                compo.AddStaticText(L("canrpgmobs:spawner-none", "Nothing to pick yet"), EditorStyle.Body(), B(LabelW, y + 4, Width - LabelW));
            else
            {
                // keep an unknown id in the list, otherwise saving would silently switch to the first entry
                int selected = Array.IndexOf(targets, current);
                var values = selected < 0 && current.Length > 0 ? targets.Prepend(current).ToArray() : targets;
                var names = values.Select(t => t == current && selected < 0
                    ? L("canrpgmobs:spawner-unknown-target", "{0} (not in the catalog)", ShortKey(t))
                    : ShortKey(t)).ToArray();
                compo.AddDropDown(values, names, Math.Max(0, Array.IndexOf(values, current)), (code, _) => SetTarget(code),
                    B(LabelW, y, Width - LabelW), NextKey());
            }

            y += RowStep;
            NumberRow(compo, y, L("canrpgmobs:spawner-max", "Max alive"), draft.MaxAlive.ToString(CultureInfo.InvariantCulture),
                v => draft.MaxAlive = int.TryParse(v, out int n) ? n : draft.MaxAlive);
            y += RowStep;
            NumberRow(compo, y, L("canrpgmobs:spawner-respawn", "Respawn, seconds"), Num(draft.RespawnSeconds), v => draft.RespawnSeconds = ParseFloat(v, draft.RespawnSeconds));
            y += RowStep;
            NumberRow(compo, y, L("canrpgmobs:spawner-radius", "Radius, blocks"), Num(draft.Radius), v => draft.Radius = ParseFloat(v, draft.Radius));
            y += RowStep;
            NumberRow(compo, y, L("canrpgmobs:spawner-activation", "Works within, blocks"), Num(draft.ActivationRange), v => draft.ActivationRange = ParseFloat(v, draft.ActivationRange));
            y += RowStep;
            NumberRow(compo, y, mode == "zone" ? L("canrpgmobs:spawner-level-zone", "Level (0 = the zone's)") : L("canrpgmobs:spawner-level", "Level (0 = template range)"),
                draft.Level.ToString(CultureInfo.InvariantCulture),
                v => draft.Level = int.TryParse(v, out int n) ? n : draft.Level);

            y += RowStep;
            Label(compo, y, L("canrpgmobs:spawner-tier", "Tier"));
            var tiers = new[] { "" }.Concat(catalog?.Tiers.Keys.OrderBy(k => k) ?? Enumerable.Empty<string>()).ToArray();
            compo.AddDropDown(tiers, tiers.Select(t => t.Length == 0 ? L("canrpgmobs:editor-default", "(default)") : Short(t)).ToArray(),
                Math.Max(0, Array.IndexOf(tiers, draft.Tier ?? "")), (code, _) => draft.Tier = code.Length == 0 ? null : code, B(LabelW, y, 200), NextKey());

            y += RowStep;
            Label(compo, y, L("canrpgmobs:spawner-faction", "Faction"));
            var factions = FactionMatrix.WithBuiltIns(catalog?.Factions.Factions.Keys ?? Enumerable.Empty<string>()).Prepend("").ToArray();
            compo.AddDropDown(factions, factions.Select(f => f.Length == 0 ? L("canrpgmobs:spawner-own-faction", "(the mob's own)") : Short(f)).ToArray(),
                Math.Max(0, Array.IndexOf(factions, draft.Faction ?? "")), (code, _) => draft.Faction = code.Length == 0 ? null : code, B(LabelW, y, 200), NextKey());

            y += RowStep + 6;
            compo.AddDynamicText(StatusText(), EditorStyle.Status(false), B(0, y, Width, 44), "status");

            y += 50;
            double bw = (Width - 18) / 4;
            compo.AddSmallButton(L("canrpgmobs:spawner-save", "Save"), OnSave, B(0, y, bw), EnumButtonStyle.Small)
                .AddSmallButton(L("canrpgmobs:spawner-spawn-now", "Spawn now"), () => Send(BEMobSpawner.PacketSpawnNow), B(bw + 6, y, bw), EnumButtonStyle.Small)
                .AddSmallButton(L("canrpgmobs:spawner-clear", "Clear"), () => Send(BEMobSpawner.PacketClear), B((bw + 6) * 2, y, bw), EnumButtonStyle.Small)
                .AddSmallButton(L("canrpgmobs:spawner-close", "Close"), () => TryClose(), B((bw + 6) * 3, y, bw), EnumButtonStyle.Small);

            SingleComposer = compo.EndChildElements().Compose();
            filling = true;
            try
            {
                foreach (var (key, value) in fills) SingleComposer.GetNumberInput(key)?.SetValue(value);
            }
            finally
            {
                filling = false;
            }
        }

        private string[] TargetValues(MobCatalog? catalog)
        {
            if (catalog == null) return Array.Empty<string>();
            if (GroupMode) return catalog.Groups.Keys.OrderBy(k => k).ToArray();
            var races = catalog.Races.Keys.OrderBy(k => k).ToList();
            var roles = catalog.Roles.Keys.OrderBy(k => k).ToList();
            return catalog.Templates.Keys.OrderBy(k => k)
                .Concat(races.SelectMany(race => roles.Select(role => race + IdRules.KeySeparator + role)))
                .ToArray();
        }

        private void SetTarget(string code)
        {
            if (GroupMode) draft.GroupId = code;
            else draft.MobKey = code;
        }

        private bool OnSave()
        {
            var config = new SpawnerConfig
            {
                MobKey = mode == "mob" ? draft.MobKey : null, GroupId = GroupMode ? draft.GroupId : null, UseZone = mode == "zone", Level = draft.Level, Tier = draft.Tier,
                Faction = draft.Faction, MaxAlive = draft.MaxAlive, RespawnSeconds = draft.RespawnSeconds, Radius = draft.Radius,
                ActivationRange = draft.ActivationRange
            };
            return Send(BEMobSpawner.PacketSave, Encoding.UTF8.GetBytes(JsonConvert.SerializeObject(config)));
        }

        private bool Send(int packetId, byte[]? data = null)
        {
            capi.Network.SendBlockEntityPacket(pos, packetId, data);
            return true;
        }

        private string StatusText()
        {
            var spawner = Spawner;
            if (spawner == null) return L("canrpgmobs:spawner-gone", "The spawner block is gone.");
            var config = spawner.Config;
            var text = new StringBuilder(config.IsSet
                ? L("canrpgmobs:spawner-status", "Saved: {0}, {1} of {2} alive", config.FromZone ? L("canrpgmobs:spawner-mode-zone", "as in the zone") : ShortKey(config.GroupId ?? config.MobKey ?? ""),
                    spawner.AliveCount, config.MaxAlive)
                : L("canrpgmobs:spawner-off", "Spawner is off"));
            if (!string.IsNullOrEmpty(spawner.LastError)) text.Append('\n').Append(L("canrpgmobs:spawner-problem", "Problem: {0}", spawner.LastError!));
            return text.ToString();
        }

        private void UpdateStatus() => SingleComposer?.GetDynamicText("status")?.SetNewText(StatusText(), false, false, false);

        private void Label(GuiComposer compo, double y, string text)
            => compo.AddStaticText(text, EditorStyle.FieldLabel(), B(0, y + 4, LabelW - 6));

        private void NumberRow(GuiComposer compo, double y, string label, string value, Action<string> onChanged)
        {
            Label(compo, y, label);
            string key = NextKey();
            compo.AddNumberInput(B(LabelW, y, 120), v => { if (!filling) onChanged(v); }, CairoFont.WhiteDetailText(), key);
            fills.Add((key, value));
        }

        private string NextKey() => "s" + keyCounter++;

        private static float ParseFloat(string text, float fallback) => TryParse(text, out float value) ? value : fallback;
    }
}
