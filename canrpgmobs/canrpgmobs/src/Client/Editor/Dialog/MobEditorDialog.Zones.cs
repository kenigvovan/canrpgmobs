using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using canrpgclasses.Client;
using canrpgmobs.Client.Editor.Controllers;
using canrpgmobs.Data;
using canrpgmobs.Runtime.Zones;
using Newtonsoft.Json.Linq;
using Vintagestory.API.Client;
using static canrpgmobs.Client.Editor.Controllers.JsonDraft;
using static canrpgmobs.Client.UiKit;

namespace canrpgmobs.Client.Editor
{
    // simple mode tabs of a zone; the outline itself is edited on the map
    public partial class MobEditorDialog
    {
        // spawn row columns, from the row's x
        private const double SpawnWeightX = 230;
        private const double SpawnCountX = 296;
        private const double SpawnTimeX = 402;
        private const double SpawnShareX = 498;

        private void ZoneMainRows(List<FormRow> rows, double x, JObject d)
        {
            double cx = x + SimpleLabelW;
            double half = SimpleControlW / 2 - 2;
            rows.Add(new FormRow(1, (c, y) =>
            {
                SimpleLabel(c, x, y, L("canrpgmobs:zones-enabled", "Works"), "enabled");
                YesNoToggle(c, d, "enabled", true, cx, y, 100);
            }));
            rows.Add(new FormRow(1, (c, y) =>
            {
                SimpleLabel(c, x, y, L("canrpgmobs:zones-level", "Level (min / max)"), "level");
                RangeInputs(c, d, "level", new FieldSpec("level", FieldKind.IntRangePair) { Min = 1, Max = 1000 }, cx, y, half, 4);
            }));
            rows.Add(new FormRow(1, (c, y) =>
            {
                SimpleLabel(c, x, y, L("canrpgmobs:zones-priority", "Priority"), "priority");
                NumberInput(c, B(cx, y, 100), ReadNumber(d["priority"]), v => SetNumber(d, "priority", v, new FieldSpec("priority", FieldKind.Int) { Min = -1000, Max = 1000 }));
            }));
            var factions = FactionIds();
            rows.Add(ChoiceRow(x, d, "faction", L("canrpgmobs:editor-simple-faction", "Faction"), factions, factions.Select(f => NameOf("factions", f)).ToArray(),
                L("canrpgmobs:spawner-own-faction", "(the mob's own)")));
            rows.Add(ChoiceRow(x, d, "leash", L("canrpgmobs:zones-leash", "Leash"), ZoneRules.LeashModes,
                ZoneRules.LeashModes.Select(m => L("canrpgmobs:zones-leash-" + m, m)).ToArray(), null));
            rows.Add(new FormRow(1, (c, y) =>
            {
                SimpleLabel(c, x, y, L("canrpgmobs:zones-announce", "Tell players on entry"), "announce");
                YesNoToggle(c, d, "announce", true, cx, y, 100);
            }));
            rows.Add(new FormRow(1, (c, y) =>
            {
                SimpleLabel(c, x, y, L("canrpgmobs:zones-color", "Map color (#rrggbb)"), "color");
                TextInput(c, B(cx, y, 100), ReadString(d, "color"), v => SetString(d, "color", v.Trim(), false));
            }));
        }

        // race, then role, instead of one list of every pair
        private bool PickRaceRole(Action<string> apply)
        {
            return OpenPick("racesNamed", L("canrpgmobs:editor-simple-race", "Race"), null, race =>
                // the picker closes after apply, so the second list opens on the next tick
                capi.Event.RegisterCallback(_ => OpenPick("rolesNamed", L("canrpgmobs:editor-simple-role", "Role"), null,
                    role => apply(race + IdRules.KeySeparator + role)), 1));
        }

