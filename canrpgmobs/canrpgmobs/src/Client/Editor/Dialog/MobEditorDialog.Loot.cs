using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using canrpgclasses.Client;
using canrpgmobs.Catalog;
using canrpgmobs.Client.Editor.Controllers;
using canrpgmobs.Data;
using Newtonsoft.Json.Linq;
using Vintagestory.API.Client;
using static canrpgmobs.Client.Editor.Controllers.JsonDraft;
using static canrpgmobs.Client.UiKit;

namespace canrpgmobs.Client.Editor
{
    // Loot in simple mode: table editor, test roll and the loot tab of mobs/races/roles.
    public partial class MobEditorDialog
    {
        private const int LootTestKills = 100;
        // entry row columns, from the row's x
        private const double LootNameX = 30;
        private const double LootCountX = 184;
        private const double LootWeightX = 274;
        private const double LootChanceX = 336;
        private const double LootShareX = 398;

        // where to return after opening a table from a mob/race/role; AddTable = add the newly saved table there
        private (string Section, string Id, bool AddTable)? lootReturn;
        private int lootTestLevel = 1;
        private string lootTestTier = "";
        private List<(string Type, string Code, int Kills, long Total)>? lootTestResult;
        // to detect a stale test result
        private string lootTestJson = "";

        private void LootTableRows(List<FormRow> rows, double x, JObject d)
        {
            double cx = x + SimpleLabelW;
            double half = SimpleControlW / 2 - 2;

            rows.Add(new FormRow(1, (c, y) =>
            {
                SimpleLabel(c, x, y, L("canrpgmobs:editor-loot-rolls", "Picks per kill (min / max)"), "rolls");
                RangeInputs(c, d, "rolls", new FieldSpec("rolls", FieldKind.IntRangePair) { Min = 0, Max = 100 }, cx, y, half, 4);
            }));
            rows.Add(new FormRow(1, (c, y) =>
            {
                SimpleLabel(c, x, y, L("canrpgmobs:editor-loot-empty", "Weight of \"nothing\""), "emptyWeight", () =>
                {
                    float sum = LootTotalWeight(d);
                    return L("canrpgmobs:editor-loot-empty-chance", "Nothing: {0}", Percent(sum <= 0 ? 0f : ReadFloat(d, "emptyWeight", 0f) / sum));
                });
                NumberInput(c, B(cx, y, 120), ReadNumber(d["emptyWeight"]), v => SetNumber(d, "emptyWeight", v, new FieldSpec("emptyWeight", FieldKind.Number) { Min = 0, Max = 100000 }));
            }));
            rows.Add(new FormRow(1, (c, y) =>
            {
                SimpleLabel(c, x, y, L("canrpgmobs:editor-loot-levelscale", "More per level"), "levelCountScale");
                NumberInput(c, B(cx, y, 120), ReadNumber(d["levelCountScale"]), v => SetNumber(d, "levelCountScale", v, new FieldSpec("levelCountScale", FieldKind.Number) { Min = 0, Max = 10 }));
            }));
            rows.Add(new FormRow(1, (c, y) =>
            {
                SimpleLabel(c, x, y, L("canrpgmobs:editor-loot-tierbonus", "Extra picks for strong tiers"), "tierRollBonus");
                YesNoToggle(c, d, "tierRollBonus", true, cx, y, 120);
            }));

            LootEntryRows(rows, x, d, "entries");
            LootEntryRows(rows, x, d, "guaranteed");
            bool limited = new[] { "entries", "guaranteed" }.Any(key => (d[key] as JArray)?.OfType<JObject>().Any(e => e["minLevel"] != null || e["tiers"] != null) == true);
            if (limited) rows.Add(TextRow(x, L("canrpgmobs:editor-loot-limited", "* limited to some levels or tiers (advanced mode)"), false));
        }

