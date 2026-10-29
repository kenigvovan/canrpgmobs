using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using canrpgclasses;
using canrpgclasses.Client;
using canrpgclasses.Core.Spells;
using canrpgmobs.Catalog;
using canrpgmobs.Client.Editor.Controllers;
using canrpgmobs.Data;
using Newtonsoft.Json.Linq;
using Vintagestory.API.Client;
using static canrpgmobs.Client.UiKit;

namespace canrpgmobs.Client.Editor
{
    // Spell picker for skill rules.
    public partial class MobEditorDialog
    {
        private const int SpellRows = 11;

        private bool spellBrowser;
        private int? spellReplace;
        private string spellFilter = "";
        private string spellFilterDraft = "";
        private bool spellShowAll;
        private string? spellSelected;
        private int spellPage;
        private List<(Spell spell, SpellSupport support)>? spellList;
        private string spellListKey = "";
        private MobCatalog? spellListCatalog;

        private bool OpenSpellBrowser(int? replaceIndex)
        {
            spellBrowser = true;
            spellReplace = replaceIndex;
            spellList = null;
            spellFilter = spellFilterDraft = "";
            spellSelected = null;
            spellPage = 0;
            Compose();
            return true;
        }

        private void AddSpellBrowser(GuiComposer compo, double x, double y, JObject d)
        {
            SearchRow(compo, x, y, 200, spellFilterDraft, v => spellFilterDraft = v,
                    L("canrpgmobs:editor-itempick-search", "Search"), x + 204, 86, () => { spellFilter = spellFilterDraft.Trim(); spellPage = 0; })
                .AddSmallButton(spellShowAll ? L("canrpgmobs:editor-skill-all", "All spells") : L("canrpgmobs:editor-skill-fitting", "Suitable only"),
                    () => { spellShowAll = !spellShowAll; spellPage = 0; Compose(); return true; }, B(x + 296, y, 180), EnumButtonStyle.Small)
                .AddSmallButton(L("canrpgmobs:editor-cancel", "Cancel"), () => { spellBrowser = false; spellReplace = null; Compose(); return true; },
                    B(x + PanelW - 110, y, 106), EnumButtonStyle.Small);

            // cached per filter + catalog, selecting/paging recomposes too
            string listKey = (spellShowAll ? "all\n" : "fit\n") + spellFilter;
            if (spellList == null || listKey != spellListKey || !ReferenceEquals(spellListCatalog, mod.Catalog))
            {
                var all = canrpgclassesModSystem.ClientInstance?.Spells.All.Values ?? Enumerable.Empty<Spell>();
                spellList = all
                    .Select(s => (spell: s, support: mod.Catalog.RateSpell(s.Id, out _)))
                    .Where(t => spellShowAll || t.support != SpellSupport.Unsupported)
                    .Where(t => spellFilter.Length == 0 || t.spell.DisplayName.IndexOf(spellFilter, StringComparison.OrdinalIgnoreCase) >= 0
                                || t.spell.Id.IndexOf(spellFilter, StringComparison.OrdinalIgnoreCase) >= 0)
                    .OrderBy(t => t.spell.DisplayName, StringComparer.CurrentCultureIgnoreCase)
                    .ToList();
                spellListKey = listKey;
                spellListCatalog = mod.Catalog;
            }
            var spells = spellList;
            int pages = Math.Max(1, (spells.Count + SpellRows - 1) / SpellRows);
            spellPage = Math.Clamp(spellPage, 0, pages - 1);

            double row = y + 32;
            foreach (var (spell, support) in spells.Skip(spellPage * SpellRows).Take(SpellRows))
            {
                string id = spell.Id;
                string tag = support switch { SpellSupport.Supported => "[OK]", SpellSupport.Degraded => "[~]", _ => "[X]" };
                compo.AddFitButton((spellSelected == id ? "> " : "") + tag + " " + spell.DisplayName, () => { spellSelected = id; Compose(); return true; },
                    B(x, row, 290), EnumButtonStyle.Small);
                row += RowStep;
            }
            double pagerY = y + 32 + SpellRows * RowStep + 2;
            Pager(compo, x, pagerY, spellPage, pages, p => spellPage = p, spells.Count);

            double rx = x + 300, rw = PanelW - 300;
            var selected = spells.Select(t => t.spell).FirstOrDefault(s => s.Id == spellSelected);
            if (selected == null)
            {
                compo.AddStaticText(L("canrpgmobs:editor-skill-pick-note", "Pick a spell on the left."), EditorStyle.Body(), B(rx, y + 36, rw, 40));
                return;
            }

            compo.AddFitText(selected.DisplayName, UiKit.SectionFont(), B(rx, y + 32, rw, 24))
                .AddStaticText(PickLists.SpellTag(mod, selected.Id) + " " + SpellReasons(selected.Id), HelpFont(), B(rx, y + 58, rw, 30));
            // strip tooltip markup
            string description = Regex.Replace(selected.Description ?? "", "<[^>]+>", "");
            compo.AddStaticText(description.Length > 0 ? description : L("canrpgmobs:editor-skill-no-desc", "No description."), EditorStyle.Body(), B(rx, y + 90, rw, 130));

            double by = y + 226;
            if (spellReplace is { } replaceIndex)
            {
                compo.AddSmallButton(L("canrpgmobs:editor-skill-use", "Use this spell"), () =>
                {
                    if (d["skills"] is JArray list && replaceIndex >= 0 && replaceIndex < list.Count && list[replaceIndex] is JObject rule) rule["spell"] = selected.Id;
                    spellBrowser = false;
                    spellReplace = null;
                    Compose();
                    return true;
                }, B(rx, by, rw), EnumButtonStyle.Small);
                return;
            }
            compo.AddStaticText(L("canrpgmobs:editor-skill-how", "How to use it:"), EditorStyle.FieldLabel(), B(rx, by + 4, rw, 20));
            by += 24;
            foreach (var use in SkillPresets.SpellUses)
            {
                string preset = use;
                compo.AddSmallButton(L("canrpgmobs:editor-skilluse-" + preset, preset), () =>
                {
                    spellBrowser = false;
                    AddSkill(d, SkillPresets.Spell(preset, selected.Id, NextSkillId(d), selected.Range));
                    Compose();
                    return true;
                }, B(rx, by, rw), EnumButtonStyle.Small);
                by += RowStep;
            }
        }

        private string SpellReasons(string spellId)
        {
            if (string.IsNullOrWhiteSpace(spellId)) return "";
            mod.Catalog.RateSpell(spellId, out var reasons);
            return reasons.Length == 0 ? "" : string.Join("; ", reasons);
        }

        private static string SpellDisplayName(string spellId)
            => canrpgclassesModSystem.ClientInstance?.Spells.All.TryGetValue(spellId, out var spell) == true ? spell.DisplayName : spellId;
    }
}