        private void ZoneSpawnRows(List<FormRow> rows, double x, JObject d)
        {
            rows.Add(new FormRow(1, (c, y) =>
            {
                const double bw = 128;
                c.AddFitText(L("canrpgmobs:zones-spawns", "Who spawns here"), UiKit.SectionFont(), B(x, y + 2, PanelW - 3 * (bw + 4)))
                    .AddFitButton(L("canrpgmobs:zones-spawn-add-mob", "+ Mob"), () => OpenPick("mobsNamed", L("canrpgmobs:zones-spawn-mob", "Mob"), null,
                        v => EnsureArray(d, "spawns").Add(new JObject { ["mob"] = v, ["weight"] = 10 })), B(x + PanelW - 3 * bw - 8, y, bw), EnumButtonStyle.Small)
                    .AddFitButton(L("canrpgmobs:zones-spawn-add-pair", "+ Race and role"), () => PickRaceRole(key => EnsureArray(d, "spawns").Add(new JObject { ["mob"] = key, ["weight"] = 10 })),
                        B(x + PanelW - 2 * bw - 4, y, bw), EnumButtonStyle.Small)
                    .AddFitButton(L("canrpgmobs:zones-spawn-add-group", "+ Group"), () => OpenPick("groups", L("canrpgmobs:zones-spawn-group", "Group"), null,
                        v => EnsureArray(d, "spawns").Add(new JObject { ["group"] = v, ["weight"] = 10 })), B(x + PanelW - bw, y, bw), EnumButtonStyle.Small);
            }));
            var spawns = (d["spawns"] as JArray)?.OfType<JObject>().ToList() ?? new List<JObject>();
            if (spawns.Count == 0)
            {
                rows.Add(TextRow(x, L("canrpgmobs:zones-spawns-none", "Nothing yet: only spawner blocks set to this zone bring mobs."), false));
                return;
            }
            rows.Add(new FormRow(1, (c, y) => c
                .AddStaticText(L("canrpgmobs:zones-spawn-col-weight", "weight"), HelpFont(), B(x + SpawnWeightX, y + 6, 60))
                .AddStaticText(L("canrpgmobs:zones-spawn-col-count", "count"), HelpFont(), B(x + SpawnCountX, y + 6, 100))
                .AddStaticText(L("canrpgmobs:zones-spawn-col-time", "time"), HelpFont(), B(x + SpawnTimeX, y + 6, 90))));
            var times = ZoneRules.Times;
            foreach (var spawn in spawns)
            {
                var e = spawn;
                rows.Add(new FormRow(1, (c, y) =>
                {
                    string who = e["mob"] is JValue mob ? ShortKey(mob.ToString()) : L("canrpgmobs:zones-spawn-group", "Group") + " " + Short(ReadString(e, "group"));
                    c.AddFitText(who, EditorStyle.Body(), B(x + 4, y + 4, SpawnWeightX - 8));
                    NumberInput(c, B(x + SpawnWeightX, y, 60), ReadNumber(e["weight"]), v => SetNumber(e, "weight", v, new FieldSpec("weight", FieldKind.Number) { Min = 0, Max = 100000 }));
                    if (e["group"] == null)
                        RangeInputs(c, e, "count", new FieldSpec("count", FieldKind.IntRangePair) { Min = 1, Max = 50 }, x + SpawnCountX, y, 48, 4);
                    string time = ReadString(e, "time");
                    c.AddDropDown(times, times.Select(t => L("canrpgmobs:zones-time-" + t, t)).ToArray(), Math.Max(0, Array.IndexOf(times, time.Length == 0 ? ZoneRules.AnyTime : time)),
                        (code, _) =>
                        {
                            if (code == ZoneRules.AnyTime) e.Remove("time");
                            else e["time"] = code;
                            Compose();
                        }, B(x + SpawnTimeX, y, 90), NextKey());
                    LiveText(c, B(x + SpawnShareX, y + 4, 60), UiKit.ValueFont(), () => "≈ " + SpawnShare(d, e));
                    RemoveButton(c, x, y, () =>
                    {
                        (d["spawns"] as JArray)?.Remove(e);
                        RemoveIfEmpty(d, "spawns");
                    });
                }));
            }
        }

        private static string SpawnShare(JObject d, JObject e)
        {
            float total = 0;
            foreach (var s in (d["spawns"] as JArray)?.OfType<JObject>() ?? Enumerable.Empty<JObject>()) total += Math.Max(0, ReadFloat(s, "weight", 1f));
            float own = Math.Max(0, ReadFloat(e, "weight", 1f));
            return total <= 0 ? "0%" : (own / total * 100f).ToString("0", CultureInfo.InvariantCulture) + "%";
        }