        private void LootEntryRows(List<FormRow> rows, double x, JObject d, string listKey)
        {
            bool guaranteed = listKey == "guaranteed";
            rows.Add(new FormRow(1, (c, y) =>
            {
                c.AddStaticText(guaranteed ? L("canrpgmobs:editor-loot-always", "Always drops") : L("canrpgmobs:editor-loot-random", "Random picks"), UiKit.SectionFont(), B(x, y + 2, 260))
                    .AddSmallButton(L("canrpgmobs:editor-loot-add-item", "+ Item"), () => OpenItemPick(L("canrpgmobs:editor-loot-pick", "Loot"), new[] { "all", "wearable", "hand", "block" },
                        v => { if (v != null) AddLootEntry(d, listKey, v, null); }, false), B(x + PanelW - 260, y, 126), EnumButtonStyle.Small)
                    .AddSmallButton(L("canrpgmobs:editor-loot-add-table", "+ Table"), () => OpenPick("lootTables", L("canrpgmobs:editor-loot-pick", "Loot"), null,
                        v => AddLootEntry(d, listKey, v, LootEntryModel.Table)), B(x + PanelW - 128, y, 124), EnumButtonStyle.Small);
            }));
            if (d[listKey] is not JArray list || list.Count == 0) return;

            rows.Add(new FormRow(1, (c, y) =>
            {
                c.AddStaticText(L("canrpgmobs:editor-loot-col-item", "Item"), EditorStyle.FieldLabel(), B(x + LootNameX, y + 6, 150))
                    .AddStaticText(L("canrpgmobs:editor-loot-col-count", "Count"), EditorStyle.FieldLabel(), B(x + LootCountX, y + 6, 84))
                    .AddStaticText(L("canrpgmobs:editor-loot-col-chance", "Chance %"), EditorStyle.FieldLabel(), B(x + LootChanceX, y + 6, 60))
                    .AddStaticText(L("canrpgmobs:editor-loot-col-result", "Per pick"), EditorStyle.FieldLabel(), B(x + LootShareX, y + 6, 110));
                if (!guaranteed) c.AddStaticText(L("canrpgmobs:editor-loot-col-weight", "Weight"), EditorStyle.FieldLabel(), B(x + LootWeightX, y + 6, 56));
            }));
            foreach (var entry in list.OfType<JObject>().ToList())
            {
                var e = entry;
                rows.Add(new FormRow(1, (c, y) => LootEntryRow(c, x, y, d, listKey, list, e, guaranteed)));
            }
        }

        private void LootEntryRow(GuiComposer c, double x, double y, JObject d, string listKey, JArray list, JObject e, bool guaranteed)
        {
            string type = ReadString(e, "type");
            if (type.Length == 0) type = LootEntryModel.Item;
            string code = ReadString(e, "code");

            string name;
            if (type == LootEntryModel.Table)
            {
                c.AddStaticText("T", UiKit.SectionFont(), B(x + 8, y + 2, 20));
                name = Short(code);
            }
            else
            {
                bool block = type == LootEntryModel.Block;
                LootIcon(c, x, y + 2, code, block);
                name = ItemCatalog.Find(capi, code, block)?.Name ?? code;
            }
            c.AddFitText(name, EditorStyle.Body(), B(x + LootNameX, y + 4, 150));

            if (type != LootEntryModel.Table)
                RangeInputs(c, e, "count", new FieldSpec("count", FieldKind.IntRangePair) { Min = 0, Max = 1000 }, x + LootCountX, y, 40, 4);
            if (!guaranteed)
                NumberInput(c, B(x + LootWeightX, y, 56), ReadNumber(e["weight"]), v => SetNumber(e, "weight", v, new FieldSpec("weight", FieldKind.Number) { Min = 0, Max = 100000 }));

            float chance = ReadFloat(e, "chance", 1f);
            NumberInput(c, B(x + LootChanceX, y, 56), (chance * 100f).ToString("0.##", CultureInfo.InvariantCulture), v =>
            {
                if (v.Trim().Length == 0)
                {
                    e.Remove("chance");
                    return;
                }
                if (!TryParse(v, out float percent)) return;
                float fraction = Math.Clamp(percent, 0f, 100f) / 100f;
                // 100% is the default
                if (fraction >= 1f) e.Remove("chance");
                else e["chance"] = fraction;
            });

            LiveText(c, B(x + LootShareX, y + 4, 110), UiKit.ValueFont(), () =>
            {
                float share = LootRoller.PickChance(ReadFloat(e, "weight", 1f), ReadFloat(e, "chance", 1f), LootTotalWeight(d), guaranteed);
                return "≈ " + Percent(share) + (e["minLevel"] != null || e["tiers"] != null ? " *" : "");
            });
            RemoveButton(c, x, y, () =>
            {
                list.Remove(e);
                RemoveIfEmpty(d, listKey);
            });
        }

        private void AddLootEntry(JObject d, string listKey, string code, string? type)
        {
            var list = EnsureArray(d, listKey);
            // block vs item comes from the picker category (codes can collide); game: domain is omitted like in files
            string kind = type ?? (itemCategory == "block" ? LootEntryModel.Block : LootEntryModel.Item);
            string written = kind == LootEntryModel.Table
                ? Short(code)
                : code.StartsWith("game:", StringComparison.Ordinal) ? code.Substring(5) : code;
            var entry = new JObject { ["code"] = written };
            if (kind != LootEntryModel.Item) entry.AddFirst(new JProperty("type", kind));
            list.Add(entry);
        }

        private void LootIcon(GuiComposer compo, double x, double y, string code, bool block)
        {
            var entry = ItemCatalog.Find(capi, code, block);
            if (entry == null)
            {
                compo.AddStaticText("?", EditorStyle.Status(true), B(x + 8, y + 2, 14));
                return;
            }
            var slot = entry.Slot;
            compo.AddCustomRender(B(x, y, 24, 24), (dt, bounds) =>
                capi.Render.RenderItemstackToGui(slot, bounds.renderX + bounds.InnerWidth / 2, bounds.renderY + bounds.InnerHeight / 2, 100,
                    (float)GuiElement.scaled(17), -1, true, false, false));
            compo.AddHoverText(entry.Name, CairoFont.WhiteDetailText(), 300, B(x, y, 24, 24));
        }

        private static float LootTotalWeight(JObject d)
        {
            float total = Math.Max(0f, ReadFloat(d, "emptyWeight", 0f));
            if (d["entries"] is JArray entries)
                foreach (var entry in entries.OfType<JObject>()) total += Math.Max(0f, ReadFloat(entry, "weight", 1f));
            return total;
        }

        private static string Percent(float fraction) => (fraction * 100f).ToString("0.#", CultureInfo.InvariantCulture) + "%";

        private void LootTestRows(List<FormRow> rows, double x, JObject d)
        {
            double cx = x + SimpleLabelW;
            rows.Add(new FormRow(1, (c, y) =>
            {
                SimpleLabel(c, x, y, L("canrpgmobs:editor-loottest-level", "Mob level / tier"), null);
                NumberInput(c, B(cx, y, 60), lootTestLevel.ToString(CultureInfo.InvariantCulture), v => lootTestLevel = int.TryParse(v, out int level) ? Math.Clamp(level, 1, 1000) : lootTestLevel);
                TierDropDown(c, lootTestTier, code => lootTestTier = code, L("canrpgmobs:editor-simple-normal-tier", "(normal)"), B(cx + 66, y, SimpleControlW - 66));
                c.AddSmallButton(L("canrpgmobs:editor-loottest-run", "Roll {0} kills", LootTestKills), () => { RunLootTest(d); Compose(); return true; },
                    B(x + PanelW - 180, y, 176), EnumButtonStyle.Small);
            }));
            if (lootTestResult == null)
            {
                rows.Add(TextRow(x, L("canrpgmobs:editor-loottest-note", "Rolls this draft as the server does, nested tables included. Nothing is saved or dropped."), false));
                return;
            }
            string summary = L("canrpgmobs:editor-loottest-summary", "{0} kills: {1} kinds of drops.", LootTestKills, lootTestResult.Count);
            // warn if the table was edited after the roll
            rows.Add(new FormRow(1, (c, y) => LiveText(c, B(x, y + 4, PanelW, 24), EditorStyle.Body(), () => Clip(summary
                + (lootTestJson.Length > 0 && d.ToString() != lootTestJson
                    ? "  " + L("canrpgmobs:editor-loottest-stale", "The table changed since this roll.")
                    : ""), 96))));
            foreach (var result in lootTestResult)
            {
                var r = result;
                rows.Add(new FormRow(1, (c, y) =>
                {
                    bool block = r.Type == LootEntryModel.Block;
                    LootIcon(c, x, y + 2, r.Code, block);
                    string name = ItemCatalog.Find(capi, r.Code, block)?.Name ?? r.Code;
                    c.AddFitText(name, EditorStyle.Body(), B(x + 30, y + 4, 240))
                        .AddStaticText(L("canrpgmobs:editor-loottest-row", "in {0} of kills, {1} per kill", Percent(r.Kills / (float)LootTestKills),
                            (r.Total / (double)LootTestKills).ToString("0.##", CultureInfo.InvariantCulture)), EditorStyle.FieldLabel(), B(x + 280, y + 4, PanelW - 280));
                }));
            }
        }