        private void ZoneDensityRows(List<FormRow> rows, double x, JObject d)
        {
            double cx = x + SimpleLabelW;
            double half = SimpleControlW / 2 - 2;
            FormRow IntRow(string key, string label, float min, float max) => new(1, (c, y) =>
            {
                SimpleLabel(c, x, y, label, key);
                NumberInput(c, B(cx, y, 100), ReadNumber(d[key]), v => SetNumber(d, key, v, new FieldSpec(key, FieldKind.Number) { Min = min, Max = max }));
            });
            FormRow PairRow(string key, string label, float min, float max) => new(1, (c, y) =>
            {
                SimpleLabel(c, x, y, label, key);
                RangeInputs(c, d, key, new FieldSpec(key, FieldKind.IntRangePair) { Min = min, Max = max }, cx, y, half, 4);
            });

            rows.Add(IntRow("maxAlive", L("canrpgmobs:zones-max-alive", "Alive at most"), 0, 500));
            rows.Add(IntRow("perPlayer", L("canrpgmobs:zones-per-player", "Per player inside"), 0, 100));
            rows.Add(IntRow("respawnSeconds", L("canrpgmobs:zones-respawn", "Respawn, seconds"), 0, 86400));
            rows.Add(PairRow("spawnDistance", L("canrpgmobs:zones-distance", "From a player (min / max)"), 0, 128));
            rows.Add(ChoiceRow(x, d, "surface", L("canrpgmobs:zones-surface", "Where"), ZoneRules.Surfaces,
                ZoneRules.Surfaces.Select(s => L("canrpgmobs:zones-surface-" + s, s)).ToArray(), null));
            rows.Add(PairRow("light", L("canrpgmobs:zones-light", "Light (min / max)"), 0, 32));
            rows.Add(IntRow("despawnAfterSeconds", L("canrpgmobs:zones-despawn", "Gone after no players, s"), 0, 86400));
            rows.Add(ChoiceRow(x, d, "vanilla", L("canrpgmobs:zones-vanilla", "Vanilla mobs"), ZoneRules.VanillaModes,
                ZoneRules.VanillaModes.Select(m => L("canrpgmobs:zones-vanilla-" + m, m)).ToArray(), null));
            if (ReadString(d, "vanilla") == ZoneRules.Deny)
                rows.Add(new FormRow(1, (c, y) =>
                {
                    SimpleLabel(c, x, y, L("canrpgmobs:zones-vanilla-deny-codes", "Not these (codes, comma)"), "vanillaDeny");
                    var list = (d["vanillaDeny"] as JArray)?.Select(t => t.ToString()) ?? Enumerable.Empty<string>();
                    TextInput(c, B(cx, y, SimpleControlW), string.Join(", ", list), v => SetList(d, "vanillaDeny", v));
                }));
        }

        private void ZoneShapeRows(List<FormRow> rows, double x, JObject d)
        {
            var polygon = (d["polygon"] as JArray)?.OfType<JArray>().Where(p => p.Count == 2).Select(p => ((int)p[0], (int)p[1])).ToArray()
                          ?? Array.Empty<(int, int)>();
            string summary = polygon.Length >= 3
                ? L("canrpgmobs:zones-size", "{0} corners, area {1}, border {2} blocks", polygon.Length,
                    ZoneGeometry.Area(polygon).ToString("0", CultureInfo.InvariantCulture), ZoneGeometry.Perimeter(polygon).ToString("0", CultureInfo.InvariantCulture))
                : L("canrpgmobs:zones-size-open", "{0} of at least 3 corners", polygon.Length);
            rows.Add(TextRow(x, summary, polygon.Length < 3));
            rows.Add(TextRow(x, L("canrpgmobs:zones-shape-hint", "The outline is drawn on the world map in markup mode: drag corners, right click to add or remove."), false));
            rows.Add(new FormRow(1, (c, y) =>
            {
                bool saved = session.OpenedId != null && mod.Catalog.Zones.ContainsKey(Q(session.OpenedId));
                string text = saved ? L("canrpgmobs:zones-edit-on-map", "Edit on the map") : L("canrpgmobs:zones-draw-on-map", "Draw a new zone on the map");
                c.AddSmallButton(text, () =>
                {
                    if (saved) mod.ShowZoneOnMap(capi, Q(session.OpenedId!));
                    else mod.OpenZonePanel(capi);
                    return true;
                }, B(x, y, 260), EnumButtonStyle.Small);
                if (session.IsDirty)
                    c.AddStaticText(L("canrpgmobs:zones-save-first", "Save here first, the map edits the saved zone."), HelpFont(), B(x + 270, y + 4, PanelW - 270, RowStep));
            }));
            foreach (var (cx, cz) in polygon.Take(12))
            {
                var (lx, lz) = ZoneText.ToLocal(capi, cx, cz);
                rows.Add(TextRow(x + 16, $"{lx}, {lz}", false));
            }
            if (polygon.Length > 12) rows.Add(TextRow(x + 16, L("canrpgmobs:zones-more-corners", "... and {0} more", polygon.Length - 12), false));
        }
    }
}