        private void RunLootTest(JObject d)
        {
            LootTableModel? table;
            try { table = d.ToObject<LootTableModel>(); }
            catch (Exception e)
            {
                SetStatus(L("canrpgmobs:editor-loottest-failed", "Can't read this table: {0}", e.Message), true);
                return;
            }
            if (table == null) return;

            string id = Q(session.DraftId);
            table.id = id;
            CatalogLoader.QualifyLootTable(table, canrpgmobsModSystem.ModId);
            var tables = new Dictionary<string, LootTableModel>(mod.Catalog.Data.LootTables) { [id] = table };
            var tier = mod.Catalog.Tier(lootTestTier.Length == 0 ? null : lootTestTier);
            var rng = new Random();
            var totals = new Dictionary<(string, string), (int kills, long total)>();
            for (int i = 0; i < LootTestKills; i++)
            {
                foreach (var drop in LootRoller.Roll(new[] { id }, tables, lootTestLevel, tier, rng))
                {
                    totals.TryGetValue((drop.Type, drop.Code), out var sum);
                    totals[(drop.Type, drop.Code)] = (sum.kills + 1, sum.total + drop.Count);
                }
            }
            lootTestResult = totals.OrderByDescending(p => p.Value.kills).Select(p => (p.Key.Item1, p.Key.Item2, p.Value.kills, p.Value.total)).ToList();
            lootTestJson = d.ToString();
        }

        private void SimpleLootRows(List<FormRow> rows, double x, JObject d, ResolvedMob? eff)
        {
            bool mob = session.Section == "mobs";
            bool replace = ReadString(d, "lootMode") == MergeModes.Replace;
            if (mob)
            {
                rows.Add(new FormRow(1, (c, y) => InheritToggle(c, x, y, L("canrpgmobs:editor-simple-loot-mode", "Race and role loot"), "lootMode", !replace, () =>
                {
                    if (replace) d.Remove("lootMode");
                    else d["lootMode"] = MergeModes.Replace;
                })));
            }

            rows.Add(new FormRow(1, (c, y) =>
            {
                c.AddStaticText(L("canrpgmobs:editor-simple-loot-own", "Own loot tables"), UiKit.SectionFont(), B(x, y + 2, 250))
                    .AddSmallButton(L("canrpgmobs:editor-simple-loot-add", "+ Table"), () => OpenPick("lootTables", L("canrpgmobs:editor-simple-loot-own", "Own loot tables"), null, v =>
                    {
                        var list = EnsureArray(d, "loot");
                        if (!list.Any(t => Same(t.ToString(), v))) list.Add(Short(v));
                    }), B(x + PanelW - 300, y, 146), EnumButtonStyle.Small)
                    .AddSmallButton(L("canrpgmobs:editor-loot-new", "+ New table"), NewLootTable, B(x + PanelW - 150, y, 146), EnumButtonStyle.Small);
            }));

            var own = (d["loot"] as JArray)?.Select(t => Q(t.ToString())).ToList() ?? new List<string>();
            foreach (var table in own)
            {
                string id = table;
                rows.Add(new FormRow(1, (c, y) => LootTableSummary(c, x, y, id, null, () =>
                {
                    if (d["loot"] is not JArray list) return;
                    foreach (var token in list.Where(t => Same(t.ToString(), id)).ToList()) token.Remove();
                    RemoveIfEmpty(d, "loot");
                })));
            }

            if (!mob || eff == null) return;
            var inherited = eff.LootTables.Where(id => !own.Contains(id)).Distinct().ToList();
            if (replace || inherited.Count == 0) return;
            rows.Add(TextRow(x, L("canrpgmobs:editor-loot-inherited", "Also drops, from race and role:"), false));
            foreach (var table in inherited)
            {
                string id = table;
                bool fromRace = eff.Race.loot?.Contains(id) == true;
                rows.Add(new FormRow(1, (c, y) => LootTableSummary(c, x, y, id,
                    FromLabel(fromRace), null)));
            }
        }

        private void LootTableSummary(GuiComposer c, double x, double y, string id, string? source, Action? remove)
        {
            string qualified = Q(id);
            c.AddFitText(Short(qualified) + (source != null ? $"  ({source})" : ""), EditorStyle.Body(), B(x + 16, y + 4, 220));
            if (mod.Catalog.Data.LootTables.TryGetValue(qualified, out var table))
            {
                double ix = x + 240;
                var shown = (table.guaranteed ?? new List<LootEntryModel>()).Concat(table.entries ?? new List<LootEntryModel>())
                    .Where(e => e != null && e.type != LootEntryModel.Table && !string.IsNullOrEmpty(e.code)).Take(6);
                foreach (var entry in shown)
                {
                    LootIcon(c, ix, y + 2, entry.code, entry.type == LootEntryModel.Block);
                    ix += 26;
                }
            }
            else c.AddStaticText(L("canrpgmobs:editor-loot-missing", "(not found)"), EditorStyle.Status(true), B(x + 240, y + 4, 120));

            c.AddSmallButton(L("canrpgmobs:editor-loot-open", "Open"), () => OpenLootTable(qualified), B(x + PanelW - 130, y, 86), EnumButtonStyle.Small);
            if (remove != null) RemoveButton(c, x, y, remove);
        }

        private bool OpenLootTable(string qualifiedId)
        {
            if (session.IsDirty || session.OpenedId == null)
            {
                SetStatus(L("canrpgmobs:editor-loot-save-first", "Save this entry first, then open the table."), true);
                Compose();
                return true;
            }
            // check first, otherwise we'd land in an empty section with no way back
            if (mod.Store.Get("lootTables", qualifiedId) == null && FromCatalog("lootTables", qualifiedId) == null)
            {
                SetStatus(L("canrpgmobs:editor-not-found", "That entry no longer exists."), true);
                Compose();
                return true;
            }
            var back = (session.Section, session.OpenedId!, false);
            SwitchSection("lootTables");
            filter = filterDraft = "";
            OpenEntry("lootTables", qualifiedId);
            lootReturn = back;
            Compose();
            return true;
        }

        private bool NewLootTable()
        {
            if (session.IsDirty || session.OpenedId == null)
            {
                SetStatus(L("canrpgmobs:editor-loot-save-first", "Save this entry first, then open the table."), true);
                Compose();
                return true;
            }
            string owner = session.OpenedId;
            lootReturn = (session.Section, owner, true);
            SwitchSection("lootTables");
            filter = filterDraft = "";
            string id = UniqueId(Q(Short(owner) + "_loot"));
            session.OpenNew("lootTables", EditorSchema.NewEntry("lootTables", id, mod.Catalog.Data));
            page = Page.Editor;
            ResetPages();
            SetStatus(L("canrpgmobs:editor-loot-new-created", "New table {0}: fill it in and save; it is then added to {1}.", Short(id), Short(owner)), false);
            Compose();
            return true;
        }

        private bool ReturnFromLoot(string? addTableId)
        {
            if (lootReturn is not { } back) return true;
            if (addTableId == null && session.IsDirty)
            {
                SetStatus(L("canrpgmobs:editor-loot-unsaved", "Save or revert the table first."), true);
                Compose();
                return true;
            }
            lootReturn = null;
            SwitchSection(back.Section);
            OpenEntry(back.Section, back.Id);
            simpleTab = "loot";
            if (addTableId != null && session.Draft is { } owner)
            {
                var list = EnsureArray(owner, "loot");
                if (!list.Any(t => Same(t.ToString(), addTableId))) list.Add(Short(addTableId));
                SetStatus(L("canrpgmobs:editor-loot-added", "Table {0} added: save to keep it.", Short(addTableId)), false);
            }
            Compose();
            return true;
        }
    }
}
